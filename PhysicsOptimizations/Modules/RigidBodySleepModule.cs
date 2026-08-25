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

        private readonly ConcurrentDictionary<long, GridIdleTracker> _trackers = new ConcurrentDictionary<long, GridIdleTracker>();
        private readonly List<long> _cleanupBuffer = new List<long>();

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

            EvaluateGrids();
        }

        private void EvaluateGrids()
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
                        float linSq = (float)grid.Physics.LinearVelocity.LengthSquared();
                        float angSq = (float)grid.Physics.AngularVelocity.LengthSquared();

                        var tracker = _trackers.GetOrAdd(grid.EntityId, id => new GridIdleTracker
                        {
                            GridEntityId = id,
                            GridRef = new WeakReference<MyCubeGrid>(grid),
                            IdleSeconds = 0,
                            IsForcedSleep = false
                        });

                        if (!isPiloted && linSq <= linThreshSq && angSq <= angThreshSq)
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

                // Periodic cleanup of stale trackers
                foreach (var kvp in _trackers)
                {
                    if (!kvp.Value.GridRef.TryGetTarget(out var g) || g.MarkedForClose)
                    {
                        _cleanupBuffer.Add(kvp.Key);
                    }
                }

                for (int i = 0; i < _cleanupBuffer.Count; i++)
                {
                    _trackers.TryRemove(_cleanupBuffer[i], out _);
                }

                if (_plugin.Telemetry != null)
                {
                    _plugin.Telemetry.ActiveRigidBodies = activeBodies;
                    _plugin.Telemetry.SleepingRigidBodies = sleepingBodies;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[RigidBodySleepModule] Error during grid sleep evaluation!");
            }
        }

        public bool IsGridPiloted(MyCubeGrid grid)
        {
            try
            {
                var controllers = grid.GridSystems?.ControlSystem;
                if (controllers == null) return false;

                var controller = controllers.GetShipController();
                return controller != null && controller.Pilot != null;
            }
            catch
            {
                return false;
            }
        }

        public void WakeGrid(MyCubeGrid grid, string reason = "External event")
        {
            if (grid?.Physics?.RigidBody == null || grid.MarkedForClose) return;

            try
            {
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
            catch (Exception ex)
            {
                Log.Error(ex, $"[RigidBodySleepModule] Error waking grid {grid.DisplayName}!");
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

