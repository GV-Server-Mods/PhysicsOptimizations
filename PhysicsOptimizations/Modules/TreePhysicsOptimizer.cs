using System;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.WorldEnvironment;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Tree Physics Optimizer: suppresses server-side Havok static compound shape baking and
    /// rigid body allocation for planet environment tree sectors (MyEnvironmentSector).
    /// Prevents CPU-intensive shape construction, solver broadphase/narrowphase overhead,
    /// and contact point callbacks on the dedicated server.
    /// Requires the companion mod GVK_TreeNoPhysics on clients to ensure zero client desync.
    /// </summary>
    public class TreePhysicsOptimizer : IPhysicsOptimizer
    {
        private const string LogSource = "TreePhysicsOptimizer";

        public string Name => "Tree Physics Optimizer";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.DisableTreePhysics;

        private PhysicsOptimizerPlugin _plugin;

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            if (IsEnabled)
            {
                DisableExistingSectorPhysics();
            }
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
            if (config != null && config.Enabled && config.DisableTreePhysics)
            {
                DisableExistingSectorPhysics();
            }
        }

        public void OnEntityAdded(MyEntity entity)
        {
            if (!IsEnabled) return;

            // If a new planet is added, immediately ensure its loaded sectors have physics disabled
            if (entity is MyPlanet planet && planet.Hierarchy != null)
            {
                DisablePlanetSectorPhysics(planet);
            }
        }

        public void OnEntityRemoved(MyEntity entity)
        {
        }

        /// <summary>
        /// Scans existing planets and disables physics on any active sectors.
        /// </summary>
        public void DisableExistingSectorPhysics()
        {
            try
            {
                var entities = MyEntities.GetEntities();
                if (entities == null) return;

                foreach (var entity in entities)
                {
                    if (entity is MyPlanet planet && planet.Hierarchy != null)
                    {
                        DisablePlanetSectorPhysics(planet);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Error disabling existing sector physics.");
            }
        }

        private static void DisablePlanetSectorPhysics(MyPlanet planet)
        {
            if (planet?.Hierarchy == null) return;

            var children = planet.Hierarchy.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var sector = children[i]?.Container?.Entity as MyEnvironmentSector;
                if (sector != null && sector.Physics != null && sector.Physics.Enabled)
                {
                    sector.EnablePhysics(false);
                }
            }
        }

        /// <summary>
        /// Registers the MyEnvironmentSector detours via Torch's PatchManager.
        /// </summary>
        public static void RegisterPatches(PatchContext ctx)
        {
            try
            {
                var enablePhysicsMethod = typeof(MyEnvironmentSector).GetMethod(
                    "EnablePhysics",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new Type[] { typeof(bool) },
                    null);

                if (enablePhysicsMethod != null)
                {
                    var prefixMethod = typeof(TreePhysicsOptimizer).GetMethod(
                        nameof(Prefix_EnablePhysics),
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    ctx.GetPattern(enablePhysicsMethod).Prefixes.Add(prefixMethod);
                    PatchConflictAudit.RegisterTarget(enablePhysicsMethod);
                    Log.Info(LogSource, "Registered MyEnvironmentSector.EnablePhysics Prefix hook.");
                }
                else
                {
                    Log.Warn(LogSource, "Could not find MyEnvironmentSector.EnablePhysics method to patch!");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to register MyEnvironmentSector hooks!");
            }
        }

        /// <summary>
        /// Prefix hook on MyEnvironmentSector.EnablePhysics.
        /// When DisableTreePhysics is enabled, forces the requested physics state to false.
        /// This ensures the dedicated server never allocates HkStaticCompoundShape,
        /// never bakes collision geometries, and never creates MyPhysicsBody static rigid bodies.
        /// </summary>
        public static bool Prefix_EnablePhysics(MyEnvironmentSector __instance, ref bool physics)
        {
            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin?.Config != null && plugin.Config.Enabled && plugin.Config.DisableTreePhysics)
            {
                physics = false;
            }
            return true;
        }
    }
}

