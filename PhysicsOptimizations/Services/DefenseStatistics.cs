using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Torch;

namespace PhysicsOptimizer.Services
{
    /// <summary>
    /// Thread-safe telemetry tracker for real-time collision evaluations, blocked crashes, allowed missile hits, and anti-clang interventions.
    /// </summary>
    public class DefenseStatistics : ViewModel
    {
        private long _totalEvaluated;
        private long _totalBlocked;
        private long _totalAllowed;
        private long _missileHitsAllowed;
        private long _rammingBlocked;
        private long _voxelCrashesBlocked;
        private long _subgridCollisionsBlocked;
        private long _lowSpeedBlocked;
        private long _stationBlocked;
        private long _debrisBlocked;
        private long _cooldownThrottled;
        private long _gridsSeparated;
        private long _armorHitsOccluded;
        private long _voxelNormalsInverted;
        private long _thrusterObstructionsVaporized;
        private long _voxelCutoutsPrevented;
        private long _pilotedBuggySaves;
        private long _playerRescuesExecuted;
        private long _adminRescuesExecuted;

        // Hot-path invocation counters (surfaced as per-second rates in the console heartbeat).
        private long _contactCallbacks;
        private long _voxelArbitratorRaycasts;
        private long _pushApartActionsExecuted;

        // Computed live rates for UI binding
        private double _contactCallbacksPerSecond;
        private double _voxelRaycastsPerSecond;
        private double _pushApartRatePerSecond;

        // GC Telemetry for UI binding
        private int _gcGen0Count;
        private int _gcGen1Count;
        private int _gcGen2Count;
        private double _managedMemoryMb;

        // Hourly digest snapshots
        private long _hourlySnapshotTotalBlocked;
        private long _hourlySnapshotGridsSeparated;
        private long _hourlySnapshotPmwHits;

        /// <summary>
        /// Total number of collision events processed by the defense engine.
        /// </summary>
        public long TotalEvaluated => Interlocked.Read(ref _totalEvaluated);

        /// <summary>
        /// Total number of collision deformations blocked by the engine.
        /// </summary>
        public long TotalBlocked => Interlocked.Read(ref _totalBlocked);

        /// <summary>
        /// Total number of collision deformations allowed (e.g. missiles).
        /// </summary>
        public long TotalAllowed => Interlocked.Read(ref _totalAllowed);

        /// <summary>
        /// Total number of player-made missile impacts allowed through the gate.
        /// </summary>
        public long MissileHitsAllowed => Interlocked.Read(ref _missileHitsAllowed);

        /// <summary>
        /// Total ship-on-ship ramming deformations blocked.
        /// </summary>
        public long RammingBlocked => Interlocked.Read(ref _rammingBlocked);

        /// <summary>
        /// Total ship-vs-voxel (planet/asteroid) crash deformations blocked.
        /// </summary>
        public long VoxelCrashesBlocked => Interlocked.Read(ref _voxelCrashesBlocked);

        /// <summary>
        /// Total mechanical subgrid self-deformations blocked.
        /// </summary>
        public long SubgridCollisionsBlocked => Interlocked.Read(ref _subgridCollisionsBlocked);

        /// <summary>
        /// Total low-speed driving and safe docking bumps blocked (below MinDrivingVelocity).
        /// </summary>
        public long LowSpeedBlocked => Interlocked.Read(ref _lowSpeedBlocked);

        /// <summary>
        /// Total non-missile collisions with static stations blocked.
        /// </summary>
        public long StationBlocked => Interlocked.Read(ref _stationBlocked);

        /// <summary>
        /// Total loose floating debris and ore impacts blocked.
        /// </summary>
        public long DebrisBlocked => Interlocked.Read(ref _debrisBlocked);

        /// <summary>
        /// Total deformations throttled by cooldown frames.
        /// </summary>
        public long CooldownThrottled => Interlocked.Read(ref _cooldownThrottled);

        /// <summary>
        /// Total persistent stuck/phased grid pairs separated via Push-Apart.
        /// </summary>
        public long GridsSeparated => Interlocked.Read(ref _gridsSeparated);

        /// <summary>
        /// Total player self-rescues executed (!unstuckit).
        /// </summary>
        public long PlayerRescuesExecuted => Interlocked.Read(ref _playerRescuesExecuted);

        /// <summary>
        /// Total admin crosshairs/forced rescues executed.
        /// </summary>
        public long AdminRescuesExecuted => Interlocked.Read(ref _adminRescuesExecuted);

