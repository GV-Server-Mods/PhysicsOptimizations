using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Rigid body sleep manager: deactivates Havok rigid bodies on unpiloted, at-rest dynamic grids.
    /// Also owns the cross-feature wake hooks (cockpit input, grid damage) that wake both this
    /// feature and WheelOptimizer suspension sleep.
    /// </summary>
    public class RigidBodySleep : IPhysicsModule
    {
        private const string LogSource = "RigidBodySleep";

        public string Name => "Rigid Body Sleep Manager";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnablePhysicsOptimizations && _plugin.Config.EnableRigidBodySleep;

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
            Log.Info(LogSource, "Initialized successfully.");
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
                                    Log.Info(LogSource, $"Put idle grid '{grid.DisplayName}' ({grid.BlocksCount} blocks) into Havok SLEEP.");
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

                int currentlyForcedSleep = 0;
                foreach (var kvp in _trackers)
                {
                    if (kvp.Value.IsForcedSleep)
                    {
                        currentlyForcedSleep++;
                    }
                }

                _plugin?.Telemetry?.UpdateActiveAndSleepingRigidBodies(activeBodies, sleepingBodies);
                _plugin?.Telemetry?.UpdateGridSleepTelemetry(_trackers.Count, currentlyForcedSleep);
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Error during grid sleep evaluation!");
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
                    Log.Info(LogSource, $"Woke grid '{grid.DisplayName}' (Reason: {reason}).");
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
                Log.Info(LogSource, $"Force-slept {sleptCount} idle dynamic grids.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Error in ForceSleepAllIdleGrids!");
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

        /// <summary>
        /// Registers the cockpit-input and grid-damage wake hooks. These wake BOTH this feature and
        /// WheelOptimizer suspension sleep (cross-feature wake by design - do not remove the
        /// WheelOptimizer.WakeRover calls).
        /// </summary>
        /// <param name="ctx">Torch patch context.</param>
        public static void RegisterPatches(PatchContext ctx)
        {
            try
            {
                var moveMethod = typeof(Sandbox.Game.Entities.MyShipController).GetMethod("MoveAndRotate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [typeof(VRageMath.Vector3), typeof(VRageMath.Vector2), typeof(float)], null);
                if (moveMethod != null)
                {
                    var postfixMethod = typeof(RigidBodySleep).GetMethod(nameof(MoveAndRotatePostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(moveMethod).Suffixes.Add(postfixMethod);
                    PatchConflictAudit.RegisterTarget(moveMethod);
                    Log.Info(LogSource, "Registered MyShipController.MoveAndRotate wake hook.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to register MyShipController wake hook!");
            }

            try
            {
                var damageMethod = typeof(MyDamageSystem).GetMethod(
                    "RaiseAfterDamageApplied",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    [typeof(object), typeof(MyDamageInformation)],
                    null);

                if (damageMethod != null)
                {
                    var postfixMethod = typeof(RigidBodySleep).GetMethod(nameof(RaiseAfterDamageAppliedPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(damageMethod).Suffixes.Add(postfixMethod);
                    PatchConflictAudit.RegisterTarget(damageMethod);
                    Log.Info(LogSource, "Registered MyDamageSystem.RaiseAfterDamageApplied wake hook.");
                }
                else
                {
                    Log.Error(LogSource, "Could not find MyDamageSystem.RaiseAfterDamageApplied method!");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to register MyDamageSystem wake hook!");
            }
        }

        /// <summary>
        /// Postfix after <see cref="Sandbox.Game.Entities.MyShipController.MoveAndRotate(VRageMath.Vector3, VRageMath.Vector2, float)"/>
        /// to wake sleeping suspensions and rigid bodies on pilot input.
        /// </summary>
        public static void MoveAndRotatePostfix(Sandbox.Game.Entities.MyShipController __instance, VRageMath.Vector3 moveIndicator, VRageMath.Vector2 rotationIndicator, float rollIndicator)
        {
            if (__instance == null || __instance.CubeGrid == null || __instance.CubeGrid.MarkedForClose || __instance.CubeGrid.Closed) return;

            // Check if player provided non-zero directional or rotational input
            if (moveIndicator == VRageMath.Vector3.Zero && rotationIndicator == VRageMath.Vector2.Zero && Math.Abs(rollIndicator) <= 0.001f) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin == null || plugin.Config == null || !plugin.Config.Enabled || !plugin.Config.EnablePhysicsOptimizations) return;

            long gridId = __instance.CubeGrid.EntityId;

            // Cross-feature wake: rover suspension sleep lives in WheelOptimizer
            if (plugin.WheelOptimizer != null && plugin.WheelOptimizer.IsGridSuspensionAsleep(gridId))
            {
                plugin.WheelOptimizer.WakeRover(gridId, "Cockpit movement input");
            }

            if (plugin.Sleep != null && plugin.Sleep.IsGridSleeping(gridId))
            {
                plugin.Sleep.WakeGrid(__instance.CubeGrid, "Cockpit movement input");
            }
        }

        /// <summary>
        /// Postfix after <see cref="MyDamageSystem.RaiseAfterDamageApplied(object, MyDamageInformation)"/>
        /// to wake sleeping suspensions and rigid bodies when damage is applied.
        /// </summary>
        public static void RaiseAfterDamageAppliedPostfix(object target, MyDamageInformation info)
        {
            if (info.Amount <= 0f || target == null) return;

            var slim = target as MySlimBlock;
            var grid = slim?.CubeGrid ?? target as MyCubeGrid;
            if (grid == null || grid.MarkedForClose || grid.Closed) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin?.Config != null && plugin.Config.Enabled && plugin.Config.EnablePhysicsOptimizations)
            {
                // Cross-feature wake: rover suspension sleep lives in WheelOptimizer
                plugin.WheelOptimizer?.WakeRover(grid.EntityId, "Grid took damage");
                plugin.Sleep?.WakeGrid(grid, "Grid took damage");
            }
        }
    }
}
