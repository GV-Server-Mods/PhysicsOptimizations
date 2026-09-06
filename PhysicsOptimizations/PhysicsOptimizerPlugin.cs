using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using NLog;
using Sandbox.Game.Entities;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using GVK.PhysicsOptimizations.Config;
using GVK.PhysicsOptimizations.Modules;
using GVK.PhysicsOptimizations.Patches;
using GVK.PhysicsOptimizations.Services;
using GVK.PhysicsOptimizations.Views;

namespace GVK.PhysicsOptimizations
{
    /// <summary>
    /// Main Torch server plugin entry point for GVK Physics Optimizations.
    /// Manages physics modules, configuration, telemetry, and native Torch method patches.
    /// </summary>
    public class PhysicsOptimizerPlugin : TorchPluginBase, IWpfPlugin
    {
        public static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer");

        public static PhysicsOptimizerPlugin Instance { get; private set; }

        private Persistent<PhysicsOptimizerConfig> _config;
        private PhysicsOptimizerControl _control;
        private ulong _frameCounter;
        private readonly object _configLock = new();

        public PhysicsOptimizerConfig Config => _config?.Data;
        public OptimizationTelemetry Telemetry { get; private set; }

        public WheelOptimizerModule WheelOptimizer { get; private set; }
        public RigidBodySleepModule SleepManager { get; private set; }
        public FloatingObjectModule OreOptimizer { get; private set; }
        public SubgridStabilizerModule SubgridStabilizer { get; private set; }
        public AdaptiveCollisionModule AdaptiveCollision { get; private set; }

        private readonly List<IPhysicsModule> _modules = [];

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;

            LoadConfig();
            Telemetry = new();

            InitializeModules();
            RegisterPatches();

            MyEntities.OnEntityAdd += OnEntityAdded;
            MyEntities.OnEntityRemove += OnEntityRemoved;

            Log.Info("[PhysicsOptimizer] Plugin initialized successfully. Havok optimization engine is ACTIVE.");
        }

        private void InitializeModules()
        {
            WheelOptimizer = new();
            SleepManager = new();
            OreOptimizer = new();
            SubgridStabilizer = new();
            AdaptiveCollision = new();

            _modules.Clear();
            _modules.AddRange([WheelOptimizer, SleepManager, OreOptimizer, SubgridStabilizer, AdaptiveCollision]);

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
                    var ctx = patchManager.AcquireContext();
                    MotorSuspensionPatch.Patch(ctx);
                    CockpitInputWakePatch.Patch(ctx);
                    GridDamageWakePatch.Patch(ctx);
                    patchManager.Commit();
                    Log.Info("[PhysicsOptimizer] All patches successfully registered with Torch PatchManager.");
                }
                else
                {
                    Log.Error("[PhysicsOptimizer] Torch PatchManager not found! Unable to register physics patches.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PhysicsOptimizer] Error registering Torch patches!");
            }
        }

        public override void Update()
        {
            base.Update();

            if (Config == null || !Config.Enabled)
            {
                return;
            }

            _frameCounter++;

            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].Update(_frameCounter);
            }
        }

        public void LoadConfig()
        {
            try
            {
                string configPath = Path.Combine(StoragePath, "PhysicsOptimizer.cfg");
                _config = Persistent<PhysicsOptimizerConfig>.Load(configPath);
                if (_config?.Data == null)
                {
                    _config = new(configPath, new());
                    _config.Save();
                }

                NotifyConfigUpdated();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PhysicsOptimizer] Failed to load configuration file! Creating default config.");
                _config = new(Path.Combine(StoragePath, "PhysicsOptimizer.cfg"), new());
                NotifyConfigUpdated();
            }
        }

        public void SaveConfig(bool async = true)
        {
            if (async)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_configLock)
                    {
                        InternalSaveConfig();
                    }
                });
            }
            else
            {
                lock (_configLock)
                {
                    InternalSaveConfig();
                }
            }
        }

        private void InternalSaveConfig()
        {
            try
            {
                _config?.Save();
                NotifyConfigUpdated();
                Log.Info("[PhysicsOptimizer] Configuration saved.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PhysicsOptimizer] Failed to save configuration file!");
            }
        }

        private void OnEntityAdded(MyEntity entity)
        {
            if (entity == null) return;
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].OnEntityAdded(entity);
            }
        }

        private void OnEntityRemoved(MyEntity entity)
        {
            if (entity == null) return;
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].OnEntityRemoved(entity);
            }
        }

        private void NotifyConfigUpdated()
        {
            if (Config == null) return;
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].UpdateConfig(Config);
            }
        }

        public UserControl GetControl()
        {
            return _control ??= new(this);
        }

        public override void Dispose()
        {
            try
            {
                MyEntities.OnEntityAdd -= OnEntityAdded;
                MyEntities.OnEntityRemove -= OnEntityRemoved;

                SaveConfig(async: false);

                for (int i = 0; i < _modules.Count; i++)
                {
                    _modules[i].Dispose();
                }
                _modules.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PhysicsOptimizer] Error during Dispose.");
            }

            base.Dispose();
            _control = null;
            Telemetry = null;
            WheelOptimizer = null;
            SleepManager = null;
            OreOptimizer = null;
            SubgridStabilizer = null;
            AdaptiveCollision = null;
            Instance = null;
        }
    }
}