        /// <summary>
        /// Total vehicle rescues executed across both players and admins.
        /// </summary>
        public long TotalRescuesExecuted => PlayerRescuesExecuted + AdminRescuesExecuted;

        /// <summary>
        /// Total internal blocks saved by layered armor occlusion.
        /// </summary>
        public long ArmorHitsOccluded => Interlocked.Read(ref _armorHitsOccluded);

        /// <summary>
        /// Total dual-sided voxel clamp inverted normals.
        /// </summary>
        public long VoxelNormalsInverted => Interlocked.Read(ref _voxelNormalsInverted);

        /// <summary>
        /// Total thruster obstructions vaporized on own construct.
        /// </summary>
        public long ThrusterObstructionsVaporized => Interlocked.Read(ref _thrusterObstructionsVaporized);

        /// <summary>
        /// Total explosive voxel crater cutouts suppressed.
        /// </summary>
        public long VoxelCutoutsPrevented => Interlocked.Read(ref _voxelCutoutsPrevented);

        /// <summary>
        /// Total piloted vehicle collision damage saves.
        /// </summary>
        public long PilotedBuggySaves => Interlocked.Read(ref _pilotedBuggySaves);

        private Config.PhysicsOptimizerConfig Config => PhysicsOptimizerPlugin.Instance?.Config;

        public bool IsGridDefenderEnabled => Config != null && Config.Enabled && Config.EnableGridDefender;
        public bool IsPmwEnabled => IsGridDefenderEnabled && Config.AllowMissileDamage;
        public bool IsArmorOcclusionEnabled => IsGridDefenderEnabled && Config.EnableLayeredArmorOcclusion;
        public bool IsPushApartEnabled => IsGridDefenderEnabled && Config.EnablePushApart;
        public bool IsVoxelArbitratorEnabled => IsGridDefenderEnabled && Config.EnableVoxelNormalArbitrator;
        public bool IsThrusterClearanceEnabled => Config != null && Config.Enabled && Config.EnableThrusterClearance;
        public bool IsVoxelCutoutSuppressionEnabled => Config != null && Config.Enabled && Config.SuppressAllVoxelExplosionDamage;
        public bool IsPlayerRescueEnabled => Config != null && Config.Enabled && Config.EnablePlayerRescue;

        public string PlayerRescuesDisplay => IsPlayerRescueEnabled
            ? $"{PlayerRescuesExecuted:N0}"
            : "[Disabled]";
        public string PlayerRescuesColor => IsPlayerRescueEnabled ? "#38BDF8" : "#6B7280";

        public string AdminRescuesDisplay => $"{AdminRescuesExecuted:N0}";
        public string AdminRescuesColor => "#38BDF8";

        public string TotalRescuesDisplay => $"{TotalRescuesExecuted:N0}";

        // Formatted display strings for Card 3 & Hot Paths
        // Formatted display strings and colors for Card 3 & Hot Paths
        public string EvaluatedCollisionsDisplay => IsGridDefenderEnabled
            ? $"{TotalEvaluated:N0}"
            : "[Disabled]";
        public string EvaluatedCollisionsColor => IsGridDefenderEnabled ? "#E0E0E0" : "#6B7280";

        public string TotalBlockedDisplay => IsGridDefenderEnabled
            ? $"{TotalBlocked:N0}"
            : "[Disabled]";

        public string TotalBlockedRatioDisplay => IsGridDefenderEnabled
            ? $"({BlockRatio:F1}%)"
            : "";
        public string TotalBlockedColor => IsGridDefenderEnabled ? "#10B981" : "#6B7280";

        public string MissileHitsAllowedDisplay => IsPmwEnabled
            ? $"{MissileHitsAllowed:N0}"
            : "[Disabled]";
        public string MissileHitsColor => IsPmwEnabled ? "#FB7185" : "#6B7280";

        public string ArmorHitsOccludedDisplay => IsArmorOcclusionEnabled
            ? $"{ArmorHitsOccluded:N0}"
            : "[Disabled]";
        public string ArmorHitsColor => IsArmorOcclusionEnabled ? "#10B981" : "#6B7280";

        public string GridsSeparatedDisplay => IsPushApartEnabled
            ? $"{GridsSeparated:N0}"
            : "[Disabled]";

        public string GridsSeparatedPushesDisplay => IsPushApartEnabled
            ? $"({PushApartActionsExecuted:N0} pushes)"
            : "";
        public string GridsSeparatedColor => IsPushApartEnabled ? "#10B981" : "#6B7280";

