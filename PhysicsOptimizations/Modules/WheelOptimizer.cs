using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRageMath;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Wheel &amp; suspension optimizer: trims redundant wheel/chassis collision queries and parks
    /// suspension updates (skipped via the Update prefix) on stationary rovers.
    /// </summary>
    public class WheelOptimizer : IPhysicsOptimizer
    {
        private const string LogSource = "WheelOptimizer";

        public string Name => "Wheel Optimizer";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnablePhysicsOptimizations && _plugin.Config.EnableWheelOptimizer;

        private PhysicsOptimizerPlugin _plugin;

        public class RoverState
        {
            public long GridEntityId;
            public WeakReference<MyCubeGrid> GridRef;
            public bool IsParked;
            public int StationaryTicks;
            public bool IsSuspensionAsleep;
            public int WheelCount;
        }

        public static volatile bool HasAnySleepingRovers;
        private static readonly ConcurrentDictionary<long, byte> _sleepingGridIds = new();

        public static bool IsSuspensionSleepingFast(long gridEntityId)
        {
            return HasAnySleepingRovers && _sleepingGridIds.ContainsKey(gridEntityId);
        }

        private readonly ConcurrentDictionary<long, RoverState> _trackedRovers = new();
        private readonly List<long> _removalBuffer = [];

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            _trackedRovers.Clear();
            _sleepingGridIds.Clear();
            HasAnySleepingRovers = false;

            DiscoverExistingRovers();
            Log.Info(LogSource, "Initialized successfully.");
        }

        public void DiscoverExistingRovers()
        {
            try
            {
                var entities = MyEntities.GetEntities();
                if (entities == null) return;

                foreach (var entity in entities)
                {
                    if (entity is MyCubeGrid grid && !grid.MarkedForClose && !grid.Closed)
                    {
                        TryRegisterRoverAndFilterWheels(grid);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Error discovering existing rovers!");
            }
        }

        public void OnEntityAdded(MyEntity entity)
        {
            if (entity is MyCubeGrid grid && !grid.MarkedForClose && !grid.Closed)
            {
                TryRegisterRoverAndFilterWheels(grid);
            }
        }

        private void TryRegisterRoverAndFilterWheels(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed) return;
            var wheelSystem = grid.GridSystems?.WheelSystem;
            if (wheelSystem != null && wheelSystem.WheelCount > 0)
            {
                RegisterRover(grid);
                if (IsEnabled && _plugin?.Config != null && _plugin.Config.EnableWheelCollisionFilter)
                {
                    foreach (var block in grid.GetFatBlocks<MyMotorSuspension>())
                    {
                        OptimizeWheelCollisionFilter(block);
                    }
                }
            }
        }

        public void Update(ulong frameCounter)
        {
            if (!IsEnabled || _plugin?.Config == null)
            {
                return;
            }

            // Run evaluation every 30 frames (~0.5s) to avoid per-tick overhead
            if (frameCounter % 30 != 0)
            {
                return;
            }

            int parkedRoversSleeping = 0;
            int sleepingWheels = 0;
            int totalWheels = 0;
            int requiredStationaryTicks = (int)(_plugin.Config.RoverSleepDelaySeconds * 60f);

            _removalBuffer.Clear();

            foreach (var kvp in _trackedRovers)
            {
                var state = kvp.Value;
                if (!state.GridRef.TryGetTarget(out var grid) || grid.MarkedForClose || grid.Physics == null)
                {
                    _removalBuffer.Add(kvp.Key);
                    continue;
                }

                var wheelSystem = grid.GridSystems?.WheelSystem;
                if (wheelSystem == null || wheelSystem.WheelCount == 0)
                {
                    _removalBuffer.Add(kvp.Key);
                    continue;
                }

                state.WheelCount = wheelSystem.WheelCount;
                totalWheels += state.WheelCount;
                bool isHandbrakeOn = wheelSystem.HandBrake;
                state.IsParked = isHandbrakeOn;

                if (_plugin.Config.SleepParkedRovers && isHandbrakeOn)
                {
                    float linSpeedSq = (float)grid.Physics.LinearVelocity.LengthSquared();
                    float angSpeedSq = (float)grid.Physics.AngularVelocity.LengthSquared();

                    // Handbrake engaged and nearly stationary (< 0.1 m/s, < 0.02 rad/s)
                    if (linSpeedSq < 0.01f && angSpeedSq < 0.0004f)
                    {
                        state.StationaryTicks += 30;
                        if (state.StationaryTicks >= requiredStationaryTicks)
                        {
                            if (!state.IsSuspensionAsleep)
                            {
                                state.IsSuspensionAsleep = true;
                                _sleepingGridIds[grid.EntityId] = 1;
                                HasAnySleepingRovers = true;

                                if (_plugin.Config.EnableDebugLogging)
                                {
                                    Log.Info(LogSource, $"Put suspension updates to SLEEP on parked rover '{grid.DisplayName}' ({wheelSystem.WheelCount} wheels).");
                                }
                            }
                        }
                    }
                    else
                    {
                        // Moving despite handbrake (sliding down steep Pertam dune or impacted)
                        if (state.IsSuspensionAsleep)
                        {
                            WakeRover(state, grid, "Velocity threshold exceeded");
                        }
                        state.StationaryTicks = 0;
                    }
                }
                else
                {
                    // Handbrake released
                    if (state.IsSuspensionAsleep)
                    {
                        WakeRover(state, grid, "Handbrake released");
                    }
                    state.StationaryTicks = 0;
                }

                if (state.IsSuspensionAsleep)
                {
                    parkedRoversSleeping++;
                    sleepingWheels += state.WheelCount;
                }
            }

            // Cleanup dead or non-rover grids immediately
            if (_removalBuffer.Count > 0)
            {
                for (int i = 0; i < _removalBuffer.Count; i++)
                {
                    UnregisterRover(_removalBuffer[i]);
                }
                _removalBuffer.Clear();
            }

            _plugin?.Telemetry?.UpdateRoverTelemetry(_trackedRovers.Count, parkedRoversSleeping, totalWheels, sleepingWheels);
        }

        public bool IsGridSuspensionAsleep(long gridEntityId)
        {
            if (!IsEnabled || !_plugin.Config.SleepParkedRovers)
            {
                return false;
            }

            return _trackedRovers.TryGetValue(gridEntityId, out var state) && state.IsSuspensionAsleep;
        }

        public void WakeRover(long gridEntityId, string reason = "External input")
        {
            if (_trackedRovers.TryGetValue(gridEntityId, out var state))
            {
                if (state.GridRef.TryGetTarget(out var grid))
                {
                    WakeRover(state, grid, reason);
                }
                else
                {
                    state.IsSuspensionAsleep = false;
                    state.StationaryTicks = 0;
                }
            }
        }

        private void WakeRover(RoverState state, MyCubeGrid grid, string reason)
        {
            if (state.IsSuspensionAsleep)
            {
                state.IsSuspensionAsleep = false;
                state.StationaryTicks = 0;
                _sleepingGridIds.TryRemove(grid.EntityId, out _);
                HasAnySleepingRovers = !_sleepingGridIds.IsEmpty;

                if (_plugin?.Config != null && _plugin.Config.EnableDebugLogging)
                {
                    Log.Info(LogSource, $"Woke suspension updates on rover '{grid.DisplayName}' (Reason: {reason}).");
                }
            }
        }

        public void RegisterRover(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed) return;

            if (!_trackedRovers.ContainsKey(grid.EntityId))
            {
                RoverState state = new()
                {
                    GridEntityId = grid.EntityId,
                    GridRef = new(grid),
                    IsParked = grid.GridSystems?.WheelSystem?.HandBrake ?? false,
                    StationaryTicks = 0,
                    IsSuspensionAsleep = false,
                    WheelCount = grid.GridSystems?.WheelSystem?.WheelCount ?? 0
                };
                _trackedRovers.TryAdd(grid.EntityId, state);
            }
        }

        public void OnEntityRemoved(MyEntity entity)
        {
            if (entity == null) return;
            UnregisterRover(entity.EntityId);
        }

        public void UnregisterRover(long gridEntityId)
        {
            _trackedRovers.TryRemove(gridEntityId, out _);
            _sleepingGridIds.TryRemove(gridEntityId, out _);
            HasAnySleepingRovers = !_sleepingGridIds.IsEmpty;
        }

        public void OptimizeWheelCollisionFilter(MyMotorSuspension suspension)
        {
            try
            {
                if (!IsEnabled || _plugin?.Config == null || !_plugin.Config.EnableWheelCollisionFilter)
                {
                    return;
                }

                var topGrid = suspension.TopGrid;
                var cubeGrid = suspension.CubeGrid;
                if (topGrid?.Physics?.RigidBody == null || cubeGrid?.Physics?.RigidBody == null)
                {
                    return;
                }

                var wheelBody = topGrid.Physics.RigidBody;
                var chassisBody = cubeGrid.Physics.RigidBody;
                int systemId = cubeGrid.Physics.HavokCollisionSystemID;

                // Symmetrical sub-system masking:
                // Havok's bitmask filter: subSystemDontCollideWith = 3 (bits 0 and 1) tells Havok to ignore BOTH sub-system 0 (chassis) and 1 (wheel).
                // This eliminates the redundant AABB compound shape queries against wheel well armor blocks!
                uint wheelFilter = HkGroupFilter.CalcFilterInfo(wheelBody.Layer, systemId, 1, 3);
                wheelBody.SetCollisionFilterInfo(wheelFilter);

                MyPhysics.RefreshCollisionFilter(topGrid.Physics);
                MyPhysics.RefreshCollisionFilter(cubeGrid.Physics);

                RegisterRover(cubeGrid);

                if (_plugin.Config.EnableDebugLogging)
                {
                    Log.Info(LogSource, $"Applied wheel broadphase mask on '{cubeGrid.DisplayName}' / wheel '{topGrid.DisplayName}'.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Error optimizing wheel collision filter!");
            }
        }

        public void Dispose()
        {
            _trackedRovers.Clear();
            _removalBuffer.Clear();
            _sleepingGridIds.Clear();
            HasAnySleepingRovers = false;
            _plugin = null;
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
            // Config updated dynamically
        }

        /// <summary>
        /// Registers the MyMotorSuspension detours via Torch's <see cref="PatchContext"/>.
        /// </summary>
        /// <param name="ctx">Torch patch context.</param>
        public static void RegisterPatches(PatchContext ctx)
        {
            try
            {
                var createConstraintMethod = typeof(MyMotorSuspension).GetMethod("CreateConstraint", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (createConstraintMethod != null)
                {
                    var postfixMethod = typeof(WheelOptimizer).GetMethod(nameof(CreateConstraintPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(createConstraintMethod).Suffixes.Add(postfixMethod);
                    PatchConflictAudit.RegisterTarget(createConstraintMethod);
                    Log.Info(LogSource, "Registered MyMotorSuspension.CreateConstraint hook.");
                }

                var physicsChangedMethod = typeof(MyMotorSuspension).GetMethod("CubeGrid_OnPhysicsChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (physicsChangedMethod != null)
                {
                    var postfixMethod = typeof(WheelOptimizer).GetMethod(nameof(PhysicsChangedPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(physicsChangedMethod).Suffixes.Add(postfixMethod);
                    PatchConflictAudit.RegisterTarget(physicsChangedMethod);
                    Log.Info(LogSource, "Registered MyMotorSuspension.CubeGrid_OnPhysicsChanged hook.");
                }

                var updateMethod = typeof(MyMotorSuspension).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (updateMethod != null)
                {
                    var prefixMethod = typeof(WheelOptimizer).GetMethod(nameof(UpdatePrefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(updateMethod).Prefixes.Add(prefixMethod);
                    PatchConflictAudit.RegisterTarget(updateMethod);
                    Log.Info(LogSource, "Registered MyMotorSuspension.Update prefix hook (Parked Suspension Sleeping).");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to register MyMotorSuspension hooks!");
            }
        }

        /// <summary>Postfix after <see cref="MyMotorSuspension"/> creates its Havok constraint to apply collision filtering.</summary>
        public static void CreateConstraintPostfix(MyMotorSuspension __instance, bool __result)
        {
            if (!__result || __instance == null) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            plugin?.WheelOptimizer?.OptimizeWheelCollisionFilter(__instance);
        }

        /// <summary>Postfix on parent grid physics changes to re-apply optimized wheel collision filtering.</summary>
        public static void PhysicsChangedPostfix(MyMotorSuspension __instance)
        {
            if (__instance == null) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            plugin?.WheelOptimizer?.OptimizeWheelCollisionFilter(__instance);
        }

        /// <summary>
        /// Prefix on <see cref="MyMotorSuspension.Update"/> that skips per-frame suspension updates when parked.
        /// </summary>
        /// <returns>False to skip Keen's internal suspension update; true to proceed normally.</returns>
        public static bool UpdatePrefix(MyMotorSuspension __instance)
        {
            if (!HasAnySleepingRovers) return true;
            if (__instance == null) return true;
            var cubeGrid = __instance.CubeGrid;
            if (cubeGrid == null) return true;

            if (IsSuspensionSleepingFast(cubeGrid.EntityId))
            {
                // Skip expensive 60Hz per-frame suspension updates when rover is parked and motionless
                return false;
            }

            return true;
        }
    }
}
