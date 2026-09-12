using System;
using System.Globalization;
using System.Text;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace PhysicsOptimizer.Commands
{
    [Category("phys")]
    public class PhysicsOptimizerCommands : CommandModule
    {
        private PhysicsOptimizerPlugin Plugin => PhysicsOptimizerPlugin.Instance;

        [Command("status", "Shows current Physics Optimizer status and active settings.")]
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
            sb.AppendLine("=== [GVK Physics Optimizer Status v2.0.0] ===");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Plugin Master: {0}", cfg.Enabled));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Physics Optimizations: {0}", cfg.EnablePhysicsOptimizations));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Grid Defender: {0}", cfg.EnableGridDefender));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Debug Logging: {0} | Telemetry Interval: {1}s", cfg.EnableDebugLogging, cfg.ConsoleTelemetryIntervalSeconds));
            sb.AppendLine();
            sb.AppendLine("* Wheel Optimizer:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Enabled: {0} | Symmetrical Masking: {1}", cfg.EnableWheelOptimizer, cfg.EnableWheelCollisionFilter));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Parked Rover Sleep: {0} (Delay: {1:F1}s)", cfg.SleepParkedRovers, cfg.RoverSleepDelaySeconds));
            sb.AppendLine();
            sb.AppendLine("* Rigid Body Sleep:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Enabled: {0}", cfg.EnableRigidBodySleep));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Thresholds: Lin < {0:F2} m/s, Ang < {1:F3} rad/s for {2}s", cfg.SleepLinearVelocityThreshold, cfg.SleepAngularVelocityThreshold, cfg.IdleSecondsBeforeSleep));
            sb.AppendLine();
            sb.AppendLine("* Ore Merge:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Enabled: {0}", cfg.EnableOreMerge));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Auto-Merge: {0} (Radius: {1:F1}m, Interval: {2} ticks)", cfg.AutoMergeNearbyOre, cfg.OreMergeRadiusMeters, cfg.OreMergeIntervalTicks));
            sb.AppendLine();
            sb.AppendLine("* Subgrid Stabilizer:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Enabled: {0} | Stabilization: {1}", cfg.EnableSubgridStabilizer, cfg.EnableSubgridStabilization));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Mask Small Utility Subgrids: {0} (Max: {1} blocks)", cfg.MaskSmallUtilitySubgrids, cfg.MaskSmallUtilitySubgridMaxBlocks));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Rest Velocity: {0:F3} rad/s for {1} frames", cfg.SubgridRestVelocityThreshold, cfg.SubgridRestFramesThreshold));
            sb.AppendLine();
            sb.AppendLine("* Adaptive Collision:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Enabled: {0}", cfg.EnableAdaptiveCollision));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Large Grid Discrete Override: {0} (Min: {1} blks)", cfg.EnforceDiscreteLargeGrids, cfg.DiscreteLargeGridMinBlocks));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Small Grid Discrete Override: {0} (Min: {1} blks)", cfg.EnforceDiscreteSmallGrids, cfg.DiscreteSmallGridMinBlocks));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Speed Limits: Enabled={0} (Discrete < {1:F1} m/s | Continuous > {2:F1} m/s)", cfg.EnableSpeedThresholds, cfg.DiscreteCollisionSpeedThreshold, cfg.ContinuousCollisionSpeedThreshold));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Safety Overrides: Proximity Revert ({0}, {1:F0}m) | Terrain Alt Revert ({2}, < {3:F1}m)", cfg.RevertNearOtherDynamicGrids, cfg.DynamicGridProximityRevertDistanceMeters, cfg.EnableAltitudeTOIReversion, cfg.ContinuousAltitudeThreshold));
            sb.AppendLine();
            sb.AppendLine("* Grid Defender:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Enabled: {0} (Allowed Multiplier: {1:F2})", cfg.EnableGridDefender, cfg.DeformationMultiplier));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Ship Ramming: {0} | Voxel Crash: {1} | Stations: {2} | Subgrids: {3} | Debris: {4}", cfg.ProtectShipsAgainstRamming, cfg.ProtectShipsAgainstVoxels, cfg.ProtectStaticGrids, cfg.ProtectSubgrids, cfg.ProtectAgainstFloatingObjects));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Fallback Gates (Applicable={0}): Safe Harbor < {1:F1} m/s | Max Deform < {2:F1} m/s | Cooldown: {3} frames", cfg.IsSpeedGatesApplicable, cfg.MinDrivingVelocity, cfg.MaxDeformationVelocity, cfg.DeformationCooldownFrames));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Push-Apart: {0} (Base: {1:F2}m, Max: {2:F2}m, Threshold: {3} frames, Max Attempts: {4}, Station On Give-Up: {5}, Debug GPS: {6}, Min Impact: {7:F1} m/s, Exclude Wheels: {8})", cfg.EnablePushApart, cfg.PushApartDistance, cfg.PushApartMaxNudgeDistance, cfg.PushApartThreshold, cfg.PushApartMaxAttempts, cfg.ConvertToStaticOnPushApartGiveUp, cfg.EnablePushApartDebugDraw, cfg.PushApartMinImpactSpeed, cfg.ExcludeWheelSubgridsFromPushApart));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Voxel Normal Arbitrator: {0} (Debug GPS: {1})", cfg.EnableVoxelNormalArbitrator, cfg.EnableVoxelNormalArbitratorDebugDraw));
            sb.AppendLine();
            sb.AppendLine("* Player-Made Missiles (PMWs):");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - PMW Damage Allowed: {0} (Piloted Buggy Exemption: {1})", cfg.AllowMissileDamage, cfg.ExemptPilotedFromMissileStatus));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Small Grid PMW: {0}-{1} blks | Large Grid PMW: {2}-{3} blks | Min Vel: {4:F1} m/s", cfg.SmallGridMissileMinBlocks, cfg.SmallGridMissileMaxBlocks, cfg.LargeGridMissileMinBlocks, cfg.LargeGridMissileMaxBlocks, cfg.MissileMinVelocity));
            sb.AppendLine();
            sb.AppendLine("* Layered Armor Occlusion:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Enabled: {0} (Armor-Only Occlusion: {1})", cfg.EnableLayeredArmorOcclusion, cfg.ArmorOnlyOcclusion));
            sb.AppendLine();
            sb.AppendLine("* Anti-Clang & Kinetic Absorption System:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Enabled: {0} (Stop Spins: {1} | Arbitrator: {2} | Exclude Wheels: {3})", cfg.EnableAntiClang, cfg.StopClangSpinning, cfg.EnableVoxelNormalArbitrator, cfg.ExcludeWheelSubgridsFromAntiClang));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Impact Velocity Damping: {0:F2} | Vibration Threshold: {1} frames", cfg.ImpactVelocityDamping, cfg.AntiClangVibrationThreshold));
            sb.AppendLine();
            sb.AppendLine("* Active Push-Apart Separation:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Enabled: {0} (Distance: {1:F2}m after {2} frames)", cfg.EnablePushApart, cfg.PushApartDistance, cfg.PushApartThreshold));
            sb.AppendLine();
            sb.AppendLine("* Voxel & Terrain Protection:");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Suppress Voxel Cutouts: {0}", cfg.SuppressAllVoxelExplosionDamage));
            sb.AppendLine();

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
            sb.AppendLine("=== [Physics Optimizer Live Telemetry v2.0.0] ===");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Server Sim Speed: {0:F2}", t.ServerSimulationSpeed));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Active Rigid Bodies: {0:N0}", t.ActiveRigidBodies));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Sleeping Rigid Bodies: {0:N0}", t.SleepingRigidBodies));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Parked Rovers Asleep: {0:N0} / {1:N0} ({2:N0} / {3:N0} wheels)", t.ParkedRoversAsleep, t.TrackedRoversCount, t.SleepingWheelsCount, t.TotalRoverWheelsCount));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Dynamic Grids Asleep: {0:N0} / {1:N0} (Total Sleep Events: {2:N0})", t.GridsCurrentlyForcedSleep, t.TrackedGridsCount, t.ForcedSleepEventsTotal));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Subgrid Joints Stabilized: {0:N0} / {1:N0}", t.StabilizedSubgridConstraints, t.TrackedSubgridConstraints));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Discrete TOI Grids: {0:N0} / {1:N0} (Continuous: {2:N0})", t.DiscreteTOIGridsCount, t.TrackedTOIGridsCount, t.ContinuousTOIGridsCount));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Floating Objects: {0:N0} (Merged: {1:N0} stacks, {2:N0} eliminated)", t.ActiveFloatingObjectsCount, t.OreStacksMergedTotal, t.OreEntitiesEliminatedTotal));

            if (Plugin?.DefenseStats != null)
            {
                var d = Plugin.DefenseStats;
                sb.AppendLine();
                sb.AppendLine("=== [Collision Defense & Armor Telemetry] ===");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Evaluated Collisions: {0:N0} (Blocked: {1:N0} [{2:F1}%], Allowed: {3:N0})", d.TotalEvaluated, d.TotalBlocked, d.BlockRatio, d.TotalAllowed));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Low-Speed Blocked: {0:N0} | Voxel Crashes: {1:N0} | Ramming: {2:N0}", d.LowSpeedBlocked, d.VoxelCrashesBlocked, d.RammingBlocked));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - PMW Torpedo Impacts Allowed: {0:N0} | Piloted Buggy Saves: {1:N0}", d.MissileHitsAllowed, d.PilotedBuggySaves));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Layered Armor Hits Saved: {0:N0} | Dual-Sided Clamps Inverted: {1:N0}", d.ArmorHitsOccluded, d.VoxelNormalsInverted));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Clang Vibrations Arrested: {0:N0} | Grids Nudged Apart: {1:N0}", d.ClangVibrationsArrested, d.GridsSeparated));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  - Thruster Obstructions Burned: {0:N0} | Voxel Cutouts Prevented: {1:N0}", d.ThrusterObstructionsVaporized, d.VoxelCutoutsPrevented));
            }

            Context.Respond(sb.ToString());
        }

        [Command("sleepall", "Forces all eligible idle dynamic grids into Havok sleep mode.")]
        [Permission(MyPromoteLevel.Admin)]
        public void SleepAll()
        {
            if (Plugin?.RigidBodySleep == null)
            {
                Context.Respond("Rigid Body Sleep is not initialized.");
                return;
            }

            var cfg = Plugin.Config;
            if (!cfg.EnableRigidBodySleep)
            {
                Context.Respond("Rigid Body Sleep is disabled. Enable it with '!phys toggle sleep' before using !phys sleepall.");
                return;
            }

            int count = Plugin.RigidBodySleep.ForceSleepAllIdleGrids();
            Context.Respond(string.Format(CultureInfo.InvariantCulture, "[PhysicsOptimizer] Force-slept {0} idle dynamic grids.", count));
        }

        [Command("mergeore", "Executes an immediate proximity merge sweep on all floating ores and items.")]
        [Permission(MyPromoteLevel.Admin)]
        public void MergeOre()
        {
            if (Plugin?.OreMerge == null)
            {
                Context.Respond("Ore Optimizer is not initialized.");
                return;
            }

            int eliminated = Plugin.OreMerge.MergeProximityFloatingObjects();
            Context.Respond(string.Format(CultureInfo.InvariantCulture, "[PhysicsOptimizer] Proximity merge complete: eliminated {0} redundant floating entities.", eliminated));
        }

        [Command("toggle", "Toggles an individual optimization or feature. Usage: !phys toggle <all|wheels|mask|parkedsleep|sleep|ore|subgrids|utilitymask|toi|discretelarge|discretesmall|pmw|armor|anticlang|pushapart|normal|thruster|thrustermode|cutout|debug|telemetry|wheelanticlang|wheelpushapart|wheelstabilizer>")]
        [Permission(MyPromoteLevel.Admin)]
        public void Toggle(string featureName)
        {
            if (Plugin?.Config == null)
            {
                Context.Respond("PhysicsOptimizer is not initialized.");
                return;
            }

            var cfg = Plugin.Config;
            string stateMsg;

            switch (featureName.ToLowerInvariant())
            {
                case "all":
                    cfg.Enabled = !cfg.Enabled;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Master Plugin is now {0}.", cfg.Enabled ? "ENABLED" : "DISABLED");
                    break;
                case "wheels":
                case "wheel":
                case "wheeloptimizer":
                    cfg.EnableWheelOptimizer = !cfg.EnableWheelOptimizer;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Wheel Optimizer is now {0}.", cfg.EnableWheelOptimizer ? "ENABLED" : "DISABLED");
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
                case "rigidbodysleep":
                    cfg.EnableRigidBodySleep = !cfg.EnableRigidBodySleep;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Rigid Body Sleep is now {0}.", cfg.EnableRigidBodySleep ? "ENABLED" : "DISABLED");
                    break;
                case "ore":
                case "oremerge":
                    cfg.EnableOreMerge = !cfg.EnableOreMerge;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Ore Merge is now {0}.", cfg.EnableOreMerge ? "ENABLED" : "DISABLED");
                    break;
                case "subgridstabilizer":
                case "stabilizer":
                    cfg.EnableSubgridStabilizer = !cfg.EnableSubgridStabilizer;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Subgrid Stabilizer is now {0}.", cfg.EnableSubgridStabilizer ? "ENABLED" : "DISABLED");
                    break;
                case "subgrids":
                case "subgridstabilization":
                    cfg.EnableSubgridStabilization = !cfg.EnableSubgridStabilization;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Subgrid Joint Stabilization is now {0}.", cfg.EnableSubgridStabilization ? "ENABLED" : "DISABLED");
                    break;
                case "utilitymask":
                case "subgridmask":
                    cfg.MaskSmallUtilitySubgrids = !cfg.MaskSmallUtilitySubgrids;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Small Utility Subgrid Masking is now {0}.", cfg.MaskSmallUtilitySubgrids ? "ENABLED" : "DISABLED");
                    break;
                case "toi":
                case "adaptivecollision":
                    cfg.EnableAdaptiveCollision = !cfg.EnableAdaptiveCollision;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Adaptive Collision is now {0}.", cfg.EnableAdaptiveCollision ? "ENABLED" : "DISABLED");
                    break;
                case "defender":
                case "griddefender":
                    cfg.EnableGridDefender = !cfg.EnableGridDefender;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Grid Defender is now {0}.", cfg.EnableGridDefender ? "ENABLED" : "DISABLED");
                    break;
                case "debug":
                    cfg.EnableDebugLogging = !cfg.EnableDebugLogging;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Debug Logging is now {0}.", cfg.EnableDebugLogging ? "ENABLED" : "DISABLED");
                    break;
                case "telemetry":
                case "telem":
                    cfg.EnablePeriodicConsoleTelemetry = !cfg.EnablePeriodicConsoleTelemetry;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Periodic Console Telemetry is now {0}.", cfg.EnablePeriodicConsoleTelemetry ? "ENABLED" : "DISABLED");
                    break;
                case "pmw":
                    cfg.AllowMissileDamage = !cfg.AllowMissileDamage;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "PMW Missile Damage is now {0}.", cfg.AllowMissileDamage ? "ENABLED" : "DISABLED");
                    break;
                case "armor":
                    cfg.EnableLayeredArmorOcclusion = !cfg.EnableLayeredArmorOcclusion;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Layered Armor Occlusion is now {0}.", cfg.EnableLayeredArmorOcclusion ? "ENABLED" : "DISABLED");
                    break;
                case "anticlang":
                    cfg.EnableAntiClang = !cfg.EnableAntiClang;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Anti-Clang System is now {0}.", cfg.EnableAntiClang ? "ENABLED" : "DISABLED");
                    break;
                case "pushapart":
                case "push":
                    cfg.EnablePushApart = !cfg.EnablePushApart;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Active Push-Apart is now {0}.", cfg.EnablePushApart ? "ENABLED" : "DISABLED");
                    break;
                case "voxelarbitrator":
                case "normal":
                    cfg.EnableVoxelNormalArbitrator = !cfg.EnableVoxelNormalArbitrator;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Voxel Normal Force Arbitrator is now {0}.", cfg.EnableVoxelNormalArbitrator ? "ENABLED" : "DISABLED");
                    break;
                case "voxelarbdebug":
                case "arbdebug":
                    cfg.EnableVoxelNormalArbitratorDebugDraw = !cfg.EnableVoxelNormalArbitratorDebugDraw;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Voxel Arbitrator debug GPS markers are now {0}.", cfg.EnableVoxelNormalArbitratorDebugDraw ? "ENABLED" : "DISABLED");
                    break;
                case "thruster":
                    cfg.EnableThrusterClearance = !cfg.EnableThrusterClearance;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Thruster Clearance Optimizer is now {0}.", cfg.EnableThrusterClearance ? "ENABLED" : "DISABLED");
                    break;
                case "thrustermode":
                    cfg.ThrusterDamageMode = cfg.ThrusterDamageMode == PhysicsOptimizer.Config.ThrusterDamageMode.Optimized
                        ? PhysicsOptimizer.Config.ThrusterDamageMode.VanillaLike
                        : PhysicsOptimizer.Config.ThrusterDamageMode.Optimized;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Thruster Damage Mode is now {0}.", cfg.ThrusterDamageMode);
                    break;
                case "discretelarge":
                case "largediscrete":
                    cfg.EnforceDiscreteLargeGrids = !cfg.EnforceDiscreteLargeGrids;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Large Grid Discrete Physics Override is now {0}.", cfg.EnforceDiscreteLargeGrids ? "ENABLED" : "DISABLED");
                    break;
                case "discretesmall":
                case "smalldiscrete":
                    cfg.EnforceDiscreteSmallGrids = !cfg.EnforceDiscreteSmallGrids;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Small Grid Discrete Physics Override is now {0}.", cfg.EnforceDiscreteSmallGrids ? "ENABLED" : "DISABLED");
                    break;
                case "speedthresholds":
                case "speedtoi":
                    cfg.EnableSpeedThresholds = !cfg.EnableSpeedThresholds;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Dynamic Speed Thresholds are now {0}.", cfg.EnableSpeedThresholds ? "ENABLED" : "DISABLED");
                    break;
                case "cutout":
                    cfg.SuppressAllVoxelExplosionDamage = !cfg.SuppressAllVoxelExplosionDamage;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Voxel Cutout Explosion Suppression is now {0}.", cfg.SuppressAllVoxelExplosionDamage ? "ENABLED" : "DISABLED");
                    break;
                case "wheelanticlang":
                case "excludewheelanticlang":
                    cfg.ExcludeWheelSubgridsFromAntiClang = !cfg.ExcludeWheelSubgridsFromAntiClang;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Wheel subgrid exclusion from Anti-Clang is now {0}.", cfg.ExcludeWheelSubgridsFromAntiClang ? "ENABLED" : "DISABLED");
                    break;
                case "wheelpushapart":
                case "excludewheelpushapart":
                    cfg.ExcludeWheelSubgridsFromPushApart = !cfg.ExcludeWheelSubgridsFromPushApart;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Wheel subgrid exclusion from Push-Apart is now {0}.", cfg.ExcludeWheelSubgridsFromPushApart ? "ENABLED" : "DISABLED");
                    break;
                case "wheelstabilizer":
                case "excludewheelstabilizer":
                    cfg.ExcludeWheelSubgridsFromSubgridStabilizer = !cfg.ExcludeWheelSubgridsFromSubgridStabilizer;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Wheel subgrid exclusion from Subgrid Stabilizer is now {0}.", cfg.ExcludeWheelSubgridsFromSubgridStabilizer ? "ENABLED" : "DISABLED");
                    break;
                case "pushstation":
                case "pushapartstation":
                case "stationongiveup":
                    cfg.ConvertToStaticOnPushApartGiveUp = !cfg.ConvertToStaticOnPushApartGiveUp;
                    stateMsg = string.Format(CultureInfo.InvariantCulture, "Converting to static station on push-apart give up is now {0}.", cfg.ConvertToStaticOnPushApartGiveUp ? "ENABLED" : "DISABLED");
                    break;
                default:
                    Context.Respond(string.Format(CultureInfo.InvariantCulture, "Unknown setting '{0}'. Valid options: all, wheels, mask, parkedsleep, sleep, ore, subgridstabilizer, subgrids, utilitymask, toi, defender, speedthresholds, discretelarge, discretesmall, pmw, armor, anticlang, pushapart, pushstation, normal, voxelarbdebug, thruster, thrustermode, cutout, debug, telemetry.", featureName));
                    return;
            }

            Plugin.SaveConfig();
            Context.Respond(string.Format(CultureInfo.InvariantCulture, "[PhysicsOptimizer] {0}", stateMsg));
        }

        [Command("wakeall", "Wakes all dynamic grids and rovers currently sleeping.")]
        [Permission(MyPromoteLevel.Admin)]
        public void WakeAll()
        {
            int count = 0;
            try
            {
                var entities = Sandbox.Game.Entities.MyEntities.GetEntities();
                foreach (var entity in entities)
                {
                    if (entity is Sandbox.Game.Entities.MyCubeGrid grid && !grid.IsStatic && !grid.MarkedForClose && grid.Physics?.RigidBody != null)
                    {
                        if (!grid.Physics.RigidBody.IsActive)
                        {
                            Plugin?.RigidBodySleep?.WakeGrid(grid, "Admin wakeall command");
                            Plugin?.WheelOptimizer?.WakeRover(grid.EntityId, "Admin wakeall command");
                            count++;
                        }
                    }
                }
                Context.Respond(string.Format(CultureInfo.InvariantCulture, "[PhysicsOptimizer] Woke {0} sleeping dynamic grids/rovers.", count));
            }
            catch (Exception ex)
            {
                Context.Respond(string.Format(CultureInfo.InvariantCulture, "[PhysicsOptimizer] Error waking grids: {0}", ex.Message));
            }
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

        [Command("resetstats", "Resets the live telemetry gauges and defense counters.")]
        [Permission(MyPromoteLevel.Admin)]
        public void ResetStats()
        {
            Plugin?.Telemetry?.ResetLiveCounters();
            Plugin?.DefenseStats?.Reset();
            Context.Respond("[PhysicsOptimizer] Live telemetry and defense counters reset.");
        }
    }
}


