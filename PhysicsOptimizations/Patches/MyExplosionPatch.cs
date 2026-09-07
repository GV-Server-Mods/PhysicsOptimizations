using System;
using System.Reflection;
using NLog;
using Sandbox.Game;
using Torch.Managers.PatchManager;
using PhysicsOptimizations.Config;
using PhysicsOptimizations.Utils;

namespace PhysicsOptimizations.Patches
{
    public static class MyExplosionPatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("PhysicsOptimizer.MyExplosionPatch");

        public static void Patch(PatchContext ctx)
        {
            try
            {
                var explosionType = typeof(MyExplosions).Assembly.GetType("Sandbox.Game.MyExplosion");
                if (explosionType == null)
                {
                    Log.Error("Could not find Sandbox.Game.MyExplosion type to patch!");
                    return;
                }

                var applyVoxelMethod = explosionType.GetMethod("ApplyExplosionOnVoxel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (applyVoxelMethod != null)
                {
                    var prefixApply = typeof(MyExplosionPatch).GetMethod(nameof(PrefixApplyExplosionOnVoxel), BindingFlags.Static | BindingFlags.NonPublic);
                    ctx.GetPattern(applyVoxelMethod).Prefixes.Add(prefixApply);
                    PatchConflictAudit.RegisterTarget(applyVoxelMethod);
                }

                var cutOutMethod = explosionType.GetMethod("CutOutVoxelMap", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (cutOutMethod != null)
                {
                    var prefixCutOut = typeof(MyExplosionPatch).GetMethod(nameof(PrefixCutOutVoxelMap), BindingFlags.Static | BindingFlags.NonPublic);
                    ctx.GetPattern(cutOutMethod).Prefixes.Add(prefixCutOut);
                    PatchConflictAudit.RegisterTarget(cutOutMethod);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to patch MyExplosion voxel cutouts!");
            }
        }

        private static bool PrefixApplyExplosionOnVoxel()
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config != null && config.Enabled && config.SuppressAllVoxelExplosionDamage)
            {
                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelCutoutsPrevented();
                return false;
            }
            return true;
        }

        private static bool PrefixCutOutVoxelMap()
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config != null && config.Enabled && config.SuppressAllVoxelExplosionDamage)
            {
                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelCutoutsPrevented();
                return false;
            }
            return true;
        }
    }
}
