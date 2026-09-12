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
        private ulong _logTickCounter;
        private readonly object _configLock = new();

        public PhysicsOptimizerConfig Config => _config?.Data;
        public OptimizationTelemetry Telemetry { get; private set; }
        public DefenseStatistics DefenseStats { get; private set; }

        public WheelOptimizer WheelOptimizer { get; private set; }
        public RigidBodySleep RigidBodySleep { get; private set; }
        public OreMerge OreMerge { get; private set; }
        public SubgridStabilizer SubgridStabilizer { get; private set; }
        public AdaptiveCollision AdaptiveCollision { get; private set; }
        public GridDefender GridDefender { get; private set; }

        private readonly List<IPhysicsOptimizer> _optimizers = [];

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;

            LoadConfig();
            Telemetry = new OptimizationTelemetry();
            DefenseStats = new DefenseStatistics();

            InitializeOptimizers();

            RegisterPatches();

            MyEntities.OnEntityAdd += OnEntityAdded;
            MyEntities.OnEntityRemove += OnEntityRemoved;

            Log.Info(LogSource, "Plugin v2.0.0 initialized successfully. Unified Havok engine is ACTIVE.");
        }

        private void InitializeOptimizers()
        {
            WheelOptimizer = new WheelOptimizer();
            RigidBodySleep = new RigidBodySleep();
            OreMerge = new OreMerge();
            SubgridStabilizer = new SubgridStabilizer();
            AdaptiveCollision = new AdaptiveCollision();
            GridDefender = new GridDefender();

            _optimizers.Add(WheelOptimizer);
            _optimizers.Add(RigidBodySleep);
            _optimizers.Add(OreMerge);
            _optimizers.Add(SubgridStabilizer);
            _optimizers.Add(AdaptiveCollision);
            _optimizers.Add(GridDefender);

            foreach (var optimizer in _optimizers)
            {
                optimizer.Init(this);
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
                    ThrusterClearance.RegisterPatches(ctx);

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
            foreach (var optimizer in _optimizers)
            {
                optimizer.OnEntityAdded(entity);
            }
        }

        private void OnEntityRemoved(MyEntity entity)
        {
            foreach (var optimizer in _optimizers)
            {
                optimizer.OnEntityRemoved(entity);
            }
        }

        public override void Update()
        {
            base.Update();
            PatchConflictAudit.RunOnce();

            // Flush coalesced repeat summaries every 10s, even when optimizations are disabled.
            _logTickCounter++;
            if (_logTickCounter % 600UL == 0UL) Log.FlushRepeats();

            if (Config == null || !Config.Enabled) return;

            _frameCounter++;

            if (Sync.ServerSimulationRatio > 0f)
            {
                Telemetry?.UpdateServerSimulationSpeed(Sync.ServerSimulationRatio);
            }

            if (Config.EnablePhysicsOptimizations)
            {
                WheelOptimizer?.Update(_frameCounter);
                RigidBodySleep?.Update(_frameCounter);
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
            Log.FlushRepeats();

            foreach (var optimizer in _optimizers)
            {
                optimizer.Dispose();
            }
            _optimizers.Clear();

            base.Dispose();
            Instance = null;
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

                foreach (var optimizer in _optimizers)
                {
                    optimizer.UpdateConfig(Config);
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
