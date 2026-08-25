using System;
using Torch;
using Torch.Views;

namespace GVK.PhysicsOptimizations.Config
{
    public class PhysicsOptimizerConfig : ViewModel
    {
        // --- General Settings ---
        private bool _enabled = true;
        private bool _enableDebugLogging = false;

        // --- Module 1: Wheel & Suspension ---
        private bool _enableWheelOptimization = true;
        private bool _enableWheelCollisionFilter = true;
        private bool _sleepParkedRovers = true;
        private float _roverSleepDelaySeconds = 2.0f;

        // --- Module 2: Rigid Body Sleeping ---
        private bool _enableAggressiveSleeping = true;
        private float _sleepLinearVelocityThreshold = 0.05f; // m/s
        private float _sleepAngularVelocityThreshold = 0.01f; // rad/s
        private int _idleSecondsBeforeSleep = 3;

        // --- Module 3: Floating Objects & Ore ---
        private bool _enableFloatingObjectOptimizer = true;
        private bool _autoMergeNearbyOre = true;
        private float _oreMergeRadiusMeters = 3.0f;
        private int _oreMergeIntervalTicks = 120; // 2 seconds
        private int _maxSectorFloatingObjects = 64;

        // --- Module 4: Subgrid Constraints ---
        private bool _enableSubgridStabilization = true;
        private float _subgridRestVelocityThreshold = 0.005f; // rad/s
        private int _subgridRestFramesThreshold = 60; // 1 second

        // --- Module 5: Adaptive TOI / Collision Detection ---
        private bool _enableAdaptiveTOI = true;
        private float _continuousCollisionSpeedThreshold = 40.0f; // m/s
        private float _discreteCollisionSpeedThreshold = 15.0f; // m/s

        // ==========================================
        // General Properties
        // ==========================================
        [Display(Order = 1, Name = "Enable Plugin", GroupName = "General", Description = "Master toggle for all Physics Optimizer systems.")]
        public bool Enabled
        {
            get => _enabled;
            set => SetValue(ref _enabled, value);
        }

        [Display(Order = 2, Name = "Enable Debug Logging", GroupName = "General", Description = "Log detailed optimization events, sleep transitions, and ore merges to Torch logs.")]
        public bool EnableDebugLogging
        {
            get => _enableDebugLogging;
            set => SetValue(ref _enableDebugLogging, value);
        }

        // ==========================================
        // Module 1: Wheel & Suspension Properties
        // ==========================================
        [Display(Order = 3, Name = "Enable Wheel Optimizer", GroupName = "Module 1: Wheel & Suspension", Description = "Master toggle for wheel collision filter and suspension optimizations.")]
        public bool EnableWheelOptimization
        {
            get => _enableWheelOptimization;
            set => SetValue(ref _enableWheelOptimization, value);
        }

        [Display(Order = 4, Name = "Wheel Collision Filter Mask", GroupName = "Module 1: Wheel & Suspension", Description = "Eliminates redundant Havok AABB compound shape checks between wheels and chassis wheel well armor blocks.")]
        public bool EnableWheelCollisionFilter
        {
            get => _enableWheelCollisionFilter;
            set => SetValue(ref _enableWheelCollisionFilter, value);
        }

        [Display(Order = 5, Name = "Sleep Parked Rovers", GroupName = "Module 1: Wheel & Suspension", Description = "Suspends 60Hz suspension raycasts, air-shock checks, and braking impulses when a rover has handbrake engaged and is stationary.")]
        public bool SleepParkedRovers
        {
            get => _sleepParkedRovers;
            set => SetValue(ref _sleepParkedRovers, value);
        }

        [Display(Order = 6, Name = "Rover Sleep Delay (Seconds)", GroupName = "Module 1: Wheel & Suspension", Description = "Seconds a parked rover must remain motionless before entering suspension sleep.")]
        public float RoverSleepDelaySeconds
        {
            get => _roverSleepDelaySeconds;
            set => SetValue(ref _roverSleepDelaySeconds, Math.Max(0.5f, Math.Min(30.0f, value)));
        }

        // ==========================================
        // Module 2: Rigid Body Sleeping Properties
        // ==========================================
        [Display(Order = 7, Name = "Enable Aggressive Sleeping", GroupName = "Module 2: Rigid Body Sleeping", Description = "Actively forces motionless dynamic grids into Havok sleep mode, waking them instantly upon control input, impact, or damage.")]
        public bool EnableAggressiveSleeping
        {
            get => _enableAggressiveSleeping;
            set => SetValue(ref _enableAggressiveSleeping, value);
        }

        [Display(Order = 8, Name = "Sleep Linear Velocity (m/s)", GroupName = "Module 2: Rigid Body Sleeping", Description = "Maximum linear speed (m/s) below which an unpiloted grid is eligible for sleep. Default: 0.05.")]
        public float SleepLinearVelocityThreshold
        {
            get => _sleepLinearVelocityThreshold;
            set => SetValue(ref _sleepLinearVelocityThreshold, Math.Max(0.01f, Math.Min(1.0f, value)));
        }

        [Display(Order = 9, Name = "Sleep Angular Velocity (rad/s)", GroupName = "Module 2: Rigid Body Sleeping", Description = "Maximum angular speed (rad/s) below which an unpiloted grid is eligible for sleep. Default: 0.01.")]
        public float SleepAngularVelocityThreshold
        {
            get => _sleepAngularVelocityThreshold;
            set => SetValue(ref _sleepAngularVelocityThreshold, Math.Max(0.001f, Math.Min(0.5f, value)));
        }