        public string VoxelNormalsInvertedDisplay => IsVoxelArbitratorEnabled
            ? $"{VoxelNormalsInverted:N0}"
            : "[Disabled]";
        public string VoxelNormalsColor => IsVoxelArbitratorEnabled ? "#E0E0E0" : "#6B7280";

        public string ThrusterObstructionsDisplay => IsThrusterClearanceEnabled
            ? $"{ThrusterObstructionsVaporized:N0}"
            : "[Disabled]";
        public string ThrusterObstructionsColor => IsThrusterClearanceEnabled ? "#E0E0E0" : "#6B7280";

        public string VoxelCutoutsPreventedDisplay => IsVoxelCutoutSuppressionEnabled
            ? $"{VoxelCutoutsPrevented:N0}"
            : "[Disabled]";
        public string VoxelCutoutsColor => IsVoxelCutoutSuppressionEnabled ? "#E0E0E0" : "#6B7280";

        public string CachesDisplay => IsVoxelArbitratorEnabled
            ? $"{VoxelBucketsCached:N0} carved regions | {TerrainCacheEntries:N0} macro terrain"
            : "[Disabled]";
        public string CachesColor => IsVoxelArbitratorEnabled ? "#9CA3AF" : "#6B7280";

        public string ContactCallbacksDisplay => IsGridDefenderEnabled
            ? $"{ContactCallbacksPerSecond:F0}/s"
            : "[Disabled]";
        public string ContactCallbacksColor => IsGridDefenderEnabled ? "#E0E0E0" : "#6B7280";

        public string VoxelRaycastsDisplay => IsVoxelArbitratorEnabled
            ? $"{VoxelRaycastsPerSecond:F1}/s"
            : "[Disabled]";
        public string VoxelRaycastsColor => IsVoxelArbitratorEnabled ? "#E0E0E0" : "#6B7280";

        /// <summary>
        /// Percentage of evaluated collisions that were blocked (0.0% to 100.0%).
        /// </summary>
        public double BlockRatio
        {
            get
            {
                long eval = TotalEvaluated;
                return eval > 0 ? (double)TotalBlocked / eval * 100.0 : 0.0;
            }
        }

        /// <summary>
        /// Human-readable summary of active constructs triggering continuous deformation / clang crashes.
        /// </summary>
        public string ActiveCollisionOffendersSummary
        {
            get
            {
                var offenders = Modules.GridDefender.GetActiveClangers(minRate: 5, maxResults: 3);
                if (offenders == null || offenders.Count == 0) return "None (Clean)";
                return string.Join(", ", offenders.Select(o => $"'{o.Name}' ({o.Rate}/s)"));
            }
        }

        /// <summary>
        /// Increments the total evaluated collisions counter.
        /// </summary>
        public void IncrementEvaluated()
        {
            Interlocked.Increment(ref _totalEvaluated);
        }

        /// <summary>
        /// Increments blocked crash counters with granular category tracking.
        /// </summary>
        public void IncrementBlocked(
            bool isRamming = false,
            bool isVoxel = false,
            bool isSubgrid = false,
            bool isCooldown = false,
            bool isLowSpeed = false,
            bool isStation = false,
            bool isDebris = false)
        {
            Interlocked.Increment(ref _totalBlocked);
            if (isRamming) Interlocked.Increment(ref _rammingBlocked);
            if (isVoxel) Interlocked.Increment(ref _voxelCrashesBlocked);
            if (isSubgrid) Interlocked.Increment(ref _subgridCollisionsBlocked);
            if (isCooldown) Interlocked.Increment(ref _cooldownThrottled);
            if (isLowSpeed) Interlocked.Increment(ref _lowSpeedBlocked);
            if (isStation) Interlocked.Increment(ref _stationBlocked);
            if (isDebris) Interlocked.Increment(ref _debrisBlocked);
        }

        /// <summary>
        /// Increments allowed deformation counters.
        /// </summary>
        public void IncrementAllowed(bool isMissile = false)
        {
            Interlocked.Increment(ref _totalAllowed);
            if (isMissile) Interlocked.Increment(ref _missileHitsAllowed);
        }

        public void IncrementMissileHits()
        {
            Interlocked.Increment(ref _missileHitsAllowed);
        }

        /// <summary>
        /// Increments the count of grids separated via Push-Apart.
        /// </summary>
        public void IncrementGridsSeparated()
        {
            Interlocked.Increment(ref _gridsSeparated);
        }

