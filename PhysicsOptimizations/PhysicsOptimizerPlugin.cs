using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;
using HarmonyLib;
using NLog;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using Torch.Managers.PatchManager;
using GVK.PhysicsOptimizations.Config;
using GVK.PhysicsOptimizations.Modules;
using GVK.PhysicsOptimizations.Patches;
using GVK.PhysicsOptimizations.Services;
using GVK.PhysicsOptimizations.Views;

namespace GVK.PhysicsOptimizations
{
    public class PhysicsOptimizerPlugin : TorchPluginBase, IWpfPlugin
    {
        public static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer");

        public static PhysicsOptimizerPlugin Instance { get; private set; }

        private Persistent<PhysicsOptimizerConfig> _config;
        private PhysicsOptimizerControl _control;
        private ulong _frameCounter;

        public PhysicsOptimizerConfig Config => _config?.Data;
        public OptimizationTelemetry Telemetry { get; private set; }

        public WheelOptimizerModule WheelOptimizer { get; private set; }
        public RigidBodySleepModule SleepManager { get; private set; }
        public FloatingObjectModule OreOptimizer { get; private set; }
        public SubgridStabilizerModule SubgridStabilizer { get; private set; }
        public AdaptiveCollisionModule AdaptiveCollision { get; private set; }

        private readonly List<IPhysicsModule> _modules = new List<IPhysicsModule>();

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;

            LoadConfig();
            Telemetry = new OptimizationTelemetry();

            InitializeModules();
            RegisterPatches();

            Log.Info("[PhysicsOptimizer] Plugin initialized successfully. Havok optimization engine is ACTIVE.");
        }

        private void InitializeModules()
        {
            WheelOptimizer = new WheelOptimizerModule();
            SleepManager = new RigidBodySleepModule();
            OreOptimizer = new FloatingObjectModule();
            SubgridStabilizer = new SubgridStabilizerModule();
            AdaptiveCollision = new AdaptiveCollisionModule();

            _modules.Clear();
            _modules.Add(WheelOptimizer);
            _modules.Add(SleepManager);
            _modules.Add(OreOptimizer);
            _modules.Add(SubgridStabilizer);
            _modules.Add(AdaptiveCollision);

            foreach (var module in _modules)
            {
                module.Init(this);
            }
        }

        private void RegisterPatches()
        {
            try
            {
                var patchManager = Torch.Managers.GetManager(typeof(PatchManager)) as PatchManager;
                if (patchManager != null)
                {
                    var ctx = patchManager.AcquireContext();
                    MotorSuspensionPatch.Patch(ctx);
                    CockpitInputWakePatch.Patch(ctx);
                    GridDamageWakePatch.Patch(ctx);
                    patchManager.Commit();
                    Log.Info("[PhysicsOptimizer] All Harmony patches successfully registered with Torch PatchManager.");
                }
                else
                {
                    Log.Warn("[PhysicsOptimizer] Torch PatchManager not found. Falling back to HarmonyLib directly.");
                    var harmony = new Harmony("GVK.PhysicsOptimizations");
                    harmony.PatchAll(typeof(PhysicsOptimizerPlugin).Assembly);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PhysicsOptimizer] Error registering Harmony patches!");
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
                    _config = new Persistent<PhysicsOptimizerConfig>(configPath, new PhysicsOptimizerConfig());
                    _config.Save();
                }

                NotifyConfigUpdated();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PhysicsOptimizer] Failed to load configuration file! Creating default config.");
                _config = new Persistent<PhysicsOptimizerConfig>(Path.Combine(StoragePath, "PhysicsOptimizer.cfg"), new PhysicsOptimizerConfig());
                NotifyConfigUpdated();
            }
        }

        public void SaveConfig()
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
            return _control ?? (_control = new PhysicsOptimizerControl(this));
        }

        public override void Dispose()
        {
            try
            {
                SaveConfig();

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

