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

        private volatile Persistent<PhysicsOptimizerConfig> _config;
        private PhysicsOptimizerControl _control;
        private ulong _frameCounter;
        private ulong _logTickCounter;
        private readonly object _configLock = new();

        // Heartbeat rate-trackers (deltas between telemetry lines) and GC baselines.
        private long _prevContactCallbacks;
        private long _prevVoxelArbRaycasts;
        private long _prevPushApartExecuted;
        private int _prevGc0 = -1;
        private int _prevGc1 = -1;
        private int _prevGc2 = -1;

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
            Telemetry.Init(this);
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

            // Hourly Defense Digest (every 216,000 frames = 1 hour at 60 TPS)
            if (Config.EnableHourlyChatDigest && _frameCounter > 0 && _frameCounter % 216000UL == 0 && DefenseStats != null)
            {
                DefenseStats.GetHourlyDigest(out long hourBlk, out long allBlk, out long hourSep, out long allSep, out _, out _);
                ChatNotificationService.SendHourlyDefenseDigest(hourBlk, allBlk, hourSep, allSep);
            }

            // Prune expired cooldowns periodically (every 10s)
            if (_frameCounter % 600UL == 0)
            {
                ChatNotificationService.PruneExpiredCooldowns(_frameCounter);
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
            long inv = DefenseStats?.VoxelNormalsInverted ?? 0;
            long sep = DefenseStats?.GridsSeparated ?? 0;

            double seconds = Math.Max(1, Config?.ConsoleTelemetryIntervalSeconds ?? 30);
            long cb = DefenseStats?.ContactCallbacks ?? 0;
            long ar = DefenseStats?.VoxelArbitratorRaycasts ?? 0;
            long ps = DefenseStats?.PushApartActionsExecuted ?? 0;
            double cbRate = (cb - _prevContactCallbacks) / seconds;
            double arRate = (ar - _prevVoxelArbRaycasts) / seconds;
            double psRate = (ps - _prevPushApartExecuted) / seconds;
            _prevContactCallbacks = cb;
            _prevVoxelArbRaycasts = ar;
            _prevPushApartExecuted = ps;

            int gc0 = GC.CollectionCount(0);
            int gc1 = GC.CollectionCount(1);
            int gc2 = GC.CollectionCount(2);
            int dGc0 = _prevGc0 < 0 ? gc0 : gc0 - _prevGc0;
            int dGc1 = _prevGc1 < 0 ? gc1 : gc1 - _prevGc1;
            int dGc2 = _prevGc2 < 0 ? gc2 : gc2 - _prevGc2;
            string gcStr = string.Format(CultureInfo.InvariantCulture, "{0}/{1}/{2}", dGc0, dGc1, dGc2);
            _prevGc0 = gc0;
            _prevGc1 = gc1;
            _prevGc2 = gc2;
            double managedMb = GC.GetTotalMemory(false) / 1048576.0;

            DefenseStats?.UpdateRates(cbRate, arRate, psRate, dGc0, dGc1, dGc2, managedMb);

            string offendersStr = "";
            var offenders = Modules.GridDefender.GetActiveClangers(minRate: 10, maxResults: 2);
            if (offenders != null && offenders.Count > 0)
            {
                offendersStr = " | Clangers: " + string.Join(", ", System.Linq.Enumerable.Select(offenders, o => $"'{o.Name}' ({o.Rate} clangs/s)"));
            }

            Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                "[PhysOpt Heartbeat] Sim: {0:F2} | Bodies: {1} Act, {2} Slp | Rovers: {3}/{4} Slp ({5} whl) | TOI: {6} Disc, {7} Cont | Def: {8} Blk ({9} PMW) | Fixes: {10} Invert, {11} Nudge | Hot: {12:F0} cb/s, {13:F1} ar/s, {14:F2} push/s | GC: {15} ({16:F0} MB){17}",
                speed, act, slp, parked, rovers, whl, disc, cont, blk, pmw, inv, sep, cbRate, arRate, psRate, gcStr, managedMb, offendersStr));
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
                var loaded = Persistent<PhysicsOptimizerConfig>.Load(configPath);
                if (loaded.Data == null)
                {
                    Log.Warn(LogSource, "Config loaded as null, creating new default config.");
                    loaded = new Persistent<PhysicsOptimizerConfig>(configPath, new PhysicsOptimizerConfig());
                }
                lock (_configLock)
                {
                    _config = loaded;
                }
                ResetDebugOnlyDiagnostics();
                Log.Info(LogSource, $"Loaded config from {configPath}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Error loading configuration.");
                lock (_configLock)
                {
                    _config = new Persistent<PhysicsOptimizerConfig>(configPath, new PhysicsOptimizerConfig());
                }
            }
        }

        /// <summary>
        /// Debug-only diagnostics never persist across restarts: verbose logging and both debug GPS
        /// marker draws are forced off on load, then written back so a stale config file cannot
        /// leave them running in normal play. Re-enabling at runtime lasts for that session only.
        /// </summary>
        private void ResetDebugOnlyDiagnostics()
        {
            PhysicsOptimizerConfig cfg = Config;
            if (cfg == null) return;

            if (!cfg.EnableDebugLogging && !cfg.EnableVoxelNormalArbitratorDebugDraw && !cfg.EnablePushApartDebugDraw) return;

            cfg.EnableDebugLogging = false;
            cfg.EnableVoxelNormalArbitratorDebugDraw = false;
            cfg.EnablePushApartDebugDraw = false;
            Log.Info(LogSource, "Debug-only diagnostics (verbose logging, push-apart and voxel-arbitrator debug GPS markers) reset to off.");

            try
            {
                lock (_configLock)
                {
                    _config.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, LogSource, "Could not persist debug diagnostic defaults; runtime state is unaffected.");
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