        /// <summary>
        /// Increments the count of player self-rescues executed.
        /// </summary>
        public void IncrementPlayerRescues()
        {
            Interlocked.Increment(ref _playerRescuesExecuted);
        }

        /// <summary>
        /// Increments the count of admin rescues executed.
        /// </summary>
        public void IncrementAdminRescues()
        {
            Interlocked.Increment(ref _adminRescuesExecuted);
        }

        /// <summary>
        /// Increments the count of internal blocks saved by layered armor shielding.
        /// </summary>
        public void IncrementArmorHitsOccluded()
        {
            Interlocked.Increment(ref _armorHitsOccluded);
        }

        /// <summary>
        /// Increments the count of inverted voxel downward normals.
        /// </summary>
        public void IncrementVoxelNormalsInverted()
        {
            Interlocked.Increment(ref _voxelNormalsInverted);
        }

        /// <summary>
        /// Increments the count of own-construct obstructions vaporized by thrusters.
        /// </summary>
        public void IncrementThrusterObstructionsVaporized()
        {
            Interlocked.Increment(ref _thrusterObstructionsVaporized);
        }

        /// <summary>
        /// Increments the count of explosive voxel cutouts suppressed.
        /// </summary>
        public void IncrementVoxelCutoutsPrevented()
        {
            Interlocked.Increment(ref _voxelCutoutsPrevented);
        }

        /// <summary>
        /// Increments the count of piloted buggy collision saves.
        /// </summary>
        public void IncrementPilotedBuggySaves()
        {
            Interlocked.Increment(ref _pilotedBuggySaves);
        }

        /// <summary>
        /// Total raw Havok contact-point callbacks seen (the hottest unstepped path).
        /// </summary>
        public long ContactCallbacks => Interlocked.Read(ref _contactCallbacks);

        /// <summary>
        /// Total voxel-collision-layer raycasts fired by the Voxel Normal Arbitrator (modified-voxel regions only).
        /// </summary>
        public long VoxelArbitratorRaycasts => Interlocked.Read(ref _voxelArbitratorRaycasts);

        /// <summary>
        /// Total queued push-apart actions actually applied to a grid.
        /// </summary>
        public long PushApartActionsExecuted => Interlocked.Read(ref _pushApartActionsExecuted);

        /// <summary>
        /// Total modified-voxel 32m buckets currently held in memory.
        /// </summary>
        public int VoxelBucketsCached => Modules.GridDefender.CachedModifiedVoxelBucketsCount;

        /// <summary>
        /// Total macroscopic terrain entries cached in memory.
        /// </summary>
        public int TerrainCacheEntries => Modules.GridDefender.CachedMacroTerrainCount;

        /// <summary>
        /// Increments the raw contact-callback counter (called once per Havok contact point).
        /// </summary>
        public void IncrementContactCallbacks()
        {
            Interlocked.Increment(ref _contactCallbacks);
        }

        /// <summary>
        /// Increments the arbitrator voxel-raycast counter.
        /// </summary>
        public void IncrementVoxelArbitratorRaycasts()
        {
            Interlocked.Increment(ref _voxelArbitratorRaycasts);
        }

        /// <summary>
        /// Increments the applied-push counter.
        /// </summary>
        public void IncrementPushApartActionsExecuted()
        {
            Interlocked.Increment(ref _pushApartActionsExecuted);
        }

        // Computed rates for UI binding
        public double ContactCallbacksPerSecond => _contactCallbacksPerSecond;
        public double VoxelRaycastsPerSecond => _voxelRaycastsPerSecond;
        public double PushApartRatePerSecond => _pushApartRatePerSecond;

        // GC Telemetry for UI binding
        public int GcGen0Count => _gcGen0Count;
        public int GcGen1Count => _gcGen1Count;
        public int GcGen2Count => _gcGen2Count;
        public double ManagedMemoryMb => _managedMemoryMb;

        public void UpdateRates(double cbRate, double arRate, double psRate, int gc0, int gc1, int gc2, double managedMb)
        {
            _contactCallbacksPerSecond = cbRate;
            _voxelRaycastsPerSecond = arRate;
            _pushApartRatePerSecond = psRate;
            _gcGen0Count = gc0;
            _gcGen1Count = gc1;
            _gcGen2Count = gc2;
            _managedMemoryMb = managedMb;
        }

