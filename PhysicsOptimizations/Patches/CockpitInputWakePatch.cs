using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRageMath;
using PhysicsOptimizations.Utils;

namespace PhysicsOptimizations.Patches
{
    /// <summary>
    /// Torch PatchManager patch for <see cref="MyShipController.MoveAndRotate(Vector3, Vector2, float)"/>
    /// to detect player movement/steering inputs and wake up sleeping rover suspensions and physics rigid bodies.
    /// </summary>
    public static class CockpitInputWakePatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.CockpitWake");

        /// <summary>
        /// Registers the MoveAndRotate postfix patch via Torch's <see cref="PatchContext"/>.
        /// </summary>
        /// <param name="ctx">Torch patch context.</param>
        public static void Patch(PatchContext ctx)
        {
            try
            {
                var moveMethod = typeof(MyShipController).GetMethod("MoveAndRotate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [typeof(Vector3), typeof(Vector2), typeof(float)], null);
                if (moveMethod != null)
                {
                    var postfixMethod = typeof(CockpitInputWakePatch).GetMethod(nameof(MoveAndRotatePostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(moveMethod).Suffixes.Add(postfixMethod);
                    PatchConflictAudit.RegisterTarget(moveMethod);
                    Log.Info("[CockpitInputWakePatch] Registered MyShipController.MoveAndRotate patch.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[CockpitInputWakePatch] Failed to patch MyShipController!");
            }
        }

        /// <summary>
        /// Postfix invoked after <see cref="MyShipController.MoveAndRotate(Vector3, Vector2, float)"/>
        /// to awaken any sleeping physics rigid bodies or disabled wheel suspension updates when pilot inputs are detected.
        /// </summary>
        /// <param name="__instance">The ship controller instance.</param>
        /// <param name="moveIndicator">Linear movement direction vector (X = left/right, Y = forward/backward, Z = up/down in SE local space).</param>
        /// <param name="rotationIndicator">Pitch and yaw rotation vector (X = pitch, Y = yaw).</param>
        /// <param name="rollIndicator">Roll rotation indicator.</param>
        public static void MoveAndRotatePostfix(MyShipController __instance, Vector3 moveIndicator, Vector2 rotationIndicator, float rollIndicator)
        {
            if (__instance == null || __instance.CubeGrid == null || __instance.CubeGrid.MarkedForClose || __instance.CubeGrid.Closed) return;

            // Check if player provided non-zero directional or rotational input
            if (moveIndicator == Vector3.Zero && rotationIndicator == Vector2.Zero && Math.Abs(rollIndicator) <= 0.001f) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin == null || plugin.Config == null || !plugin.Config.Enabled || !plugin.Config.EnablePhysicsOptimizations) return;

            long gridId = __instance.CubeGrid.EntityId;

            if (plugin.WheelOptimizer != null && plugin.WheelOptimizer.IsGridSuspensionAsleep(gridId))
            {
                plugin.WheelOptimizer.WakeRover(gridId, "Cockpit movement input");
            }

            if (plugin.SleepManager != null && plugin.SleepManager.IsGridSleeping(gridId))
            {
                plugin.SleepManager.WakeGrid(__instance.CubeGrid, "Cockpit movement input");
            }
        }
    }
}

