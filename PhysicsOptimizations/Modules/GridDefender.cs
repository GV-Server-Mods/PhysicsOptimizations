using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Havok;
using Sandbox;
using Sandbox.Engine.Multiplayer;
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
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage.Collections;

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
            public bool ConvertToStatic;
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

        // Push-apart context tracking: latest Havok contact penetration depth per grid (distance < 0 means penetrating)
        private static readonly ConcurrentDictionary<long, float> _lastVoxelPenetrations = new();

        // Per-voxel-map coarse modification map: Key: voxel EntityId, Value: set of modified 32m region buckets.
        // Tracked from MyVoxelBase.RangeChanged (fires per carve operation), so a drilled planet only records
        // the carved regions - pristine terrain everywhere else keeps the raycast-free push direction fast path.
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _modifiedVoxelRegions = new();
        private static readonly ConcurrentDictionary<long, MyVoxelBase.StorageChanged> _voxelRangeHandlers = new();
        private const int VoxelRegionBucketShift = 5; // 32m buckets (voxel storage cells are 1m)

        private static bool _damageHandlerRegistered;

        [ThreadStatic]
        private static List<MyPhysics.HitInfo> _voxelHitsCache;

        [ThreadStatic]
        private static List<MyCubeGrid> _wheelResolveBuffer;

        [ThreadStatic]
        private static List<MyCubeGrid> _pushMechanicalGroupBuffer;

        public static void UpdateCollisionContext(long gridId, Vector3D position)
        {
            _lastImpactPositions[gridId] = position;
        }

        public static void RemoveCollisionContext(long gridId)
        {
            _lastImpactPositions.TryRemove(gridId, out _);
            _lastVoxelContactNormals.TryRemove(gridId, out _);
            _lastVoxelPenetrations.TryRemove(gridId, out _);
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
            ConcurrentDictionary<long, byte> buckets = _modifiedVoxelRegions.GetOrAdd(voxel.EntityId, _ => new ConcurrentDictionary<long, byte>());
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
            if (!_modifiedVoxelRegions.TryGetValue(voxel.EntityId, out ConcurrentDictionary<long, byte> buckets) || buckets.IsEmpty) return false;
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
            else if (entity is MyVoxelBase voxel)
            {
                if (_voxelRangeHandlers.TryRemove(voxel.EntityId, out var handler))
                {
                    voxel.RangeChanged -= handler;
                }
                _modifiedVoxelRegions.TryRemove(voxel.EntityId, out _);
            }
        }

        public void Dispose()
        {
            RestoreVoxelFakes();
            UnregisterDamageHandler();
            ClearCollections();
            _plugin = null;
        }

        private static void UnregisterDamageHandler()
        {
            if (!_damageHandlerRegistered) return;
            try
            {
                if (MyDamageSystem.Static != null)
                {
                    FieldInfo beforeField = typeof(MyDamageSystem).GetField("m_beforeDamageHandlers", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (beforeField?.GetValue(MyDamageSystem.Static) is List<Tuple<int, BeforeDamageApplied>> handlers)
                    {
                        handlers.RemoveAll(t => t.Item2 == OnBeforeDamageApplied);
                        Log.Info(LogSource, "Unregistered Layered Armor Occlusion damage handler on Dispose.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, LogSource, "Failed to unregister damage handler on Dispose.");
            }
            finally
            {
                _damageHandlerRegistered = false;
            }
        }

        private void ClearCollections()
        {
            while (_pushQueue.TryDequeue(out _)) { }
            foreach (var id in _activeMissiles.Keys)
            {
                if (MyEntities.TryGetEntityById(id, out MyEntity ent) && ent is MyCubeGrid g)
                {
                    g.OnClose -= OnTrackedGridClosed;
                    g.OnGridSplit -= OnTrackedGridSplit;
                }
            }
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
            _lastVoxelPenetrations.Clear();
            _pushApartAttempts.Clear();
            _lastPushApartGiveUpLogFrames.Clear();
            _wheelBaseGridIds.Clear();
            _lastEscapes.Clear();
            _contactStartPositions.Clear();
            _lastVoxelContactFrames.Clear();
            _burialProbeStates.Clear();

            foreach (var kvp in _voxelRangeHandlers)
            {
                if (MyEntities.TryGetEntityById(kvp.Key, out MyEntity ent) && ent is MyVoxelBase v)
                {
                    v.RangeChanged -= kvp.Value;
                }
            }
            _voxelRangeHandlers.Clear();
            _modifiedVoxelRegions.Clear();
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
            PhysicsOptimizerConfig config = _plugin.Config;

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
            PhysicsOptimizerConfig config = _plugin.Config;
            DefenseStatistics stats = _plugin.DefenseStats;

            if (physics?.Entity is not MyCubeGrid grid || grid.MarkedForClose || grid.Closed) return true;
            if (otherEntity == null || otherEntity.MarkedForClose || otherEntity.Closed) return true;

            stats?.IncrementEvaluated();

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;

            // 1. Subgrid / Mechanicals (Pistons, Rotors, Hinges, Connectors) Protection
            bool isConnectedSubgrid = false;
            if (otherEntity is MyCubeGrid otherGrid)
            {
                isConnectedSubgrid = GridUtils.AreInSameMechanicalGroup(grid, otherGrid) || GridUtils.AreInSameLogicalGroup(grid, otherGrid);
                if (config.ProtectSubgrids && isConnectedSubgrid)
                {
                    if (config.EnableDebugLogging && ShouldLog(_lastSubgridLogFrames, grid.EntityId ^ otherGrid.EntityId, currentFrame, 120))
                    {
                        Log.Info(LogSource, $"[SUBGRID] Protected: Blocked collision between '{grid.DisplayName}' and '{otherGrid.DisplayName}'.");
                    }
                    float subgridImpactSpeed = Math.Max(Math.Abs(separatingVelocity), Math.Max(grid.GetSpeed(), otherGrid.GetSpeed()));
                    ApplyAntiClang(grid, physics, otherEntity, subgridImpactSpeed, isConnectedSubgrid: true);
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

            // 3. Speed calculations (hoisted after subgrid/debris early exits; pre-squared to minimize sqrt calls)
            float gridSpeedSq = grid.GetSpeedSquared();
            float otherSpeedSq = (otherEntity as MyCubeGrid)?.GetSpeedSquared() ?? 0f;
            float absSepVelocity = Math.Abs(separatingVelocity);
            float impactSpeedSq = Math.Max(absSepVelocity * absSepVelocity, Math.Max(gridSpeedSq, otherSpeedSq));
            float impactSpeed = (float)Math.Sqrt(impactSpeedSq);

            // 4. Missile (PMW) Evaluation & Engagement Tracking (Targets grids only, never voxels)
            if (otherEntity is MyCubeGrid targetGrid)
            {
                bool gridInMissile = _activeMissiles.TryGetValue(grid.EntityId, out MissileEngagement gridEngage) && currentFrame <= gridEngage.ExpireFrame;
                bool otherInMissile = _activeMissiles.TryGetValue(targetGrid.EntityId, out MissileEngagement otherEngage) && currentFrame <= otherEngage.ExpireFrame;

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
                        engagement.ExpireFrame = Math.Max(engagement.ExpireFrame, currentFrame + 30);
                    }
                    else if (otherInMissile)
                    {
                        engagement = otherEngage;
                        engagement.ExpireFrame = Math.Max(engagement.ExpireFrame, currentFrame + 30);
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
                            if (gridQualifies && otherQualifies)
                            {
                                Log.Info(LogSource, $"[MISSILE] Head-on missile collision ALLOWED: '{grid.DisplayName}' and '{targetGrid.DisplayName}' collided at {impactSpeed:F1} m/s.");
                            }
                            else
                            {
                                Log.Info(LogSource, $"[MISSILE] Impact ALLOWED: '{engagement.MissileName}' struck '{engagement.TargetName}' at {engagement.InitialSpeed:F1} m/s.");
                            }
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
                ApplyAntiClang(grid, physics, otherEntity, impactSpeed, isConnectedSubgrid);
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
                ApplyAntiClang(grid, physics, otherEntity, impactSpeed, isConnectedSubgrid);
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
                        ApplyAntiClang(otherCubeGrid, otherCubeGrid.Physics as MyGridPhysics, grid, impactSpeed, isConnectedSubgrid);
                    }
                }
                else
                {
                    ApplyImpactDamping(physics, false);
                    ApplyAntiClang(grid, physics, otherEntity, impactSpeed, isConnectedSubgrid);
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
                        Log.Info(LogSource, $"[VOXEL] Terrain Crash Blocked: '{grid.DisplayName}' ({grid.BlocksCount} blocks) hit voxels (damage suppressed).");
                    }
                    ApplyImpactDamping(physics, grid.IsStatic);
                    ApplyAntiClang(grid, physics, otherEntity, impactSpeed, false);
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
                ApplyAntiClang(grid, physics, otherEntity, impactSpeed, isConnectedSubgrid);
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
            if (_activeMissiles.TryRemove(id, out _) && MyEntities.TryGetEntityById(id, out MyEntity ent) && ent is MyCubeGrid grid)
            {
                grid.OnClose -= OnTrackedGridClosed;
                grid.OnGridSplit -= OnTrackedGridSplit;
            }
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
            _lastVoxelPenetrations.TryRemove(id, out _);
            _pushApartAttempts.TryRemove(id, out _);
            _lastPushApartGiveUpLogFrames.TryRemove(id, out _);
            _wheelBaseGridIds.TryRemove(id, out _);
            _lastEscapes.TryRemove(id, out _);
            _contactStartPositions.TryRemove(id, out _);
        }

        private void OnTrackedGridClosed(IMyEntity entity)
        {
            if (entity == null) return;
            if (entity is MyCubeGrid grid)
            {
                grid.OnClose -= OnTrackedGridClosed;
                grid.OnGridSplit -= OnTrackedGridSplit;
            }
            EvictGrid(entity.EntityId);
        }

        private void OnTrackedGridSplit(MyCubeGrid originalGrid, MyCubeGrid newGrid)
        {
            if (originalGrid == null || newGrid == null || newGrid.MarkedForClose || newGrid.Closed) return;

            if (_activeMissiles.TryGetValue(originalGrid.EntityId, out MissileEngagement engagement))
            {
                RegisterActiveMissile(newGrid, engagement);
            }
        }

        private void ApplyImpactDamping(MyGridPhysics physics, bool isStatic)
        {
            PhysicsOptimizerConfig config = _plugin?.Config;
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
            foreach (MyCubeBlock fat in grid.GetFatBlocks())
            {
                if (fat is MyMotorRotor rotor && rotor.Stator is MyMotorSuspension)
                {
                    MyMechanicalConnectionBlockBase stator = rotor.Stator;
                    if (stator?.CubeGrid != null && !stator.CubeGrid.MarkedForClose && !stator.CubeGrid.Closed)
                    {
                        return stator.CubeGrid.EntityId;
                    }
                }
            }

            // Fallback: mechanical group scan for the base grid (wheels should always have a stator,
            // but if the stator reference is not yet wired, pick the first non-rotor member).
            _wheelResolveBuffer ??= new List<MyCubeGrid>();
            _wheelResolveBuffer.Clear();
            try
            {
                GridUtils.GetMechanicalGroupMembers(grid, _wheelResolveBuffer);
                foreach (MyCubeGrid member in _wheelResolveBuffer)
                {
                    if (member == null || member.MarkedForClose || member.Closed || member.EntityId == grid.EntityId) continue;
                    foreach (MyCubeBlock fat in member.GetFatBlocks())
                    {
                        if (fat != null && !(fat is MyMotorRotor)) return member.EntityId;
                    }
                }
                return -1L;
            }
            finally
            {
                _wheelResolveBuffer.Clear();
            }
        }

        private void ApplyAntiClang(MyCubeGrid grid, MyGridPhysics physics, MyEntity otherEntity, float impactSpeed, bool isConnectedSubgrid = false)
        {
            PhysicsOptimizerConfig config = _plugin?.Config;
            if (config == null || grid == null || grid.MarkedForClose || grid.Closed) return;

            bool isWheelVoxel = otherEntity is MyVoxelBase && IsWheelSubgrid(grid, out _);

            // Push-apart contact tracking runs even with Anti-Clang disabled - Phase 2 is gated on EnablePushApart only
            int contactCount = UpdateContactContext(grid, otherEntity, config, impactSpeed, isWheelVoxel, isConnectedSubgrid);

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

        private int UpdateContactContext(MyCubeGrid grid, MyEntity otherEntity, PhysicsOptimizerConfig config, float impactSpeed, bool isWheelVoxel, bool isConnectedSubgrid)
        {
            long gridEntityId = grid.EntityId;
            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (currentFrame == 0) return 0;

            int contactCount;
            if (_lastContactFrameTracker.TryGetValue(gridEntityId, out ulong lastFrame))
            {
                if (currentFrame == lastFrame)
                {
                    contactCount = _consecutiveContactFrames.TryGetValue(gridEntityId, out int c) ? c : 1;
                }
                else if (currentFrame == lastFrame + 1)
                {
                    contactCount = _consecutiveContactFrames.AddOrUpdate(gridEntityId, 1, (k, v) => v + 1);
                    _lastContactFrameTracker[gridEntityId] = currentFrame;
                }
                else
                {
                    contactCount = 1;
                    _consecutiveContactFrames[gridEntityId] = 1;
                    _lastContactFrameTracker[gridEntityId] = currentFrame;
                    _contactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
                }
            }
            else
            {
                contactCount = 1;
                _consecutiveContactFrames[gridEntityId] = 1;
                _lastContactFrameTracker[gridEntityId] = currentFrame;
                _contactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
            }

            // RigidBodySleep guard feed: sleeping bodies emit no contact callbacks, so a voxel-contact
            // timestamp is the only signal keeping a terrain-grinding grid awake long enough to be rescued
            if (otherEntity is MyVoxelBase)
            {
                _lastVoxelContactFrames[gridEntityId] = currentFrame;
            }

            // Phase 2: Active Push-Apart (Excludes mechanically/logically connected subgrids)
            bool areConnectedSubgrids = isConnectedSubgrid;

            // Impact-speed & Clang vibration gate: resting grids (contacts at ~0 m/s and 0 rad/s)
            // never arm pushes; only energetic contacts from driving, docking bumps, hard hits,
            // or active rotational Clang shuddering (>= 0.5 rad/s) qualify
            float angularSpeed = grid.Physics?.AngularVelocity.Length() ?? 0f;
            bool isClangVibrating = angularSpeed >= 0.5f;
            bool speedGateOpen = config.PushApartMinImpactSpeed <= 0f || impactSpeed >= config.PushApartMinImpactSpeed;
            bool impactGateOpen = speedGateOpen || isClangVibrating;

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

        public void RecordVoxelContactFrame(MyCubeGrid grid, MyEntity otherEntity, PhysicsOptimizerConfig config, float distance, Vector3 gridForceDir)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed || grid.IsStatic || otherEntity == null) return;

            long gridEntityId = grid.EntityId;
            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (currentFrame == 0) return;

            int contactCount;
            if (_lastContactFrameTracker.TryGetValue(gridEntityId, out ulong lastFrame))
            {
                if (currentFrame == lastFrame)
                {
                    return;
                }
                else if (currentFrame == lastFrame + 1)
                {
                    contactCount = _consecutiveContactFrames.AddOrUpdate(gridEntityId, 1, (k, v) => v + 1);
                    _lastContactFrameTracker[gridEntityId] = currentFrame;
                }
                else
                {
                    contactCount = 1;
                    _consecutiveContactFrames[gridEntityId] = 1;
                    _lastContactFrameTracker[gridEntityId] = currentFrame;
                    _contactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
                }
            }
            else
            {
                contactCount = 1;
                _consecutiveContactFrames[gridEntityId] = 1;
                _lastContactFrameTracker[gridEntityId] = currentFrame;
                _contactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
            }

            bool isWheelVoxel = otherEntity is MyVoxelBase && IsWheelSubgrid(grid, out _);
            if (!isWheelVoxel)
            {
                _lastVoxelPenetrations[gridEntityId] = distance;
            }
            bool pushApartAllowed = !isWheelVoxel || !config.ExcludeWheelSubgridsFromPushApart;

            if (pushApartAllowed && config.EnablePushApart && contactCount >= config.PushApartThreshold)
            {
                bool driftGateOpen = config.PushApartMaxDrift <= 0f;
                if (!driftGateOpen && _contactStartPositions.TryGetValue(gridEntityId, out Vector3D startPos))
                {
                    Vector3D drift = grid.PositionComp.WorldVolume.Center - startPos;
                    driftGateOpen = drift.LengthSquared() <= (double)config.PushApartMaxDrift * config.PushApartMaxDrift;
                }

                if (driftGateOpen)
                {
                    Vector3D gridCenter = grid.PositionComp.WorldVolume.Center;
                    MyPlanet planet = otherEntity as MyPlanet ?? MyGamePruningStructure.GetClosestPlanet(gridCenter);
                    bool isUnderSurface = false;
                    if (planet != null)
                    {
                        Vector3D core = planet.PositionComp.WorldVolume.Center;
                        Vector3D surfacePt = planet.GetClosestSurfacePointGlobal(ref gridCenter);
                        isUnderSurface = (gridCenter - core).LengthSquared() < (surfacePt - core).LengthSquared();
                    }

                    // A grid is physically embedded if Havok reports contact penetration exceeding the configured threshold
                    // (default 0.20m), or its center of mass is submerged below the planet heightmap surface.
                    // Embedded or submerged grids bypass speed/vibration gates entirely and push immediately.
                    bool isPhysicallyEmbedded = distance < -config.PushApartEmbeddedDepth;
                    bool isEmbedded = isPhysicallyEmbedded || isUnderSurface;

                    float speed = grid.Physics?.LinearVelocity.Length() ?? 0f;
                    float angularSpeed = grid.Physics?.AngularVelocity.Length() ?? 0f;

                    // Energetic impact or active solver torque vibration (Clang shuddering):
                    // Resting grids have near-zero angular velocity (< 0.05 rad/s) and speed below MinImpactSpeed.
                    // Clang loops against voxels violently twist with angular velocity spikes (>= 0.5 rad/s).
                    bool speedImpact = config.PushApartMinImpactSpeed > 0f && speed >= config.PushApartMinImpactSpeed;
                    bool isClangVibrating = angularSpeed >= 0.5f;
                    bool isEnergetic = speedImpact || isClangVibrating;

                    // Non-wheel chassis body is wedged if physically embedded/submerged, or experiencing sustained energetic/clang vibration
                    bool isChassisWedged = !isWheelVoxel && (isEmbedded || isEnergetic) && driftGateOpen && contactCount >= config.PushApartThreshold;
                    bool impactGateOpen = isEmbedded || isChassisWedged || isEnergetic;

                    if (impactGateOpen)
                    {
                        TryPushApart(grid, otherEntity);
                        _consecutiveContactFrames[gridEntityId] = 0;
                    }
                }
            }
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
        /// Resolves the push-apart escape direction for a grid embedded in or colliding with voxels.
        /// Uses the raw collision impact normal parallel to the surface, aligned away from the voxel face into open air
        /// when above ground, or toward/above the heightmap surface when underground.
        /// </summary>
        private bool TryResolveVoxelEscapeDirection(MyCubeGrid grid, PhysicsOptimizerConfig config, MyVoxelBase contactVoxel, out Vector3D dir)
        {
            dir = Vector3D.Zero;
            Vector3D center = grid.PositionComp.WorldVolume.Center;
            Vector3D up = grid.Physics != null && grid.Physics.Gravity.LengthSquared() > 0.1f
                ? -Vector3D.Normalize(grid.Physics.Gravity)
                : Vector3D.Up;

            MyPlanet planet = contactVoxel as MyPlanet ?? MyGamePruningStructure.GetClosestPlanet(center);
            if (planet != null)
            {
                Vector3D planetCore = planet.PositionComp.WorldVolume.Center;
                if (grid.Physics == null || grid.Physics.Gravity.LengthSquared() <= 0.1f)
                {
                    Vector3D radial = center - planetCore;
                    if (radial.LengthSquared() > 0.001) up = Vector3D.Normalize(radial);
                }

                // Query planet heightmap at the grid center to check whether the center is under or over the surface
                Vector3D surfacePoint = planet.GetClosestSurfacePointGlobal(ref center);
                double centerDist = (center - planetCore).Length();
                double surfaceDist = (surfacePoint - planetCore).Length();
                bool isUnderSurface = centerDist < surfaceDist;

                Vector3D recordedNormal = Vector3D.Zero;
                if (!_lastVoxelContactNormals.TryGetValue(grid.EntityId, out recordedNormal) || recordedNormal.LengthSquared() <= 0.001)
                {
                    _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
                    _pushMechanicalGroupBuffer.Clear();
                    try
                    {
                        GridUtils.GetMechanicalGroupMembers(grid, _pushMechanicalGroupBuffer);
                        foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                        {
                            if (member != null && _lastVoxelContactNormals.TryGetValue(member.EntityId, out Vector3D memberNormal) && memberNormal.LengthSquared() > 0.001)
                            {
                                recordedNormal = memberNormal;
                                break;
                            }
                        }
                    }
                    finally
                    {
                        _pushMechanicalGroupBuffer.Clear();
                    }
                }

                if (recordedNormal.LengthSquared() > 0.001)
                {
                    dir = Vector3D.Normalize(recordedNormal);

                    // Use impact normal as reference and rectify it so it always points OUT of the planet
                    if (Vector3D.Dot(dir, up) < 0.0)
                    {
                        dir = -dir;
                    }

                    if (config.LogPushApartDiagnostics || config.EnablePushApartDebugDraw)
                    {
                        double dotUp = Vector3D.Dot(dir, up);
                        Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                            "[PUSH-APART DIAG] Grid '{0}' ({1}) | Center: {2:F1} | Surface: {3:F1} | AltDiff: {4:F2}m | UnderSurface: {5} | RawNormal: {6:F3} | Dot(norm,up): {7:F3} | ResolvedDir: {8:F3}",
                            grid.DisplayName, grid.EntityId, center, surfacePoint, centerDist - surfaceDist, isUnderSurface, recordedNormal, dotUp, dir));
                    }
                    return true;
                }

                // Fallback when no impact normal was recorded: escape radially away from planet (gravity up)
                dir = up;
                if (config.LogPushApartDiagnostics || config.EnablePushApartDebugDraw)
                {
                    Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                        "[PUSH-APART DIAG] Grid '{0}' ({1}) [FALLBACK-UP] | Center: {2:F1} | Surface: {3:F1} | AltDiff: {4:F2}m | UnderSurface: {5} | ResolvedDir: {6:F3}",
                        grid.DisplayName, grid.EntityId, center, surfacePoint, centerDist - surfaceDist, isUnderSurface, dir));
                }
                return true;
            }

            // Non-planet voxels (asteroids): use contact normal pointing toward open air or local up
            Vector3D astNormal = Vector3D.Zero;
            if (!_lastVoxelContactNormals.TryGetValue(grid.EntityId, out astNormal) || astNormal.LengthSquared() <= 0.001)
            {
                _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
                _pushMechanicalGroupBuffer.Clear();
                try
                {
                    GridUtils.GetMechanicalGroupMembers(grid, _pushMechanicalGroupBuffer);
                    foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                    {
                        if (member != null && _lastVoxelContactNormals.TryGetValue(member.EntityId, out Vector3D memberNormal) && memberNormal.LengthSquared() > 0.001)
                        {
                            astNormal = memberNormal;
                            break;
                        }
                    }
                }
                finally
                {
                    _pushMechanicalGroupBuffer.Clear();
                }
            }

            if (astNormal.LengthSquared() > 0.001)
            {
                dir = Vector3D.Normalize(astNormal);
                return true;
            }

            dir = up;
            return true;
        }

        private void TryPushApart(MyCubeGrid grid, MyEntity otherEntity)
        {
            PhysicsOptimizerConfig config = _plugin?.Config;
            if (config == null || grid == null || otherEntity == null || grid.MarkedForClose || grid.Closed || otherEntity.MarkedForClose || otherEntity.Closed) return;

            // Subgrids must delegate to the construct's main grid so the whole construct escapes together along one unified vector
            MyCubeGrid topGrid = GridUtils.GetMainGrid(grid) ?? grid;
            if (topGrid != grid)
            {
                if (_lastVoxelContactNormals.TryGetValue(grid.EntityId, out Vector3D subNormal) && subNormal.LengthSquared() > 0.001)
                {
                    if (!_lastVoxelContactNormals.TryGetValue(topGrid.EntityId, out Vector3D topNormal) || topNormal.LengthSquared() <= 0.001)
                    {
                        _lastVoxelContactNormals[topGrid.EntityId] = subNormal;
                    }
                }
                if (_lastVoxelPenetrations.TryGetValue(grid.EntityId, out float subPen))
                {
                    _lastVoxelPenetrations[topGrid.EntityId] = subPen;
                }
                TryPushApart(topGrid, otherEntity);
                return;
            }

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            // Cooldown: do not push the same construct more frequently than every 30 frames (~0.5s) to allow Havok to settle
            if (_lastEscapes.TryGetValue(grid.EntityId, out EscapeRecord recentEsc) && currentFrame < recentEsc.Frame + 30)
            {
                return;
            }

            Vector3D separationDir;

            if (otherEntity is MyCubeGrid otherGrid)
            {
                MyCubeGrid otherTopGrid = GridUtils.GetMainGrid(otherGrid) ?? otherGrid;
                // If otherGrid belongs to the same mechanical construct, skip (anti-clang handles subgrids)
                if (otherTopGrid == grid) return;

                // Push along vector separating bounding centers; if co-located, fallback to recorded contact normal or up
                Vector3D centerDiff = grid.PositionComp.WorldVolume.Center - otherTopGrid.PositionComp.WorldVolume.Center;
                if (centerDiff.LengthSquared() > 0.001)
                {
                    separationDir = Vector3D.Normalize(centerDiff);
                }
                else if (_lastVoxelContactNormals.TryGetValue(grid.EntityId, out Vector3D normal) && normal.LengthSquared() > 0.001)
                {
                    separationDir = Vector3D.Normalize(normal);
                }
                else
                {
                    separationDir = Vector3D.Up;
                }

                // Prevent pushing lower grids downward into terrain:
                // If this grid is the lower body (separationDir points downward against gravity) and voxels are nearby,
                // deflect the push horizontally so the bottom grid slides sideways instead of getting shoved underground.
                if (grid.Physics != null && grid.Physics.Gravity.LengthSquared() > 0.1f)
                {
                    Vector3D up = -Vector3D.Normalize(grid.Physics.Gravity);
                    double downDot = Vector3D.Dot(separationDir, up);
                    if (downDot < -0.1)
                    {
                        // Project separation vector onto horizontal plane (remove downward component)
                        Vector3D horizontal = separationDir - (up * downDot);
                        if (horizontal.LengthSquared() > 0.01)
                        {
                            separationDir = Vector3D.Normalize(horizontal);
                        }
                        else
                        {
                            // Exactly vertical stack - do not push lower grid down; let top grid take the upward push
                            return;
                        }
                    }
                }
            }
            else if (otherEntity is MyVoxelBase contactVoxel)
            {
                long trackingId = grid.EntityId;
                int attempts = _pushApartAttempts.TryGetValue(trackingId, out int a) ? a : 0;
                if (config.PushApartMaxAttempts > 0 && attempts >= config.PushApartMaxAttempts)
                {
                    // Escape-attempt cap: repeated failed pushes mean the direction is wrong (cliff face)
                    if (config.ConvertToStaticOnPushApartGiveUp)
                    {
                        if (config.LogPushApartStationConversion || ShouldLog(_lastPushApartGiveUpLogFrames, trackingId, currentFrame, 300))
                        {
                            Log.Warn(LogSource, $"[PUSH-APART GIVE-UP] '{grid.DisplayName}' ({trackingId}) reached attempt limit ({attempts}/{config.PushApartMaxAttempts}); enqueuing convert-to-station to save server sim.");
                        }
                        _pushQueue.Enqueue(new PushApartAction
                        {
                            GridId = trackingId,
                            ConvertToStatic = true
                        });
                    }
                    else
                    {
                        if (ShouldLog(_lastPushApartGiveUpLogFrames, trackingId, currentFrame, 300))
                        {
                            Log.Warn(LogSource, $"[PUSH-APART GIVE-UP] '{grid.DisplayName}' ({trackingId}) reached attempt limit ({attempts}/{config.PushApartMaxAttempts}); give-up attempt limit reached (stationing disabled).");
                        }
                    }
                    return;
                }

                // Direction & distance escalation:
                // Rather than hard-locking to Attempt 1's vector for 5 seconds, each re-trigger re-evaluates
                // the freshest escape direction and smoothly blends with prior momentum (50/50 rolling average).
                // Distance escalates up to PushApartMaxNudgeDistance to overcome deep wedges.
                double distance = config.PushApartDistance;
                bool hasPriorEscape = _lastEscapes.TryGetValue(grid.EntityId, out EscapeRecord esc) && currentFrame <= esc.Frame + 300;

                if (!TryResolveVoxelEscapeDirection(grid, config, contactVoxel, out Vector3D resolvedDir))
                {
                    if (hasPriorEscape && esc.Direction.LengthSquared() > 0.001)
                    {
                        resolvedDir = esc.Direction;
                    }
                    else
                    {
                        if (ShouldLog(_lastPushApartGiveUpLogFrames, grid.EntityId, currentFrame, 1200))
                        {
                            Log.Warn(LogSource, $"[PUSH-APART] No escape direction found for '{grid.DisplayName}' (all candidates blocked by voxels); retrying on later contacts.");
                        }
                        return;
                    }
                }

                if (hasPriorEscape)
                {
                    esc.Level = Math.Min(esc.Level + 1, 1000);
                    esc.Frame = currentFrame;
                    distance = Math.Min(config.PushApartDistance * (esc.Level + 1), config.PushApartMaxNudgeDistance);

                    // Rolling blend: 50% prior escape vector + 50% latest resolved vector
                    Vector3D blended = esc.Direction + resolvedDir;
                    separationDir = blended.LengthSquared() > 0.01 ? Vector3D.Normalize(blended) : resolvedDir;
                    esc.Direction = separationDir;
                    _lastEscapes[grid.EntityId] = esc;
                }
                else
                {
                    separationDir = resolvedDir;
                    _lastEscapes[grid.EntityId] = new EscapeRecord { Direction = separationDir, Frame = currentFrame, Level = 0 };
                }

                _pushApartAttempts[trackingId] = attempts + 1;
                if (config.LogPushApartDiagnostics)
                {
                    string underSurfaceInfo = "";
                    Vector3D gridCenter = grid.PositionComp.WorldVolume.Center;
                    MyPlanet planet = contactVoxel as MyPlanet ?? MyGamePruningStructure.GetClosestPlanet(gridCenter);
                    if (planet != null)
                    {
                        Vector3D surfacePt = planet.GetClosestSurfacePointGlobal(ref gridCenter);
                        Vector3D core = planet.PositionComp.WorldVolume.Center;
                        double altDiff = (gridCenter - core).Length() - (surfacePt - core).Length();
                        underSurfaceInfo = string.Format(CultureInfo.InvariantCulture, " | UnderSurface: {0} (AltDiff: {1:F2}m)", altDiff < 0, altDiff);
                    }
                    float pushSpeed = grid.Physics?.LinearVelocity.Length() ?? 0f;
                    float pushAngular = grid.Physics?.AngularVelocity.Length() ?? 0f;
                    _lastVoxelPenetrations.TryGetValue(trackingId, out float contactDist);
                    float penetration = contactDist < 0f ? -contactDist : 0f;
                    string lockInfo = hasPriorEscape ? string.Format(CultureInfo.InvariantCulture, "Blended (Lvl {0})", esc.Level) : "False";
                    Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                        "[PUSH-APART DIAG] '{0}' ({1}) | Attempt: {2}/{3}{4} | Penetration: {5:F3}m (Max: {6:F2}m) | Speed: {7:F2} m/s | Angular: {8:F2} rad/s | Nudge: {9:F2}m | Dir: {10:F3} | Locked: {11}",
                        grid.DisplayName, trackingId, attempts + 1, config.PushApartMaxAttempts, underSurfaceInfo, penetration, config.PushApartEmbeddedDepth, pushSpeed, pushAngular, distance, separationDir, lockInfo));
                }
                EnqueuePush(grid, separationDir, true, distance);
                return;
            }
            else
            {
                separationDir = Vector3D.Up;
            }

            EnqueuePush(grid, separationDir, false, config.PushApartDistance);
        }

        private void EnqueuePush(MyCubeGrid grid, Vector3D separationDir, bool voxelPush, double distance)
        {
            PhysicsOptimizerConfig config = _plugin?.Config;
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
            PhysicsOptimizerConfig config = _plugin?.Config;
            DefenseStatistics stats = _plugin?.DefenseStats;
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
            foreach (KeyValuePair<long, ulong> kvp in dict)
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
            PhysicsOptimizerConfig config = _plugin?.Config;
            while (_pushQueue.TryDequeue(out PushApartAction action))
            {
                try
                {
                    if (!MyEntities.TryGetEntityById(action.GridId, out MyEntity entity) || entity is not MyCubeGrid grid)
                    {
                        continue;
                    }

                    if (grid.MarkedForClose || grid.Closed) continue;

                    if (action.ConvertToStatic)
                    {
                        _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
                        _pushMechanicalGroupBuffer.Clear();
                        try
                        {
                            GridUtils.GetMechanicalGroupMembers(grid, _pushMechanicalGroupBuffer);
                            if (config?.LogPushApartStationConversion == true)
                            {
                                Log.Warn(LogSource, $"[PUSH-APART STATION] Executing station conversion for construct '{grid.DisplayName}' ({_pushMechanicalGroupBuffer.Count} members)...");
                            }

                            foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                            {
                                if (member == null || member.MarkedForClose || member.Closed) continue;

                                if (member.Physics != null)
                                {
                                    member.Physics.LinearVelocity = Vector3.Zero;
                                    member.Physics.AngularVelocity = Vector3.Zero;
                                    if (member.Physics.RigidBody != null)
                                    {
                                        member.Physics.RigidBody.LinearVelocity = Vector3.Zero;
                                        member.Physics.RigidBody.AngularVelocity = Vector3.Zero;
                                        member.Physics.RigidBody.Deactivate();
                                    }
                                }

                                try
                                {
                                    if (MyMultiplayer.Static != null)
                                    {
                                        MyMultiplayer.RaiseEvent(member, (MyCubeGrid x) => x.ConvertToStatic);
                                    }
                                    else
                                    {
                                        member.ConvertToStatic();
                                    }
                                }
                                catch
                                {
                                    member.ConvertToStatic();
                                }

                                if (!member.IsStatic)
                                {
                                    member.Physics?.ConvertToStatic();
                                }

                                if (config?.LogPushApartStationConversion == true)
                                {
                                    if (member.IsStatic)
                                    {
                                        Log.Info(LogSource, $"[PUSH-APART STATION] Member '{member.DisplayName}' ({member.EntityId}) is now STATIC.");
                                    }
                                    else
                                    {
                                        Log.Warn(LogSource, $"[PUSH-APART STATION] Member '{member.DisplayName}' ({member.EntityId}) FAILED to convert to static!");
                                    }
                                }

                                _consecutiveContactFrames.TryRemove(member.EntityId, out _);
                                _lastContactFrameTracker.TryRemove(member.EntityId, out _);
                                _pushApartAttempts.TryRemove(member.EntityId, out _);
                                _lastEscapes.TryRemove(member.EntityId, out _);
                            }
                        }
                        finally
                        {
                            _pushMechanicalGroupBuffer.Clear();
                        }
                        continue;
                    }

                    if (config?.EnablePushApartDebugDraw == true)
                    {
                        DrawPushDebugGps(action, grid.DisplayName);
                    }

                    // Apply the same translation to the entire mechanical group (wheels are separate
                    // physics bodies - moving only the main grid leaves wheels buried and clanging)
                    _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
                    _pushMechanicalGroupBuffer.Clear();
                    try
                    {
                        GridUtils.GetMechanicalGroupMembers(grid, _pushMechanicalGroupBuffer);

                        foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                        {
                            if (member == null || member.MarkedForClose || member.Closed) continue;

                            MatrixD matrix = member.WorldMatrix;
                            matrix.Translation += action.SeparationDir * action.Distance;
                            member.PositionComp.SetWorldMatrix(ref matrix);

                            if (member.Physics != null)
                            {
                                // Wake the body first - velocity writes on a deactivated rigid body are lost
                                member.Physics.RigidBody?.Activate();
                                member.Physics.LinearVelocity = Vector3.Zero;
                                member.Physics.AngularVelocity = Vector3.Zero;
                            }

                            _consecutiveContactFrames[member.EntityId] = 0;
                        }
                    }
                    finally
                    {
                        _pushMechanicalGroupBuffer.Clear();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, LogSource, "Error while applying queued push-apart action!");
                }
            }
        }

        // --- Capped Debug GPS Draws ---
        // Shared by the voxel-arbitrator (physics thread) and push-apart (game thread) debug draws.

        private const int MaxLiveDebugGpsMarkers = 10;
        private const int DebugGpsTtlSeconds = 10;

        private struct DebugGpsMarker
        {
            public string Name;
            public DateTime ExpiresUtc;
        }

        private static readonly object _debugGpsLock = new object();
        private static readonly List<DebugGpsMarker> _liveDebugGps = new List<DebugGpsMarker>();

        /// <summary>
        /// Capped debug GPS add visible to all players: at most MaxLiveDebugGpsMarkers live markers
        /// globally (10s TTL), and the previous same-name marker is deleted before re-adding so a
        /// repeatedly-inverting grid holds one marker instead of stacking duplicates.
        /// </summary>
        private static void AddCappedDebugGps(string name, string description, Vector3D pos, Color color)
        {
            DateTime now = DateTime.UtcNow;
            lock (_debugGpsLock)
            {
                for (int i = _liveDebugGps.Count - 1; i >= 0; i--)
                {
                    if (_liveDebugGps[i].ExpiresUtc <= now || _liveDebugGps[i].Name == name)
                        _liveDebugGps.RemoveAt(i);
                }

                if (_liveDebugGps.Count >= MaxLiveDebugGpsMarkers) return;
                _liveDebugGps.Add(new DebugGpsMarker { Name = name, ExpiresUtc = now.AddSeconds(DebugGpsTtlSeconds) });
            }

            RemoveDebugGpsForAll(name);
            MyVisualScriptLogicProvider.AddGPSForAll(name, description, pos, color, DebugGpsTtlSeconds);
        }

        /// <summary>
        /// Bright GPS marker pair at the push origin/end so admins can watch pushes live in the HUD.
        /// One pair per grid via the shared cap: the previous pair is deleted before re-adding, so a
        /// repeatedly-pushed grid holds exactly two markers instead of flooding the GPS list.
        /// </summary>
        private static void DrawPushDebugGps(PushApartAction action, string gridName)
        {
            Color color = action.VoxelPush ? Color.Magenta : Color.Yellow;
            AddCappedDebugGps($"PD {gridName} START", $"push-apart debug: [{(action.VoxelPush ? "VOXEL" : "GRID")}] push origin", action.StartPos, color);
            AddCappedDebugGps($"PD {gridName} END (5m)", $"push-apart debug: pushed {action.Distance:F1}m along vector", action.StartPos + action.SeparationDir * 5.0, color);
        }

        private static void RemoveDebugGpsForAll(string name)
        {
            ICollection<MyPlayer> players = Sandbox.Game.World.MySession.Static?.Players?.GetOnlinePlayers();
            if (players == null) return;

            foreach (MyPlayer player in players)
            {
                IMyGps gps = Sandbox.Game.World.MySession.Static.Gpss.GetGpsByName(player.Identity.IdentityId, name);
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
            PhysicsOptimizerConfig config = _plugin?.Config;
            if (config == null || !config.EnableBurialProbe || !config.EnablePushApart) return;

            foreach (MyEntity entity in MyEntities.GetEntities())
            {
                if (entity is not MyCubeGrid grid || grid.IsStatic || grid.MarkedForClose || grid.Closed || grid.Physics?.RigidBody == null) continue;

                // Burial probe only runs on the main chassis/topgrid of a construct - never on subgrids or wheels
                if (IsWheelSubgrid(grid, out _) || (GridUtils.GetMainGrid(grid) ?? grid) != grid) continue;

                long id = grid.EntityId;
                Vector3D pos = grid.PositionComp.GetPosition();

                if (!_burialProbeStates.TryGetValue(id, out BurialProbeState state))
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
                    if (!TryResolveVoxelEscapeDirection(grid, config, null, out Vector3D escapeDir))
                    {
                        escapeDir = grid.Physics.Gravity.LengthSquared() > 0.1f ? -Vector3D.Normalize(grid.Physics.Gravity) : Vector3D.Up;
                    }

                    EnqueuePush(grid, escapeDir, voxelPush: true, config.PushApartDistance);
                    state.StationarySweeps = 0;

                    if (config.EnableDebugLogging)
                    {
                        Log.Info(LogSource, $"[BURIAL PROBE] Queued rescue push for buried grid '{grid.DisplayName}'.");
                    }
                }
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
                foreach (KeyValuePair<long, MissileEngagement> kvp in _activeMissiles)
                {
                    if (currentFrame > kvp.Value.ExpireFrame + 300)
                    {
                        if (_activeMissiles.TryRemove(kvp.Key, out _) && MyEntities.TryGetEntityById(kvp.Key, out MyEntity ent) && ent is MyCubeGrid g)
                        {
                            g.OnClose -= OnTrackedGridClosed;
                            g.OnGridSplit -= OnTrackedGridSplit;
                        }
                    }
                }

                TrimDictionary(_lastContactFrameTracker, currentFrame, 600, _consecutiveContactFrames);
                TrimDictionary(_lastDeformationFrames, currentFrame, 600);
                TrimDictionary(_lastVoxelContactFrames, currentFrame, 1800);
                TrimDictionary(_lastPushApartGiveUpLogFrames, currentFrame, 1200);

                // Only refund attempt budget when grid has had no voxel contact for 300 frames (5s)
                foreach (var trackedId in _pushApartAttempts.Keys)
                {
                    if (!HadVoxelContactWithin(trackedId, 300)) _pushApartAttempts.TryRemove(trackedId, out _);
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
                MethodInfo targetMethod = typeof(MyGridPhysics).GetMethod("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (targetMethod != null)
                {
                    MethodInfo prefixMethod = typeof(GridDefender).GetMethod(nameof(Prefix_PerformDeformation), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(targetMethod).Prefixes.Add(prefixMethod);
                    PatchConflictAudit.RegisterTarget(targetMethod);
                    Log.Info(LogSource, "Registered MyGridPhysics.PerformDeformation hook.");
                }

                MethodInfo contactMethod = typeof(MyGridPhysics).GetMethod("RigidBody_ContactPointCallbackImpl", BindingFlags.Instance | BindingFlags.NonPublic);
                if (contactMethod != null)
                {
                    MethodInfo contactPrefix = typeof(GridDefender).GetMethod(nameof(Prefix_RigidBody_ContactPointCallbackImpl), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
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
                Type explosionType = typeof(MyExplosions).Assembly.GetType("Sandbox.Game.MyExplosion");
                if (explosionType != null)
                {
                    MethodInfo applyVoxelMethod = explosionType.GetMethod("ApplyExplosionOnVoxel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (applyVoxelMethod != null)
                    {
                        MethodInfo prefixApply = typeof(GridDefender).GetMethod(nameof(PrefixApplyExplosionOnVoxel), BindingFlags.Static | BindingFlags.NonPublic);
                        ctx.GetPattern(applyVoxelMethod).Prefixes.Add(prefixApply);
                        PatchConflictAudit.RegisterTarget(applyVoxelMethod);
                    }

                    MethodInfo cutOutMethod = explosionType.GetMethod("CutOutVoxelMap", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cutOutMethod != null)
                    {
                        MethodInfo prefixCutOut = typeof(GridDefender).GetMethod(nameof(PrefixCutOutVoxelMap), BindingFlags.Static | BindingFlags.NonPublic);
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
                MethodInfo initMethod = typeof(MyDamageSystem).GetMethod("LoadData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? typeof(MyDamageSystem).GetMethod("Init", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (initMethod != null)
                {
                    MethodInfo initPostfix = typeof(GridDefender).GetMethod(nameof(DamageSystemInitPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(initMethod).Suffixes.Add(initPostfix);
                    PatchConflictAudit.RegisterTarget(initMethod);
                    Log.Info(LogSource, "Registered MyDamageSystem hook for Layered Armor Occlusion.");
                }
                else if (MyDamageSystem.Static != null)
                {
                    FieldInfo beforeField = typeof(MyDamageSystem).GetField("m_beforeDamageHandlers", BindingFlags.Instance | BindingFlags.NonPublic);
                    var handlers = beforeField?.GetValue(MyDamageSystem.Static) as List<Tuple<int, BeforeDamageApplied>>;
                    if (handlers == null || !handlers.Exists(t => t.Item2 == OnBeforeDamageApplied))
                    {
                        MyDamageSystem.Static.RegisterBeforeDamageHandler(100, OnBeforeDamageApplied);
                        _damageHandlerRegistered = true;
                        Log.Info(LogSource, "Registered Layered Armor Occlusion damage handler directly.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyDamageSystem for Layered Armor Occlusion!");
            }
        }

        // --- MyGridPhysics Patches ---
        //
        // NOTE: Keen Software House engine quirk & internal preconditions for PerformDeformation:
        // PerformDeformation is NOT called on every collision. It is exclusively invoked by
        // MyGridPhysics.BreakAtPoint (Havok BreakPartsHandler callback) under the following strict conditions:
        // 1. Havok Break-Off Threshold: Contact force must exceed the rigid body / shape break-off limit.
        // 2. Safezones: MySessionComponentSafeZones.IsActionAllowedFullyInside must allow damage.
        // 3. Collision Momentum Threshold: (separatingVelocity * Math.Min(massA, massB)) > 21,000.
        //    - If momentum <= 21,000, BreakAtPoint returns false immediately and NEVER invokes PerformDeformation.
        //    - Example: A 10,000 kg grid must have separating velocity > 2.1 m/s to qualify. Low-speed wedged or
        //      vibrating grids (< 2.1 m/s) never reach this threshold.
        // 4. AABB Containment: Contact point must lie within the grid's WorldAABB inflated by 0.1m.
        // Therefore, systems requiring per-frame continuous contact detection (anti-clang oscillation arrest,
        // stuck-grid push-apart, resting detection) MUST NOT rely on PerformDeformation; they must be driven
        // from RigidBody_ContactPointCallbackImpl. High-energy kinetic impact features can use this 21,000 gate.

        public static bool Prefix_PerformDeformation(MyGridPhysics __instance, MyEntity otherEntity, ref float separatingVelocity)
        {
            PhysicsOptimizerPlugin plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin?.GridDefender == null) return true;

            return plugin.GridDefender.ShouldAllowDeformation(__instance, otherEntity, ref separatingVelocity);
        }

        public static bool Prefix_RigidBody_ContactPointCallbackImpl(MyGridPhysics __instance, ref HkContactPointEvent value)
        {
            if (__instance.Entity is MyCubeGrid gridLocal)
            {
                UpdateCollisionContext(gridLocal.EntityId, __instance.ClusterToWorld(value.ContactPoint.Position));
            }

            PhysicsOptimizerConfig config = PhysicsOptimizerPlugin.Instance?.Config;
            bool arbitratorEnabled = config != null && config.EnableVoxelNormalArbitrator;
            if (config == null)
                return true;

            if (__instance.Entity is not MyCubeGrid grid || grid.MarkedForClose || grid.Closed)
                return true;

            MyPhysicsBody rb = value.GetPhysicsBody(0);
            MyPhysicsBody otherRb = value.GetPhysicsBody(1);
            if (rb == null || otherRb == null) return true;

            bool isVoxel = false;
            IMyEntity otherEnt = otherRb.Entity;
            if (otherEnt is MyVoxelBase)
            {
                isVoxel = true;
            }
            else
            {
                IMyEntity selfEnt = rb.Entity;
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
                if (currentFrame > 0)
                {
                    _lastVoxelContactFrames[grid.EntityId] = currentFrame;
                    if (IsWheelSubgrid(grid, out long baseGridId) && baseGridId > 0L)
                    {
                        _lastVoxelContactFrames[baseGridId] = currentFrame;
                    }
                }

                // Havok contact normal points from Body A (index 0) to Body B (index 1).
                // Separating force on Body A is along -Normal; separating force on Body B is along +Normal.
                bool gridIsBodyA = rb.Entity == grid;
                Vector3 gridForceDir = gridIsBodyA ? -value.ContactPoint.Normal : value.ContactPoint.Normal;
                Vector3 rawForceDir = gridForceDir;

                // Push-apart escape direction source: record the true force direction and impact position
                // Suspension wheel subgrids are excluded so tire ground-reaction forces never overwrite chassis normals
                if (!IsWheelSubgrid(grid, out _))
                {
                    Vector3D contactWorldPos = __instance.ClusterToWorld(value.ContactPoint.Position);
                    _lastVoxelContactNormals[grid.EntityId] = gridForceDir;
                    _lastImpactPositions[grid.EntityId] = contactWorldPos;
                    _lastVoxelPenetrations[grid.EntityId] = value.ContactPoint.Distance;
                }

                if (arbitratorEnabled)
                {
                    Vector3 gravity = __instance.Gravity;
                    if (gravity.LengthSquared() > 0.01f)
                    {
                        Vector3 upVector = -Vector3.Normalize((Vector3)gravity);
                        float upDot = Vector3.Dot(gridForceDir, upVector);

                        if (upDot < 0f)
                        {
                            // Contact positions arrive in Havok cluster space (origin-offset world); vanilla converts
                            // every consumer via ClusterToWorld - raw values are shifted by the cluster world anchor.
                            Vector3D contactPos = __instance.ClusterToWorld(value.ContactPoint.Position);

                            // Same two-tier rule as push-apart: in pristine voxel regions a gravity-downward normal can
                            // only be a Keen compression artifact pointing into the planet core, so invert it directly.
                            // Only carved regions (real cave ceilings are possible) pay for the confirmation raycast.
                            MyVoxelBase voxelEnt = otherEnt as MyVoxelBase ?? rb.Entity as MyVoxelBase;
                            bool hitAir = voxelEnt == null || !IsVoxelRegionModified(voxelEnt, contactPos);
                            if (!hitAir)
                            {
                                hitAir = true; // Assume air above unless raycast hits solid voxel
                                Vector3D rayStart = contactPos;
                                Vector3D rayEnd = rayStart + (Vector3D)upVector * 1.5f;

                                _voxelHitsCache ??= [];
                                _voxelHitsCache.Clear();
                                MyPhysics.CastRay(rayStart, rayEnd, _voxelHitsCache, MyPhysics.CollisionLayers.VoxelCollisionLayer);
                                for (int i = 0; i < _voxelHitsCache.Count; i++)
                                {
                                    IMyEntity hitEnt = _voxelHitsCache[i].HkHitInfo.GetHitEntity();
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
                                Vector3 oldGridForceDir = gridForceDir;
                                Vector3 newGridForceDir = -gridForceDir;

                                // HkContactPoint wraps native m_handle - calling Flip() executes native HkContactPoint_Flip
                                // directly on Havok's internal contact data used by the solver.
                                var cp = value.ContactPoint;
                                cp.Flip();

                                gridForceDir = newGridForceDir;

                                // Zero friction on inverted/phased contacts to stop Havok friction solver pinning the grid into voxel banks
                                var props = value.ContactProperties;
                                props.Friction = 0f;

                                // Modifying velocity in Havok's active solver buffer allows the grid to actively emerge
                                int bodyIndex = gridIsBodyA ? 0 : 1;
                                value.AccessVelocities(bodyIndex);
                                if (__instance.RigidBody != null)
                                {
                                    float velUp = Vector3.Dot(__instance.RigidBody.LinearVelocity, upVector);
                                    if (velUp < 0.5f)
                                    {
                                        __instance.RigidBody.LinearVelocity += upVector * (0.5f - velUp);
                                    }
                                }
                                value.UpdateVelocities(bodyIndex);

                                // If this is a wheel subgrid, apply upward velocity to the base chassis too so the suspension constraint doesn't pin the wheel
                                if (IsWheelSubgrid(grid, out long baseGridId) && baseGridId > 0L && MyEntities.TryGetEntityById(baseGridId, out MyEntity baseEnt) && baseEnt is MyCubeGrid baseGrid && baseGrid.Physics != null)
                                {
                                    float baseVelUp = Vector3.Dot((Vector3)baseGrid.Physics.LinearVelocity, upVector);
                                    if (baseVelUp < 0.5f)
                                    {
                                        baseGrid.Physics.LinearVelocity += upVector * (0.5f - baseVelUp);
                                    }
                                }

                                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelNormalsInverted();

                                if (config.EnableDebugLogging && config.LogVoxelNormals)
                                {
                                    Log.Info(LogSource, $"[Voxel Arbitrator] Inverted downward normal for grid '{grid.DisplayName}' at {contactPos}.");
                                }

                                if (config.EnableVoxelNormalArbitratorDebugDraw && grid.PositionComp != null)
                                {
                                    Vector3D startPos = contactPos;
                                    Vector3D endPos = startPos + (Vector3D)newGridForceDir * 5.0;
                                    AddCappedDebugGps(
                                        $"VA {grid.DisplayName} OLD",
                                        $"voxel-arb debug: pre-invert force dir",
                                        startPos + (Vector3D)oldGridForceDir * 0.5,
                                        Color.Orange);
                                    AddCappedDebugGps(
                                        $"VA {grid.DisplayName} NEW",
                                        $"voxel-arb debug: post-invert force dir",
                                        endPos,
                                        Color.Cyan);
                                }
                            }
                        }
                    }
                }

                if (currentFrame > 0 && config.EnablePushApart && !grid.IsStatic)
                {
                    MyVoxelBase voxel = (otherRb.Entity as MyVoxelBase) ?? (rb.Entity as MyVoxelBase);
                    if (voxel != null)
                    {
                        // Pass raw un-inverted contact normal so arbitrator's upward inversion does not mask embedding
                        PhysicsOptimizerPlugin.Instance?.GridDefender?.RecordVoxelContactFrame(grid, voxel, config, value.ContactPoint.Distance, rawForceDir);
                    }
                }
            }

            return true;
        }

        // --- MyExplosion Patches ---

        private static bool PrefixApplyExplosionOnVoxel()
        {
            PhysicsOptimizerConfig config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config != null && config.Enabled && config.SuppressAllVoxelExplosionDamage)
            {
                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelCutoutsPrevented();
                return false;
            }
            return true;
        }

        private static bool PrefixCutOutVoxelMap()
        {
            PhysicsOptimizerConfig config = PhysicsOptimizerPlugin.Instance?.Config;
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
                FieldInfo beforeField = typeof(MyDamageSystem).GetField("m_beforeDamageHandlers", BindingFlags.Instance | BindingFlags.NonPublic);
                var handlers = beforeField?.GetValue(__instance) as List<Tuple<int, BeforeDamageApplied>>;
                if (handlers != null && handlers.Exists(t => t.Item2 == OnBeforeDamageApplied))
                {
                    return;
                }

                __instance.RegisterBeforeDamageHandler(100, OnBeforeDamageApplied);
                _damageHandlerRegistered = true;
                Log.Info(LogSource, "Registered Layered Armor Occlusion damage handler via Postfix.");
            }
        }

        public static void OnBeforeDamageApplied(object target, ref MyDamageInformation info)
        {
            if (info.Amount <= 0f || info.Type != MyDamageType.Deformation) return;

            PhysicsOptimizerConfig config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.EnableLayeredArmorOcclusion) return;

            if (target is MySlimBlock slimBlock && slimBlock.CubeGrid != null)
            {
                MyCubeGrid grid = slimBlock.CubeGrid;

                // Note: Occlusion relies on the most recent contact point recorded in _lastImpactPositions.
                // In simultaneous multi-point impacts from opposing directions, the single recorded position
                // may attribute occlusion to the latest contact point. This design is optimized for rover
                // collisions and frontal impacts without per-contact memory allocations.
                if (!_lastImpactPositions.TryGetValue(grid.EntityId, out Vector3D globalHitPos))
                {
                    return; // Can't determine direction without a collision point
                }

                Vector3D localHitPos = Vector3D.Transform(globalHitPos, grid.PositionComp.WorldMatrixNormalizedInv);
                Vector3D blockCenterLocal = slimBlock.Position * grid.GridSize;
                Vector3D D = localHitPos - blockCenterLocal;

                if (D.LengthSquared() < 0.001f) return;

                Vector3I step = Vector3I.Round(Vector3D.Normalize(D));
                Vector3I neighborCoord = slimBlock.Position + step;

                MySlimBlock occluder = grid.GetCubeBlock(neighborCoord);

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