        /// <summary>
        /// Computes hourly deltas against all-time cumulative counts and updates the snapshot.
        /// </summary>
        public void GetHourlyDigest(out long hourBlocked, out long allTimeBlocked, out long hourSeparated, out long allTimeSeparated, out long hourPmw, out long allTimePmw)
        {
            allTimeBlocked = TotalBlocked;
            allTimeSeparated = GridsSeparated;
            allTimePmw = MissileHitsAllowed;

            hourBlocked = Math.Max(0, allTimeBlocked - _hourlySnapshotTotalBlocked);
            hourSeparated = Math.Max(0, allTimeSeparated - _hourlySnapshotGridsSeparated);
            hourPmw = Math.Max(0, allTimePmw - _hourlySnapshotPmwHits);

            _hourlySnapshotTotalBlocked = allTimeBlocked;
            _hourlySnapshotGridsSeparated = allTimeSeparated;
            _hourlySnapshotPmwHits = allTimePmw;
        }

        /// <summary>
        /// Notifies the WPF UI of property changes across all telemetry metrics.
        /// </summary>
        public void NotifyAll()
        {
            OnPropertyChanged(nameof(TotalEvaluated));
            OnPropertyChanged(nameof(TotalBlocked));
            OnPropertyChanged(nameof(TotalAllowed));
            OnPropertyChanged(nameof(BlockRatio));
            OnPropertyChanged(nameof(MissileHitsAllowed));
            OnPropertyChanged(nameof(RammingBlocked));
            OnPropertyChanged(nameof(VoxelCrashesBlocked));
            OnPropertyChanged(nameof(SubgridCollisionsBlocked));
            OnPropertyChanged(nameof(LowSpeedBlocked));
            OnPropertyChanged(nameof(StationBlocked));
            OnPropertyChanged(nameof(DebrisBlocked));
            OnPropertyChanged(nameof(CooldownThrottled));
            OnPropertyChanged(nameof(GridsSeparated));
            OnPropertyChanged(nameof(PlayerRescuesExecuted));
            OnPropertyChanged(nameof(AdminRescuesExecuted));
            OnPropertyChanged(nameof(TotalRescuesExecuted));
            OnPropertyChanged(nameof(ArmorHitsOccluded));
            OnPropertyChanged(nameof(VoxelNormalsInverted));
            OnPropertyChanged(nameof(ThrusterObstructionsVaporized));
            OnPropertyChanged(nameof(VoxelCutoutsPrevented));
            OnPropertyChanged(nameof(PilotedBuggySaves));
            OnPropertyChanged(nameof(ContactCallbacks));
            OnPropertyChanged(nameof(VoxelArbitratorRaycasts));
            OnPropertyChanged(nameof(PushApartActionsExecuted));
            OnPropertyChanged(nameof(VoxelBucketsCached));
            OnPropertyChanged(nameof(TerrainCacheEntries));
            OnPropertyChanged(nameof(ContactCallbacksPerSecond));
            OnPropertyChanged(nameof(VoxelRaycastsPerSecond));
            OnPropertyChanged(nameof(PushApartRatePerSecond));
            OnPropertyChanged(nameof(GcGen0Count));
            OnPropertyChanged(nameof(GcGen1Count));
            OnPropertyChanged(nameof(GcGen2Count));
            OnPropertyChanged(nameof(ManagedMemoryMb));
            OnPropertyChanged(nameof(ActiveCollisionOffendersSummary));
            OnPropertyChanged(nameof(AlertStatusText));
            OnPropertyChanged(nameof(AlertBackgroundColor));
            OnPropertyChanged(nameof(AlertBorderColor));
            OnPropertyChanged(nameof(AlertIcon));
            OnPropertyChanged(nameof(TopOffender));
            OnPropertyChanged(nameof(HasActiveClangers));
            OnPropertyChanged(nameof(HasMultipleClangers));
            OnPropertyChanged(nameof(ActiveClangersCount));
            OnPropertyChanged(nameof(SessionTopClangers));
            OnPropertyChanged(nameof(RecentIncidents));
            OnPropertyChanged(nameof(EvaluatedCollisionsDisplay));
            OnPropertyChanged(nameof(EvaluatedCollisionsColor));
            OnPropertyChanged(nameof(TotalBlockedDisplay));
            OnPropertyChanged(nameof(TotalBlockedRatioDisplay));
            OnPropertyChanged(nameof(TotalBlockedColor));
            OnPropertyChanged(nameof(MissileHitsAllowedDisplay));
            OnPropertyChanged(nameof(MissileHitsColor));
            OnPropertyChanged(nameof(ArmorHitsOccludedDisplay));
            OnPropertyChanged(nameof(ArmorHitsColor));
            OnPropertyChanged(nameof(GridsSeparatedDisplay));
            OnPropertyChanged(nameof(GridsSeparatedPushesDisplay));
            OnPropertyChanged(nameof(GridsSeparatedColor));
            OnPropertyChanged(nameof(PlayerRescuesDisplay));
            OnPropertyChanged(nameof(PlayerRescuesColor));
            OnPropertyChanged(nameof(AdminRescuesDisplay));
            OnPropertyChanged(nameof(AdminRescuesColor));
            OnPropertyChanged(nameof(TotalRescuesDisplay));
            OnPropertyChanged(nameof(VoxelNormalsInvertedDisplay));
            OnPropertyChanged(nameof(VoxelNormalsColor));
            OnPropertyChanged(nameof(ThrusterObstructionsDisplay));
            OnPropertyChanged(nameof(ThrusterObstructionsColor));
            OnPropertyChanged(nameof(VoxelCutoutsPreventedDisplay));
            OnPropertyChanged(nameof(VoxelCutoutsColor));
            OnPropertyChanged(nameof(CachesDisplay));
            OnPropertyChanged(nameof(CachesColor));
            OnPropertyChanged(nameof(ContactCallbacksDisplay));
            OnPropertyChanged(nameof(ContactCallbacksColor));
            OnPropertyChanged(nameof(VoxelRaycastsDisplay));
            OnPropertyChanged(nameof(VoxelRaycastsColor));
        }

