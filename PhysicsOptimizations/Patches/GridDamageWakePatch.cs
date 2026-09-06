using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;

namespace GVK.PhysicsOptimizations.Patches
{
    /// <summary>
    /// Torch PatchManager patch for <see cref="MyDamageSystem.RaiseAfterDamageApplied(object, MyDamageInformation)"/>
    /// to awaken sleeping physics rigid bodies and rover suspensions when grids or blocks sustain damage
    /// from weapons (vanilla &amp; WeaponCore), grinders, projectiles, explosions, or collisions.
    /// </summary>
    public static class GridDamageWakePatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.DamageWake");

        /// <summary>
        /// Registers the RaiseAfterDamageApplied postfix patch via Torch's <see cref="PatchContext"/>.
        /// </summary>
        /// <param name="ctx">Torch patch context.</param>
        public static void Patch(PatchContext ctx)
        {
            try
            {
                var damageMethod = typeof(MyDamageSystem).GetMethod(
                    "RaiseAfterDamageApplied",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    [typeof(object), typeof(MyDamageInformation)],
                    null);

                if (damageMethod != null)
                {
                    var postfixMethod = typeof(GridDamageWakePatch).GetMethod(
                        nameof(RaiseAfterDamageAppliedPostfix),
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    ctx.GetPattern(damageMethod).Suffixes.Add(postfixMethod);
                    Log.Info("[GridDamageWakePatch] Registered MyDamageSystem.RaiseAfterDamageApplied patch.");
                }
                else
                {
                    Log.Error("[GridDamageWakePatch] Could not find MyDamageSystem.RaiseAfterDamageApplied method!");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[GridDamageWakePatch] Failed to patch MyDamageSystem.RaiseAfterDamageApplied!");
            }
        }

        /// <summary>
        /// Postfix invoked after <see cref="MyDamageSystem.RaiseAfterDamageApplied(object, MyDamageInformation)"/>
        /// to awaken sleeping grids and suspensions when damage is applied.
        /// </summary>
        /// <param name="target">The target entity or block receiving damage (almost exclusively MySlimBlock in SE).</param>
        /// <param name="info">Damage context payload.</param>
        public static void RaiseAfterDamageAppliedPostfix(object target, MyDamageInformation info)
        {
            if (info.Amount <= 0f || target == null) return;

            var slim = target as MySlimBlock;
            var grid = slim?.CubeGrid ?? target as MyCubeGrid;
            if (grid == null || grid.MarkedForClose || grid.Closed) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin != null)
            {
                plugin.WheelOptimizer?.WakeRover(grid.EntityId, "Grid took damage");
                plugin.SleepManager?.WakeGrid(grid, "Grid took damage");
            }
        }
    }
}


