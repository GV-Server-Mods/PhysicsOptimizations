using System;
using System.Xml.Serialization;
using Torch;
using Torch.Views;

namespace PhysicsOptimizer.Config
{
    public enum ThrusterDamageMode
    {
        Optimized = 0,
        VanillaLike = 1
    }

    public class PhysicsOptimizerConfig : ViewModel
    {
        // --- General Settings ---
        private bool _enabled = true;
        private bool _enablePhysicsOptimizations = true;
        private bool _enableDebugLogging = false;
        private bool _enablePeriodicConsoleTelemetry = true;
        private int _consoleTelemetryIntervalSeconds = 30;

        // Logging specific modules
        private bool _logWheelOptimizer = false;
        private bool _logRigidBodySleep = false;
        private bool _logOreMerge = false;
        private bool _logSubgridStabilizer = false;
        private bool _logAdaptiveCollision = false;
        private bool _logGridDefender = false;
        private bool _logThrusterClearance = false;
        private bool _logMissileDefense = false;
        private bool _logVoxelNormals = false;

        // --- Module 1: Wheel & Suspension ---
        private bool _enableWheelOptimizer = true;
        private bool _enableWheelCollisionFilter = true;
        private bool _sleepParkedRovers = true;
        private float _roverSleepDelaySeconds = 2.0f;

        // --- Module 2: Rigid Body Sleeping ---
        private bool _enableRigidBodySleep = true;
        private float _sleepLinearVelocityThreshold = 0.05f; // m/s
        private float _sleepAngularVelocityThreshold = 0.01f; // rad/s
        private int _idleSecondsBeforeSleep = 3;

        // --- Module 3: Floating Objects & Ore ---
        private bool _enableOreMerge = true;
        private bool _autoMergeNearbyOre = true;
        private float _oreMergeRadiusMeters = 3.0f;
        private int _oreMergeIntervalTicks = 120;
        private int _maxSectorFloatingObjects = 64;

        // --- Module 4: Subgrid Constraints ---
        private bool _enableSubgridStabilizer = true;
        private bool _enableSubgridStabilization = true;
        private float _subgridRestVelocityThreshold = 0.005f;
        private int _subgridRestFramesThreshold = 60;
        private bool _maskSmallUtilitySubgrids = true;
        private int _maskSmallUtilitySubgridMaxBlocks = 10;

        // --- Module 5: Adaptive TOI / Collision Detection ---
        private bool _enableAdaptiveCollision = true;
        private bool _enableSpeedThresholds = true;
        private float _continuousCollisionSpeedThreshold = 40.0f;
        private float _discreteCollisionSpeedThreshold = 15.0f;
        private bool _revertNearOtherDynamicGrids = true;
        private float _dynamicGridProximityRevertDistanceMeters = 500.0f;
        private bool _enableAltitudeTOIReversion = true;
        private float _continuousAltitudeThreshold = 50.0f;
        private bool _enforceDiscreteLargeGrids = false;
        private int _discreteLargeGridMinBlocks = 20;
        private bool _enforceDiscreteSmallGrids = false;
        private int _discreteSmallGridMinBlocks = 40;

        // --- Thruster Clearance Optimizer ---
        private bool _enableThrusterClearance = true;
        private ThrusterDamageMode _thrusterDamageMode = ThrusterDamageMode.Optimized;

        // --- Ship, Rover & Station Protection ---
        private bool _enableGridDefender = true;
        private float _collisionSpeedThreshold = 110.0f;
        private bool _protectAgainstRamming = true;
        private bool _protectAgainstVoxels = true;
        private bool _protectStaticGrids = true;
        private bool _protectSubgrids = true;
        private bool _protectAgainstFloatingObjects = true;
        private bool _suppressVoxelCutoutExplosions = true;

        // --- Player-Made Missiles (PMWs) ---
        private bool _allowPMWDamage = true;
        private bool _exemptPilotedFromMissileStatus = true;
        private int _pmwMinBlocksSmallGrid = 4;
        private int _pmwMaxBlocksSmallGrid = 150;
        private int _pmwMinBlocksLargeGrid = 3;
        private int _pmwMaxBlocksLargeGrid = 50;
        private float _pmwMinVelocity = 20.0f;

        // --- Layered Armor Occlusion ---
        private bool _enableLayeredArmorOcclusion = true;
        private bool _enforceStructuralArmorCheck = true;
        private float _voxelDeformationScale = 1.0f;

        // --- Group 4: Anti-Clang, Separation & Voxel Arbitrator ---
        private bool _enableAntiClangSystem = true;
        private bool _stopTorsionalDeathSpins = true;
        private bool _enableVoxelNormalArbitrator = true;
        private bool _enablePushApartSeparation = true;
        private float _impactVelocityDamping = 0.5f;
        private int _antiClangVibrationThreshold = 8;
        private int _pushApartThreshold = 25;
        private float _pushApartDistance = 0.5f;

