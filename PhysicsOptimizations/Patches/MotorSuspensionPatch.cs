using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using GVK.PhysicsOptimizations.Modules;

namespace GVK.PhysicsOptimizations.Patches
{
    /// <summary>
    /// Torch PatchManager patches for <see cref="MyMotorSuspension"/> to filter internal subgrid wheel collisions
    /// and throttle per-frame suspension updates when rovers are parked.
    /// </summary>
    public static class MotorSuspensionPatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.WheelPatch");

        /// <summary>
        /// Registers the CreateConstraint, CubeGrid_OnPhysicsChanged, and Update patches via Torch's <see cref="PatchContext"/>.
        /// </summary>
        /// <param name="ctx">Torch patch context.</param>
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

        /// <summary>
        /// Postfix invoked after <see cref="MyMotorSuspension"/> creates its Havok constraint to configure collision filtering.
        /// </summary>
        /// <param name="__instance">The motor suspension block instance.</param>
        /// <param name="__result">True if the constraint was successfully created.</param>
        public static void CreateConstraintPostfix(MyMotorSuspension __instance, bool __result)
        {
            if (!__result || __instance == null) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            plugin?.WheelOptimizer?.OptimizeWheelCollisionFilter(__instance);
        }

        /// <summary>
        /// Postfix invoked when the parent grid physics changes to re-apply optimized wheel collision filtering.
        /// </summary>
        /// <param name="__instance">The motor suspension block instance.</param>
        public static void PhysicsChangedPostfix(MyMotorSuspension __instance)
        {
            if (__instance == null) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            plugin?.WheelOptimizer?.OptimizeWheelCollisionFilter(__instance);
        }

        /// <summary>
        /// Prefix on <see cref="MyMotorSuspension.Update"/> that skips expensive per-frame suspension updates
        /// when a rover is detected as stationary and parked.
        /// </summary>
        /// <param name="__instance">The motor suspension block instance.</param>
        /// <returns>False to skip Keen's internal suspension update; true to proceed normally.</returns>
        public static bool UpdatePrefix(MyMotorSuspension __instance)
        {
            if (!WheelOptimizerModule.HasAnySleepingRovers) return true;
            if (__instance == null) return true;
            var cubeGrid = __instance.CubeGrid;
            if (cubeGrid == null) return true;

            if (WheelOptimizerModule.IsSuspensionSleepingFast(cubeGrid.EntityId))
            {
                // Skip expensive 60Hz per-frame suspension updates when rover is parked and motionless
                return false;
            }

            return true;
        }
    }
}

