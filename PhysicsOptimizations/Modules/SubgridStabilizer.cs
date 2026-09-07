using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Subgrid stabilizer: micro-dampens constraint solver oscillations on resting mechanical joints
    /// and masks small utility subgrids. Owns the mechanical detach/split broadphase-reset detours.
    /// </summary>
    public class SubgridStabilizer : IPhysicsOptimizer
    {
        private const string LogSource = "SubgridStabilizer";

        public string Name => "Subgrid Stabilizer";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnablePhysicsOptimizations && _plugin.Config.EnableSubgridStabilizer;

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
            Log.Info(LogSource, "Initialized successfully.");
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

                            var topGrid = mechBlock.TopGrid;
                            if (config.MaskSmallUtilitySubgrids && topGrid?.Physics?.RigidBody != null && grid.Physics?.RigidBody != null)
                            {
                                if (topGrid.BlocksCount <= config.MaskSmallUtilitySubgridMaxBlocks)
                                {
                                    TryApplySubgridCollisionMask(grid, topGrid, config.EnableDebugLogging);
                                }
                            }

                            if (!config.EnableSubgridStabilization)
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
                                            Log.Info(LogSource, $"Stabilized joint on '{grid.DisplayName}' / '{mechBlock.CustomName}'.");
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

                _plugin?.Telemetry?.UpdateSubgridTelemetry(_trackedJoints.Count, stabilizedCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Error during mechanical subgrid stabilization!");
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

        private static bool HasBlacklistedBlocks(MyCubeGrid topGrid)
        {
            foreach (var fat in topGrid.GetFatBlocks())
            {
                if (fat is Sandbox.ModAPI.IMyUserControllableGun ||
                    fat is Sandbox.ModAPI.IMyShipToolBase ||
                    fat is Sandbox.ModAPI.IMyWarhead)
                {
                    return true;
                }
            }
            return false;
        }

        private void TryApplySubgridCollisionMask(MyCubeGrid baseGrid, MyCubeGrid subGrid, bool debugLogging)
        {
            try
            {
                var subBody = subGrid.Physics?.RigidBody;
                if (subBody == null || HasBlacklistedBlocks(subGrid)) return;

                int systemId = baseGrid.Physics.HavokCollisionSystemID;
                if (systemId == 0) return;

                uint maskFilter = HkGroupFilter.CalcFilterInfo(subBody.Layer, systemId, 1, 3);
                if (subBody.GetCollisionFilterInfo() != maskFilter)
                {
                    subBody.SetCollisionFilterInfo(maskFilter);
                    MyPhysics.RefreshCollisionFilter(subGrid.Physics);
                    if (debugLogging)
                    {
                        Log.Info(LogSource, $"Applied collision mask to small utility subgrid '{subGrid.DisplayName}' ({subGrid.BlocksCount} blocks <= {_plugin.Config.MaskSmallUtilitySubgridMaxBlocks}) on '{baseGrid.DisplayName}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, LogSource, "Error applying subgrid collision mask.");
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

        /// <summary>
        /// Registers the mechanical detach/split broadphase-reset detours via Torch's <see cref="PatchContext"/>.
        /// </summary>
        /// <param name="ctx">Torch patch context.</param>
        public static void RegisterPatches(PatchContext ctx)
        {
            try
            {
                var detachMethod = typeof(MyMechanicalConnectionBlockBase).GetMethod(
                    "Detach",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    [typeof(MyCubeGrid), typeof(bool)],
                    null);

                if (detachMethod != null)
                {
                    var detachPostfix = typeof(SubgridStabilizer).GetMethod(nameof(DetachPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(detachMethod).Suffixes.Add(detachPostfix);
                    PatchConflictAudit.RegisterTarget(detachMethod);
                    Log.Info(LogSource, "Registered MyMechanicalConnectionBlockBase.Detach(MyCubeGrid, bool) hook.");
                }
                else
                {
                    Log.Warn(LogSource, "MyMechanicalConnectionBlockBase.Detach not found");
                }

                var splitMethod = typeof(MyCubeGrid).GetMethod(
                    "CreateSplit",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    [typeof(MyCubeGrid), typeof(System.Collections.Generic.List<MySlimBlock>), typeof(bool), typeof(long)],
                    null);

                if (splitMethod != null)
                {
                    var splitPostfix = typeof(SubgridStabilizer).GetMethod(nameof(CreateSplitPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(splitMethod).Suffixes.Add(splitPostfix);
                    PatchConflictAudit.RegisterTarget(splitMethod);
                    Log.Info(LogSource, "Registered MyCubeGrid.CreateSplit hook.");
                }
                else
                {
                    Log.Warn(LogSource, "MyCubeGrid.CreateSplit not found");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Critical error during subgrid hook registration!");
            }
        }

        /// <summary>Postfix after mechanical detach: refresh broadphase IDs so detached subgrids leave stale filter groups.</summary>
        public static void DetachPostfix(MyMechanicalConnectionBlockBase __instance, MyCubeGrid topGrid, bool updateGroups)
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.EnableSubgridStabilization || !config.MaskSmallUtilitySubgrids) return;

            if (__instance?.CubeGrid != null)
            {
                ResetSubgridBroadphase(__instance.CubeGrid);
            }
            if (topGrid != null)
            {
                ResetSubgridBroadphase(topGrid);
            }
        }

        /// <summary>Postfix after grid split: refresh broadphase IDs on both halves.</summary>
        public static void CreateSplitPostfix(MyCubeGrid originalGrid, MyCubeGrid __result)
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.EnableSubgridStabilization || !config.MaskSmallUtilitySubgrids) return;

            if (originalGrid != null)
            {
                ResetSubgridBroadphase(originalGrid);
            }
            if (__result != null)
            {
                ResetSubgridBroadphase(__result);
            }
        }

        private static readonly PropertyInfo HavokCollisionSystemIdProp =
            typeof(MyGridPhysics).GetProperty("HavokCollisionSystemID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static void ResetSubgridBroadphase(MyCubeGrid grid)
        {
            if (grid == null || grid.Physics == null || grid.Closed || grid.MarkedForClose) return;

            try
            {
                int newId = 0;
                var world = grid.Physics.HavokWorld;
                if (world != null)
                {
                    var filter = world.GetCollisionFilter();
                    newId = filter.GetNewSystemGroup();
                }

                if (newId != 0 && HavokCollisionSystemIdProp != null)
                {
                    HavokCollisionSystemIdProp.SetValue(grid.Physics, newId);
                    MyPhysics.RefreshCollisionFilter(grid.Physics);

                    if (PhysicsOptimizerPlugin.Instance?.Config?.EnableDebugLogging == true)
                    {
                        Log.Info(LogSource, $"Reset Havok broadphase collision ID for grid '{grid.DisplayName}' to {newId}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, LogSource, "Failed to reset HavokCollisionSystemID on subgrid detach/split.");
            }
        }
    }
}