        // --- Speed Gates & Rate Limits (Fallback) ---
        private float _minDrivingVelocity = 10.0f;
        private int _deformationCooldownFrames = 30;

        // ==========================================
        // General Properties
        // ==========================================
        public bool Enabled { get => _enabled; set => SetValue(ref _enabled, value); }
        public bool EnablePhysicsOptimizations { get => _enablePhysicsOptimizations; set => SetValue(ref _enablePhysicsOptimizations, value); }
        public bool EnableDebugLogging { get => _enableDebugLogging; set => SetValue(ref _enableDebugLogging, value); }
        public bool EnablePeriodicConsoleTelemetry { get => _enablePeriodicConsoleTelemetry; set => SetValue(ref _enablePeriodicConsoleTelemetry, value); }
        public int ConsoleTelemetryIntervalSeconds { get => _consoleTelemetryIntervalSeconds; set => SetValue(ref _consoleTelemetryIntervalSeconds, value); }

        public bool LogWheelOptimizer { get => _logWheelOptimizer; set => SetValue(ref _logWheelOptimizer, value); }
        public bool LogRigidBodySleep { get => _logRigidBodySleep; set => SetValue(ref _logRigidBodySleep, value); }
        public bool LogOreMerge { get => _logOreMerge; set => SetValue(ref _logOreMerge, value); }
        public bool LogSubgridStabilizer { get => _logSubgridStabilizer; set => SetValue(ref _logSubgridStabilizer, value); }
        public bool LogAdaptiveCollision { get => _logAdaptiveCollision; set => SetValue(ref _logAdaptiveCollision, value); }
        public bool LogGridDefender { get => _logGridDefender; set => SetValue(ref _logGridDefender, value); }
        public bool LogThrusterClearance { get => _logThrusterClearance; set => SetValue(ref _logThrusterClearance, value); }
        public bool LogMissileDefense { get => _logMissileDefense; set => SetValue(ref _logMissileDefense, value); }
        public bool LogVoxelNormals { get => _logVoxelNormals; set => SetValue(ref _logVoxelNormals, value); }

        public bool EnableWheelOptimizer { get => _enableWheelOptimizer; set => SetValue(ref _enableWheelOptimizer, value); }
        public bool EnableWheelCollisionFilter { get => _enableWheelCollisionFilter; set => SetValue(ref _enableWheelCollisionFilter, value); }
        public bool SleepParkedRovers { get => _sleepParkedRovers; set => SetValue(ref _sleepParkedRovers, value); }
        public float RoverSleepDelaySeconds { get => _roverSleepDelaySeconds; set => SetValue(ref _roverSleepDelaySeconds, value); }

        public bool EnableRigidBodySleep { get => _enableRigidBodySleep; set => SetValue(ref _enableRigidBodySleep, value); }
        public float SleepLinearVelocityThreshold { get => _sleepLinearVelocityThreshold; set => SetValue(ref _sleepLinearVelocityThreshold, value); }
        public float SleepAngularVelocityThreshold { get => _sleepAngularVelocityThreshold; set => SetValue(ref _sleepAngularVelocityThreshold, value); }
        public int IdleSecondsBeforeSleep { get => _idleSecondsBeforeSleep; set => SetValue(ref _idleSecondsBeforeSleep, value); }

        public bool EnableOreMerge { get => _enableOreMerge; set => SetValue(ref _enableOreMerge, value); }
        public bool AutoMergeNearbyOre { get => _autoMergeNearbyOre; set => SetValue(ref _autoMergeNearbyOre, value); }
        public float OreMergeRadiusMeters { get => _oreMergeRadiusMeters; set => SetValue(ref _oreMergeRadiusMeters, value); }
        public int OreMergeIntervalTicks { get => _oreMergeIntervalTicks; set => SetValue(ref _oreMergeIntervalTicks, value); }
        public int MaxSectorFloatingObjects { get => _maxSectorFloatingObjects; set => SetValue(ref _maxSectorFloatingObjects, value); }

