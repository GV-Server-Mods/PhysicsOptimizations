using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Havok;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using VRage.Game.Entity;
using VRageMath;
using GVK.PhysicsOptimizations.Config;

namespace GVK.PhysicsOptimizations.Modules
{
    public class WheelOptimizerModule : IPhysicsModule
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.Wheels");

        public string Name => "Wheel & Suspension Optimizer";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnableWheelOptimization;

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

        private readonly ConcurrentDictionary<long, RoverState> _trackedRovers = new ConcurrentDictionary<long, RoverState>();
        private readonly List<long> _removalBuffer = new List<long>();

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            _trackedRovers.Clear();
            Log.Info("[WheelOptimizerModule] Initialized successfully.");
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
                                if (_plugin.Config.EnableDebugLogging)
                                {
                                    Log.Debug($"[WheelOptimizer] Put suspension updates to SLEEP on parked rover '{grid.DisplayName}' ({wheelSystem.WheelCount} wheels).");
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

            // Clean up closed grids
            for (int i = 0; i < _removalBuffer.Count; i++)
            {
                _trackedRovers.TryRemove(_removalBuffer[i], out _);
            }

            if (_plugin.Telemetry != null)
            {
                _plugin.Telemetry.ParkedRoversAsleep = parkedRoversSleeping;
                _plugin.Telemetry.SleepingWheelsCount = sleepingWheels;
            }
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

                if (_plugin?.Config != null && _plugin.Config.EnableDebugLogging)
                {
                    Log.Debug($"[WheelOptimizer] Woke suspension updates on rover '{grid.DisplayName}' (Reason: {reason}).");
                }
            }
        }

        public void RegisterRover(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose) return;

            _trackedRovers.GetOrAdd(grid.EntityId, id => new RoverState
            {
                GridEntityId = id,
                GridRef = new WeakReference<MyCubeGrid>(grid),
                IsParked = grid.GridSystems?.WheelSystem?.HandBrake ?? false,
                StationaryTicks = 0,
                IsSuspensionAsleep = false,
                WheelCount = grid.GridSystems?.WheelSystem?.WheelCount ?? 0
            });
        }

        public void UnregisterRover(long gridEntityId)
        {
            _trackedRovers.TryRemove(gridEntityId, out _);
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
                    Log.Debug($"[WheelOptimizer] Applied wheel broadphase mask on '{cubeGrid.DisplayName}' / wheel '{topGrid.DisplayName}'.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[WheelOptimizer] Error optimizing wheel collision filter!");
            }
        }

        public void Dispose()
        {
            _trackedRovers.Clear();
            _removalBuffer.Clear();
            _plugin = null;
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
            // Config updated dynamically
        }
    }
}

