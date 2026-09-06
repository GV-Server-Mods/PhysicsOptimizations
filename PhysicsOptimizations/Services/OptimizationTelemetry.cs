using System.ComponentModel;
using System.Threading;

namespace GVK.PhysicsOptimizations.Services
{
    public class OptimizationTelemetry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public void NotifyAllPropertiesChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

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