        public List<Modules.GridDefender.ClangOffender> SessionTopClangers => Modules.GridDefender.GetSessionTopClangers(20);
        public List<Modules.GridDefender.PhysicsIncident> RecentIncidents => Modules.GridDefender.GetRecentIncidents(10);

        public Modules.GridDefender.ClangOffender TopOffender
        {
            get
            {
                if (!PhysicsOptimizerPlugin.IsServerOnline) return null;
                var clangers = SessionTopClangers;
                if (clangers != null && clangers.Count > 0 && clangers[0].IsActive)
                {
                    return clangers[0];
                }
                return null;
            }
        }

        public bool HasActiveClangers => PhysicsOptimizerPlugin.IsServerOnline && TopOffender != null;
        public int ActiveClangersCount => PhysicsOptimizerPlugin.IsServerOnline ? (SessionTopClangers?.Count(c => c.IsActive) ?? 0) : 0;
        public bool HasMultipleClangers => ActiveClangersCount > 1;

        public string AlertStatusText
        {
            get
            {
                var serverState = PhysicsOptimizerPlugin.GetServerLifecycleState();
                if (serverState == ServerLifecycleState.Offline)
                {
                    return "SERVER OFFLINE — Simulation suspended until server starts";
                }
                if (serverState == ServerLifecycleState.Starting)
                {
                    return "SERVER STARTING — Initializing world and physics solvers...";
                }
                if (serverState == ServerLifecycleState.Stopping)
                {
                    return "SERVER STOPPING — Unloading session and halting physics...";
                }

                float simSpeed = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;
                var top = TopOffender;
                string multi = HasMultipleClangers ? $" (1 of {ActiveClangersCount} active)" : "";

                // 1. RED CRITICAL: Sim speed severely compromised (<0.80), or clanger causing lag (<0.90), or runaway explosion (>=20/s)
                if (top != null && (simSpeed < 0.90f || top.CurrentRate >= 20))
                {
                    return $"CRITICAL CLANG ({simSpeed:F2} TPS) — Culprit: '{top.DisplayName}' ({top.CurrentRate}/s at {top.LocationDisplay}){multi}";
                }

                if (simSpeed < 0.80f)
                {
                    return $"CRITICAL LAG ({simSpeed:F2} TPS) — Severe solver slowdown detected!";
                }

                // 2. YELLOW ADVISORY: Active clanger (>=5/s) while TPS is still healthy, or elevated solver contacts
                if (top != null)
                {
                    return $"CLANG ADVISORY ({simSpeed:F2} TPS) — Construct '{top.DisplayName}' ({top.CurrentRate}/s at {top.LocationDisplay}){multi}";
                }

                if (simSpeed < 0.95f || ContactCallbacksPerSecond > 2500)
                {
                    return $"SOLVER STRESS ({simSpeed:F2} TPS) — High contact callbacks ({ContactCallbacksPerSecond:F0}/s)";
                }

                // 3. GREEN OPTIMAL: Solvers running nominal and no active clangers
                return $"OPTIMAL ({simSpeed:F2} TPS) — Physics solvers quiet, all constructs nominal";
            }
        }

