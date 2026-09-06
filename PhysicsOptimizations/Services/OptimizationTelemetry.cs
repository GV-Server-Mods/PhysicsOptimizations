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
        private int _activeRigidBodies;
        private int _sleepingRigidBodies;
        private int _parkedRoversAsleep;
        private int _sleepingWheelsCount;
        private int _stabilizedSubgridConstraints;
        private int _discreteTOIGridsCount;

        // Cumulative counters
        private long _oreStacksMergedTotal;
        private long _oreEntitiesEliminatedTotal;
        private long _forcedSleepEventsTotal;

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

        public int ParkedRoversAsleep
        {
            get => _parkedRoversAsleep;
            set => _parkedRoversAsleep = value;
        }

        public void UpdateParkedRoversAsleep(int count)
        {
            _parkedRoversAsleep = count;
        }

        public int SleepingWheelsCount
        {
            get => _sleepingWheelsCount;
            set => _sleepingWheelsCount = value;
        }

        public void UpdateSleepingWheelsCount(int count)
        {
            _sleepingWheelsCount = count;
        }

        public int StabilizedSubgridConstraints
        {
            get => _stabilizedSubgridConstraints;
            set => _stabilizedSubgridConstraints = value;
        }

        public void UpdateStabilizedSubgridConstraints(int count)
        {
            _stabilizedSubgridConstraints = count;
        }

        public int DiscreteTOIGridsCount
        {
            get => _discreteTOIGridsCount;
            set => _discreteTOIGridsCount = value;
        }

        public void UpdateDiscreteTOIGridsCount(int count)
        {
            _discreteTOIGridsCount = count;
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
            _parkedRoversAsleep = 0;
            _sleepingWheelsCount = 0;
            _stabilizedSubgridConstraints = 0;
            _discreteTOIGridsCount = 0;
        }
    }
}

