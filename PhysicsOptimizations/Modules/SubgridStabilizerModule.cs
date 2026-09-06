using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using GVK.PhysicsOptimizations.Config;

namespace GVK.PhysicsOptimizations.Modules
{
    public class SubgridStabilizerModule : IPhysicsModule
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.Subgrids");

        public string Name => "Subgrid Constraint Stabilizer";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnableSubgridStabilization;

        private PhysicsOptimizerPlugin _plugin;

        public class SubgridJointState
        {
            public long BlockEntityId;
            public WeakReference<MyMechanicalConnectionBlockBase> BlockRef;
            public int RestFrames;
            public bool IsStabilized;
        }

        private readonly ConcurrentDictionary<long, SubgridJointState> _trackedJoints = new();
        private readonly List<long> _cleanupBuffer = [];

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            _trackedJoints.Clear();
            Log.Info("[SubgridStabilizerModule] Initialized successfully.");
        }

        public void Update(ulong frameCounter)
        {
            if (!IsEnabled || _plugin?.Config == null)
            {
                return;
            }

            // Run evaluation every 30 frames (~0.5s)
            if (frameCounter % 30 != 0)
            {
                return;
            }

            EvaluateMechanicalSubgrids(frameCounter);
        }

        private void EvaluateMechanicalSubgrids(ulong frameCounter)
        {
            try
            {
                var config = _plugin.Config;
                float velThreshSq = config.SubgridRestVelocityThreshold * config.SubgridRestVelocityThreshold;
                int requiredRestFrames = config.SubgridRestFramesThreshold;

                int stabilizedCount = 0;
                _cleanupBuffer.Clear();

                var entities = MyEntities.GetEntities();
                foreach (var entity in entities)
                {
                    if (entity is MyCubeGrid grid && !grid.MarkedForClose && !grid.Closed && grid.Physics != null)
                    {
                        foreach (var mechBlock in grid.GetFatBlocks<MyMechanicalConnectionBlockBase>())
                        {
                            if (mechBlock is MyMotorSuspension || mechBlock.MarkedForClose || mechBlock.Closed || mechBlock.TopGrid == null)
                            {
                                continue;
                            }

                            if (!_trackedJoints.TryGetValue(mechBlock.EntityId, out var state))
                            {
                                state = new()
                                {
                                    BlockEntityId = mechBlock.EntityId,
                                    BlockRef = new(mechBlock),
                                    RestFrames = 0,
                                    IsStabilized = false
                                };
                                _trackedJoints[mechBlock.EntityId] = state;
                            }

                            bool isCommanded = IsJointCommanded(mechBlock);
                            bool isStationary = IsJointStationary(mechBlock, velThreshSq);

                            if (!isCommanded && isStationary)
                            {
                                state.RestFrames += 30;
                                if (state.RestFrames >= requiredRestFrames)
                                {
                                    if (!state.IsStabilized)
                                    {
                                        state.IsStabilized = true;
                                        if (config.EnableDebugLogging)
                                        {
                                            Log.Debug($"[SubgridStabilizer] Stabilized joint on '{grid.DisplayName}' / '{mechBlock.CustomName}'.");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (state.IsStabilized)
                                {
                                    state.IsStabilized = false;
                                }
                                state.RestFrames = 0;
                            }

                            if (state.IsStabilized)
                            {
                                stabilizedCount++;

                                // Active Havok Constraint Micro-Dampening:
                                // Synchronize the subgrid velocities to the base grid to eliminate
                                // constraint solver micro-oscillations and Clang vibration loops.
                                var topGrid = mechBlock.TopGrid;
                                if (topGrid?.Physics?.RigidBody != null && grid.Physics?.RigidBody != null)
                                {
                                    var angDiff = topGrid.Physics.AngularVelocity - grid.Physics.AngularVelocity;
                                    var linDiff = topGrid.Physics.LinearVelocity - grid.Physics.LinearVelocity;
                                    if (angDiff.LengthSquared() > 0.000001f || linDiff.LengthSquared() > 0.000001f)
                                    {
                                        topGrid.Physics.AngularVelocity = grid.Physics.AngularVelocity;
                                        topGrid.Physics.LinearVelocity = grid.Physics.LinearVelocity;
                                    }
                                }
                            }
                        }
                    }
                }

                // Cold-path cleanup of stale trackers (entity eviction handles immediate removals)
                if (frameCounter % 300 == 0)
                {
                    foreach (var kvp in _trackedJoints)
                    {
                        if (!kvp.Value.BlockRef.TryGetTarget(out var b) || b.MarkedForClose || b.Closed)
                        {
                            _cleanupBuffer.Add(kvp.Key);
                        }
                    }

                    for (int i = 0; i < _cleanupBuffer.Count; i++)
                    {
                        _trackedJoints.TryRemove(_cleanupBuffer[i], out _);
                    }
                    _cleanupBuffer.Clear();
                }

                _plugin?.Telemetry?.UpdateStabilizedSubgridConstraints(stabilizedCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SubgridStabilizerModule] Error during mechanical subgrid stabilization!");
            }
        }

        public void OnEntityAdded(MyEntity entity)
        {
        }

        public void OnEntityRemoved(MyEntity entity)
        {
            if (entity == null) return;
            _trackedJoints.TryRemove(entity.EntityId, out _);
        }

        private bool IsJointCommanded(MyMechanicalConnectionBlockBase mechBlock)
        {
            if (mechBlock is IMyMotorStator rotor)
            {
                if (rotor.RotorLock) return false;
                return Math.Abs(rotor.TargetVelocityRPM) > 0.001f;
            }
            if (mechBlock is IMyPistonBase piston)
            {
                return Math.Abs(piston.Velocity) > 0.001f ||
                       piston.Status == Sandbox.ModAPI.Ingame.PistonStatus.Extending ||
                       piston.Status == Sandbox.ModAPI.Ingame.PistonStatus.Retracting;
            }
            return false;
        }

        private bool IsJointStationary(MyMechanicalConnectionBlockBase mechBlock, float velThreshSq)
        {
            var topGrid = mechBlock.TopGrid;
            var cubeGrid = mechBlock.CubeGrid;
            if (topGrid?.Physics == null || cubeGrid?.Physics == null)
            {
                return false;
            }

            var relLinVel = topGrid.Physics.LinearVelocity - cubeGrid.Physics.LinearVelocity;
            var relAngVel = topGrid.Physics.AngularVelocity - cubeGrid.Physics.AngularVelocity;

            return relLinVel.LengthSquared() < 0.01 && relAngVel.LengthSquared() < velThreshSq;
        }

        public void WakeJoint(long blockEntityId)
        {
            if (_trackedJoints.TryGetValue(blockEntityId, out var state))
            {
                state.IsStabilized = false;
                state.RestFrames = 0;
            }
        }

        public void Dispose()
        {
            _trackedJoints.Clear();
            _cleanupBuffer.Clear();
            _plugin = null;
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
        }
    }
}