        public string AlertBackgroundColor
        {
            get
            {
                var serverState = PhysicsOptimizerPlugin.GetServerLifecycleState();
                if (serverState == ServerLifecycleState.Offline) return "#202026";
                if (serverState == ServerLifecycleState.Starting || serverState == ServerLifecycleState.Stopping) return "#2B2618";

                float simSpeed = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;
                var top = TopOffender;
                if (simSpeed < 0.80f || (top != null && (simSpeed < 0.90f || top.CurrentRate >= 20))) return "#451212";
                if (top != null || simSpeed < 0.95f || ContactCallbacksPerSecond > 2500) return "#3D2D12";
                return "#1B382B";
            }
        }

        public string AlertBorderColor
        {
            get
            {
                var serverState = PhysicsOptimizerPlugin.GetServerLifecycleState();
                if (serverState == ServerLifecycleState.Offline) return "#4B5563";
                if (serverState == ServerLifecycleState.Starting || serverState == ServerLifecycleState.Stopping) return "#D97706";

                float simSpeed = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;
                var top = TopOffender;
                if (simSpeed < 0.80f || (top != null && (simSpeed < 0.90f || top.CurrentRate >= 20))) return "#EF4444";
                if (top != null || simSpeed < 0.95f || ContactCallbacksPerSecond > 2500) return "#F59E0B";
                return "#2E7D32";
            }
        }

        public string AlertIcon
        {
            get
            {
                var serverState = PhysicsOptimizerPlugin.GetServerLifecycleState();
                if (serverState == ServerLifecycleState.Offline) return "⚪";
                if (serverState == ServerLifecycleState.Starting || serverState == ServerLifecycleState.Stopping) return "⏳";

                float simSpeed = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;
                var top = TopOffender;
                if (simSpeed < 0.80f || (top != null && (simSpeed < 0.90f || top.CurrentRate >= 20))) return "🔴";
                if (top != null || simSpeed < 0.95f || ContactCallbacksPerSecond > 2500) return "🟡";
                return "🟢";
            }
        }

        public string GenerateDiscordIncidentReport()
        {
            var serverState = PhysicsOptimizerPlugin.GetServerLifecycleState();
            float speed = PhysicsOptimizerPlugin.IsServerOnline ? Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio : 0f;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("```markdown");
            sb.AppendLine($"# Physics Incident Report ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC) [{serverState.ToString().ToUpperInvariant()}]");
            sb.AppendLine(PhysicsOptimizerPlugin.IsServerOnline
                ? $"* Server Sim Speed: {speed:F2} TPS"
                : $"* Server State: {serverState} (Simulation suspended)");
            sb.AppendLine($"* Evaluated Collisions: {TotalEvaluated:N0} (Blocked: {TotalBlocked:N0})");
            sb.AppendLine($"* Grids Nudged Apart: {GridsSeparated:N0} | Inverted Normals: {VoxelNormalsInverted:N0}");
            sb.AppendLine($"* Vehicle Rescues: {TotalRescuesExecuted:N0} ({PlayerRescuesExecuted:N0} player, {AdminRescuesExecuted:N0} admin)");

            var topClangers = SessionTopClangers;
            if (topClangers != null && topClangers.Count > 0)
            {
                sb.AppendLine("\n### Top Clang Offenders (Session):");
                for (int i = 0; i < Math.Min(5, topClangers.Count); i++)
                {
                    var c = topClangers[i];
                    string status = c.IsActive ? $"ACTIVE ({c.CurrentRate}/s)" : "IDLE";
                    sb.AppendLine($"{i + 1}. '{c.DisplayName}' (ID: {c.ConstructId}) - {c.TotalClangs:N0} clangs | {c.LocationDisplay} [{status}]");
                    sb.AppendLine($"   GPS:GPS:{c.DisplayName}:{c.LastPosition.X:F1}:{c.LastPosition.Y:F1}:{c.LastPosition.Z:F1}:#FFFF0000:");
                }
            }
            else
            {
                sb.AppendLine("\n* Active Clang Offenders: None (Clean)");
            }

            var incidents = RecentIncidents;
            if (incidents != null && incidents.Count > 0)
            {
                sb.AppendLine("\n### Recent Physics Incidents:");
                foreach (var inc in incidents)
                {
                    sb.AppendLine($"- {inc.FullText}");
                }
            }

            sb.AppendLine("```");
            return sb.ToString();
        }

