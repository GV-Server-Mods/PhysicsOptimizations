using System;
using System.Collections.Concurrent;
using System.Threading;
using NLog;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;
using PhysicsOptimizations.Config;
using PhysicsOptimizations.Services;
using PhysicsOptimizations.Utils;

namespace PhysicsOptimizations.Engine
{
    /// <summary>
    /// Core collision defense engine. Evaluates collision deformation allowance,
    /// missile penetration tracking, anti-clang damping, and push-apart separation.
    /// </summary>
    public class DeformationDefenseEngine : IDisposable
    {
        private static readonly ILogger Log = LogManager.GetLogger("PhysicsOptimizer.Engine");

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

        private volatile PhysicsOptimizerConfig _config;
        private readonly DefenseStatistics _stats;
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

        /// <summary>
        /// Creates a new deformation defense engine instance.
        /// </summary>
        public DeformationDefenseEngine(PhysicsOptimizerConfig config, DefenseStatistics stats)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            SyncVoxelFakes();
            MyEntities.OnEntityRemove += OnEntityRemoved;
        }

        /// <summary>
        /// Updates the active configuration reference.
        /// </summary>
        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
            if (config != null)
            {
                _config = config;
                SyncVoxelFakes();
            }
        }

        private void SyncVoxelFakes()
        {
            if (_config != null && _config.Enabled && _config.SuppressAllVoxelExplosionDamage)
            {
                MyFakes.DEFORMATION_EXPLOSIONS = false; // Disables voxel cutouts from collision deformation
            }
        }

        /// <summary>
        /// Evaluates whether a given grid qualifies as a player-made missile based on size and speed.
        /// </summary>
        public bool IsMissile(MyCubeGrid testGrid, float speed)
        {
            if (testGrid == null || testGrid.MarkedForClose || testGrid.Closed || testGrid.IsStatic) return false;
            if (speed < _config.MissileMinVelocity) return false;
            if (_config.ExemptPilotedFromMissileStatus && (testGrid.GridSystems?.ControlSystem?.IsControlled ?? false))
            {
                int b = testGrid.BlocksCount;
                bool wouldBeMissile = testGrid.IsLargeGrid()
                    ? (b >= _config.LargeGridMissileMinBlocks && b <= _config.LargeGridMissileMaxBlocks)
                    : (b >= _config.SmallGridMissileMinBlocks && b <= _config.SmallGridMissileMaxBlocks);
                if (wouldBeMissile)
                {
                    _stats.IncrementPilotedBuggySaves();
                }
                return false;
            }

            int blocks = testGrid.BlocksCount;
            if (testGrid.IsLargeGrid())
            {
                return blocks >= _config.LargeGridMissileMinBlocks && blocks <= _config.LargeGridMissileMaxBlocks;
            }
            if (testGrid.IsSmallGrid())
            {
                return blocks >= _config.SmallGridMissileMinBlocks && blocks <= _config.SmallGridMissileMaxBlocks;
            }
            return false;
        }

        /// <summary>
        /// Main collision decision hook. Filters deformation across the sequential defense pipeline.
        /// </summary>
        public bool ShouldAllowDeformation(MyGridPhysics physics, MyEntity otherEntity, ref float separatingVelocity)
        {
            if (!_config.Enabled) return true;

            if (physics?.Entity is not MyCubeGrid grid || grid.MarkedForClose || grid.Closed) return true;
            if (otherEntity == null || otherEntity.MarkedForClose || otherEntity.Closed) return true;

            _stats.IncrementEvaluated();
            SyncVoxelFakes();

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;

            // 1. Subgrid / Mechanicals (Pistons, Rotors, Hinges, Connectors) Protection
            if (otherEntity is MyCubeGrid otherGrid)
            {
                if (_config.ProtectSubgrids && (GridUtils.AreInSameMechanicalGroup(grid, otherGrid) || GridUtils.AreInSameLogicalGroup(grid, otherGrid)))
                {
                    if (_config.EnableDebugLogging && ShouldLog(_lastSubgridLogFrames, grid.EntityId ^ otherGrid.EntityId, currentFrame, 120))
                    {
                        Log.Info($"[GridDefender] [SUBGRID] Protected: Blocked collision between '{grid.DisplayName}' and '{otherGrid.DisplayName}'.");
                    }
                    ApplyAntiClang(grid, physics, otherEntity);
                    _stats.IncrementBlocked(isSubgrid: true);
                    return false;
                }
            }

            // 2. Floating Objects / Ores / Loose Debris Protection
            if (otherEntity is MyFloatingObject && _config.ProtectAgainstFloatingObjects)
            {
                _stats.IncrementBlocked(isDebris: true);
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

                bool gridQualifies = gridInMissile || (_config.AllowMissileDamage && IsMissile(grid, impactSpeed));
                bool otherQualifies = otherInMissile || (_config.AllowMissileDamage && IsMissile(targetGrid, impactSpeed));

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

                        if (_config.EnableDebugLogging)
                        {
                            Log.Info($"[GridDefender] [MISSILE] Impact ALLOWED: '{engagement.MissileName}' struck '{engagement.TargetName}' at {engagement.InitialSpeed:F1} m/s.");
                        }
                    }

                    Interlocked.Increment(ref engagement.ImpactCount);

                    if (gridQualifies && !gridInMissile) RegisterActiveMissile(grid, engagement);
                    if (otherQualifies && !otherInMissile) RegisterActiveMissile(targetGrid, engagement);

                    // Clamp extreme torsional death-spins without bleeding forward kinetic momentum
                    if (_config.EnableAntiClang && _config.StopClangSpinning && physics.AngularVelocity.LengthSquared() > 16.0f)
                    {
                        physics.AngularVelocity = Vector3.Zero;
                    }

                    return AllowOrScale(grid.EntityId, ref separatingVelocity, isMissile: true);
                }
            }

            // 5. Safe Docking, Parking, and Slow Driving Check (Low-speed floor)
            if (impactSpeed < _config.MinDrivingVelocity)
            {
                if (_config.EnableDebugLogging && impactSpeed >= 1.5f && ShouldLog(_lastRammingLogFrames, grid.EntityId, currentFrame, 120))
                {
                    Log.Info($"[GridDefender] [DOCKING] Safe Docking: Suppressed low-speed bump on '{grid.DisplayName}' ({impactSpeed:F1} m/s < {_config.MinDrivingVelocity:F1} m/s).");
                }
                ApplyAntiClang(grid, physics, otherEntity);
                _stats.IncrementBlocked(isLowSpeed: true);
                return false;
            }

            // 6. Extreme Velocity Anti-Freeze Limit (Non-missiles only)
            if (_config.MaxDeformationVelocity > 0 && impactSpeed > _config.MaxDeformationVelocity)
            {
                if (_config.EnableDebugLogging && ShouldLog(_lastExtremeSpeedLogFrames, grid.EntityId, currentFrame, 60))
                {
                    Log.Warn($"[GridDefender] [SPEED] Limit: Suppressed collision on '{grid.DisplayName}' ({impactSpeed:F1} m/s > {_config.MaxDeformationVelocity:F1} m/s limit).");
                }
                ApplyImpactDamping(physics, grid.IsStatic);
                ApplyAntiClang(grid, physics, otherEntity);
                _stats.IncrementBlocked(isRamming: otherEntity is MyCubeGrid, isVoxel: otherEntity is MyVoxelBase);
                return false;
            }

            // 7. Non-Missile Collisions (Ships, Rovers, Stations, Voxels)
            // A. Static Station Protection
            bool otherIsStaticGrid = otherEntity is MyCubeGrid { IsStatic: true };
            if ((grid.IsStatic || otherIsStaticGrid) && _config.ProtectStaticGrids)
            {
                if (_config.EnableDebugLogging && ShouldLog(_lastStationLogFrames, grid.EntityId ^ otherEntity.EntityId, currentFrame, 60))
                {
                    string stationName = grid.IsStatic ? grid.DisplayName : otherEntity.DisplayName;
                    string strikingName = grid.IsStatic ? otherEntity.DisplayName : grid.DisplayName;
                    Log.Info($"[GridDefender] [STATION] Protected: '{strikingName}' struck static station '{stationName}' at {impactSpeed:F1} m/s (damage suppressed).");
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
                _stats.IncrementBlocked(isStation: true);
                return false;
            }

            // B. Ship vs Voxel Protection (Asteroids, terrain, and off-target missiles hitting dirt)
            if (otherEntity is MyVoxelBase)
            {
                if (_config.ProtectShipsAgainstVoxels)
                {
                    if (_config.EnableDebugLogging && ShouldLog(_lastVoxelLogFrames, grid.EntityId, currentFrame, 60))
                    {
                        Log.Info($"[GridDefender] [VOXEL] Terrain Crash Blocked: '{grid.DisplayName}' ({grid.BlocksCount} blocks) hit voxels at {impactSpeed:F1} m/s (damage suppressed).");
                    }
                    ApplyImpactDamping(physics, grid.IsStatic);
                    ApplyAntiClang(grid, physics, otherEntity);
                    _stats.IncrementBlocked(isVoxel: true);
                    return false;
                }
                return AllowOrScale(grid.EntityId, ref separatingVelocity, isMissile: false);
            }

            // C. Ship vs Ship Ramming Protection
            if (otherEntity is MyCubeGrid && _config.ProtectShipsAgainstRamming)
            {
                if (_config.EnableDebugLogging && ShouldLog(_lastRammingLogFrames, grid.EntityId ^ otherEntity.EntityId, currentFrame, 60))
                {
                    Log.Info($"[GridDefender] [RAMMING] Blocked: '{grid.DisplayName}' ({grid.BlocksCount} blocks) hit '{otherEntity.DisplayName}' at {impactSpeed:F1} m/s (damage suppressed).");
                }
                ApplyImpactDamping(physics, grid.IsStatic);
                ApplyAntiClang(grid, physics, otherEntity);
                _stats.IncrementBlocked(isRamming: true);
                return false;
            }

            // 8. Rate Limiting / Cooldown for any unprotected continuous deformations
            if (_config.DeformationCooldownFrames > 0 && currentFrame > 0)
            {
                if (_lastDeformationFrames.TryGetValue(grid.EntityId, out ulong lastFrame))
                {
                    if (currentFrame >= lastFrame && (currentFrame - lastFrame) < (ulong)_config.DeformationCooldownFrames)
                    {
                        _stats.IncrementBlocked(isCooldown: true);
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

        private void OnEntityRemoved(MyEntity entity)
        {
            if (entity is MyCubeGrid grid)
            {
                EvictGrid(grid.EntityId);
            }
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
            if (isStatic || physics == null || !_config.EnableAntiClang || _config.ImpactVelocityDamping <= 0.0f) return;
            if (physics.Entity == null || physics.Entity.MarkedForClose || physics.Entity.Closed) return;

            float factor = Math.Max(0.0f, 1.0f - (_config.ImpactVelocityDamping * 0.6f));
            physics.LinearVelocity *= factor;
        }

        private void ApplyAntiClang(MyCubeGrid grid, MyGridPhysics physics, MyEntity otherEntity)
        {
            if (grid == null || physics == null || grid.MarkedForClose || grid.Closed || !_config.EnableAntiClang) return;

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
            if (contactCount >= _config.AntiClangVibrationThreshold)
            {
                physics.LinearVelocity *= 0.75f;
                physics.AngularVelocity *= 0.2f;

                if (_config.StopClangSpinning && physics.AngularVelocity.LengthSquared() > 16.0f)
                {
                    physics.AngularVelocity = Vector3.Zero;
                }

                _stats.IncrementClangArrested();

                if (_config.EnableDebugLogging && contactCount == _config.AntiClangVibrationThreshold)
                {
                    Log.Warn($"[GridDefender] [ANTI-CLANG] Activated for '{grid.DisplayName}' ({contactCount} contact frames).");
                }
            }

            // Phase 2: Active Push-Apart (Excludes mechanically/logically connected subgrids)
            bool areConnectedSubgrids = otherEntity is MyCubeGrid otherGrid &&
                (GridUtils.AreInSameMechanicalGroup(grid, otherGrid) || GridUtils.AreInSameLogicalGroup(grid, otherGrid));

            if (!areConnectedSubgrids && _config.EnablePushApart && !grid.IsStatic && contactCount >= _config.PushApartThreshold)
            {
                TryPushApart(grid, otherEntity);
                _consecutiveContactFrames[gridEntityId] = 0;
            }
        }

        private void TryPushApart(MyCubeGrid grid, MyEntity otherEntity)
        {
            if (grid == null || otherEntity == null || grid.MarkedForClose || grid.Closed || otherEntity.MarkedForClose || otherEntity.Closed) return;

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

            float distance = _config.PushApartDistance;

            _pushQueue.Enqueue(new PushApartAction
            {
                GridId = grid.EntityId,
                SeparationDir = separationDir,
                Distance = distance
            });

            _stats.IncrementGridsSeparated();

            if (_config.EnableDebugLogging)
            {
                Log.Info($"[GridDefender] [PUSH-APART] Separated: Queued push for '{grid.DisplayName}' {distance:F2}m away from '{otherEntity.DisplayName}'.");
            }
        }

        private bool AllowOrScale(long gridEntityId, ref float separatingVelocity, bool isMissile)
        {
            if (_config.DeformationMultiplier <= 0.0f)
            {
                _stats.IncrementBlocked();
                return false;
            }

            if (_config.DeformationMultiplier < 1.0f)
            {
                separatingVelocity *= _config.DeformationMultiplier;
            }

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            _lastDeformationFrames[gridEntityId] = currentFrame;

            _stats.IncrementAllowed(isMissile: isMissile);
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
                    Log.Error(ex, "[GridDefender] Error while applying queued push-apart action!");
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
                Log.Warn(ex, "[GridDefender] Error while trimming expired frame tracking collections.");
            }
        }

        /// <summary>
        /// Restores vanilla Keen voxel deformation fake flags.
        /// </summary>
        public static void RestoreVoxelFakes()
        {
            MyFakes.DEFORMATION_EXPLOSIONS = true;
        }

        /// <summary>
        /// Disposes engine resources, clears tracking collections, unhooks entity events, and restores voxel flags.
        /// </summary>
        public void Dispose()
        {
            MyEntities.OnEntityRemove -= OnEntityRemoved;
            RestoreVoxelFakes();
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
        }
    }
}

