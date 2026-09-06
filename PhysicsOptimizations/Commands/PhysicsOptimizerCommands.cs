using System;
using System.Globalization;
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
            sb.AppendLine("=== [GVK Physics Optimizer Status] ===");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Master Enabled: {0}", cfg.Enabled));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Debug Logging: {0}", cfg.EnableDebugLogging));
            sb.AppendLine();
            sb.AppendLine("[1] Wheels & Suspension:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Wheel Optimizer: {0}", cfg.EnableWheelOptimization));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Symmetrical Collision Masking: {0}", cfg.EnableWheelCollisionFilter));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Parked Rover Sleep: {0} (Delay: {1:F1}s)", cfg.SleepParkedRovers, cfg.RoverSleepDelaySeconds));
            sb.AppendLine();
            sb.AppendLine("[2] Rigid Body Sleeping:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Aggressive Sleeping: {0}", cfg.EnableAggressiveSleeping));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Thresholds: Lin < {0:F2} m/s, Ang < {1:F3} rad/s for {2}s", cfg.SleepLinearVelocityThreshold, cfg.SleepAngularVelocityThreshold, cfg.IdleSecondsBeforeSleep));
            sb.AppendLine();
            sb.AppendLine("[3] Floating Objects & Ore:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Ore Optimizer: {0}", cfg.EnableFloatingObjectOptimizer));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Auto-Merge: {0} (Radius: {1:F1}m, Interval: {2} ticks)", cfg.AutoMergeNearbyOre, cfg.OreMergeRadiusMeters, cfg.OreMergeIntervalTicks));
            sb.AppendLine();
            sb.AppendLine("[4] Subgrid Constraints:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Subgrid Stabilization: {0}", cfg.EnableSubgridStabilization));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Rest Velocity: {0:F3} rad/s for {1} frames", cfg.SubgridRestVelocityThreshold, cfg.SubgridRestFramesThreshold));
            sb.AppendLine();
            sb.AppendLine("[5] Adaptive TOI & Collision:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Adaptive TOI: {0}", cfg.EnableAdaptiveTOI));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Discrete Speed: < {0:F1} m/s | Continuous Speed: > {1:F1} m/s", cfg.DiscreteCollisionSpeedThreshold, cfg.ContinuousCollisionSpeedThreshold));

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
            sb.AppendLine("=== [Physics Optimizer Live Telemetry] ===");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Active Rigid Bodies: {0:N0}", t.ActiveRigidBodies));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Sleeping Rigid Bodies: {0:N0}", t.SleepingRigidBodies));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Parked Rovers Asleep: {0:N0} ({1:N0} wheels)", t.ParkedRoversAsleep, t.SleepingWheelsCount));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Ore Stacks Merged: {0:N0} ({1:N0} entities removed)", t.OreStacksMergedTotal, t.OreEntitiesEliminatedTotal));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Forced Sleep Events: {0:N0}", t.ForcedSleepEventsTotal));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Stabilized Subgrid Joints: {0:N0}", t.StabilizedSubgridConstraints));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Discrete TOI Grids: {0:N0}", t.DiscreteTOIGridsCount));

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
            Context.Respond(string.Format(CultureInfo.InvariantCulture, "[PhysicsOptimizer] Force-slept {0} idle dynamic grids.", count));
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
            Context.Respond(string.Format(CultureInfo.InvariantCulture, "[PhysicsOptimizer] Proximity merge complete: eliminated {0} redundant floating entities.", eliminated));
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
            string stateMsg;

            switch (moduleName.ToLowerInvariant())
            {
                case "all":
                    cfg.Enabled = !cfg.Enabled;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Master Plugin is now {0}.", cfg.Enabled ? "ENABLED" : "DISABLED");
                    break;
                case "wheels":
                    cfg.EnableWheelOptimization = !cfg.EnableWheelOptimization;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Wheel Optimizer is now {0}.", cfg.EnableWheelOptimization ? "ENABLED" : "DISABLED");
                    break;
                case "mask":
                    cfg.EnableWheelCollisionFilter = !cfg.EnableWheelCollisionFilter;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Wheel Broadphase Masking is now {0}.", cfg.EnableWheelCollisionFilter ? "ENABLED" : "DISABLED");
                    break;
                case "parkedsleep":
                    cfg.SleepParkedRovers = !cfg.SleepParkedRovers;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Parked Rover Suspension Sleep is now {0}.", cfg.SleepParkedRovers ? "ENABLED" : "DISABLED");
                    break;
                case "sleep":
                    cfg.EnableAggressiveSleeping = !cfg.EnableAggressiveSleeping;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Aggressive Rigid Body Sleeping is now {0}.", cfg.EnableAggressiveSleeping ? "ENABLED" : "DISABLED");
                    break;
                case "ore":
                    cfg.EnableFloatingObjectOptimizer = !cfg.EnableFloatingObjectOptimizer;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Floating Object / Ore Optimizer is now {0}.", cfg.EnableFloatingObjectOptimizer ? "ENABLED" : "DISABLED");
                    break;
                case "subgrids":
                    cfg.EnableSubgridStabilization = !cfg.EnableSubgridStabilization;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Subgrid Constraint Stabilization is now {0}.", cfg.EnableSubgridStabilization ? "ENABLED" : "DISABLED");
                    break;
                case "toi":
                    cfg.EnableAdaptiveTOI = !cfg.EnableAdaptiveTOI;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Adaptive TOI Collision Pruning is now {0}.", cfg.EnableAdaptiveTOI ? "ENABLED" : "DISABLED");
                    break;
                case "debug":
                    cfg.EnableDebugLogging = !cfg.EnableDebugLogging;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Debug Logging is now {0}.", cfg.EnableDebugLogging ? "ENABLED" : "DISABLED");
                    break;
                default:
                    Context.Respond(string.Format(CultureInfo.InvariantCulture, "Unknown module '{0}'. Valid options: all, wheels, mask, parkedsleep, sleep, ore, subgrids, toi, debug.", moduleName));
                    return;
            }

            Plugin.SaveConfig(async: true);
            Context.Respond(string.Format(CultureInfo.InvariantCulture, "[PhysicsOptimizer] {0}", stateMsg));
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


