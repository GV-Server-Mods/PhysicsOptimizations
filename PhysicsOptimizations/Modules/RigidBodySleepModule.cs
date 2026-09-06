using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using GVK.PhysicsOptimizations.Config;

namespace GVK.PhysicsOptimizations.Modules
{
    public class RigidBodySleepModule : IPhysicsModule
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.Sleep");

        public string Name => "Rigid Body Sleep Manager";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnableAggressiveSleeping;

        private PhysicsOptimizerPlugin _plugin;

        public class GridIdleTracker
        {
            public long GridEntityId;
            public WeakReference<MyCubeGrid> GridRef;
            public int IdleSeconds;
            public bool IsForcedSleep;
        }

        private readonly ConcurrentDictionary<long, GridIdleTracker> _trackers = new();
        private readonly List<long> _cleanupBuffer = [];

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            _trackers.Clear();
            Log.Info("[RigidBodySleepModule] Initialized successfully.");
        }

        public void Update(ulong frameCounter)
        {
            if (!IsEnabled || _plugin?.Config == null)
            {
                return;
            }

            // Run evaluation every 60 frames (1 second)
            if (frameCounter % 60 != 0)
            {
                return;
            }

            EvaluateGrids(frameCounter);
        }

        private void EvaluateGrids(ulong frameCounter)
        {
            try
            {
                var config = _plugin.Config;
                float linThreshSq = config.SleepLinearVelocityThreshold * config.SleepLinearVelocityThreshold;
                float angThreshSq = config.SleepAngularVelocityThreshold * config.SleepAngularVelocityThreshold;
                int requiredSeconds = config.IdleSecondsBeforeSleep;

                int activeBodies = 0;
                int sleepingBodies = 0;

                _cleanupBuffer.Clear();

                // Scan all entities currently in scene
                var entities = MyEntities.GetEntities();
                foreach (var entity in entities)
                {
                    if (entity is MyCubeGrid grid)
                    {
                        if (grid.MarkedForClose || grid.IsStatic || grid.Physics?.RigidBody == null)
                        {
                            continue;
                        }

                        var rb = grid.Physics.RigidBody;
                        bool isActive = rb.IsActive;

                        if (isActive)
                        {
                            activeBodies++;
                        }
                        else
                        {
                            sleepingBodies++;
                        }

                        // Check if grid is unpiloted and at rest
                        bool isPiloted = IsGridPiloted(grid);
                        bool isSupported = IsGridSafelySupportedOrNotInGravity(grid);
                        float linSq = (float)grid.Physics.LinearVelocity.LengthSquared();
                        float angSq = (float)grid.Physics.AngularVelocity.LengthSquared();

                        if (!_trackers.TryGetValue(grid.EntityId, out var tracker))
                        {
                            tracker = new()
                            {
                                GridEntityId = grid.EntityId,
                                GridRef = new(grid),
                                IdleSeconds = 0,
                                IsForcedSleep = false
                            };
                            _trackers[grid.EntityId] = tracker;
                        }

                        if (!isPiloted && isSupported && linSq <= linThreshSq && angSq <= angThreshSq)
                        {
                            tracker.IdleSeconds++;
                            if (tracker.IdleSeconds >= requiredSeconds && isActive)
                            {
                                // Deactivate Havok rigid body into sleep mode
                                rb.Deactivate();
                                tracker.IsForcedSleep = true;
                                _plugin.Telemetry?.IncrementForcedSleepEvents();

                                if (config.EnableDebugLogging)
                                {
                                    Log.Debug($"[SleepManager] Put idle grid '{grid.DisplayName}' ({grid.BlocksCount} blocks) into Havok SLEEP.");
                                }
                            }
                        }
                        else
                        {
                            tracker.IdleSeconds = 0;
                            tracker.IsForcedSleep = false;
                        }
                    }
                }

                // Cold-path cleanup of stale trackers (entity eviction handles immediate removals)
                if (frameCounter % 300 == 0)
                {
                    foreach (var kvp in _trackers)
                    {
                        if (!kvp.Value.GridRef.TryGetTarget(out var g) || g.MarkedForClose || g.Closed)
                        {
                            _cleanupBuffer.Add(kvp.Key);
                        }
                    }

                    for (int i = 0; i < _cleanupBuffer.Count; i++)
                    {
                        _trackers.TryRemove(_cleanupBuffer[i], out _);
                    }
                    _cleanupBuffer.Clear();
                }

                _plugin?.Telemetry?.UpdateActiveAndSleepingRigidBodies(activeBodies, sleepingBodies);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[RigidBodySleepModule] Error during grid sleep evaluation!");
            }
        }

        public bool IsGridPiloted(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed) return false;
            var controlSystem = grid.GridSystems?.ControlSystem;
            if (controlSystem == null) return false;

            if (controlSystem.IsControlled) return true;

            var controller = controlSystem.GetShipController();
            if (controller != null)
            {
                if (controller.Pilot != null) return true;
                if (controller.ControllerInfo?.Controller != null) return true;
                if (controller is MyShipController sc && sc.IsAutopilotControlled) return true;
            }

            return false;
        }

        private bool IsGridSafelySupportedOrNotInGravity(MyCubeGrid grid)
        {
            if (grid?.Physics == null) return false;

            // If not in significant gravity, safe to sleep
            if (grid.Physics.Gravity.LengthSquared() < 0.1f) return true;

            // In gravity: Check if grid is parked/supported
            var wheelSystem = grid.GridSystems?.WheelSystem;
            if (wheelSystem != null && wheelSystem.WheelCount > 0 && wheelSystem.HandBrake)
            {
                return true; // Parked rover on ground
            }

            var landingSystem = grid.GridSystems?.LandingSystem;
            if (landingSystem != null && (landingSystem.IsParked || landingSystem.Locked == VRage.MyMultipleEnabledEnum.AllEnabled || landingSystem.Locked == VRage.MyMultipleEnabledEnum.Mixed))
            {
                return true; // Locked landing gears/feet
            }

            // Check if thrusters are actively firing against gravity (holding mid-air hover)
            foreach (var thruster in grid.GetFatBlocks<MyThrust>())
            {
                if (thruster.IsWorking && (thruster.ThrustForceLength > 0.01f || thruster.ThrustOverride > 0f))
                {
                    // Active thrust in gravity: do not freeze mid-air
                    return false;
                }
            }

            // If no thrusters fighting gravity and stationary, it's a wreck, debris, or landed grid at rest on voxels
            return true;
        }

        public bool IsGridSleeping(long gridEntityId)
        {
            return _trackers.TryGetValue(gridEntityId, out var tracker) && tracker.IsForcedSleep;
        }

        public void OnEntityAdded(MyEntity entity)
        {
        }

        public void OnEntityRemoved(MyEntity entity)
        {
            if (entity == null) return;
            _trackers.TryRemove(entity.EntityId, out _);
        }

        public void WakeGrid(MyCubeGrid grid, string reason = "External event")
        {
            if (grid?.Physics?.RigidBody == null || grid.MarkedForClose || grid.Closed) return;

            if (!grid.Physics.RigidBody.IsActive)
            {
                grid.Physics.RigidBody.Activate();
                if (_plugin?.Config != null && _plugin.Config.EnableDebugLogging)
                {
                    Log.Debug($"[SleepManager] Woke grid '{grid.DisplayName}' (Reason: {reason}).");
                }
            }

            if (_trackers.TryGetValue(grid.EntityId, out var tracker))
            {
                tracker.IdleSeconds = 0;
                tracker.IsForcedSleep = false;
            }
        }

        public int ForceSleepAllIdleGrids()
        {
            int sleptCount = 0;
            try
            {
                var entities = MyEntities.GetEntities();
                foreach (var entity in entities)
                {
                    if (entity is MyCubeGrid grid && !grid.IsStatic && !grid.MarkedForClose && grid.Physics?.RigidBody != null)
                    {
                        if (!IsGridPiloted(grid) && grid.Physics.RigidBody.IsActive)
                        {
                            grid.Physics.RigidBody.Deactivate();
                            sleptCount++;
                            _plugin.Telemetry?.IncrementForcedSleepEvents();
                        }
                    }
                }
                Log.Info($"[RigidBodySleepModule] Force-slept {sleptCount} idle dynamic grids.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[RigidBodySleepModule] Error in ForceSleepAllIdleGrids!");
            }
            return sleptCount;
        }

        public void Dispose()
        {
            _trackers.Clear();
            _cleanupBuffer.Clear();
            _plugin = null;
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
        }
    }
}

