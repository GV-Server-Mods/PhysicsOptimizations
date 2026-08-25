using System;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRageMath;

namespace GVK.PhysicsOptimizations.Patches
{
    [HarmonyPatch]
    public static class CockpitInputWakePatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.CockpitWake");

        public static void Patch(PatchContext ctx)
        {
            try
            {
                var moveMethod = typeof(MyShipController).GetMethod("MoveAndRotate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new Type[] { typeof(Vector3), typeof(Vector2), typeof(float) }, null);
                if (moveMethod != null)
                {
                    var postfixMethod = typeof(CockpitInputWakePatch).GetMethod(nameof(MoveAndRotatePostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(moveMethod).Suffixes.Add(postfixMethod);
                    Log.Info("[CockpitInputWakePatch] Registered MyShipController.MoveAndRotate patch.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[CockpitInputWakePatch] Failed to patch MyShipController!");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MyShipController), "MoveAndRotate", new Type[] { typeof(Vector3), typeof(Vector2), typeof(float) })]
        public static void MoveAndRotatePostfix(MyShipController __instance, Vector3 moveIndicator, Vector2 rotationIndicator, float roll)
        {
            if (__instance?.CubeGrid == null) return;

            // Check if player provided non-zero directional or rotational input
            if (moveIndicator != Vector3.Zero || rotationIndicator != Vector2.Zero || Math.Abs(roll) > 0.001f)
            {
                var plugin = PhysicsOptimizerPlugin.Instance;
                if (plugin != null)
                {
                    plugin.WheelOptimizer?.WakeRover(__instance.CubeGrid.EntityId, "Cockpit movement input");
                    plugin.SleepManager?.WakeGrid(__instance.CubeGrid, "Cockpit movement input");
                }
            }
        }
    }
}

