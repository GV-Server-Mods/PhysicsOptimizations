using System.ComponentModel;
using System.Threading;

namespace PhysicsOptimizer.Services
{
    public class OptimizationTelemetry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public void NotifyAllPropertiesChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        private PhysicsOptimizerPlugin _plugin;

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
        }

        public bool IsRigidBodySleepEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnablePhysicsOptimizations && _plugin.Config.EnableRigidBodySleep;
        public bool IsWheelOptimizerEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnablePhysicsOptimizations && _plugin.Config.EnableWheelOptimizer;
        public bool IsOreMergeEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnablePhysicsOptimizations && _plugin.Config.EnableOreMerge;
        public bool IsSubgridStabilizerEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnablePhysicsOptimizations && _plugin.Config.EnableSubgridStabilizer;
        public bool IsAdaptiveCollisionEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnablePhysicsOptimizations && _plugin.Config.EnableAdaptiveCollision;

        // Progress bar percentages (0 to 100)
        public double DynamicSleepRatioPercent => (IsRigidBodySleepEnabled && TrackedGridsCount > 0) ? (double)GridsCurrentlyForcedSleep / TrackedGridsCount * 100.0 : 0.0;
        public double SleepingWheelsRatioPercent => (IsWheelOptimizerEnabled && TotalRoverWheelsCount > 0) ? (double)SleepingWheelsCount / TotalRoverWheelsCount * 100.0 : 0.0;
        public double StabilizedJointsRatioPercent => (IsSubgridStabilizerEnabled && TrackedSubgridConstraints > 0) ? (double)StabilizedSubgridConstraints / TrackedSubgridConstraints * 100.0 : 0.0;

        // Formatted display strings for UI
        public string RigidBodiesDisplay => IsRigidBodySleepEnabled
            ? $"{ActiveRigidBodies:N0} Act / {SleepingRigidBodies:N0} Slp"
            : "[Disabled]";

        public string DynamicGridsSleepDisplay => IsRigidBodySleepEnabled
            ? $"{GridsCurrentlyForcedSleep:N0} / {TrackedGridsCount:N0} ({ForcedSleepEventsTotal:N0} events)"
            : "[Disabled]";

        public string MergedOreDisplay => IsOreMergeEnabled
            ? $"{OreStacksMergedTotal:N0} ({OreEntitiesEliminatedTotal:N0} eliminated)"
            : "[Disabled]";

        public string RoversDisplay => IsWheelOptimizerEnabled
            ? $"{TrackedRoversCount:N0} (Parked Asleep: {ParkedRoversAsleep:N0})"
            : "[Disabled]";

        public string SleepingWheelsDisplay => IsWheelOptimizerEnabled
            ? $"{SleepingWheelsCount:N0} / {TotalRoverWheelsCount:N0} sleeping"
            : "[Disabled]";

        public string SubgridJointsDisplay => IsSubgridStabilizerEnabled
            ? $"{StabilizedSubgridConstraints:N0} / {TrackedSubgridConstraints:N0} dampened"
            : "[Disabled]";

        public string AdaptiveTOIDisplay => IsAdaptiveCollisionEnabled
            ? $"{DiscreteTOIGridsCount:N0} Disc / {ContinuousTOIGridsCount:N0} Cont"
            : "[Disabled]";

        // Live gauges
        private float _serverSimulationSpeed = 1.0f;
        private int _activeRigidBodies;
        private int _sleepingRigidBodies;

        private int _trackedRoversCount;
        private int _parkedRoversAsleep;
        private int _totalRoverWheelsCount;
        private int _sleepingWheelsCount;

        private int _trackedGridsCount;
        private int _gridsCurrentlyForcedSleep;

        private int _activeFloatingObjectsCount;
        private int _lastMergeEliminatedCount;

        private int _trackedSubgridConstraints;
        private int _stabilizedSubgridConstraints;

        private int _trackedTOIGridsCount;
        private int _discreteTOIGridsCount;
        private int _continuousTOIGridsCount;

        // Cumulative counters
        private long _oreStacksMergedTotal;
        private long _oreEntitiesEliminatedTotal;
        private long _forcedSleepEventsTotal;

        public float ServerSimulationSpeed
        {
            get => _serverSimulationSpeed;
            set => _serverSimulationSpeed = value;
        }

        public void UpdateServerSimulationSpeed(float speed)
        {
            _serverSimulationSpeed = speed;
        }

        public int ActiveRigidBodies
        {
            get => _activeRigidBodies;
            set => _activeRigidBodies = value;
        }

        public int SleepingRigidBodies
        {
            get => _sleepingRigidBodies;
            set => _sleepingRigidBodies = value;
        }

        public void UpdateActiveAndSleepingRigidBodies(int active, int sleeping)
        {
            _activeRigidBodies = active;
            _sleepingRigidBodies = sleeping;
        }

        public int TrackedRoversCount
        {
            get => _trackedRoversCount;
            set => _trackedRoversCount = value;
        }

        public int ParkedRoversAsleep
        {
            get => _parkedRoversAsleep;
            set => _parkedRoversAsleep = value;
        }

        public int TotalRoverWheelsCount
        {
            get => _totalRoverWheelsCount;
            set => _totalRoverWheelsCount = value;
        }

        public int SleepingWheelsCount
        {
            get => _sleepingWheelsCount;
            set => _sleepingWheelsCount = value;
        }

        public void UpdateRoverTelemetry(int trackedRovers, int sleepingRovers, int totalWheels, int sleepingWheels)
        {
            _trackedRoversCount = trackedRovers;
            _parkedRoversAsleep = sleepingRovers;
            _totalRoverWheelsCount = totalWheels;
            _sleepingWheelsCount = sleepingWheels;
        }

        public int TrackedGridsCount
        {
            get => _trackedGridsCount;
            set => _trackedGridsCount = value;
        }

        public int GridsCurrentlyForcedSleep
        {
            get => _gridsCurrentlyForcedSleep;
            set => _gridsCurrentlyForcedSleep = value;
        }

        public void UpdateGridSleepTelemetry(int trackedGrids, int sleepingGrids)
        {
            _trackedGridsCount = trackedGrids;
            _gridsCurrentlyForcedSleep = sleepingGrids;
        }

        public int ActiveFloatingObjectsCount
        {
            get => _activeFloatingObjectsCount;
            set => _activeFloatingObjectsCount = value;
        }

        public int LastMergeEliminatedCount
        {
            get => _lastMergeEliminatedCount;
            set => _lastMergeEliminatedCount = value;
        }

        public void UpdateFloatingObjectTelemetry(int activeCount, int lastEliminated)
        {
            _activeFloatingObjectsCount = activeCount;
            _lastMergeEliminatedCount = lastEliminated;
        }

        public int TrackedSubgridConstraints
        {
            get => _trackedSubgridConstraints;
            set => _trackedSubgridConstraints = value;
        }

        public int StabilizedSubgridConstraints
        {
            get => _stabilizedSubgridConstraints;
            set => _stabilizedSubgridConstraints = value;
        }

        public void UpdateSubgridTelemetry(int tracked, int stabilized)
        {
            _trackedSubgridConstraints = tracked;
            _stabilizedSubgridConstraints = stabilized;
        }

        public int TrackedTOIGridsCount
        {
            get => _trackedTOIGridsCount;
            set => _trackedTOIGridsCount = value;
        }

        public int DiscreteTOIGridsCount
        {
            get => _discreteTOIGridsCount;
            set => _discreteTOIGridsCount = value;
        }

        public int ContinuousTOIGridsCount
        {
            get => _continuousTOIGridsCount;
            set => _continuousTOIGridsCount = value;
        }

        public void UpdateTOITelemetry(int tracked, int discrete, int continuous)
        {
            _trackedTOIGridsCount = tracked;
            _discreteTOIGridsCount = discrete;
            _continuousTOIGridsCount = continuous;
        }

        public long OreStacksMergedTotal => Interlocked.Read(ref _oreStacksMergedTotal);
        public long OreEntitiesEliminatedTotal => Interlocked.Read(ref _oreEntitiesEliminatedTotal);
        public long ForcedSleepEventsTotal => Interlocked.Read(ref _forcedSleepEventsTotal);

        public void IncrementOreMerged(int count = 1)
        {
            Interlocked.Add(ref _oreStacksMergedTotal, count);
        }

        public void IncrementOreEntitiesEliminated(int count = 1)
        {
            Interlocked.Add(ref _oreEntitiesEliminatedTotal, count);
        }

        public void IncrementForcedSleepEvents(int count = 1)
        {
            Interlocked.Add(ref _forcedSleepEventsTotal, count);
        }

        public string GetDiagnosticSummary()
        {
            return $"Sim Speed: {ServerSimulationSpeed:F2} TPS\n" +
                   $"Active Bodies: {ActiveRigidBodies}, Sleeping: {SleepingRigidBodies}\n" +
                   $"Rovers: {TrackedRoversCount}, Parked: {ParkedRoversAsleep}, Sleeping Wheels: {SleepingWheelsCount}/{TotalRoverWheelsCount}\n" +
                   $"Forced Sleep Events: {ForcedSleepEventsTotal}\n" +
                   $"Merged Ore Stacks: {OreStacksMergedTotal}, Eliminated: {OreEntitiesEliminatedTotal}\n" +
                   $"Subgrid Constraints Stabilized: {StabilizedSubgridConstraints}/{TrackedSubgridConstraints}\n" +
                   $"TOI Grids Discrete: {DiscreteTOIGridsCount}, Continuous: {ContinuousTOIGridsCount}";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Sim Speed: {0:F2} TPS", ServerSimulationSpeed));

            sb.AppendLine(IsRigidBodySleepEnabled
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "Active Bodies: {0:N0}, Sleeping: {1:N0} (Forced Sleep: {2:N0}/{3:N0}, {4:N0} events)", ActiveRigidBodies, SleepingRigidBodies, GridsCurrentlyForcedSleep, TrackedGridsCount, ForcedSleepEventsTotal)
                : "Rigid Body Sleep: [Disabled]");

            sb.AppendLine(IsWheelOptimizerEnabled
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "Rovers: {0:N0}, Parked Asleep: {1:N0}, Sleeping Wheels: {2:N0}/{3:N0}", TrackedRoversCount, ParkedRoversAsleep, SleepingWheelsCount, TotalRoverWheelsCount)
                : "Wheel Optimizer: [Disabled]");

            sb.AppendLine(IsOreMergeEnabled
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "Merged Ore Stacks: {0:N0}, Eliminated: {1:N0}", OreStacksMergedTotal, OreEntitiesEliminatedTotal)
                : "Ore Merge: [Disabled]");

            sb.AppendLine(IsSubgridStabilizerEnabled
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "Subgrid Constraints Stabilized: {0:N0}/{1:N0}", StabilizedSubgridConstraints, TrackedSubgridConstraints)
                : "Subgrid Stabilizer: [Disabled]");

            sb.Append(IsAdaptiveCollisionEnabled
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "TOI Grids Discrete: {0:N0}, Continuous: {1:N0}", DiscreteTOIGridsCount, ContinuousTOIGridsCount)
                : "Adaptive Collision: [Disabled]");

            return sb.ToString();
        }

        public void ResetLiveCounters()
        {
            _activeRigidBodies = 0;
            _sleepingRigidBodies = 0;
            _trackedRoversCount = 0;
            _parkedRoversAsleep = 0;
            _totalRoverWheelsCount = 0;
            _sleepingWheelsCount = 0;
            _trackedGridsCount = 0;
            _gridsCurrentlyForcedSleep = 0;
            _activeFloatingObjectsCount = 0;
            _lastMergeEliminatedCount = 0;
            _trackedSubgridConstraints = 0;
            _stabilizedSubgridConstraints = 0;
            _trackedTOIGridsCount = 0;
            _discreteTOIGridsCount = 0;
            _continuousTOIGridsCount = 0;
        }
    }
}

