using System;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;
using VRageMath;

namespace GVK.PhysicsOptimizations.Patches
{
    [HarmonyPatch]
    public static class GridDamageWakePatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.DamageWake");

        public static void Patch(PatchContext ctx)
        {
            try
            {
                var damageMethod = typeof(MyCubeGrid).GetMethod("DoDamage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new Type[] { typeof(float), typeof(MyHitInfo), typeof(Vector3?), typeof(long) }, null);
                if (damageMethod != null)
                {
                    var postfixMethod = typeof(GridDamageWakePatch).GetMethod(nameof(DoDamagePostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(damageMethod).Suffixes.Add(postfixMethod);
                    Log.Info("[GridDamageWakePatch] Registered MyCubeGrid.DoDamage patch.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[GridDamageWakePatch] Failed to patch MyCubeGrid.DoDamage!");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MyCubeGrid), "DoDamage", new Type[] { typeof(float), typeof(MyHitInfo), typeof(Vector3?), typeof(long) })]
        public static void DoDamagePostfix(MyCubeGrid __instance, float damage)
        {
            if (__instance == null || damage <= 0f) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin != null)
            {
                plugin.WheelOptimizer?.WakeRover(__instance.EntityId, "Grid took damage");
                plugin.SleepManager?.WakeGrid(__instance, "Grid took damage");
            }
        }
    }
}