        public string GetDiagnosticSummary()
        {
            var sb = new System.Text.StringBuilder();
            if (IsGridDefenderEnabled)
            {
                sb.AppendLine($"Evaluated Collisions: {TotalEvaluated:N0} (Blocked: {TotalBlocked:N0} [{BlockRatio:F1}%], Allowed: {TotalAllowed:N0})");
                sb.AppendLine($"Low-Speed Blocked: {LowSpeedBlocked:N0}, Voxel Crashes: {VoxelCrashesBlocked:N0}, Ramming: {RammingBlocked:N0}");
            }
            else
            {
                sb.AppendLine("Grid Defender: [Disabled]");
            }

            sb.AppendLine(IsPmwEnabled ? $"PMW Torpedoes Allowed: {MissileHitsAllowed:N0}, Piloted Buggy Saves: {PilotedBuggySaves:N0}" : "PMW Torpedoes: [Disabled]");
            sb.AppendLine(IsArmorOcclusionEnabled ? $"Internal Armor Saved: {ArmorHitsOccluded:N0}" : "Layered Armor Occlusion: [Disabled]");
            sb.AppendLine(IsVoxelArbitratorEnabled ? $"Voxel Normals Inverted: {VoxelNormalsInverted:N0}" : "Voxel Normal Arbitrator: [Disabled]");
            sb.AppendLine(IsPushApartEnabled ? $"Grids Nudged Apart: {GridsSeparated:N0} (Total Pushes: {PushApartActionsExecuted:N0})" : "Push-Apart: [Disabled]");
            sb.AppendLine($"Vehicle Rescues: Players={PlayerRescuesExecuted:N0}, Admins={AdminRescuesExecuted:N0}");
            sb.AppendLine(IsThrusterClearanceEnabled ? $"Thruster Obstructions Burned: {ThrusterObstructionsVaporized:N0}" : "Thruster Clearance: [Disabled]");
            sb.AppendLine(IsVoxelCutoutSuppressionEnabled ? $"Voxel Cutouts Prevented: {VoxelCutoutsPrevented:N0}" : "Voxel Cutout Suppression: [Disabled]");

            var topClangers = SessionTopClangers;
            if (topClangers != null && topClangers.Count > 0)
            {
                sb.AppendLine("\n=== SESSION WALL OF CLANG (TOP CLANG OFFENDERS) ===");
                for (int i = 0; i < Math.Min(10, topClangers.Count); i++)
                {
                    var c = topClangers[i];
                    string status = c.IsActive ? $"🔥 Active: {c.CurrentRate}/s" : "💤 Idle";
                    sb.AppendLine($"{i + 1}. '{c.DisplayName}' ({c.ConstructId}) - {c.TotalClangs:N0} clangs | {c.LocationDisplay} [{status}]");
                }
            }
            else
            {
                sb.AppendLine("Active Clang Offenders: None (Clean)");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Resets all collision and telemetry counters to zero.
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _totalEvaluated, 0);
            Interlocked.Exchange(ref _totalBlocked, 0);
            Interlocked.Exchange(ref _totalAllowed, 0);
            Interlocked.Exchange(ref _missileHitsAllowed, 0);
            Interlocked.Exchange(ref _rammingBlocked, 0);
            Interlocked.Exchange(ref _voxelCrashesBlocked, 0);
            Interlocked.Exchange(ref _subgridCollisionsBlocked, 0);
            Interlocked.Exchange(ref _lowSpeedBlocked, 0);
            Interlocked.Exchange(ref _stationBlocked, 0);
            Interlocked.Exchange(ref _debrisBlocked, 0);
            Interlocked.Exchange(ref _cooldownThrottled, 0);
            Interlocked.Exchange(ref _gridsSeparated, 0);
            Interlocked.Exchange(ref _playerRescuesExecuted, 0);
            Interlocked.Exchange(ref _adminRescuesExecuted, 0);
            Interlocked.Exchange(ref _armorHitsOccluded, 0);
            Interlocked.Exchange(ref _voxelNormalsInverted, 0);
            Interlocked.Exchange(ref _thrusterObstructionsVaporized, 0);
            Interlocked.Exchange(ref _voxelCutoutsPrevented, 0);
            Interlocked.Exchange(ref _pilotedBuggySaves, 0);
            Interlocked.Exchange(ref _contactCallbacks, 0);
            Interlocked.Exchange(ref _voxelArbitratorRaycasts, 0);
            Interlocked.Exchange(ref _pushApartActionsExecuted, 0);

            NotifyAll();
        }
    }
}

