using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Havok;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Services;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Grid Defender: zero-cost collision defense, kinetic damage interception, player-made missile
    /// tracking, anti-clang damping, push-apart separation, voxel cut-out suppression, and layered
    /// armor occlusion.
    /// </summary>
    public class GridDefender : IPhysicsOptimizer
    {
        private const string LogSource = "GridDefender";

        public string Name => "Grid Defender";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnableGridDefender;

        private PhysicsOptimizerPlugin _plugin;

        private class MissileEngagement
        {
            public long GroupId;
            public ulong ExpireFrame;
            public string MissileName;
            public string TargetName;
            public float InitialSpeed;
            public int ImpactCount;
        }

        private struct PushApartAction
        {
            public long GridId;
            public Vector3D SeparationDir;
            public float Distance;
            public Vector3D StartPos;
            public bool VoxelPush;
        }

        private sealed class BurialProbeState
        {
            public Vector3D LastPosition;
            public int StationarySweeps;
        }


        private sealed class EscapeRecord
        {
            public Vector3D Direction;
            public ulong Frame;
            public int Level;
        }

        private static long _nextMissileGroupId = 0;

        private readonly ConcurrentQueue<PushApartAction> _pushQueue = new();
        private readonly ConcurrentDictionary<long, MissileEngagement> _activeMissiles = new();
        private readonly ConcurrentDictionary<long, ulong> _lastDeformationFrames = new();
        private readonly ConcurrentDictionary<long, int> _consecutiveContactFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastContactFrameTracker = new();
        private readonly ConcurrentDictionary<long, ulong> _lastRammingLogFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastVoxelLogFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastStationLogFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastSubgridLogFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastExtremeSpeedLogFrames = new();
        private static readonly ConcurrentDictionary<long, ulong> _lastVoxelContactFrames = new();
        private readonly ConcurrentDictionary<long, BurialProbeState> _burialProbeStates = new();
        private readonly List<MyCubeGrid> _groupMembersBuffer = new();
        private readonly ConcurrentDictionary<long, int> _pushApartAttempts = new();
        private readonly ConcurrentDictionary<long, EscapeRecord> _lastEscapes = new();

        // Wheel subgrid cache: key=wheel subgrid id, value=base grid id (negative sentinel = not a wheel).
        private static readonly ConcurrentDictionary<long, long> _wheelBaseGridIds = new();
        private readonly ConcurrentDictionary<long, Vector3D> _contactStartPositions = new();
        private readonly ConcurrentDictionary<long, ulong> _lastPushApartGiveUpLogFrames = new();

        // Layered Armor Occlusion context tracking: Key: Grid EntityId, Value: Global impact position
        private static readonly ConcurrentDictionary<long, Vector3D> _lastImpactPositions = new();

        // Push-apart escape direction source: raw voxel contact normal cached per grid, independent of the arbitrator toggle
        private static readonly ConcurrentDictionary<long, Vector3D> _lastVoxelContactNormals = new();

        // Per-voxel-map coarse modification map: Key: voxel EntityId, Value: set of modified 32m region buckets.
        // Tracked from MyVoxelBase.RangeChanged (fires per carve operation), so a drilled planet only records
        // the carved regions - pristine terrain everywhere else keeps the raycast-free push direction fast path.
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _modifiedVoxelRegions = new();
        private static readonly ConcurrentDictionary<long, MyVoxelBase.StorageChanged> _voxelRangeHandlers = new();
        private const int VoxelRegionBucketShift = 5; // 32m buckets (voxel storage cells are 1m)

        [ThreadStatic]
        private static List<MyPhysics.HitInfo> _voxelHitsCache;

        public static void UpdateCollisionContext(long gridId, Vector3D position)
        {
            _lastImpactPositions[gridId] = position;
        }

        public static void RemoveCollisionContext(long gridId)
        {
            _lastImpactPositions.TryRemove(gridId, out _);
            _lastVoxelContactNormals.TryRemove(gridId, out _);
        }

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            ClearCollections();
            SyncVoxelFakes();
            Log.Info(LogSource, "Initialized successfully.");
        }

        public void Update(ulong frameCounter)
        {
            SweepCaches(frameCounter);
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
            SyncVoxelFakes();
        }

        public void OnEntityAdded(MyEntity entity)
        {
            if (entity is MyVoxelBase voxel && voxel.Storage != null) HookVoxelStorage(voxel);
        }

        private static void HookVoxelStorage(MyVoxelBase voxel)
        {
            MyVoxelBase.StorageChanged handler = (v, min, max, flags) => MarkVoxelRange(v, min, max);
            if (_voxelRangeHandlers.TryAdd(voxel.EntityId, handler))
            {
                voxel.RangeChanged += handler;
            }
        }

        private static void MarkVoxelRange(MyVoxelBase voxel, Vector3I min, Vector3I max)
        {
            var buckets = _modifiedVoxelRegions.GetOrAdd(voxel.EntityId, _ => new ConcurrentDictionary<long, byte>());
            int bMinX = min.X >> VoxelRegionBucketShift, bMinY = min.Y >> VoxelRegionBucketShift, bMinZ = min.Z >> VoxelRegionBucketShift;
            int bMaxX = max.X >> VoxelRegionBucketShift, bMaxY = max.Y >> VoxelRegionBucketShift, bMaxZ = max.Z >> VoxelRegionBucketShift;
            long cellCount = (long)(bMaxX - bMinX + 1) * (bMaxY - bMinY + 1) * (bMaxZ - bMinZ + 1);
            if (cellCount > 4096)
            {
                // Huge carve (voxel clear/removal tool) - mark the whole map modified instead of iterating
                buckets[-1L] = 1;
                return;
            }
            for (int x = bMinX; x <= bMaxX; x++)
            {
                for (int y = bMinY; y <= bMaxY; y++)
                {
                    for (int z = bMinZ; z <= bMaxZ; z++)
                    {
                        buckets[PackBucket(x, y, z)] = 1;
                    }
                }
            }
        }

        private static long PackBucket(int x, int y, int z)
        {
            return ((long)(uint)x << 42) | ((long)(uint)y << 21) | (uint)z;
        }

        /// <summary>True if the 32m region around worldPos was carved/drilled at some point (per RangeChanged tracking).</summary>
        private static bool IsVoxelRegionModified(MyVoxelBase voxel, Vector3D worldPos)
        {
            if (!_modifiedVoxelRegions.TryGetValue(voxel.EntityId, out var buckets) || buckets.IsEmpty) return false;
            if (buckets.ContainsKey(-1L)) return true;
            Vector3D local = (worldPos - voxel.PositionLeftBottomCorner) / voxel.VoxelSize;
            int cx = (int)Math.Floor(local.X), cy = (int)Math.Floor(local.Y), cz = (int)Math.Floor(local.Z);
            long bucket = PackBucket(cx >> VoxelRegionBucketShift, cy >> VoxelRegionBucketShift, cz >> VoxelRegionBucketShift);
            return buckets.ContainsKey(bucket);
        }

        public void OnEntityRemoved(MyEntity entity)
        {
            if (entity is MyCubeGrid grid)
            {
                EvictGrid(grid.EntityId);
                RemoveCollisionContext(grid.EntityId);
            }
        }

        public void Dispose()
        {
            RestoreVoxelFakes();
            ClearCollections();
            _plugin = null;
        }

        private void ClearCollections()
        {
            while (_pushQueue.TryDequeue(out _)) { }
            _activeMissiles.Clear();
            _lastDeformationFrames.Clear();
            _consecutiveContactFrames.Clear();
            _lastContactFrameTracker.Clear();
            _lastRammingLogFrames.Clear();
            _lastVoxelLogFrames.Clear();
            _lastStationLogFrames.Clear();
            _lastSubgridLogFrames.Clear();
            _lastExtremeSpeedLogFrames.Clear();
            _lastImpactPositions.Clear();
            _lastVoxelContactNormals.Clear();
            _pushApartAttempts.Clear();
            _lastPushApartGiveUpLogFrames.Clear();
            _wheelBaseGridIds.Clear();
            _lastEscapes.Clear();
            _contactStartPositions.Clear();
            _lastVoxelContactFrames.Clear();
            _burialProbeStates.Clear();
            _groupMembersBuffer.Clear();
        }

        public void SyncVoxelFakes()
        {
            if (_plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.SuppressAllVoxelExplosionDamage)
            {
                MyFakes.DEFORMATION_EXPLOSIONS = false; // Disables voxel cutouts from collision deformation
            }
        }

        public static void RestoreVoxelFakes()
        {
            MyFakes.DEFORMATION_EXPLOSIONS = true;
        }

        /// <summary>
        /// Evaluates whether a given grid qualifies as a player-made missile based on size and speed.
        /// </summary>
        public bool IsMissile(MyCubeGrid testGrid, float speed)
        {
            if (_plugin?.Config == null) return false;
            var config = _plugin.Config;

            if (testGrid == null || testGrid.MarkedForClose || testGrid.Closed || testGrid.IsStatic) return false;
            if (speed < config.MissileMinVelocity) return false;
            if (config.ExemptPilotedFromMissileStatus && (testGrid.GridSystems?.ControlSystem?.IsControlled ?? false))
            {
                int b = testGrid.BlocksCount;
                bool wouldBeMissile = testGrid.IsLargeGrid()
                    ? (b >= config.LargeGridMissileMinBlocks && b <= config.LargeGridMissileMaxBlocks)
                    : (b >= config.SmallGridMissileMinBlocks && b <= config.SmallGridMissileMaxBlocks);
                if (wouldBeMissile)
                {
                    _plugin?.DefenseStats?.IncrementPilotedBuggySaves();
                }
                return false;
            }

            int blocks = testGrid.BlocksCount;
            if (testGrid.IsLargeGrid())
            {
                return blocks >= config.LargeGridMissileMinBlocks && blocks <= config.LargeGridMissileMaxBlocks;
            }
            if (testGrid.IsSmallGrid())
            {
                return blocks >= config.SmallGridMissileMinBlocks && blocks <= config.SmallGridMissileMaxBlocks;
            }
            return false;
        }

        /// <summary>
        /// Main collision decision hook. Filters deformation across the sequential defense pipeline.
        /// </summary>
        public bool ShouldAllowDeformation(MyGridPhysics physics, MyEntity otherEntity, ref float separatingVelocity)
        {
            if (_plugin?.Config == null || !_plugin.Config.Enabled || !_plugin.Config.EnableGridDefender) return true;
            var config = _plugin.Config;
            var stats = _plugin.DefenseStats;

            if (physics?.Entity is not MyCubeGrid grid || grid.MarkedForClose || grid.Closed) return true;
            if (otherEntity == null || otherEntity.MarkedForClose || otherEntity.Closed) return true;

            stats?.IncrementEvaluated();
            SyncVoxelFakes();

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;

            // 3. Speed calculations (hoisted - the subgrid branch and impact gate need impactSpeed)
            float gridSpeed = grid.GetSpeed();
            float otherSpeed = (otherEntity as MyCubeGrid)?.GetSpeed() ?? 0f;
            float absSepVelocity = Math.Abs(separatingVelocity);
            float impactSpeed = Math.Max(absSepVelocity, Math.Max(gridSpeed, otherSpeed));

            // 1. Subgrid / Mechanicals (Pistons, Rotors, Hinges, Connectors) Protection
            if (otherEntity is MyCubeGrid otherGrid)
            {
                if (config.ProtectSubgrids && (GridUtils.AreInSameMechanicalGroup(grid, otherGrid) || GridUtils.AreInSameLogicalGroup(grid, otherGrid)))
                {
                    if (config.EnableDebugLogging && ShouldLog(_lastSubgridLogFrames, grid.EntityId ^ otherGrid.EntityId, currentFrame, 120))
                    {
                        Log.Info(LogSource, $"[SUBGRID] Protected: Blocked collision between '{grid.DisplayName}' and '{otherGrid.DisplayName}'.");
                    }
                    ApplyAntiClang(grid, physics, otherEntity, impactSpeed);
                    stats?.IncrementBlocked(isSubgrid: true);
                    return false;
                }
            }

            // 2. Floating Objects / Ores / Loose Debris Protection
            if (otherEntity is MyFloatingObject && config.ProtectAgainstFloatingObjects)
            {
                stats?.IncrementBlocked(isDebris: true);
                return false;
            }

            // 4. Missile (PMW) Evaluation & Engagement Tracking (Targets grids only, never voxels)
            if (otherEntity is MyCubeGrid targetGrid)
            {
                bool gridInMissile = _activeMissiles.TryGetValue(grid.EntityId, out var gridEngage) && currentFrame <= gridEngage.ExpireFrame;
                bool otherInMissile = _activeMissiles.TryGetValue(targetGrid.EntityId, out var otherEngage) && currentFrame <= otherEngage.ExpireFrame;

                // Friendly-fire shield: Suppress self-damage between splits of the same missile
                if (gridInMissile && otherInMissile && gridEngage.GroupId == otherEngage.GroupId)
                {
                    return false;
                }

                bool gridQualifies = gridInMissile || (config.AllowMissileDamage && IsMissile(grid, impactSpeed));
                bool otherQualifies = otherInMissile || (config.AllowMissileDamage && IsMissile(targetGrid, impactSpeed));

                if (gridQualifies || otherQualifies)
                {
                    MyCubeGrid missileObj = gridQualifies ? grid : targetGrid;
                    MyCubeGrid targetObj = ReferenceEquals(missileObj, grid) ? targetGrid : grid;

                    MissileEngagement engagement;
                    if (gridInMissile)
                    {
                        engagement = gridEngage;
                    }
                    else if (otherInMissile)
                    {
                        engagement = otherEngage;
                    }
                    else
                    {
                        long groupId = Interlocked.Increment(ref _nextMissileGroupId);
                        ulong expire = currentFrame + 60; // 60 frames (~1.0s) active penetration window

                        engagement = new MissileEngagement
                        {
                            GroupId = groupId,
                            ExpireFrame = expire,
                            MissileName = missileObj.DisplayName,
                            TargetName = targetObj.DisplayName,
                            InitialSpeed = impactSpeed,
                            ImpactCount = 0
                        };

                        if (config.EnableDebugLogging && config.LogMissileDefense)
                        {
                            Log.Info(LogSource, $"[MISSILE] Impact ALLOWED: '{engagement.MissileName}' struck '{engagement.TargetName}' at {engagement.InitialSpeed:F1} m/s.");
                        }
                    }

                    Interlocked.Increment(ref engagement.ImpactCount);

                    if (gridQualifies && !gridInMissile) RegisterActiveMissile(grid, engagement);
                    if (otherQualifies && !otherInMissile) RegisterActiveMissile(targetGrid, engagement);

                    // Clamp extreme torsional death-spins without bleeding forward kinetic momentum
                    if (config.EnableAntiClang && config.StopClangSpinning && physics.AngularVelocity.LengthSquared() > 16.0f)
                    {
                        physics.AngularVelocity = Vector3.Zero;
                    }

                    return AllowOrScale(grid.EntityId, ref separatingVelocity, isMissile: true);
                }
            }

            // 5. Safe Docking, Parking, and Slow Driving Check (Low-speed floor)
            if (impactSpeed < config.MinDrivingVelocity)
            {
                if (config.EnableDebugLogging && impactSpeed >= 1.5f && ShouldLog(_lastRammingLogFrames, grid.EntityId, currentFrame, 120))
                {
                    if (otherEntity is MyVoxelBase)
                        Log.Info(LogSource, $"[DRIVING] Low-speed voxel contact on '{grid.DisplayName}' - normal driving, suppression harmless.");
                    else
                        Log.Info(LogSource, $"[DOCKING] Safe Docking: Suppressed low-speed bump on '{grid.DisplayName}'.");
                }
                ApplyAntiClang(grid, physics, otherEntity, impactSpeed);
                stats?.IncrementBlocked(isLowSpeed: true);
                return false;
            }

            // 6. Extreme Velocity Anti-Freeze Limit (Non-missiles only)
            if (config.MaxDeformationVelocity > 0 && impactSpeed > config.MaxDeformationVelocity)
            {
                if (config.EnableDebugLogging && ShouldLog(_lastExtremeSpeedLogFrames, grid.EntityId, currentFrame, 60))
                {
                    Log.Warn(LogSource, $"[SPEED] Limit: Suppressed collision on '{grid.DisplayName}' ({impactSpeed:F1} m/s > {config.MaxDeformationVelocity:F1} m/s limit).");
                }
                ApplyImpactDamping(physics, grid.IsStatic);
                ApplyAntiClang(grid, physics, otherEntity, impactSpeed);
                stats?.IncrementBlocked(isRamming: otherEntity is MyCubeGrid, isVoxel: otherEntity is MyVoxelBase);
                return false;
            }

            // 7. Non-Missile Collisions (Ships, Rovers, Stations, Voxels)
            // A. Static Station Protection
            bool otherIsStaticGrid = otherEntity is MyCubeGrid { IsStatic: true };
            if ((grid.IsStatic || otherIsStaticGrid) && config.ProtectStaticGrids)
            {
                if (config.EnableDebugLogging && ShouldLog(_lastStationLogFrames, grid.EntityId ^ otherEntity.EntityId, currentFrame, 60))
                {
                    string stationName = grid.IsStatic ? grid.DisplayName : otherEntity.DisplayName;
                    string strikingName = grid.IsStatic ? otherEntity.DisplayName : grid.DisplayName;
                    Log.Info(LogSource, $"[STATION] Protected: '{strikingName}' struck static station '{stationName}' at {impactSpeed:F1} m/s (damage suppressed).");
                }
                if (grid.IsStatic)
                {
                    if (otherEntity is MyCubeGrid otherCubeGrid && !otherCubeGrid.IsStatic && otherCubeGrid.Physics != null)
                    {
                        ApplyImpactDamping(otherCubeGrid.Physics as MyGridPhysics, false);
                        ApplyAntiClang(otherCubeGrid, otherCubeGrid.Physics as MyGridPhysics, grid, impactSpeed);
                    }
                }
                else
                {
                    ApplyImpactDamping(physics, false);
                    ApplyAntiClang(grid, physics, otherEntity, impactSpeed);
                }
                stats?.IncrementBlocked(isStation: true);
                return false;
            }

            // B. Ship vs Voxel Protection (Asteroids, terrain, and off-target missiles hitting dirt)
            if (otherEntity is MyVoxelBase)
            {
                if (config.ProtectShipsAgainstVoxels)
                {
                    if (config.EnableDebugLogging && ShouldLog(_lastVoxelLogFrames, grid.EntityId, currentFrame, 60))
                    {
                        Log.Info(LogSource, $"[VOXEL] Terrain Crash Blocked: '{grid.DisplayName}' ({grid.BlocksCount} blocks) hit voxels at {impactSpeed:F1} m/s (damage suppressed).");
                    }
                    ApplyImpactDamping(physics, grid.IsStatic);
                    ApplyAntiClang(grid, physics, otherEntity, impactSpeed);
                    stats?.IncrementBlocked(isVoxel: true);
                    return false;
                }
                return AllowOrScale(grid.EntityId, ref separatingVelocity, isMissile: false);
            }

            // C. Ship vs Ship Ramming Protection
            if (otherEntity is MyCubeGrid && config.ProtectShipsAgainstRamming)
            {
                if (config.EnableDebugLogging && ShouldLog(_lastRammingLogFrames, grid.EntityId ^ otherEntity.EntityId, currentFrame, 60))
                {
                    Log.Info(LogSource, $"[RAMMING] Blocked: '{grid.DisplayName}' ({grid.BlocksCount} blocks) hit '{otherEntity.DisplayName}' at {impactSpeed:F1} m/s (damage suppressed).");
                }
                ApplyImpactDamping(physics, grid.IsStatic);
                ApplyAntiClang(grid, physics, otherEntity, impactSpeed);
                stats?.IncrementBlocked(isRamming: true);
                return false;
            }

            // 8. Rate Limiting / Cooldown for any unprotected continuous deformations
            if (config.DeformationCooldownFrames > 0 && currentFrame > 0)
            {
                if (_lastDeformationFrames.TryGetValue(grid.EntityId, out ulong lastFrame))
                {
                    if (currentFrame >= lastFrame && (currentFrame - lastFrame) < (ulong)config.DeformationCooldownFrames)
                    {
                        stats?.IncrementBlocked(isCooldown: true);
                        return false;
                    }
                }
            }

            return AllowOrScale(grid.EntityId, ref separatingVelocity, isMissile: false);
        }

        private void RegisterActiveMissile(MyCubeGrid missileGrid, MissileEngagement engagement)
        {
            if (missileGrid == null || missileGrid.MarkedForClose || missileGrid.Closed || engagement == null) return;

            long id = missileGrid.EntityId;
            _activeMissiles[id] = engagement;

            missileGrid.OnClose -= OnTrackedGridClosed;
            missileGrid.OnClose += OnTrackedGridClosed;
            missileGrid.OnGridSplit -= OnTrackedGridSplit;
            missileGrid.OnGridSplit += OnTrackedGridSplit;
        }

        private void EvictGrid(long id)
        {
            _activeMissiles.TryRemove(id, out _);
            _lastDeformationFrames.TryRemove(id, out _);
            _consecutiveContactFrames.TryRemove(id, out _);
            _lastContactFrameTracker.TryRemove(id, out _);
            _lastRammingLogFrames.TryRemove(id, out _);
            _lastVoxelLogFrames.TryRemove(id, out _);
            _lastStationLogFrames.TryRemove(id, out _);
            _lastSubgridLogFrames.TryRemove(id, out _);
            _lastExtremeSpeedLogFrames.TryRemove(id, out _);
            _lastVoxelContactFrames.TryRemove(id, out _);
            _burialProbeStates.TryRemove(id, out _);
            _lastVoxelContactNormals.TryRemove(id, out _);
            _pushApartAttempts.TryRemove(id, out _);
            _lastPushApartGiveUpLogFrames.TryRemove(id, out _);
            _wheelBaseGridIds.TryRemove(id, out _);
            _lastEscapes.TryRemove(id, out _);
            _contactStartPositions.TryRemove(id, out _);
        }

        private void OnTrackedGridClosed(IMyEntity entity)
        {
            if (entity == null) return;
            EvictGrid(entity.EntityId);
        }

        private void OnTrackedGridSplit(MyCubeGrid originalGrid, MyCubeGrid newGrid)
        {
            if (originalGrid == null || newGrid == null || newGrid.MarkedForClose || newGrid.Closed) return;

            if (_activeMissiles.TryGetValue(originalGrid.EntityId, out var engagement))
            {
                RegisterActiveMissile(newGrid, engagement);
            }
        }

        private void ApplyImpactDamping(MyGridPhysics physics, bool isStatic)
        {
            var config = _plugin?.Config;
            if (config == null || isStatic || physics == null || !config.EnableAntiClang || config.ImpactVelocityDamping <= 0.0f) return;
            if (physics.Entity == null || physics.Entity.MarkedForClose || physics.Entity.Closed) return;

            float factor = Math.Max(0.0f, 1.0f - (config.ImpactVelocityDamping * 0.6f));
            physics.LinearVelocity *= factor;
        }

        /// <summary>
        /// Cached wheel subgrid test. Wheels are single-block subgrids, so the scan is trivial.
        /// Returns true with baseGridId set when the grid hosts a MyWheel attached to a suspension stator.
        /// </summary>
        private static bool IsWheelSubgrid(MyCubeGrid grid, out long baseGridId)
        {
            baseGridId = -1L;
            if (grid == null || grid.MarkedForClose || grid.Closed) return false;

            if (_wheelBaseGridIds.TryGetValue(grid.EntityId, out long cached))
            {
                baseGridId = cached > 0L ? cached : -1L;
                return cached > 0L;
            }

            long resolved = TryResolveWheelBaseGrid(grid);
            _wheelBaseGridIds[grid.EntityId] = resolved;
            baseGridId = resolved > 0L ? resolved : -1L;
            return resolved > 0L;
        }

        private static long TryResolveWheelBaseGrid(MyCubeGrid grid)
        {
            foreach (var fat in grid.GetFatBlocks())
            {
                if (fat is MyMotorRotor rotor && rotor.Stator is MyMotorSuspension)
                {
                    var stator = rotor.Stator;
                    if (stator?.CubeGrid != null && !stator.CubeGrid.MarkedForClose && !stator.CubeGrid.Closed)
                    {
                        return stator.CubeGrid.EntityId;
                    }
                }
            }

            // Fallback: mechanical group scan for the base grid (wheels should always have a stator,
            // but if the stator reference is not yet wired, pick the first non-rotor member).
            var members = new List<MyCubeGrid>();
            GridUtils.GetMechanicalGroupMembers(grid, members);
            foreach (var member in members)
            {
                if (member == null || member.MarkedForClose || member.Closed || member.EntityId == grid.EntityId) continue;
                foreach (var fat in member.GetFatBlocks())
                {
                    if (fat != null && !(fat is MyMotorRotor)) return member.EntityId;
                }
            }
            return -1L;
        }

        private void ApplyAntiClang(MyCubeGrid grid, MyGridPhysics physics, MyEntity otherEntity, float impactSpeed)
        {
            var config = _plugin?.Config;
            if (config == null || grid == null || grid.MarkedForClose || grid.Closed) return;

            bool isWheelVoxel = otherEntity is MyVoxelBase && IsWheelSubgrid(grid, out _);

            // Push-apart contact tracking runs even with Anti-Clang disabled - Phase 2 is gated on EnablePushApart only
            int contactCount = UpdateContactContext(grid, otherEntity, config, impactSpeed, isWheelVoxel);

            if (physics == null || !config.EnableAntiClang) return;

            // Wheel subgrids are the suspension rotor body; damping their spin mid-drive ruins handling.
            if (isWheelVoxel && config.ExcludeWheelSubgridsFromAntiClang) return;

            // Phase 1: Vibration & Torque Arrest (Early Clang threshold)
            if (contactCount >= config.AntiClangVibrationThreshold)
            {
                physics.LinearVelocity *= 0.75f;
                physics.AngularVelocity *= 0.2f;

                if (config.StopClangSpinning && physics.AngularVelocity.LengthSquared() > 16.0f)
                {
                    physics.AngularVelocity = Vector3.Zero;
                }

                _plugin?.DefenseStats?.IncrementClangArrested();

                if (config.EnableDebugLogging && contactCount == config.AntiClangVibrationThreshold)
                {
                    Log.Warn(LogSource, $"[ANTI-CLANG] Activated for '{grid.DisplayName}' ({contactCount} contact frames).");
                }
            }
        }

        private int UpdateContactContext(MyCubeGrid grid, MyEntity otherEntity, PhysicsOptimizerConfig config, float impactSpeed, bool isWheelVoxel)
        {
            long gridEntityId = grid.EntityId;
            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (currentFrame == 0) return 0;

            int contactCount = 1;
            if (_lastContactFrameTracker.TryGetValue(gridEntityId, out ulong lastFrame))
            {
                if (currentFrame == lastFrame + 1 || currentFrame == lastFrame)
                {
                    contactCount = _consecutiveContactFrames.AddOrUpdate(gridEntityId, 1, (k, v) => v + 1);
                }
                else
                {
                    _consecutiveContactFrames[gridEntityId] = 1;
                }
            }
            else
            {
                _consecutiveContactFrames[gridEntityId] = 1;
            }
            _lastContactFrameTracker[gridEntityId] = currentFrame;
            if (contactCount == 1) _contactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;

            // RigidBodySleep guard feed: sleeping bodies emit no contact callbacks, so a voxel-contact
            // timestamp is the only signal keeping a terrain-grinding grid awake long enough to be rescued
            if (otherEntity is MyVoxelBase)
            {
                _lastVoxelContactFrames[gridEntityId] = currentFrame;
            }

            // Phase 2: Active Push-Apart (Excludes mechanically/logically connected subgrids)
            bool areConnectedSubgrids = otherEntity is MyCubeGrid otherGrid &&
                (GridUtils.AreInSameMechanicalGroup(grid, otherGrid) || GridUtils.AreInSameLogicalGroup(grid, otherGrid));

            // Impact-speed gate: resting grids (contacts at ~0 m/s) never arm pushes; only energetic
            // contacts from driving, docking bumps, or clang oscillation qualify
            bool impactGateOpen = config.PushApartMinImpactSpeed <= 0f || impactSpeed >= config.PushApartMinImpactSpeed;

            // Drift gate: a grid covering ground during the contact window is driving, not stuck -
            // wheel-terrain contacts fire every frame of normal driving, so contact counts alone
            // would micro-teleport every moving rover. Wedged grids drift near zero.
            bool driftGateOpen = config.PushApartMaxDrift <= 0f;
            if (!driftGateOpen && _contactStartPositions.TryGetValue(gridEntityId, out Vector3D startPos))
            {
                Vector3D drift = grid.PositionComp.WorldVolume.Center - startPos;
                driftGateOpen = drift.LengthSquared() <= (double)config.PushApartMaxDrift * config.PushApartMaxDrift;
            }
            // Wheel-terrain contacts fire every frame of normal driving; excluding them removes the
            // driving false positives that cause rovers to micro-teleport across flat terrain.
            bool pushApartAllowed = !isWheelVoxel || !config.ExcludeWheelSubgridsFromPushApart;
            if (pushApartAllowed && !areConnectedSubgrids && impactGateOpen && driftGateOpen && config.EnablePushApart && !grid.IsStatic && contactCount >= config.PushApartThreshold)
            {
                TryPushApart(grid, otherEntity);
                _consecutiveContactFrames[gridEntityId] = 0;
            }

            return contactCount;
        }

        /// <summary>True if the grid recorded a voxel contact within the last 30 sim frames.</summary>
        public static bool HadRecentVoxelContact(long gridId) => HadVoxelContactWithin(gridId, 30);

        /// <summary>True if the grid recorded a voxel contact within the given number of sim frames.</summary>
        private static bool HadVoxelContactWithin(long gridId, ulong frames)
        {
            ulong frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            return frame > 0 && _lastVoxelContactFrames.TryGetValue(gridId, out ulong last) && frame >= last && (frame - last) <= frames;
        }

        /// <summary>
        /// Picks a push direction that actually leads out of the terrain. Unmodified voxel-map terrain is a
        /// fast path: the grid sits on the open surface (caves and convex traps only exist where voxels were
        /// modified), so a gravity-validated contact normal needs no raycast. Modified terrain (player caves)
        /// raycast-confirms every candidate so wedged grids escape sideways instead of staircasing skyward.
        /// </summary>
        private bool TryResolveVoxelEscapeDirection(MyCubeGrid grid, PhysicsOptimizerConfig config, MyVoxelBase contactVoxel, out Vector3D dir)
        {
            dir = Vector3D.Zero;
            Vector3D center = grid.PositionComp.WorldVolume.Center;
            double rayLength = grid.PositionComp.WorldVolume.Radius + config.PushApartDistance + 0.5;
            Vector3D up = grid.Physics != null && grid.Physics.Gravity.LengthSquared() > 0.1f
                ? -Vector3D.Normalize(grid.Physics.Gravity)
                : Vector3D.Up;

            bool hasNormal = _lastVoxelContactNormals.TryGetValue(grid.EntityId, out Vector3D normal) && normal.LengthSquared() > 0.001;
            Vector3D testPos = _lastImpactPositions.TryGetValue(grid.EntityId, out Vector3D hitPos) ? hitPos : center;

            // Terrain lies on the opposite side of the contact from the grid body, so the contact-to-center
            // vector is the free-side reference: any normal pointing against it would shove into the terrain
            // (underside/wall contacts) and is flipped before use. Free - no raycast.
            Vector3D freeSide = center - testPos;
            double freeSideLen = freeSide.Length();
            if (hasNormal && freeSideLen > 0.01)
            {
                Vector3D freeDir = freeSide / freeSideLen;
                if (Vector3D.Dot(normal, freeDir) < 0f) normal = -normal;
                hasNormal = Vector3D.Dot(normal, freeDir) > 0.001;
            }

            // Fast path: pristine voxel region. A normal pointing against gravity points into the planet
            // core (Keen compression artifact) - reverse it like the Normal Force Arbitrator does, no raycast.
            if (contactVoxel == null || !IsVoxelRegionModified(contactVoxel, testPos))
            {
                if (hasNormal)
                {
                    dir = Vector3D.Dot(normal, up) < 0f ? -normal : normal;
                    return true;
                }
                dir = up;
                return true;
            }

            // Slow path: modified voxels (drilled caves/cutouts). Downward normals can be real cave ceilings,
            // so nothing is trusted without raycast confirmation - from the contact point, not the grid
            // center, so bank/wall height blocking is actually detected.
            if (hasNormal
                && Vector3D.Dot(normal, up) > -0.1
                && HasVoxelFreeLine(testPos, normal, rayLength))
            {
                dir = normal;
                return true;
            }

            if (HasVoxelFreeLine(center, up, rayLength))
            {
                dir = up;
                return true;
            }

            // Cold path (once per PushApartThreshold contact frames) - the tiny candidate array is acceptable
            Vector3D east = Vector3D.Cross(up, Vector3D.Forward);
            if (east.LengthSquared() < 0.001) east = Vector3D.Cross(up, Vector3D.Right);
            if (east.LengthSquared() < 0.001) return false;
            east = Vector3D.Normalize(east);
            Vector3D north = Vector3D.Normalize(Vector3D.Cross(up, east));

            Vector3D[] candidates = { east, -east, north, -north };
            foreach (var candidate in candidates)
            {
                if (HasVoxelFreeLine(center, candidate, rayLength))
                {
                    dir = candidate;
                    return true;
                }
            }

            // No sideways exit exists (deep burial, overhang) - fly up and phase through the terrain.
            // Upward escape is always legal; a stuck grid must never be left without a direction.
            dir = up;
            return true;
        }

        /// <summary>True if a voxel-layer raycast along dir finds no terrain within length meters.</summary>
        private static bool HasVoxelFreeLine(Vector3D from, Vector3D dir, double length)
        {
            return !MyPhysics.CastRay(from, from + dir * length, MyPhysics.CollisionLayers.VoxelCollisionLayer).HasValue;
        }

        private void TryPushApart(MyCubeGrid grid, MyEntity otherEntity)
        {
            var config = _plugin?.Config;
            if (config == null || grid == null || otherEntity == null || grid.MarkedForClose || grid.Closed || otherEntity.MarkedForClose || otherEntity.Closed) return;

            Vector3D gridPos = grid.PositionComp.GetPosition();
            Vector3D separationDir;

            if (otherEntity is MyCubeGrid otherGrid)
            {
                separationDir = gridPos - otherGrid.PositionComp.GetPosition();
                separationDir = separationDir.LengthSquared() < 0.01 ? Vector3D.Up : Vector3D.Normalize(separationDir);
            }
            else if (otherEntity is MyVoxelBase contactVoxel)
            {
                ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                int attempts = _pushApartAttempts.TryGetValue(grid.EntityId, out int a) ? a : 0;
                if (config.PushApartMaxAttempts > 0 && attempts >= config.PushApartMaxAttempts)
                {
                    // Escape-attempt cap: repeated failed pushes mean the direction is wrong (cliff face) - stop staircasing the grid skyward
                    if (ShouldLog(_lastPushApartGiveUpLogFrames, grid.EntityId, currentFrame, 1200))
                    {
                        Log.Warn(LogSource, $"[PUSH-APART] '{grid.DisplayName}' still voxel-embedded after {attempts} pushes; standing down pending contact loss or admin rescue.");
                    }
                    return;
                }

                // Direction lock: a grid that re-triggers within ~5s of the last push is still stuck -
                // reuse the previous escape direction and escalate the distance so repeated positional
                // teleports accumulate and dig submerged wheels out, instead of re-resolving from scratch
                double distance = config.PushApartDistance;
                if (_lastEscapes.TryGetValue(grid.EntityId, out var esc) && currentFrame <= esc.Frame + 300)
                {
                    separationDir = esc.Direction;
                    esc.Level = Math.Min(esc.Level + 1, 3);
                    esc.Frame = currentFrame;
                    distance = Math.Min(config.PushApartDistance * (esc.Level + 1), config.PushApartMaxNudgeDistance);
                    _lastEscapes[grid.EntityId] = esc;
                }
                else
                {
                    if (!TryResolveVoxelEscapeDirection(grid, config, contactVoxel, out separationDir))
                    {
                        if (ShouldLog(_lastPushApartGiveUpLogFrames, grid.EntityId, currentFrame, 1200))
                        {
                            Log.Warn(LogSource, $"[PUSH-APART] No escape direction found for '{grid.DisplayName}' (all candidates blocked by voxels); retrying on later contacts.");
                        }
                        return;
                    }
                    _lastEscapes[grid.EntityId] = new EscapeRecord { Direction = separationDir, Frame = currentFrame, Level = 0 };
                }

                _pushApartAttempts[grid.EntityId] = attempts + 1;
                EnqueuePush(grid, separationDir, true, distance);
                return;
            }
            else
            {
                separationDir = Vector3D.Up;
            }

            EnqueuePush(grid, separationDir, otherEntity is MyVoxelBase, config.PushApartDistance);
        }

        private void EnqueuePush(MyCubeGrid grid, Vector3D separationDir, bool voxelPush, double distance)
        {
            var config = _plugin?.Config;
            if (config == null || grid == null || grid.MarkedForClose || grid.Closed) return;

            _pushQueue.Enqueue(new PushApartAction
            {
                GridId = grid.EntityId,
                SeparationDir = separationDir,
                Distance = (float)distance,
                StartPos = grid.PositionComp.WorldVolume.Center,
                VoxelPush = voxelPush
            });

            _plugin?.DefenseStats?.IncrementGridsSeparated();

            if (config.EnableDebugLogging)
            {
                Log.Info(LogSource, $"[PUSH-APART] Queued push for '{grid.DisplayName}' {distance:F2}m along separation vector.");
            }
        }

        private bool AllowOrScale(long gridEntityId, ref float separatingVelocity, bool isMissile)
        {
            var config = _plugin?.Config;
            var stats = _plugin?.DefenseStats;
            if (config == null) return true;

            if (config.DeformationMultiplier <= 0.0f)
            {
                stats?.IncrementBlocked();
                return false;
            }

            if (config.DeformationMultiplier < 1.0f)
            {
                separatingVelocity *= config.DeformationMultiplier;
            }

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            _lastDeformationFrames[gridEntityId] = currentFrame;

            stats?.IncrementAllowed(isMissile: isMissile);
            return true;
        }

        private static bool ShouldLog(ConcurrentDictionary<long, ulong> dict, long key, ulong currentFrame, ulong intervalFrames = 60)
        {
            if (currentFrame == 0) return true;
            if (dict.TryGetValue(key, out ulong lastFrame))
            {
                if (currentFrame >= lastFrame && (currentFrame - lastFrame) < intervalFrames)
                {
                    return false;
                }
            }
            dict[key] = currentFrame;
            return true;
        }

        private static void TrimDictionary(ConcurrentDictionary<long, ulong> dict, ulong currentFrame, ulong maxAge, ConcurrentDictionary<long, int> secondaryDict = null)
        {
            foreach (var kvp in dict)
            {
                if (currentFrame > kvp.Value && (currentFrame - kvp.Value) > maxAge)
                {
                    if (dict.TryRemove(kvp.Key, out _) && secondaryDict != null)
                    {
                        secondaryDict.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }

        /// <summary>
        /// Cold-path periodic cache sweep. Evicts expired missile engagements and frame trackers.
        /// </summary>
        public void SweepCaches(ulong? frame = null)
        {
            ulong currentFrame = frame ?? MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (currentFrame == 0) return;

            if (currentFrame % 600 == 0)
            {
                TrimOldFrames(currentFrame);
                RunBurialProbe(currentFrame);
            }

            ProcessPushQueue();
        }

        private void ProcessPushQueue()
        {
            var config = _plugin?.Config;
            while (_pushQueue.TryDequeue(out var action))
            {
                try
                {
                    if (!MyEntities.TryGetEntityById(action.GridId, out var entity) || entity is not MyCubeGrid grid)
                    {
                        continue;
                    }

                    if (grid.MarkedForClose || grid.Closed) continue;

                    if (config?.EnablePushApartDebugDraw == true)
                    {
                        DrawPushDebugGps(action, grid.DisplayName);
                    }

                    // Apply the same translation to the entire mechanical group (wheels are separate
                    // physics bodies - moving only the main grid leaves wheels buried and clanging)
                    _groupMembersBuffer.Clear();
                    GridUtils.GetMechanicalGroupMembers(grid, _groupMembersBuffer);

                    foreach (var member in _groupMembersBuffer)
                    {
                        if (member == null || member.MarkedForClose || member.Closed) continue;

                        var matrix = member.WorldMatrix;
                        matrix.Translation += action.SeparationDir * action.Distance;
                        member.PositionComp.SetWorldMatrix(ref matrix);

                        if (member.Physics != null)
                        {
                            // Wake the body first - velocity writes on a deactivated rigid body are lost
                            member.Physics.RigidBody?.Activate();
                            member.Physics.LinearVelocity = (Vector3)action.SeparationDir * 0.8f;
                            member.Physics.AngularVelocity = Vector3.Zero;
                        }

                        _consecutiveContactFrames[member.EntityId] = 0;
                        _lastVoxelContactFrames.TryRemove(member.EntityId, out _);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, LogSource, "Error while applying queued push-apart action!");
                }
            }
        }

        /// <summary>
        /// Bright GPS marker pair at the push origin/end so admins can watch pushes live in the HUD.
        /// One pair per grid: the previous pair is deleted before re-adding, so a repeatedly-pushed
        /// grid holds exactly two markers instead of flooding the GPS list. Auto-expires after 10s.
        /// </summary>
        private static void DrawPushDebugGps(PushApartAction action, string gridName)
        {
            string startName = $"PD {gridName} START";
            string endName = $"PD {gridName} END (5m)";
            RemoveDebugGpsForAll(startName);
            RemoveDebugGpsForAll(endName);

            Color color = action.VoxelPush ? Color.Magenta : Color.Yellow;
            MyVisualScriptLogicProvider.AddGPSForAll(startName, $"push-apart debug: [{(action.VoxelPush ? "VOXEL" : "GRID")}] push origin", action.StartPos, color, 10);
            MyVisualScriptLogicProvider.AddGPSForAll(endName, $"push-apart debug: pushed {action.Distance:F1}m along vector", action.StartPos + action.SeparationDir * 5.0, color, 10);
        }

        private static void RemoveDebugGpsForAll(string name)
        {
            var players = Sandbox.Game.World.MySession.Static?.Players?.GetOnlinePlayers();
            if (players == null) return;

            foreach (var player in players)
            {
                var gps = Sandbox.Game.World.MySession.Static.Gpss.GetGpsByName(player.Identity.IdentityId, name);
                if (gps != null)
                {
                    Sandbox.Game.World.MySession.Static.Gpss.SendDeleteGpsRequest(player.Identity.IdentityId, gps.Hash);
                }
            }
        }


        /// <summary>
        /// Cold-path burial probe: grids stationary for 2+ sweeps (about 20s) confined by voxel
        /// material get an active rescue push - 3+ solid sides for silent burials, relaxed to 2
        /// sides when terrain contact was seen within ~60s (cliff wedges). Covers grids where
        /// Havok emits no contact callbacks, so contact-driven push-apart never fires.
        /// </summary>
        private void RunBurialProbe(ulong currentFrame)
        {
            var config = _plugin?.Config;
            if (config == null || !config.EnableBurialProbe || !config.EnablePushApart) return;

            var entities = MyEntities.GetEntities();
            try
            {
                foreach (var entity in entities)
                {
                    if (entity is not MyCubeGrid grid || grid.IsStatic || grid.MarkedForClose || grid.Closed || grid.Physics?.RigidBody == null) continue;

                    long id = grid.EntityId;
                    Vector3D pos = grid.PositionComp.GetPosition();

                    if (!_burialProbeStates.TryGetValue(id, out var state))
                    {
                        _burialProbeStates[id] = new BurialProbeState { LastPosition = pos, StationarySweeps = 0 };
                        continue;
                    }

                    if (Vector3D.DistanceSquared(pos, state.LastPosition) > 0.25)
                    {
                        state.LastPosition = pos;
                        state.StationarySweeps = 0;
                        continue;
                    }

                    state.LastPosition = pos;
                    state.StationarySweeps++;
                    if (state.StationarySweeps < 2) continue;

                    // Contact-driven push-apart owns grids that are actively grinding; probe is for silent burials
                    if (HadRecentVoxelContact(id)) continue;

                    // Cliff-wedged grids are confined on only 1-2 horizontal sides, so they get a relaxed
                    // 2-side test when terrain contact was seen recently; silent burials still need 3 sides
                    int minSolidSides = HadVoxelContactWithin(id, 1800) ? 2 : 3;
                    if (IsGridBuried(grid, config, minSolidSides))
                    {
                        Vector3D up = grid.Physics.Gravity.LengthSquared() > 0.1f ? -Vector3D.Normalize(grid.Physics.Gravity) : Vector3D.Up;
                        EnqueuePush(grid, up, voxelPush: true, config.PushApartDistance);
                        state.StationarySweeps = 0;

                        if (config.EnableDebugLogging)
                        {
                            Log.Info(LogSource, $"[BURIAL PROBE] Queued rescue push for buried grid '{grid.DisplayName}'.");
                        }
                    }
                }
            }
            finally
            {
                entities.Clear();
            }
        }

        /// <summary>
        /// Burial test: voxel layer hit within (bounding radius + probe margin) on 3+ of 4 horizontal
        /// sides means the grid is solidly confined by terrain. Air pockets bigger than the margin
        /// pass cleanly, so underground-but-not-touching grids are not nudged.
        /// </summary>
        private static bool IsGridBuried(MyCubeGrid grid, PhysicsOptimizerConfig config, int minSolidSides)
        {
            Vector3D center = grid.PositionComp.WorldVolume.Center;
            float radius = (float)grid.PositionComp.WorldVolume.Radius;
            float rayLength = radius + config.BurialProbeRadius;
            int solidSides = 0;

            for (int i = 0; i < 4; i++)
            {
                Vector3D dir = i == 0 ? Vector3D.Right : i == 1 ? Vector3D.Left : i == 2 ? Vector3D.Forward : Vector3D.Backward;
                if (MyPhysics.CastRay(center + dir * (rayLength + 2.0), center, MyPhysics.CollisionLayers.VoxelCollisionLayer).HasValue)
                {
                    solidSides++;
                }
            }

            return solidSides >= minSolidSides;
        }

        private void TrimOldFrames(ulong currentFrame)
        {
            try
            {
                foreach (var kvp in _activeMissiles)
                {
                    if (currentFrame > kvp.Value.ExpireFrame + 300)
                    {
                        _activeMissiles.TryRemove(kvp.Key, out _);
                    }
                }

                TrimDictionary(_lastContactFrameTracker, currentFrame, 600, _consecutiveContactFrames);
                TrimDictionary(_lastDeformationFrames, currentFrame, 600);
                TrimDictionary(_lastVoxelContactFrames, currentFrame, 1800);
                TrimDictionary(_lastPushApartGiveUpLogFrames, currentFrame, 1200);

                // No recent voxel contact means the last push freed the grid - refund its attempt budget
                // and clear the direction lock so the next wedge re-resolves from scratch
                foreach (var trackedId in _pushApartAttempts.Keys)
                {
                    if (!HadRecentVoxelContact(trackedId)) _pushApartAttempts.TryRemove(trackedId, out _);
                }
                foreach (var trackedId in _lastEscapes.Keys)
                {
                    if (!HadVoxelContactWithin(trackedId, 600)) _lastEscapes.TryRemove(trackedId, out _);
                }
                foreach (var trackedId in _contactStartPositions.Keys)
                {
                    if (!HadRecentVoxelContact(trackedId)) _contactStartPositions.TryRemove(trackedId, out _);
                }
                TrimDictionary(_lastRammingLogFrames, currentFrame, 600);
                TrimDictionary(_lastVoxelLogFrames, currentFrame, 600);
                TrimDictionary(_lastStationLogFrames, currentFrame, 600);
                TrimDictionary(_lastSubgridLogFrames, currentFrame, 600);
                TrimDictionary(_lastExtremeSpeedLogFrames, currentFrame, 600);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, LogSource, "Error while trimming expired frame tracking collections.");
            }
        }

        // =========================================================================
        // DISSOLVED PATCHES & ENGINE HOOKS
        // =========================================================================

        public static void RegisterPatches(PatchContext ctx)
        {
            // 1. MyGridPhysics: PerformDeformation & ContactPointCallback
            try
            {
                var targetMethod = typeof(MyGridPhysics).GetMethod("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (targetMethod != null)
                {
                    var prefixMethod = typeof(GridDefender).GetMethod(nameof(Prefix_PerformDeformation), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(targetMethod).Prefixes.Add(prefixMethod);
                    PatchConflictAudit.RegisterTarget(targetMethod);
                    Log.Info(LogSource, "Registered MyGridPhysics.PerformDeformation hook.");
                }

                var contactMethod = typeof(MyGridPhysics).GetMethod("RigidBody_ContactPointCallbackImpl", BindingFlags.Instance | BindingFlags.NonPublic);
                if (contactMethod != null)
                {
                    var contactPrefix = typeof(GridDefender).GetMethod(nameof(Prefix_RigidBody_ContactPointCallbackImpl), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(contactMethod).Prefixes.Add(contactPrefix);
                    PatchConflictAudit.RegisterTarget(contactMethod);
                    Log.Info(LogSource, "Registered MyGridPhysics.RigidBody_ContactPointCallbackImpl hook.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyGridPhysics methods!");
            }

            // 2. MyExplosion: Voxel Cutouts
            try
            {
                var explosionType = typeof(MyExplosions).Assembly.GetType("Sandbox.Game.MyExplosion");
                if (explosionType != null)
                {
                    var applyVoxelMethod = explosionType.GetMethod("ApplyExplosionOnVoxel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (applyVoxelMethod != null)
                    {
                        var prefixApply = typeof(GridDefender).GetMethod(nameof(PrefixApplyExplosionOnVoxel), BindingFlags.Static | BindingFlags.NonPublic);
                        ctx.GetPattern(applyVoxelMethod).Prefixes.Add(prefixApply);
                        PatchConflictAudit.RegisterTarget(applyVoxelMethod);
                    }

                    var cutOutMethod = explosionType.GetMethod("CutOutVoxelMap", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cutOutMethod != null)
                    {
                        var prefixCutOut = typeof(GridDefender).GetMethod(nameof(PrefixCutOutVoxelMap), BindingFlags.Static | BindingFlags.NonPublic);
                        ctx.GetPattern(cutOutMethod).Prefixes.Add(prefixCutOut);
                        PatchConflictAudit.RegisterTarget(cutOutMethod);
                    }
                    Log.Info(LogSource, "Registered MyExplosion voxel cutout hooks.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyExplosion voxel cutouts!");
            }

            // 3. Layered Armor Occlusion via MyDamageSystem
            try
            {
                var initMethod = typeof(MyDamageSystem).GetMethod("LoadData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? typeof(MyDamageSystem).GetMethod("Init", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (initMethod != null)
                {
                    var initPostfix = typeof(GridDefender).GetMethod(nameof(DamageSystemInitPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(initMethod).Suffixes.Add(initPostfix);
                    PatchConflictAudit.RegisterTarget(initMethod);
                    Log.Info(LogSource, "Registered MyDamageSystem hook for Layered Armor Occlusion.");
                }
                else if (MyDamageSystem.Static != null)
                {
                    MyDamageSystem.Static.RegisterBeforeDamageHandler(100, OnBeforeDamageApplied);
                    Log.Info(LogSource, "Registered Layered Armor Occlusion damage handler directly.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyDamageSystem for Layered Armor Occlusion!");
            }
        }

        // --- MyGridPhysics Patches ---

        public static bool Prefix_PerformDeformation(MyGridPhysics __instance, MyEntity otherEntity, ref float separatingVelocity)
        {
            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin?.GridDefender == null) return true;

            return plugin.GridDefender.ShouldAllowDeformation(__instance, otherEntity, ref separatingVelocity);
        }

        public static bool Prefix_RigidBody_ContactPointCallbackImpl(MyGridPhysics __instance, ref HkContactPointEvent value)
        {
            if (__instance.Entity is MyCubeGrid gridLocal)
            {
                UpdateCollisionContext(gridLocal.EntityId, value.ContactPoint.Position);
            }

            var config = PhysicsOptimizerPlugin.Instance?.Config;
            bool arbitratorEnabled = config != null && config.EnableVoxelNormalArbitrator;
            if (config == null)
                return true;

            if (__instance.Entity is not MyCubeGrid grid || grid.MarkedForClose || grid.Closed)
                return true;

            var rb = value.GetPhysicsBody(0);
            var otherRb = value.GetPhysicsBody(1);
            if (rb == null || otherRb == null) return true;

            bool isVoxel = false;
            var otherEnt = otherRb.Entity;
            if (otherEnt is MyVoxelBase)
            {
                isVoxel = true;
            }
            else
            {
                var selfEnt = rb.Entity;
                if (selfEnt is MyVoxelBase)
                {
                    isVoxel = true;
                }
            }

            if (isVoxel)
            {
                // Wheel subgrids: stamp both wheel and base grid so burial probe / sleep guards can
                // still rescue wheel-only wedges, while anti-clang/push-apart skip driving false positives.
                ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                if (currentFrame > 0 && IsWheelSubgrid(grid, out long baseGridId))
                {
                    _lastVoxelContactFrames[grid.EntityId] = currentFrame;
                    if (baseGridId > 0L) _lastVoxelContactFrames[baseGridId] = currentFrame;
                }

                // Push-apart escape direction source: must record before the arbitrator toggle check so push-apart works with the arbitrator disabled
                _lastVoxelContactNormals[grid.EntityId] = value.ContactPoint.Normal;

                if (!arbitratorEnabled) return true;

                var gravity = __instance.Gravity;
                if (gravity.LengthSquared() < 0.01f) return true;

                var upVector = -Vector3.Normalize((Vector3)gravity);
                float upDot = Vector3.Dot(value.ContactPoint.Normal, upVector);

                if (upDot < 0f)
                {
                    var contactPos = value.ContactPoint.Position;

                    // Same two-tier rule as push-apart: in pristine voxel regions a gravity-downward normal can
                    // only be a Keen compression artifact pointing into the planet core, so invert it directly.
                    // Only carved regions (real cave ceilings are possible) pay for the confirmation raycast.
                    var voxelEnt = otherEnt as MyVoxelBase ?? rb.Entity as MyVoxelBase;
                    bool hitAir = voxelEnt == null || !IsVoxelRegionModified(voxelEnt, contactPos);
                    if (!hitAir)
                    {
                        var rayStart = contactPos;
                        var rayEnd = rayStart + upVector * 1.5f;

                        _voxelHitsCache ??= [];
                        _voxelHitsCache.Clear();
                        MyPhysics.CastRay(rayStart, rayEnd, _voxelHitsCache, MyPhysics.CollisionLayers.VoxelCollisionLayer);
                        for (int i = 0; i < _voxelHitsCache.Count; i++)
                        {
                            var hitEnt = _voxelHitsCache[i].HkHitInfo.GetHitEntity();
                            if (hitEnt is MyVoxelBase)
                            {
                                hitAir = false;
                                break;
                            }
                        }
                        _voxelHitsCache.Clear();
                    }

                    if (hitAir)
                    {
                        var cp = value.ContactPoint;
                        cp.Normal = -cp.Normal;

                        PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelNormalsInverted();

                        if (config.EnableDebugLogging && config.LogVoxelNormals)
                        {
                            Log.Info(LogSource, $"[Voxel Arbitrator] Inverted downward normal for grid '{grid.DisplayName}' at {contactPos}.");
                        }
                    }
                }
            }

            return true;
        }

        // --- MyExplosion Patches ---

        private static bool PrefixApplyExplosionOnVoxel()
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config != null && config.Enabled && config.SuppressAllVoxelExplosionDamage)
            {
                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelCutoutsPrevented();
                return false;
            }
            return true;
        }

        private static bool PrefixCutOutVoxelMap()
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config != null && config.Enabled && config.SuppressAllVoxelExplosionDamage)
            {
                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelCutoutsPrevented();
                return false;
            }
            return true;
        }

        // --- Layered Armor Occlusion Patches ---

        public static void DamageSystemInitPostfix(MyDamageSystem __instance)
        {
            if (__instance != null)
            {
                __instance.RegisterBeforeDamageHandler(100, OnBeforeDamageApplied);
                Log.Info(LogSource, "Registered Layered Armor Occlusion damage handler via Postfix.");
            }
        }

        public static void OnBeforeDamageApplied(object target, ref MyDamageInformation info)
        {
            if (info.Amount <= 0f || info.Type != MyDamageType.Deformation) return;

            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.EnableLayeredArmorOcclusion) return;

            if (target is MySlimBlock slimBlock && slimBlock.CubeGrid != null)
            {
                var grid = slimBlock.CubeGrid;

                if (!_lastImpactPositions.TryGetValue(grid.EntityId, out var globalHitPos))
                {
                    return; // Can't determine direction without a collision point
                }

                Vector3D localHitPos = Vector3D.Transform(globalHitPos, grid.PositionComp.WorldMatrixNormalizedInv);
                Vector3D blockCenterLocal = slimBlock.Position * grid.GridSize;
                Vector3D D = localHitPos - blockCenterLocal;

                if (D.LengthSquared() < 0.001f) return;

                Vector3I step = Vector3I.Round(Vector3D.Normalize(D));
                Vector3I neighborCoord = slimBlock.Position + step;

                var occluder = grid.GetCubeBlock(neighborCoord);

                if (occluder == null || ReferenceEquals(occluder, slimBlock)) return;

                // Enforced mode: ONLY pure armor blocks occlude, and only while less than 50% deformed.
                // Functional blocks have no deformation skeleton (DeformationRatio always 0) and never qualify.
                bool isValidOccluder = !config.ArmorOnlyOcclusion ||
                    (occluder.FatBlock == null && occluder.DeformationRatio < 0.5f);

                // Physics-less fat blocks (interior lights, decorative blocks) are not structural and never occlude
                if (occluder.FatBlock != null && occluder.FatBlock.Physics == null)
                {
                    isValidOccluder = false;
                }

                if (isValidOccluder && !occluder.IsDestroyed)
                {
                    info.Amount = 0f; // Shielded!
                    PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementArmorHitsOccluded();
                }
            }
        }
    }
}
