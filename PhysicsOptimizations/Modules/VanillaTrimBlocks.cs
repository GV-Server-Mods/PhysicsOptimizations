using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Sandbox.Game.Entities.Interfaces;
using Sandbox.Game.Weapons;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Weapons;
using Torch.Managers.PatchManager;
using VRage.Game.Entity.EntityComponents.Interfaces;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Block-level Vanilla Trim: vanilla turret logic that WeaponCore duplicates (target search, conveyor ammo
    /// pull). The prefix that skips a block update still runs the block's mod game logic.
    /// </summary>
    public static class VanillaTrimBlocks
    {
        private const string LogSource = "VanillaTrim";

        // Plain increments: turret timer ticks and block updates run on the game thread only.
        private static long _turretSearchesSkipped;
        private static long _turretAmmoPullsSkipped;
        private static Func<MyLargeTurretTargetingSystem, IMyTargetingReceiver> _getTargetReceiver;

        public static long TurretSearchesSkipped => Interlocked.Read(ref _turretSearchesSkipped);
        public static long TurretAmmoPullsSkipped => Interlocked.Read(ref _turretAmmoPullsSkipped);

        public static void ResetSessionStatus()
        {
            Interlocked.Exchange(ref _turretSearchesSkipped, 0);
            Interlocked.Exchange(ref _turretAmmoPullsSkipped, 0);
        }

        public static void RegisterPatches(PatchContext ctx)
        {
            VanillaTrim.RegisterPrefix(ctx,
                typeof(MyLargeConveyorTurretBase).GetMethod(nameof(MyLargeConveyorTurretBase.UpdateBeforeSimulation100), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null),
                typeof(VanillaTrimBlocks), nameof(Prefix_ConveyorTurretUpdateBeforeSimulation100),
                "MyLargeConveyorTurretBase.UpdateBeforeSimulation100");

            var receiverField = typeof(MyLargeTurretTargetingSystem).GetField("m_targetReceiver", BindingFlags.Instance | BindingFlags.NonPublic);
            if (receiverField == null)
            {
                Log.Warn(LogSource, "Could not find MyLargeTurretTargetingSystem.m_targetReceiver! WC turret targeting trim is inactive.");
                return;
            }

            var system = Expression.Parameter(typeof(MyLargeTurretTargetingSystem), "system");
            _getTargetReceiver = Expression.Lambda<Func<MyLargeTurretTargetingSystem, IMyTargetingReceiver>>(
                Expression.Field(system, receiverField), system).Compile();

            VanillaTrim.RegisterPrefix(ctx,
                typeof(MyLargeTurretTargetingSystem).GetMethod(nameof(MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null),
                typeof(VanillaTrimBlocks), nameof(Prefix_CheckAndSelectNearTargetsParallel),
                "MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel");
        }

        private static bool WcTurretTrimActive()
        {
            var cfg = PhysicsOptimizerPlugin.Instance?.Config;
            return cfg != null && cfg.Enabled && cfg.EnableVanillaTrim && cfg.TrimWcVanillaTurretLogic;
        }

        /// <summary>
        /// Prefix hook on MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel. Vanilla gates this search
        /// on working state, target flags and ammo, never on AI enabled, so it keeps running under WeaponCore.
        /// Skipped for vanilla turret types (WC-only server) and for Custom Turret Controllers with AI off (WC
        /// forces AIEnabled = false on every controller it takes over; with AI off vanilla never uses the result).
        /// Searchlights share this targeting system and are never skipped.
        /// </summary>
        public static bool Prefix_CheckAndSelectNearTargetsParallel(MyLargeTurretTargetingSystem __instance)
        {
            if (!WcTurretTrimActive()) return true;

            var owner = _getTargetReceiver?.Invoke(__instance);
            if (!(owner is MyLargeTurretBase) && !(owner is MyTurretControlBlock controller && !controller.AIEnabled)) return true;

            _turretSearchesSkipped++;
            return false;
        }

        /// <summary>
        /// Prefix hook on MyLargeConveyorTurretBase.UpdateBeforeSimulation100. Its only server work is the vanilla
        /// conveyor ammo pull, which WeaponCore duplicates with its own inventory system. Runs the block's
        /// 100-tick game logic (the rest of the base chain is client-only sound) and skips the pull.
        /// </summary>
        public static bool Prefix_ConveyorTurretUpdateBeforeSimulation100(MyLargeConveyorTurretBase __instance)
        {
            if (!WcTurretTrimActive()) return true;

            ((IMyGameLogicComponent)__instance.GameLogic)?.UpdateBeforeSimulation100(true);
            _turretAmmoPullsSkipped++;
            return false;
        }
    }
}
