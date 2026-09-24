using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using Torch;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Vanilla Trim: stops vanilla session components that can never do anything on GVK from loading,
    /// and stops the procedural world generator tracking entities while it has nothing to generate.
    /// Block-level trims (WC turret logic) live in VanillaTrimBlocks.
    /// Component skips are decided at world load (restart required); nothing is written to the save,
    /// so removing the plugin restores vanilla behavior.
    /// </summary>
    public class VanillaTrim : ViewModel, IPhysicsOptimizer
    {
        private const string LogSource = "VanillaTrim";

        // Verified against the decompiled server: none is IsRequiredByGame, and no game code or installed
        // mod dereferences them without a null check. Never add a component here without the same audit.
        private const string PlanetaryEncountersType = "SpaceEngineers.Game.SessionComponents.MyPlanetaryEncountersGenerator";
        private const string ItemTriggeredContractsType = "SpaceEngineers.Game.SessionComponents.MySessionComponentItemTriggeredContracts";
        private const string GridTriggeredContractsType = "SpaceEngineers.Game.SessionComponents.MySessionComponentGridTriggeredContracts";
        private const string ContractRiskType = "SpaceEngineers.Game.SessionComponents.MyContractRiskSessionComponent";

        private static readonly object SkippedLock = new();
        private static readonly List<string> SkippedComponents = new();
        private static long _entitiesNotTracked;
        private static float _forcedEncounterDensityFrom;

        public string Name => "Vanilla Trim";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnableVanillaTrim;

        private PhysicsOptimizerPlugin _plugin;

        public static long EntitiesNotTracked => Interlocked.Read(ref _entitiesNotTracked);

        public string StatusText
        {
            get
            {
                string skipped = GetSkippedSummary();
                return $"Skipped at last world load: {skipped}\n{GetEncounterDensitySummary()}\nEntities not tracked by the procedural generator this session: {EntitiesNotTracked}\n" +
                       $"Vanilla turret target searches / ammo pulls skipped this session: {VanillaTrimBlocks.TurretSearchesSkipped} / {VanillaTrimBlocks.TurretAmmoPullsSkipped}";
            }
        }

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            Log.Info(LogSource, "Initialized successfully.");
        }

        public void Update(ulong frameCounter)
        {
            // Passive patch-based optimization; no per-tick polling needed.
        }

        public void Dispose()
        {
            _plugin = null;
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
        }

        public void OnEntityAdded(MyEntity entity)
        {
        }

        public void OnEntityRemoved(MyEntity entity)
        {
        }

        public void RefreshStatus() => OnPropertyChanged(nameof(StatusText));

        /// <summary>Clears per-session status once a world has unloaded, ready for the next load.</summary>
        public static void ResetSessionStatus()
        {
            lock (SkippedLock)
            {
                SkippedComponents.Clear();
            }
            Interlocked.Exchange(ref _entitiesNotTracked, 0);
            _forcedEncounterDensityFrom = 0f;
            VanillaTrimBlocks.ResetSessionStatus();
        }

        public static string GetEncounterDensitySummary()
        {
            float from = _forcedEncounterDensityFrom;
            return from > 0f
                ? $"Encounter density forced to 0 at last world load (world/config had {from.ToString("0.##", CultureInfo.InvariantCulture)})"
                : "Encounter density not forced at last world load (already 0 or trim off)";
        }

        public static string GetSkippedSummary()
        {
            lock (SkippedLock)
            {
                return SkippedComponents.Count == 0 ? "none" : string.Join(", ", SkippedComponents);
            }
        }

        /// <summary>
        /// Registers the session component load gate and the procedural tracking gate via Torch's PatchManager.
        /// </summary>
        public static void RegisterPatches(PatchContext ctx)
        {
            RegisterPrefix(ctx,
                typeof(MySession).GetMethod("TryRegisterSessionComponent", BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(Type), typeof(bool), typeof(MyModContext) }, null),
                typeof(VanillaTrim), nameof(Prefix_TryRegisterSessionComponent),
                "MySession.TryRegisterSessionComponent");

            RegisterPrefix(ctx,
                typeof(MyProceduralWorldGenerator).GetMethod("TrackEntity", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(MyEntity) }, null),
                typeof(VanillaTrim), nameof(Prefix_TrackEntity),
                "MyProceduralWorldGenerator.TrackEntity");

            RegisterPrefix(ctx,
                typeof(MyProceduralWorldGenerator).GetMethod("LoadData", BindingFlags.Instance | BindingFlags.Public, null,
                    Type.EmptyTypes, null),
                typeof(VanillaTrim), nameof(Prefix_ProceduralLoadData),
                "MyProceduralWorldGenerator.LoadData");

            VanillaTrimBlocks.RegisterPatches(ctx);
        }

        internal static void RegisterPrefix(PatchContext ctx, MethodInfo target, Type prefixOwner, string prefixName, string label)
        {
            try
            {
                if (target == null)
                {
                    Log.Warn(LogSource, $"Could not find {label} to patch! That trim is inactive.");
                    return;
                }

                var prefix = prefixOwner.GetMethod(prefixName, BindingFlags.Static | BindingFlags.Public);
                ctx.GetPattern(target).Prefixes.Add(prefix);
                PatchConflictAudit.RegisterTarget(target);
                Log.Info(LogSource, $"Registered {label} Prefix hook.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, $"Failed to register {label} hook!");
            }
        }

        /// <summary>
        /// Prefix hook on MySession.TryRegisterSessionComponent. Skips the enabled trim targets before
        /// they are constructed, so their LoadData, event hooks and update ticks never run.
        /// Mod assemblies are never touched.
        /// </summary>
        public static bool Prefix_TryRegisterSessionComponent(Type type, bool modAssembly)
        {
            if (modAssembly || type == null) return true;

            var cfg = PhysicsOptimizerPlugin.Instance?.Config;
            if (cfg == null || !cfg.Enabled || !cfg.EnableVanillaTrim) return true;
            if (!IsTrimmed(cfg, type.FullName)) return true;

            lock (SkippedLock)
            {
                if (!SkippedComponents.Contains(type.Name))
                    SkippedComponents.Add(type.Name);
            }
            Log.Info(LogSource, $"Skipped vanilla session component {type.Name}.");
            return false;
        }

        private static bool IsTrimmed(PhysicsOptimizerConfig cfg, string fullName)
        {
            switch (fullName)
            {
                case PlanetaryEncountersType: return cfg.TrimPlanetaryEncounters;
                case ItemTriggeredContractsType: return cfg.TrimItemTriggeredContracts;
                case GridTriggeredContractsType: return cfg.TrimGridTriggeredContracts;
                case ContractRiskType: return cfg.TrimContractRisk;
                default: return false;
            }
        }

        /// <summary>
        /// Prefix hook on MyProceduralWorldGenerator.LoadData. Keen only serializes EncounterDensity when it is
        /// above 0 while its field default is 0.35, so a configured 0 is dropped on save and reloads as 0.35.
        /// Forcing it here, right before the generator reads it, is the only way 0 sticks. The generator is
        /// the sole runtime reader of EncounterDensity, so nothing else is affected.
        /// </summary>
        public static void Prefix_ProceduralLoadData()
        {
            var cfg = PhysicsOptimizerPlugin.Instance?.Config;
            if (cfg == null || !cfg.Enabled || !cfg.EnableVanillaTrim || !cfg.DisableProceduralEncounters) return;

            var settings = MySession.Static?.Settings;
            if (settings == null || settings.EncounterDensity == 0f) return;

            _forcedEncounterDensityFrom = settings.EncounterDensity;
            settings.EncounterDensity = 0f;
            Log.Info(LogSource, $"Forced EncounterDensity from {_forcedEncounterDensityFrom.ToString(CultureInfo.InvariantCulture)} to 0 before procedural generator load.");
        }

        /// <summary>
        /// Prefix hook on MyProceduralWorldGenerator.TrackEntity. With economy off and both asteroid and
        /// encounter density at 0 the generator has no module that can produce anything, so tracking
        /// every character and dynamic grid is pure overhead. Any of those settings on restores vanilla.
        /// </summary>
        public static bool Prefix_TrackEntity()
        {
            var cfg = PhysicsOptimizerPlugin.Instance?.Config;
            if (cfg == null || !cfg.Enabled || !cfg.EnableVanillaTrim || !cfg.DisableProceduralEncounters) return true;

            var settings = MySession.Static?.Settings;
            if (settings == null || settings.EnableEconomy || settings.ProceduralDensity != 0f || settings.EncounterDensity != 0f) return true;

            Interlocked.Increment(ref _entitiesNotTracked);
            return false;
        }
    }
}
