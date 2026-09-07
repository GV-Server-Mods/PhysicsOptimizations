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
    /// tracking, anti-clang damping, push-apart separation, voxel cut-out suppression, layered armor
    /// occlusion, and thruster clearance optimization.
    /// </summary>
    public class GridDefender : IPhysicsModule
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

        // Layered Armor Occlusion context tracking: Key: Grid EntityId, Value: Global impact position
        private static readonly ConcurrentDictionary<long, Vector3D> _lastImpactPositions = new();

        [ThreadStatic]
        private static List<MyPhysics.HitInfo> _voxelHitsCache;

        [ThreadStatic]
        private static List<MyPhysics.HitInfo> _thrusterHitList;

        public static void UpdateCollisionContext(long gridId, Vector3D position)
        {
            _lastImpactPositions[gridId] = position;
        }

        public static void RemoveCollisionContext(long gridId)
        {
            _lastImpactPositions.TryRemove(gridId, out _);
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

            // 1. Subgrid / Mechanicals (Pistons, Rotors, Hinges, Connectors) Protection
            if (otherEntity is MyCubeGrid otherGrid)
            {
                if (config.ProtectSubgrids && (GridUtils.AreInSameMechanicalGroup(grid, otherGrid) || GridUtils.AreInSameLogicalGroup(grid, otherGrid)))
                {
                    if (config.EnableDebugLogging && ShouldLog(_lastSubgridLogFrames, grid.EntityId ^ otherGrid.EntityId, currentFrame, 120))
                    {
                        Log.Info(LogSource, $"[SUBGRID] Protected: Blocked collision between '{grid.DisplayName}' and '{otherGrid.DisplayName}'.");
                    }
                    ApplyAntiClang(grid, physics, otherEntity);
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

            // 3. Speed calculations
            float gridSpeed = grid.GetSpeed();
            float otherSpeed = (otherEntity as MyCubeGrid)?.GetSpeed() ?? 0f;
            float absSepVelocity = Math.Abs(separatingVelocity);
            float impactSpeed = Math.Max(absSepVelocity, Math.Max(gridSpeed, otherSpeed));

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
                    Log.Info(LogSource, $"[DOCKING] Safe Docking: Suppressed low-speed bump on '{grid.DisplayName}' ({impactSpeed:F1} m/s < {config.MinDrivingVelocity:F1} m/s).");
                }
                ApplyAntiClang(grid, physics, otherEntity);
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
                ApplyAntiClang(grid, physics, otherEntity);
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
                        ApplyAntiClang(otherCubeGrid, otherCubeGrid.Physics as MyGridPhysics, grid);
                    }
                }
                else
                {
                    ApplyImpactDamping(physics, false);
                    ApplyAntiClang(grid, physics, otherEntity);
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
                    ApplyAntiClang(grid, physics, otherEntity);
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
                ApplyAntiClang(grid, physics, otherEntity);
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

        private void ApplyAntiClang(MyCubeGrid grid, MyGridPhysics physics, MyEntity otherEntity)
        {
            var config = _plugin?.Config;
            if (config == null || grid == null || physics == null || grid.MarkedForClose || grid.Closed || !config.EnableAntiClang) return;

            long gridEntityId = grid.EntityId;
            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (currentFrame == 0) return;

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
            _lastContactFrameTracker[gridEntityId] = currentFrame;

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

            // Phase 2: Active Push-Apart (Excludes mechanically/logically connected subgrids)
            bool areConnectedSubgrids = otherEntity is MyCubeGrid otherGrid &&
                (GridUtils.AreInSameMechanicalGroup(grid, otherGrid) || GridUtils.AreInSameLogicalGroup(grid, otherGrid));

            if (!areConnectedSubgrids && config.EnablePushApart && !grid.IsStatic && contactCount >= config.PushApartThreshold)
            {
                TryPushApart(grid, otherEntity);
                _consecutiveContactFrames[gridEntityId] = 0;
            }
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
            else if (otherEntity is MyVoxelBase voxel)
            {
                // Push skyward along planetary gravity up-vector
                if (grid.Physics != null && grid.Physics.Gravity.LengthSquared() > 0.1f)
                {
                    separationDir = -Vector3D.Normalize(grid.Physics.Gravity);
                }
                else
                {
                    separationDir = gridPos - voxel.PositionComp.GetPosition();
                    separationDir = separationDir.LengthSquared() < 0.01 ? Vector3D.Up : Vector3D.Normalize(separationDir);
                }
            }
            else
            {
                separationDir = Vector3D.Up;
            }

            float distance = config.PushApartDistance;

            _pushQueue.Enqueue(new PushApartAction
            {
                GridId = grid.EntityId,
                SeparationDir = separationDir,
                Distance = distance
            });

            _plugin?.DefenseStats?.IncrementGridsSeparated();

            if (config.EnableDebugLogging)
            {
                Log.Info(LogSource, $"[PUSH-APART] Separated: Queued push for '{grid.DisplayName}' {distance:F2}m away from '{otherEntity.DisplayName}'.");
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
            }

            ProcessPushQueue();
        }

        private void ProcessPushQueue()
        {
            while (_pushQueue.TryDequeue(out var action))
            {
                try
                {
                    if (MyEntities.TryGetEntityById(action.GridId, out var entity) && entity is MyCubeGrid grid)
                    {
                        if (grid.MarkedForClose || grid.Closed) continue;

                        var matrix = grid.WorldMatrix;
                        matrix.Translation += action.SeparationDir * action.Distance;
                        grid.PositionComp.SetWorldMatrix(ref matrix);

                        if (grid.Physics != null)
                        {
                            grid.Physics.LinearVelocity = (Vector3)action.SeparationDir * 0.8f;
                            grid.Physics.AngularVelocity = Vector3.Zero;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, LogSource, "Error while applying queued push-apart action!");
                }
            }
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

            // 4. Thruster Clearance Optimizer
            try
            {
                var targetMethod = typeof(MyThrust).GetMethod("ThrustDamageAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? typeof(MyThrust).GetMethod("DamageGrid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (targetMethod != null)
                {
                    var prefixMethod = typeof(GridDefender).GetMethod(nameof(ThrustDamagePrefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(targetMethod).Prefixes.Add(prefixMethod);
                    PatchConflictAudit.RegisterTarget(targetMethod);
                    Log.Info(LogSource, $"Registered {targetMethod.Name} for Thruster Clearance Optimizer.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyThrust for Thruster Clearance Optimizer!");
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
            if (config == null || !config.EnableVoxelNormalArbitrator)
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
                var gravity = __instance.Gravity;
                if (gravity.LengthSquared() < 0.01f) return true;

                var upVector = -Vector3.Normalize((Vector3)gravity);
                float upDot = Vector3.Dot(value.ContactPoint.Normal, upVector);

                if (upDot < 0f)
                {
                    var contactPos = value.ContactPoint.Position;
                    var rayStart = contactPos;
                    var rayEnd = rayStart + upVector * 1.5f;

                    bool hitAir = true;
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

                bool isValidOccluder = true;
                if (config.EnforceStructuralArmorCheck)
                {
                    isValidOccluder = (occluder.FatBlock == null) || (occluder.DeformationRatio < 0.5f);
                }

                if (isValidOccluder && !occluder.IsDestroyed)
                {
                    info.Amount = 0f; // Shielded!
                    PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementArmorHitsOccluded();
                }
            }
        }

        // --- Thruster Clearance Optimizer Patches ---

        public static bool ThrustDamagePrefix(MyThrust __instance)
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.Enabled || !config.EnablePhysicsOptimizations || !config.EnableThrusterClearance) return true;

            if (__instance == null || __instance.Closed || __instance.CubeGrid == null || __instance.BlockDefinition == null) return false;

            // Inactive thrusters deal no damage
            if (__instance.CurrentStrength <= 0f && !MyFakes.INACTIVE_THRUSTER_DMG) return false;

            try
            {
                var flames = __instance.Flames;
                if (flames.Count == 0)
                {
                    // Fallback to vanilla if block definition has no recognized flame dummies
                    return true;
                }

                MatrixD worldMatrix = __instance.WorldMatrix;
                float flameDamageScale = __instance.BlockDefinition.FlameDamageLengthScale;
                if (flameDamageScale <= 0f) flameDamageScale = 1.0f;

                for (int f = 0; f < flames.Count; f++)
                {
                    var flame = flames[f];
                    if (!flame.HasDamage) continue;

                    Vector3D flameStart = Vector3D.Transform(flame.Position, worldMatrix);
                    Vector3D flameDir = Vector3D.TransformNormal(flame.Direction, worldMatrix);
                    if (flameDir.LengthSquared() < 0.0001)
                        flameDir = worldMatrix.Forward;
                    else
                        flameDir.Normalize();

                    float length = flame.Radius * flameDamageScale * 2.5f;
                    if (length <= 0f) length = 2.5f;

                    float radius = flame.Radius;

                    // Multi-nozzle / radial coverage based on nozzle radius:
                    if (radius <= 0.75f)
                    {
                        // Small nozzle (<= 1.5m diameter): Single center ray
                        CastThrusterRay(flameStart, flameDir, length, __instance, config);
                    }
                    else if (radius <= 2.0f)
                    {
                        // Medium nozzle (1.5m - 4.0m diameter): 5-ray crosshair (center + 4 cardinal offsets)
                        Vector3D right = Vector3D.CalculatePerpendicularVector(flameDir);
                        Vector3D up = Vector3D.Cross(flameDir, right);
                        float offset = 0.7f * radius;

                        CastThrusterRay(flameStart, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + right * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart - right * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + up * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart - up * offset, flameDir, length, __instance, config);
                    }
                    else
                    {
                        // Giant / Titan nozzle (> 4.0m diameter, e.g. 5x5, 7x7):
                        // 9-ray radial fan: Center + 8 outer rays (4 cardinal + 4 diagonal)
                        // Eliminates all diagonal corner blind spots on square/rectangular thrusters
                        Vector3D right = Vector3D.CalculatePerpendicularVector(flameDir);
                        Vector3D up = Vector3D.Cross(flameDir, right);
                        float offset = 0.75f * radius;
                        float diagOffset = offset * 0.7071f;

                        CastThrusterRay(flameStart, flameDir, length, __instance, config);

                        // Cardinal rays
                        CastThrusterRay(flameStart + right * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart - right * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + up * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart - up * offset, flameDir, length, __instance, config);

                        // Diagonal rays
                        CastThrusterRay(flameStart + (right + up) * diagOffset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + (right - up) * diagOffset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + (-right + up) * diagOffset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + (-right - up) * diagOffset, flameDir, length, __instance, config);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, LogSource, "[ThrusterClearance] Error in raycast clearance; falling back to vanilla.");
                return true;
            }
        }

        private static void CastThrusterRay(Vector3D start, Vector3D dir, float length, MyThrust thruster, PhysicsOptimizerConfig config)
        {
            _thrusterHitList ??= [];
            _thrusterHitList.Clear();

            Vector3D end = start + dir * length;
            MyPhysics.CastRay(start, end, _thrusterHitList, MyPhysics.CollisionLayers.DefaultCollisionLayer);

            for (int i = 0; i < _thrusterHitList.Count; i++)
            {
                var hit = _thrusterHitList[i];
                var hitEntity = hit.HkHitInfo.GetHitEntity();
                if (hitEntity == null) continue;

                if (hitEntity is IMyCharacter character)
                {
                    character.DoDamage(50f, MyDamageType.Environment, true, null, thruster.EntityId);
                    continue;
                }

                if (hitEntity is MyCubeGrid hitGrid)
                {
                    bool isOwnConstruct = hitGrid == thruster.CubeGrid ||
                        (thruster.CubeGrid.GridSystems != null && hitGrid.GridSystems != null &&
                         MyCubeGridGroups.Static.Physical.GetGroup(hitGrid) == MyCubeGridGroups.Static.Physical.GetGroup(thruster.CubeGrid));

                    Vector3I blockPos = hitGrid.WorldToGridInteger(hit.Position + dir * 0.1f);
                    var block = hitGrid.GetCubeBlock(blockPos);

                    if (block == null) continue;

                    // Critical safety guard: NEVER damage the thruster itself
                    if (ReferenceEquals(block, thruster.SlimBlock)) continue;

                    if (isOwnConstruct)
                    {
                        if (config.ThrusterDamageMode == ThrusterDamageMode.Optimized)
                        {
                            // Anti-exploit: Instantly vaporize buried internal blocks on own construct
                            block.DoDamage(float.MaxValue, MyDamageType.Deformation, true, null, thruster.EntityId);
                            PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementThrusterObstructionsVaporized();

                            if (config.EnableDebugLogging && config.LogThrusterClearance)
                            {
                                Log.Info(LogSource, $"[ThrusterClearance] Vaporized own-construct obstruction on '{hitGrid.DisplayName}'.");
                            }
                        }
                        else
                        {
                            // Vanilla-like mode: Gradual thermal damage on own construct
                            float dmg = thruster.BlockDefinition.FlameDamage * thruster.CurrentStrength;
                            block.DoDamage(dmg, MyDamageType.Thruster, true, null, thruster.EntityId);
                        }
                    }
                    else
                    {
                        // External Grid (Carrier Deck, Landing Pad, Enemy Hull)
                        if (config.ThrusterDamageMode == ThrusterDamageMode.VanillaLike)
                        {
                            // Vanilla-like mode: Gradual thermal damage to carrier deck / external grid
                            float dmg = thruster.BlockDefinition.FlameDamage * thruster.CurrentStrength;
                            block.DoDamage(dmg, MyDamageType.Thruster, true, null, thruster.EntityId);

                            if (config.EnableDebugLogging && config.LogThrusterClearance)
                            {
                                Log.Info(LogSource, $"[ThrusterClearance] Applied vanilla-rate thermal damage ({dmg:F1}) to external grid '{hitGrid.DisplayName}'.");
                            }
                        }
                        // If Optimized mode: External grids are 100% immune (0 damage, 0 allocations)
                    }
                }
            }

            _thrusterHitList.Clear();
        }
    }
}

