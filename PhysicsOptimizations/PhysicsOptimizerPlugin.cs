using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Modules;
using PhysicsOptimizer.Services;
using PhysicsOptimizer.Views;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer
{
    public class PhysicsOptimizerPlugin : TorchPluginBase, IWpfPlugin
    {
        private const string LogSource = "Plugin";

        public static PhysicsOptimizerPlugin Instance { get; private set; }

        private Persistent<PhysicsOptimizerConfig> _config;
        private PhysicsOptimizerControl _control;
        private ulong _frameCounter;
        private readonly object _configLock = new();

        public PhysicsOptimizerConfig Config => _config?.Data;
        public OptimizationTelemetry Telemetry { get; private set; }
        public DefenseStatistics DefenseStats { get; private set; }

        public WheelOptimizer WheelOptimizer { get; private set; }
        public RigidBodySleep Sleep { get; private set; }
        public OreMerge OreMerge { get; private set; }
        public SubgridStabilizer SubgridStabilizer { get; private set; }
        public AdaptiveCollision AdaptiveCollision { get; private set; }
        public GridDefender GridDefender { get; private set; }

        private readonly List<IPhysicsModule> _modules = [];

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;

            LoadConfig();
            Telemetry = new OptimizationTelemetry();
            DefenseStats = new DefenseStatistics();

            InitializeModules();

            RegisterPatches();

            MyEntities.OnEntityAdd += OnEntityAdded;
            MyEntities.OnEntityRemove += OnEntityRemoved;

            Log.Info(LogSource, "Plugin v2.0.0 initialized successfully. Unified Havok engine is ACTIVE.");
        }

        private void InitializeModules()
        {
            WheelOptimizer = new WheelOptimizer();
            Sleep = new RigidBodySleep();
            OreMerge = new OreMerge();
            SubgridStabilizer = new SubgridStabilizer();
            AdaptiveCollision = new AdaptiveCollision();
            GridDefender = new GridDefender();

            _modules.Add(WheelOptimizer);
            _modules.Add(Sleep);
            _modules.Add(OreMerge);
            _modules.Add(SubgridStabilizer);
            _modules.Add(AdaptiveCollision);
            _modules.Add(GridDefender);

            foreach (var module in _modules)
            {
                module.Init(this);
            }
        }

        private void RegisterPatches()
        {
            try
            {
                if (Torch.Managers.GetManager(typeof(PatchManager)) is PatchManager patchManager)
                {
                    PatchConflictAudit.CapturePatchManager(patchManager);
                    var ctx = patchManager.AcquireContext();

                    WheelOptimizer.RegisterPatches(ctx);
                    RigidBodySleep.RegisterPatches(ctx);
                    SubgridStabilizer.RegisterPatches(ctx);
                    GridDefender.RegisterPatches(ctx);

                    patchManager.Commit();
                    Log.Info(LogSource, "All patches successfully registered with Torch PatchManager.");
                }
                else
                {
                    Log.Warn(LogSource, "Torch PatchManager not found. Optimizations requiring patches will not function.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Critical error during patch registration.");
            }
        }

        private void OnEntityAdded(MyEntity entity)
        {
            foreach (var module in _modules)
            {
                module.OnEntityAdded(entity);
            }
        }

        private void OnEntityRemoved(MyEntity entity)
        {
            foreach (var module in _modules)
            {
                module.OnEntityRemoved(entity);
            }
        }

        public override void Update()
        {
            base.Update();
            PatchConflictAudit.RunOnce();
            if (Config == null || !Config.Enabled) return;

            _frameCounter++;

            if (Sync.ServerSimulationRatio > 0f)
            {
                Telemetry?.UpdateServerSimulationSpeed(Sync.ServerSimulationRatio);
            }

            if (Config.EnablePhysicsOptimizations)
            {
                WheelOptimizer?.Update(_frameCounter);
                Sleep?.Update(_frameCounter);
                OreMerge?.Update(_frameCounter);
                SubgridStabilizer?.Update(_frameCounter);
                AdaptiveCollision?.Update(_frameCounter);
            }

            GridDefender?.Update(_frameCounter);

            int intervalTicks = Math.Max(1, Config.ConsoleTelemetryIntervalSeconds) * 60;
            if (Config.EnablePeriodicConsoleTelemetry && _frameCounter % (ulong)intervalTicks == 0)
            {
                EmitConsoleTelemetryHeartbeat();
            }
        }

        private void EmitConsoleTelemetryHeartbeat()
        {
            if (Telemetry == null) return;

            float speed = Telemetry.ServerSimulationSpeed;
            int act = Telemetry.ActiveRigidBodies;
            int slp = Telemetry.SleepingRigidBodies;
            int parked = Telemetry.ParkedRoversAsleep;
            int rovers = Telemetry.TrackedRoversCount;
            int whl = Telemetry.SleepingWheelsCount;
            int disc = Telemetry.DiscreteTOIGridsCount;
            int cont = Telemetry.ContinuousTOIGridsCount;
            long blk = DefenseStats?.TotalBlocked ?? 0;
            long pmw = DefenseStats?.MissileHitsAllowed ?? 0;
            long arr = DefenseStats?.ClangVibrationsArrested ?? 0;
            long inv = DefenseStats?.VoxelNormalsInverted ?? 0;

            Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                "[PhysOpt Heartbeat] Sim: {0:F2} | Bodies: {1} Act, {2} Slp | Rovers: {3}/{4} Slp ({5} whl) | TOI: {6} Disc, {7} Cont | Def: {8} Blk ({9} PMW) | Clang: {10} Arr, {11} Inv",
                speed, act, slp, parked, rovers, whl, disc, cont, blk, pmw, arr, inv));
        }

        public override void Dispose()
        {
            MyEntities.OnEntityAdd -= OnEntityAdded;
            MyEntities.OnEntityRemove -= OnEntityRemoved;

            foreach (var module in _modules)
            {
                module.Dispose();
            }
            _modules.Clear();

            base.Dispose();
        }

        public void LoadConfig()
        {
            var configPath = Path.Combine(StoragePath, "PhysicsOptimizer.cfg");
            try
            {
                _config = Persistent<PhysicsOptimizerConfig>.Load(configPath);
                if (_config.Data == null)
                {
                    Log.Warn(LogSource, "Config loaded as null, creating new default config.");
                    _config = new Persistent<PhysicsOptimizerConfig>(configPath, new PhysicsOptimizerConfig());
                }
                Log.Info(LogSource, $"Loaded config from {configPath}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Error loading configuration.");
                _config = new Persistent<PhysicsOptimizerConfig>(configPath, new PhysicsOptimizerConfig());
            }
        }

        public void SaveConfig()
        {
            try
            {
                lock (_configLock)
                {
                    _config.Save();
                }

                foreach (var module in _modules)
                {
                    module.UpdateConfig(Config);
                }

                Log.Info(LogSource, "Configuration saved successfully.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Error saving configuration.");
            }
        }

        public UserControl GetControl()
        {
            return _control ??= new PhysicsOptimizerControl(this);
        }
    }
}
