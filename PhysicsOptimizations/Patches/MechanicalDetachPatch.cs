using System;
using System.Reflection;
using Havok;
using NLog;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities;
using Sandbox.Engine.Physics;
using Torch.Managers.PatchManager;
using PhysicsOptimizations;
using Sandbox.Game.Entities.Blocks;
using PhysicsOptimizations.Utils;

namespace PhysicsOptimizations.Patches
{
    public static class MechanicalDetachPatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("PhysicsOptimizer.MechanicalDetach");

        public static void Patch(PatchContext ctx)
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
                    var detachPostfix = typeof(MechanicalDetachPatch).GetMethod(nameof(DetachPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(detachMethod).Suffixes.Add(detachPostfix);
                    PatchConflictAudit.RegisterTarget(detachMethod);
                    Log.Info("[MechanicalDetachPatch] Patched MyMechanicalConnectionBlockBase.Detach(MyCubeGrid, bool)");
                }
                else
                {
                    Log.Warn("[MechanicalDetachPatch] MyMechanicalConnectionBlockBase.Detach not found");
                }

                var splitMethod = typeof(MyCubeGrid).GetMethod(
                    "CreateSplit",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    [typeof(MyCubeGrid), typeof(System.Collections.Generic.List<MySlimBlock>), typeof(bool), typeof(long)],
                    null);

                if (splitMethod != null)
                {
                    var splitPostfix = typeof(MechanicalDetachPatch).GetMethod(nameof(CreateSplitPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(splitMethod).Suffixes.Add(splitPostfix);
                    PatchConflictAudit.RegisterTarget(splitMethod);
                    Log.Info("[MechanicalDetachPatch] Patched MyCubeGrid.CreateSplit");
                }
                else
                {
                    Log.Warn("[MechanicalDetachPatch] MyCubeGrid.CreateSplit not found");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MechanicalDetachPatch] Critical error during MechanicalDetachPatch registration!");
            }
        }

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
                        Log.Info($"[MechanicalDetachPatch] Reset Havok broadphase collision ID for grid '{grid.DisplayName}' to {newId}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "[MechanicalDetachPatch] Failed to reset HavokCollisionSystemID on subgrid detach/split.");
            }
        }
    }
}