        public bool EnableSubgridStabilizer { get => _enableSubgridStabilizer; set => SetValue(ref _enableSubgridStabilizer, value); }
        public bool EnableSubgridStabilization { get => _enableSubgridStabilization; set => SetValue(ref _enableSubgridStabilization, value); }
        public float SubgridRestVelocityThreshold { get => _subgridRestVelocityThreshold; set => SetValue(ref _subgridRestVelocityThreshold, value); }
        public int SubgridRestFramesThreshold { get => _subgridRestFramesThreshold; set => SetValue(ref _subgridRestFramesThreshold, value); }
        public bool MaskSmallUtilitySubgrids { get => _maskSmallUtilitySubgrids; set => SetValue(ref _maskSmallUtilitySubgrids, value); }
        public int MaskSmallUtilitySubgridMaxBlocks { get => _maskSmallUtilitySubgridMaxBlocks; set => SetValue(ref _maskSmallUtilitySubgridMaxBlocks, Math.Max(1, value)); }

        public bool EnableAdaptiveCollision { get => _enableAdaptiveCollision; set => SetValue(ref _enableAdaptiveCollision, value); }
        public bool EnableSpeedThresholds { get => _enableSpeedThresholds; set => SetValue(ref _enableSpeedThresholds, value); }
        public float ContinuousCollisionSpeedThreshold { get => _continuousCollisionSpeedThreshold; set => SetValue(ref _continuousCollisionSpeedThreshold, value); }
        public float DiscreteCollisionSpeedThreshold { get => _discreteCollisionSpeedThreshold; set => SetValue(ref _discreteCollisionSpeedThreshold, value); }
        public bool RevertNearOtherDynamicGrids { get => _revertNearOtherDynamicGrids; set => SetValue(ref _revertNearOtherDynamicGrids, value); }
        public float DynamicGridProximityRevertDistanceMeters { get => _dynamicGridProximityRevertDistanceMeters; set => SetValue(ref _dynamicGridProximityRevertDistanceMeters, Math.Max(10.0f, value)); }
        public bool EnableAltitudeTOIReversion { get => _enableAltitudeTOIReversion; set => SetValue(ref _enableAltitudeTOIReversion, value); }
        public float ContinuousAltitudeThreshold { get => _continuousAltitudeThreshold; set => SetValue(ref _continuousAltitudeThreshold, value); }
        public bool EnforceDiscreteLargeGrids { get => _enforceDiscreteLargeGrids; set => SetValue(ref _enforceDiscreteLargeGrids, value); }
        public int DiscreteLargeGridMinBlocks { get => _discreteLargeGridMinBlocks; set => SetValue(ref _discreteLargeGridMinBlocks, Math.Max(0, value)); }
        public bool EnforceDiscreteSmallGrids { get => _enforceDiscreteSmallGrids; set => SetValue(ref _enforceDiscreteSmallGrids, value); }
        public int DiscreteSmallGridMinBlocks { get => _discreteSmallGridMinBlocks; set => SetValue(ref _discreteSmallGridMinBlocks, Math.Max(0, value)); }

        public bool EnableThrusterClearance { get => _enableThrusterClearance; set => SetValue(ref _enableThrusterClearance, value); }

        public ThrusterDamageMode ThrusterDamageMode
        {
            get => _thrusterDamageMode;
            set
            {
                SetValue(ref _thrusterDamageMode, value);
                OnPropertyChanged(nameof(IsThrusterModeOptimized));
                OnPropertyChanged(nameof(IsThrusterModeVanillaLike));
            }
        }

        [XmlIgnore]
        public bool IsThrusterModeOptimized
        {
            get => _thrusterDamageMode == ThrusterDamageMode.Optimized;
            set { if (value) ThrusterDamageMode = ThrusterDamageMode.Optimized; }
        }

        [XmlIgnore]
        public bool IsThrusterModeVanillaLike
        {
            get => _thrusterDamageMode == ThrusterDamageMode.VanillaLike;
            set { if (value) ThrusterDamageMode = ThrusterDamageMode.VanillaLike; }
        }

        public bool EnableGridDefender { get => _enableGridDefender; set => SetValue(ref _enableGridDefender, value); }
        public float MaxDeformationVelocity { get => _collisionSpeedThreshold; set => SetValue(ref _collisionSpeedThreshold, Math.Max(0.0f, value)); }
        public bool ProtectShipsAgainstRamming
        {
            get => _protectAgainstRamming;
            set
            {
                SetValue(ref _protectAgainstRamming, value);
                OnPropertyChanged(nameof(IsSpeedGatesApplicable));
            }
        }
        public bool ProtectShipsAgainstVoxels
        {
            get => _protectAgainstVoxels;
            set
            {
                SetValue(ref _protectAgainstVoxels, value);
                OnPropertyChanged(nameof(IsSpeedGatesApplicable));
            }
        }
        public bool ProtectStaticGrids
        {
            get => _protectStaticGrids;
            set
            {
                SetValue(ref _protectStaticGrids, value);
                OnPropertyChanged(nameof(IsSpeedGatesApplicable));
            }
        }
        public bool ProtectSubgrids { get => _protectSubgrids; set => SetValue(ref _protectSubgrids, value); }
        public bool ProtectAgainstFloatingObjects { get => _protectAgainstFloatingObjects; set => SetValue(ref _protectAgainstFloatingObjects, value); }
        public bool SuppressAllVoxelExplosionDamage { get => _suppressVoxelCutoutExplosions; set => SetValue(ref _suppressVoxelCutoutExplosions, value); }

