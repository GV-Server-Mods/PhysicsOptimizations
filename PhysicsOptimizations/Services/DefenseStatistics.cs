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

        /// <summary>
        /// Increments the count of grids separated via Push-Apart.
        /// </summary>
        public void IncrementGridsSeparated()
        {
            Interlocked.Increment(ref _gridsSeparated);
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
            OnPropertyChanged(nameof(Zone0Status));
            OnPropertyChanged(nameof(Zone1Status));
            OnPropertyChanged(nameof(Zone2Status));
            OnPropertyChanged(nameof(Zone3Status));
            OnPropertyChanged(nameof(Zone0Color));
            OnPropertyChanged(nameof(Zone1Color));
            OnPropertyChanged(nameof(Zone2Color));
            OnPropertyChanged(nameof(Zone3Color));
        }

        public List<Modules.GridDefender.ClangOffender> SessionTopClangers => Modules.GridDefender.GetSessionTopClangers(20);
        public List<Modules.GridDefender.PhysicsIncident> RecentIncidents => Modules.GridDefender.GetRecentIncidents(4);

        public Modules.GridDefender.ClangOffender TopOffender
        {
            get
            {
                var clangers = SessionTopClangers;
                if (clangers != null && clangers.Count > 0 && clangers[0].IsActive)
                {
                    return clangers[0];
                }
                return null;
            }
        }

        public bool HasActiveClangers => TopOffender != null;
        public int ActiveClangersCount => SessionTopClangers?.Count(c => c.IsActive) ?? 0;
        public bool HasMultipleClangers => ActiveClangersCount > 1;

        public string AlertStatusText
        {
            get
            {
                float simSpeed = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;
                var top = TopOffender;
                string multi = HasMultipleClangers ? $" (1 of {ActiveClangersCount} active)" : "";

                // 1. RED CRITICAL: Sim speed severely compromised (<0.80), or clanger causing lag (<0.90), or runaway explosion (>=20/s)
                if (top != null && (simSpeed < 0.90f || top.CurrentRate >= 20))
                {
                    return $"CRITICAL CLANG ({simSpeed:F2} TPS) — Culprit: '{top.DisplayName}' ({top.CurrentRate}/s in {top.ZoneName}){multi}";
                }

                if (simSpeed < 0.80f)
                {
                    return $"CRITICAL LAG ({simSpeed:F2} TPS) — Severe solver slowdown detected!";
                }

                // 2. YELLOW ADVISORY: Active clanger (>=5/s) while TPS is still healthy, or elevated solver contacts
                if (top != null)
                {
                    return $"CLANG ADVISORY ({simSpeed:F2} TPS) — Construct '{top.DisplayName}' ({top.CurrentRate}/s in {top.ZoneName}){multi}";
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
                float simSpeed = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;
                var top = TopOffender;
                if (simSpeed < 0.80f || (top != null && (simSpeed < 0.90f || top.CurrentRate >= 20))) return "🔴";
                if (top != null || simSpeed < 0.95f || ContactCallbacksPerSecond > 2500) return "🟡";
                return "🟢";
            }
        }

        public (int active, int total) GetZoneStats(int zone)
        {
            var clangers = SessionTopClangers;
            int count = 0;
            int active = 0;
            if (clangers != null)
            {
                foreach (var c in clangers)
                {
                    if (c.ZoneNumber == zone)
                    {
                        count++;
                        if (c.IsActive) active++;
                    }
                }
            }
            return (active, count);
        }

        public string Zone0Status => GetZoneStatusText(0, "Zone 0 (Starter Hub, 0-20km)");
        public string Zone1Status => GetZoneStatusText(1, "Zone 1 (Salvage, 20-35km)");
        public string Zone2Status => GetZoneStatusText(2, "Zone 2 (Contested, 35-50km)");
        public string Zone3Status => GetZoneStatusText(3, "Zone 3 (Deep Desert, >50km)");

        public string Zone0Color => GetZoneColor(0);
        public string Zone1Color => GetZoneColor(1);
        public string Zone2Color => GetZoneColor(2);
        public string Zone3Color => GetZoneColor(3);

        private string GetZoneStatusText(int zone, string title)
        {
            var stats = GetZoneStats(zone);
            if (stats.active > 0) return $"🔴 {title}: {stats.active} ACTIVE CLANGER!";
            if (stats.total > 0) return $"🟡 {title}: Clean ({stats.total} logged earlier)";
            return $"🟢 {title}: Clean";
        }

        private string GetZoneColor(int zone)
        {
            var stats = GetZoneStats(zone);
            if (stats.active > 0) return "#EF4444";
            if (stats.total > 0) return "#F59E0B";
            return "#4CAF50";
        }

        public string GenerateDiscordIncidentReport()
        {
            float speed = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("```markdown");
            sb.AppendLine($"# GVK Physics Incident Report ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC)");
            sb.AppendLine($"* Server Sim Speed: {speed:F2} TPS");
            sb.AppendLine($"* Evaluated Collisions: {TotalEvaluated:N0} (Blocked: {TotalBlocked:N0})");
            sb.AppendLine($"* Grids Nudged Apart: {GridsSeparated:N0} | Inverted Normals: {VoxelNormalsInverted:N0}");

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
                    sb.AppendLine($"• {inc.FullText}");
                }
            }

            sb.AppendLine("```");
            return sb.ToString();
        }

        public string GetDiagnosticSummary()
        {
            return $"Evaluated Collisions: {TotalEvaluated:N0} (Blocked: {TotalBlocked:N0} [{BlockRatio:F1}%], Allowed: {TotalAllowed:N0})\n" +
                   $"Low-Speed Blocked: {LowSpeedBlocked:N0}, Voxel Crashes: {VoxelCrashesBlocked:N0}, Ramming: {RammingBlocked:N0}\n" +
                   $"PMW Torpedoes Allowed: {MissileHitsAllowed:N0}, Piloted Buggy Saves: {PilotedBuggySaves:N0}\n" +
                   $"Layered Armor Saved: {ArmorHitsOccluded:N0}, Voxel Normals Inverted: {VoxelNormalsInverted:N0}\n" +
                   $"Grids Nudged Apart: {GridsSeparated:N0}\n" +
                   $"Hot Path: Contacts {ContactCallbacks:N0}, Arb Raycasts {VoxelArbitratorRaycasts:N0}, Pushes {PushApartActionsExecuted:N0}\n" +
                   $"Thruster Obstructions Vaporized: {ThrusterObstructionsVaporized:N0}, Voxel Cutouts Prevented: {VoxelCutoutsPrevented:N0}\n" +
                   $"Active Clang Offenders: {ActiveCollisionOffendersSummary}";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Evaluated Collisions: {TotalEvaluated:N0} (Blocked: {TotalBlocked:N0} [{BlockRatio:F1}%], Allowed: {TotalAllowed:N0})");
            sb.AppendLine($"Low-Speed Blocked: {LowSpeedBlocked:N0}, Voxel Crashes: {VoxelCrashesBlocked:N0}, Ramming: {RammingBlocked:N0}");
            sb.AppendLine($"PMW Torpedoes Allowed: {MissileHitsAllowed:N0}, Piloted Buggy Saves: {PilotedBuggySaves:N0}");
            sb.AppendLine($"Internal Armor Saved: {ArmorHitsOccluded:N0}, Voxel Normals Inverted: {VoxelNormalsInverted:N0}");
            sb.AppendLine($"Grids Nudged Apart: {GridsSeparated:N0} (Total Pushes: {PushApartActionsExecuted:N0})");
            sb.AppendLine($"Thruster Obstructions Burned: {ThrusterObstructionsVaporized:N0}, Voxel Cutouts Prevented: {VoxelCutoutsPrevented:N0}");

            var topClangers = SessionTopClangers;
            if (topClangers != null && topClangers.Count > 0)
            {
                sb.AppendLine("\n=== SESSION WALL OF SHAME (TOP CLANG OFFENDERS) ===");
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