        [Display(Order = 10, Name = "Idle Seconds Before Sleep", GroupName = "Module 2: Rigid Body Sleeping", Description = "Consecutive seconds an unpiloted grid must remain under velocity thresholds before entering sleep. Default: 3.")]
        public int IdleSecondsBeforeSleep
        {
            get => _idleSecondsBeforeSleep;
            set => SetValue(ref _idleSecondsBeforeSleep, Math.Max(1, Math.Min(60, value)));
        }

        // ==========================================
        // Module 3: Floating Objects & Ore Properties
        // ==========================================
        [Display(Order = 11, Name = "Enable Floating Object Optimizer", GroupName = "Module 3: Floating Objects & Ore", Description = "Master toggle for proximity stack merging of floating ores and dropped components.")]
        public bool EnableFloatingObjectOptimizer
        {
            get => _enableFloatingObjectOptimizer;
            set => SetValue(ref _enableFloatingObjectOptimizer, value);
        }

        [Display(Order = 12, Name = "Auto-Merge Nearby Ore & Items", GroupName = "Module 3: Floating Objects & Ore", Description = "Combines matching floating ore rocks and items into single larger stacks to dramatically reduce rigid body counts.")]
        public bool AutoMergeNearbyOre
        {
            get => _autoMergeNearbyOre;
            set => SetValue(ref _autoMergeNearbyOre, value);
        }

        [Display(Order = 13, Name = "Merge Radius (Meters)", GroupName = "Module 3: Floating Objects & Ore", Description = "Spatial radius (meters) within which matching floating items will merge into a single item stack. Default: 3.0m.")]
        public float OreMergeRadiusMeters
        {
            get => _oreMergeRadiusMeters;
            set => SetValue(ref _oreMergeRadiusMeters, Math.Max(0.5f, Math.Min(20.0f, value)));
        }

        [Display(Order = 14, Name = "Merge Interval (Ticks)", GroupName = "Module 3: Floating Objects & Ore", Description = "Simulation frames between proximity merge sweeps (60 = 1 sec, 120 = 2 sec). Default: 120.")]
        public int OreMergeIntervalTicks
        {
            get => _oreMergeIntervalTicks;
            set => SetValue(ref _oreMergeIntervalTicks, Math.Max(30, Math.Min(600, value)));
        }

        [Display(Order = 15, Name = "Max Sector Floating Objects", GroupName = "Module 3: Floating Objects & Ore", Description = "Threshold count of floating objects in a sector before accelerating merging and deactivation. Default: 64.")]
        public int MaxSectorFloatingObjects
        {
            get => _maxSectorFloatingObjects;
            set => SetValue(ref _maxSectorFloatingObjects, Math.Max(16, Math.Min(512, value)));
        }

        // ==========================================
        // Module 4: Subgrid Constraints Properties
        // ==========================================
        [Display(Order = 16, Name = "Enable Subgrid Stabilization", GroupName = "Module 4: Subgrid Constraints", Description = "Stabilizes mechanical subgrid joints (rotors, hinges, pistons) when resting to prevent solver micro-vibrations and Clang loops.")]
        public bool EnableSubgridStabilization
        {
            get => _enableSubgridStabilization;
            set => SetValue(ref _enableSubgridStabilization, value);
        }

        [Display(Order = 17, Name = "Subgrid Rest Velocity (rad/s)", GroupName = "Module 4: Subgrid Constraints", Description = "Joint angular speed below which a mechanical connection is considered at rest. Default: 0.005.")]
        public float SubgridRestVelocityThreshold
        {
            get => _subgridRestVelocityThreshold;
            set => SetValue(ref _subgridRestVelocityThreshold, Math.Max(0.001f, Math.Min(0.1f, value)));
        }

        [Display(Order = 18, Name = "Subgrid Rest Frames", GroupName = "Module 4: Subgrid Constraints", Description = "Consecutive frames of rest before stabilizing the constraint solver. Default: 60 (~1 sec).")]
        public int SubgridRestFramesThreshold
        {
            get => _subgridRestFramesThreshold;
            set => SetValue(ref _subgridRestFramesThreshold, Math.Max(10, Math.Min(300, value)));
        }

        // ==========================================
        // Module 5: Adaptive TOI / Collision Detection Properties
        // ==========================================
        [Display(Order = 19, Name = "Enable Adaptive TOI", GroupName = "Module 5: Adaptive TOI / Collision", Description = "Dynamically switches low-speed grids to discrete collision quality to eliminate heavy continuous broadphase queries.")]
        public bool EnableAdaptiveTOI
        {
            get => _enableAdaptiveTOI;
            set => SetValue(ref _enableAdaptiveTOI, value);
        }

        [Display(Order = 20, Name = "Continuous TOI Speed (m/s)", GroupName = "Module 5: Adaptive TOI / Collision", Description = "Grids and missiles moving above this speed retain continuous collision detection (CCD) to prevent tunneling. Default: 40 m/s.")]
        public float ContinuousCollisionSpeedThreshold
        {
            get => _continuousCollisionSpeedThreshold;
            set => SetValue(ref _continuousCollisionSpeedThreshold, Math.Max(10.0f, Math.Min(150.0f, value)));
        }

        [Display(Order = 21, Name = "Discrete Collision Speed (m/s)", GroupName = "Module 5: Adaptive TOI / Collision", Description = "Grids moving below this speed use discrete collision detection to maximize sim-speed. Default: 15 m/s.")]
        public float DiscreteCollisionSpeedThreshold
        {
            get => _discreteCollisionSpeedThreshold;
            set => SetValue(ref _discreteCollisionSpeedThreshold, Math.Max(1.0f, Math.Min(_continuousCollisionSpeedThreshold, value)));
        }
    }
}