        public bool AllowMissileDamage { get => _allowPMWDamage; set => SetValue(ref _allowPMWDamage, value); }
        public bool ExemptPilotedFromMissileStatus { get => _exemptPilotedFromMissileStatus; set => SetValue(ref _exemptPilotedFromMissileStatus, value); }
        public int SmallGridMissileMinBlocks
        {
            get => _pmwMinBlocksSmallGrid;
            set
            {
                SetValue(ref _pmwMinBlocksSmallGrid, Math.Max(1, value));
                if (_pmwMaxBlocksSmallGrid < _pmwMinBlocksSmallGrid)
                {
                    SmallGridMissileMaxBlocks = _pmwMinBlocksSmallGrid;
                }
            }
        }
        public int SmallGridMissileMaxBlocks
        {
            get => _pmwMaxBlocksSmallGrid;
            set => SetValue(ref _pmwMaxBlocksSmallGrid, Math.Max(_pmwMinBlocksSmallGrid, value));
        }
        public int LargeGridMissileMinBlocks
        {
            get => _pmwMinBlocksLargeGrid;
            set
            {
                SetValue(ref _pmwMinBlocksLargeGrid, Math.Max(1, value));
                if (_pmwMaxBlocksLargeGrid < _pmwMinBlocksLargeGrid)
                {
                    LargeGridMissileMaxBlocks = _pmwMinBlocksLargeGrid;
                }
            }
        }
        public int LargeGridMissileMaxBlocks
        {
            get => _pmwMaxBlocksLargeGrid;
            set => SetValue(ref _pmwMaxBlocksLargeGrid, Math.Max(_pmwMinBlocksLargeGrid, value));
        }
        public float MissileMinVelocity { get => _pmwMinVelocity; set => SetValue(ref _pmwMinVelocity, Math.Max(0.0f, value)); }

        public bool EnableLayeredArmorOcclusion { get => _enableLayeredArmorOcclusion; set => SetValue(ref _enableLayeredArmorOcclusion, value); }
        public bool EnforceStructuralArmorCheck { get => _enforceStructuralArmorCheck; set => SetValue(ref _enforceStructuralArmorCheck, value); }
        public float DeformationMultiplier
        {
            get => _voxelDeformationScale;
            set => SetValue(ref _voxelDeformationScale, Math.Max(0.0f, Math.Min(1.0f, value)));
        }

        public bool EnableAntiClang { get => _enableAntiClangSystem; set => SetValue(ref _enableAntiClangSystem, value); }
        public bool StopClangSpinning { get => _stopTorsionalDeathSpins; set => SetValue(ref _stopTorsionalDeathSpins, value); }
        public bool EnableVoxelNormalArbitrator { get => _enableVoxelNormalArbitrator; set => SetValue(ref _enableVoxelNormalArbitrator, value); }
        public bool EnablePushApart { get => _enablePushApartSeparation; set => SetValue(ref _enablePushApartSeparation, value); }
        public float ImpactVelocityDamping
        {
            get => _impactVelocityDamping;
            set => SetValue(ref _impactVelocityDamping, Math.Max(0.0f, Math.Min(1.0f, value)));
        }
        public int AntiClangVibrationThreshold
        {
            get => _antiClangVibrationThreshold;
            set => SetValue(ref _antiClangVibrationThreshold, Math.Max(1, value));
        }
        public int PushApartThreshold
        {
            get => _pushApartThreshold;
            set => SetValue(ref _pushApartThreshold, Math.Max(5, value));
        }
        public float PushApartDistance
        {
            get => _pushApartDistance;
            set => SetValue(ref _pushApartDistance, Math.Max(0.1f, Math.Min(5.0f, value)));
        }

        public float MinDrivingVelocity
        {
            get => _minDrivingVelocity;
            set => SetValue(ref _minDrivingVelocity, Math.Max(0.0f, value));
        }
        public int DeformationCooldownFrames
        {
            get => _deformationCooldownFrames;
            set => SetValue(ref _deformationCooldownFrames, Math.Max(0, value));
        }

        [XmlIgnore]
        public bool IsSpeedGatesApplicable => !ProtectShipsAgainstRamming || !ProtectShipsAgainstVoxels || !ProtectStaticGrids;
    }
}
