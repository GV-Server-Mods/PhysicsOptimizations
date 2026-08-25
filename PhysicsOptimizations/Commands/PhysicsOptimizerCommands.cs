using System;
using System.Text;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace GVK.PhysicsOptimizations.Commands
{
    [Category("phys")]
    public class PhysicsOptimizerCommands : CommandModule
    {
        private PhysicsOptimizerPlugin Plugin => PhysicsOptimizerPlugin.Instance;

        [Command("status", "Shows the current Physics Optimizer status and active modules.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Status()
        {
            if (Plugin?.Config == null)
            {
                Context.Respond("PhysicsOptimizer is not initialized.");
                return;
            }

            var cfg = Plugin.Config;
            var sb = new StringBuilder();
            sb.AppendLine("=== ⚡ GVK Physics Optimizer Status ===");
            sb.AppendLine($"Master Enabled: {cfg.Enabled}");
            sb.AppendLine($"Debug Logging: {cfg.EnableDebugLogging}");
            sb.AppendLine();
            sb.AppendLine("🏎️ MODULE 1 (Wheels & Suspension):");
            sb.AppendLine($"• Wheel Optimizer: {cfg.EnableWheelOptimization}");
            sb.AppendLine($"• Symmetrical Collision Masking: {cfg.EnableWheelCollisionFilter}");
            sb.AppendLine($"• Parked Rover Sleep: {cfg.SleepParkedRovers} (Delay: {cfg.RoverSleepDelaySeconds:F1}s)");
            sb.AppendLine();
            sb.AppendLine("💤 MODULE 2 (Rigid Body Sleeping):");
            sb.AppendLine($"• Aggressive Sleeping: {cfg.EnableAggressiveSleeping}");
            sb.AppendLine($"• Thresholds: Lin < {cfg.SleepLinearVelocityThreshold:F2} m/s, Ang < {cfg.SleepAngularVelocityThreshold:F3} rad/s for {cfg.IdleSecondsBeforeSleep}s");
            sb.AppendLine();
            sb.AppendLine("⛏️ MODULE 3 (Floating Objects & Ore):");
            sb.AppendLine($"• Ore Optimizer: {cfg.EnableFloatingObjectOptimizer}");
            sb.AppendLine($"• Auto-Merge: {cfg.AutoMergeNearbyOre} (Radius: {cfg.OreMergeRadiusMeters:F1}m, Interval: {cfg.OreMergeIntervalTicks} ticks)");
            sb.AppendLine();
            sb.AppendLine("🦾 MODULE 4 (Subgrid Constraints):");
            sb.AppendLine($"• Subgrid Stabilization: {cfg.EnableSubgridStabilization}");
            sb.AppendLine($"• Rest Velocity: {cfg.SubgridRestVelocityThreshold:F3} rad/s for {cfg.SubgridRestFramesThreshold} frames");
            sb.AppendLine();
            sb.AppendLine("🚀 MODULE 5 (Adaptive TOI / Collision):");
            sb.AppendLine($"• Adaptive TOI: {cfg.EnableAdaptiveTOI}");
            sb.AppendLine($"• Discrete Speed: < {cfg.DiscreteCollisionSpeedThreshold:F1} m/s | Continuous Speed: > {cfg.ContinuousCollisionSpeedThreshold:F1} m/s");

            Context.Respond(sb.ToString());
        }

        [Command("stats", "Displays live physics solver gauges and cumulative optimization counters.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Stats()
        {
            if (Plugin?.Telemetry == null)
            {
                Context.Respond("PhysicsOptimizer telemetry is not available.");
                return;
            }

            var t = Plugin.Telemetry;
            var sb = new StringBuilder();
            sb.AppendLine("=== ⚡ Physics Optimizer Live Telemetry ===");
            sb.AppendLine($"🔴 Active Rigid Bodies: {t.ActiveRigidBodies:N0}");
            sb.AppendLine($"🟢 Sleeping Rigid Bodies: {t.SleepingRigidBodies:N0}");
            sb.AppendLine($"🏎️ Parked Rovers Asleep: {t.ParkedRoversAsleep:N0} ({t.SleepingWheelsCount:N0} wheels)");
            sb.AppendLine($"⛏️ Ore Stacks Merged: {t.OreStacksMergedTotal:N0} ({t.OreEntitiesEliminatedTotal:N0} entities removed)");
            sb.AppendLine($"💤 Forced Sleep Events: {t.ForcedSleepEventsTotal:N0}");
            sb.AppendLine($"🦾 Stabilized Subgrid Joints: {t.StabilizedSubgridConstraints:N0}");
            sb.AppendLine($"🚀 Discrete TOI Grids: {t.DiscreteTOIGridsCount:N0}");

            Context.Respond(sb.ToString());
        }

        [Command("sleepall", "Forces all eligible idle dynamic grids into Havok sleep mode.")]
        [Permission(MyPromoteLevel.Admin)]
        public void SleepAll()
        {
            if (Plugin?.SleepManager == null)
            {
                Context.Respond("Sleep Manager module is not initialized.");
                return;
            }

            int count = Plugin.SleepManager.ForceSleepAllIdleGrids();
            Context.Respond($"[PhysicsOptimizer] Force-slept {count} idle dynamic grids.");
        }

        [Command("mergeore", "Executes an immediate proximity merge sweep on all floating ores and items.")]
        [Permission(MyPromoteLevel.Admin)]
        public void MergeOre()
        {
            if (Plugin?.OreOptimizer == null)
            {
                Context.Respond("Ore Optimizer module is not initialized.");
                return;
            }

            int eliminated = Plugin.OreOptimizer.MergeProximityFloatingObjects();
            Context.Respond($"[PhysicsOptimizer] Proximity merge complete: eliminated {eliminated} redundant floating entities.");
        }

        [Command("toggle", "Toggles an individual optimization module. Usage: !phys toggle <all|wheels|mask|parkedsleep|sleep|ore|subgrids|toi|debug>")]
        [Permission(MyPromoteLevel.Admin)]
        public void Toggle(string moduleName)
        {
            if (Plugin?.Config == null)
            {
                Context.Respond("PhysicsOptimizer is not initialized.");
                return;
            }

            var cfg = Plugin.Config;
            string stateMsg = "";

            switch (moduleName.ToLowerInvariant())
            {
                case "all":
                    cfg.Enabled = !cfg.Enabled;
                    stateMsg = $"Master Plugin is now {(cfg.Enabled ? "ENABLED" : "DISABLED")}.";
                    break;
                case "wheels":
                    cfg.EnableWheelOptimization = !cfg.EnableWheelOptimization;
                    stateMsg = $"Wheel Optimizer is now {(cfg.EnableWheelOptimization ? "ENABLED" : "DISABLED")}.";
                    break;
                case "mask":
                    cfg.EnableWheelCollisionFilter = !cfg.EnableWheelCollisionFilter;
                    stateMsg = $"Wheel Broadphase Masking is now {(cfg.EnableWheelCollisionFilter ? "ENABLED" : "DISABLED")}.";
                    break;
                case "parkedsleep":
                    cfg.SleepParkedRovers = !cfg.SleepParkedRovers;
                    stateMsg = $"Parked Rover Suspension Sleep is now {(cfg.SleepParkedRovers ? "ENABLED" : "DISABLED")}.";
                    break;
                case "sleep":
                    cfg.EnableAggressiveSleeping = !cfg.EnableAggressiveSleeping;
                    stateMsg = $"Aggressive Rigid Body Sleeping is now {(cfg.EnableAggressiveSleeping ? "ENABLED" : "DISABLED")}.";
                    break;
                case "ore":
                    cfg.EnableFloatingObjectOptimizer = !cfg.EnableFloatingObjectOptimizer;
                    stateMsg = $"Floating Object / Ore Optimizer is now {(cfg.EnableFloatingObjectOptimizer ? "ENABLED" : "DISABLED")}.";
                    break;
                case "subgrids":
                    cfg.EnableSubgridStabilization = !cfg.EnableSubgridStabilization;
                    stateMsg = $"Subgrid Constraint Stabilization is now {(cfg.EnableSubgridStabilization ? "ENABLED" : "DISABLED")}.";
                    break;
                case "toi":
                    cfg.EnableAdaptiveTOI = !cfg.EnableAdaptiveTOI;
                    stateMsg = $"Adaptive TOI Collision Pruning is now {(cfg.EnableAdaptiveTOI ? "ENABLED" : "DISABLED")}.";
                    break;
                case "debug":
                    cfg.EnableDebugLogging = !cfg.EnableDebugLogging;
                    stateMsg = $"Debug Logging is now {(cfg.EnableDebugLogging ? "ENABLED" : "DISABLED")}.";
                    break;
                default:
                    Context.Respond($"Unknown module '{moduleName}'. Valid options: all, wheels, mask, parkedsleep, sleep, ore, subgrids, toi, debug.");
                    return;
            }

            Plugin.SaveConfig();
            Context.Respond($"[PhysicsOptimizer] {stateMsg}");
        }

        [Command("reload", "Reloads the PhysicsOptimizer configuration from disk.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Reload()
        {
            if (Plugin == null)
            {
                Context.Respond("PhysicsOptimizer is not initialized.");
                return;
            }

            Plugin.LoadConfig();
            Context.Respond("[PhysicsOptimizer] Configuration reloaded from disk.");
        }

        [Command("resetstats", "Resets the live telemetry gauges.")]
        [Permission(MyPromoteLevel.Admin)]
        public void ResetStats()
        {
            Plugin?.Telemetry?.ResetLiveCounters();
            Context.Respond("[PhysicsOptimizer] Live telemetry counters reset.");
        }
    }
}

