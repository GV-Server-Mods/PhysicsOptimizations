using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
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

        private readonly ConcurrentDictionary<long, SubgridJointState> _trackedJoints = new ConcurrentDictionary<long, SubgridJointState>();
        private readonly List<long> _cleanupBuffer = new List<long>();

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

            EvaluateMechanicalSubgrids();
        }

        private void EvaluateMechanicalSubgrids()
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
                    if (entity is MyCubeGrid grid && !grid.MarkedForClose && grid.Physics != null)
                    {
                        var blocks = grid.GetBlocks();
                        foreach (var slim in blocks)
                        {
                            if (slim.FatBlock is MyMechanicalConnectionBlockBase mechBlock)
                            {
                                if (mechBlock.MarkedForClose || mechBlock.Closed || mechBlock.TopGrid == null)
                                {
                                    continue;
                                }

                                var state = _trackedJoints.GetOrAdd(mechBlock.EntityId, id => new SubgridJointState
                                {
                                    BlockEntityId = id,
                                    BlockRef = new WeakReference<MyMechanicalConnectionBlockBase>(mechBlock),
                                    RestFrames = 0,
                                    IsStabilized = false
                                });

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
                                }
                            }
                        }
                    }
                }

                // Cleanup dead references
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

                if (_plugin.Telemetry != null)
                {
                    _plugin.Telemetry.StabilizedSubgridConstraints = stabilizedCount;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SubgridStabilizerModule] Error during mechanical subgrid stabilization!");
            }
        }

        private bool IsJointCommanded(MyMechanicalConnectionBlockBase mechBlock)
        {
            if (mechBlock is MyMotorStator rotor)
            {
                return Math.Abs(rotor.TargetVelocityRPM) > 0.001f;
            }
            if (mechBlock is MyExtendedPistonBase piston)
            {
                return Math.Abs(piston.Velocity) > 0.001f;
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
