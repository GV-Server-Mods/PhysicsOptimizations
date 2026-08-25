using System.Threading;

namespace GVK.PhysicsOptimizations.Services
{
    public class OptimizationTelemetry
    {
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

        public int ParkedRoversAsleep
        {
            get => _parkedRoversAsleep;
            set => _parkedRoversAsleep = value;
        }

        public int SleepingWheelsCount
        {
            get => _sleepingWheelsCount;
            set => _sleepingWheelsCount = value;
        }

        public int StabilizedSubgridConstraints
        {
            get => _stabilizedSubgridConstraints;
            set => _stabilizedSubgridConstraints = value;
        }

        public int DiscreteTOIGridsCount
        {
            get => _discreteTOIGridsCount;
            set => _discreteTOIGridsCount = value;
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

