using System;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;

namespace GVK.PhysicsOptimizations.Patches
{
    [HarmonyPatch]
    public static class MotorSuspensionPatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.WheelPatch");

        public static void Patch(PatchContext ctx)
        {
            try
            {
                var createConstraintMethod = typeof(MyMotorSuspension).GetMethod("CreateConstraint", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (createConstraintMethod != null)
                {
                    var postfixMethod = typeof(MotorSuspensionPatch).GetMethod(nameof(CreateConstraintPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(createConstraintMethod).Suffixes.Add(postfixMethod);
                    Log.Info("[MotorSuspensionPatch] Registered CreateConstraint patch.");
                }

                var physicsChangedMethod = typeof(MyMotorSuspension).GetMethod("CubeGrid_OnPhysicsChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (physicsChangedMethod != null)
                {
                    var postfixMethod = typeof(MotorSuspensionPatch).GetMethod(nameof(PhysicsChangedPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(physicsChangedMethod).Suffixes.Add(postfixMethod);
                    Log.Info("[MotorSuspensionPatch] Registered CubeGrid_OnPhysicsChanged patch.");
                }

                var updateMethod = typeof(MyMotorSuspension).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (updateMethod != null)
                {
                    var prefixMethod = typeof(MotorSuspensionPatch).GetMethod(nameof(UpdatePrefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(updateMethod).Prefixes.Add(prefixMethod);
                    Log.Info("[MotorSuspensionPatch] Registered Update prefix patch (Parked Suspension Sleeping).");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MotorSuspensionPatch] Failed to patch MyMotorSuspension!");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MyMotorSuspension), "CreateConstraint")]
        public static void CreateConstraintPostfix(MyMotorSuspension __instance, bool __result)
        {
            if (!__result || __instance == null) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            plugin?.WheelOptimizer?.OptimizeWheelCollisionFilter(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MyMotorSuspension), "CubeGrid_OnPhysicsChanged")]
        public static void PhysicsChangedPostfix(MyMotorSuspension __instance)
        {
            if (__instance == null) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            plugin?.WheelOptimizer?.OptimizeWheelCollisionFilter(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MyMotorSuspension), "Update")]
        public static bool UpdatePrefix(MyMotorSuspension __instance)
        {
            if (__instance?.CubeGrid == null) return true;

            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin?.WheelOptimizer != null && plugin.WheelOptimizer.IsGridSuspensionAsleep(__instance.CubeGrid.EntityId))
            {
                // Skip expensive 60Hz per-frame suspension updates when rover is parked and motionless
                return false;
            }

            return true;
        }
    }
}

