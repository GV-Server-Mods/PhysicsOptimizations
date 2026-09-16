using System;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Engine.Physics;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;
using VRageMath;
using PhysicsOptimizer.Utils;
using PhysicsOptimizer.Services;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Torch PatchManager patches for Space Engineers grid lifecycle management.
    /// 1. OnConvertToDynamic Postfix: Universal physics awakening and module state synchronization.
    /// 2. OnConvertedToShipRequest Prefix: Seamless auto-rescue when players convert mobile vehicles in the Terminal near voxels.
    /// </summary>
    public static class PhysicsOptimizerPatches
    {
        private const string LogSource = "Patches";

        private static readonly MethodInfo _shouldBeStaticMethod =
            typeof(MyCubeGrid).GetMethod("ShouldBeStatic", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly Type _testDynamicReasonType =
            typeof(MyCubeGrid).Assembly.GetType("Sandbox.Game.Entities.MyCubeGrid+MyTestDynamicReason");

        public static void RegisterPatches(PatchContext ctx)
        {
            // 1. Hook MyCubeGrid.OnConvertToDynamic (Postfix) for universal physics awakening
            try
            {
                MethodInfo dynamicMethod = typeof(MyCubeGrid).GetMethod("OnConvertToDynamic", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (dynamicMethod != null)
                {
                    MethodInfo postfix = typeof(PhysicsOptimizerPatches).GetMethod(nameof(Postfix_OnConvertToDynamic), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    ctx.GetPattern(dynamicMethod).Suffixes.Add(postfix);
                    PatchConflictAudit.RegisterTarget(dynamicMethod);
                    Log.Info(LogSource, "Registered MyCubeGrid.OnConvertToDynamic Postfix hook.");
                }
                else
                {
                    Log.Warn(LogSource, "Could not find MyCubeGrid.OnConvertToDynamic method to patch!");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyCubeGrid.OnConvertToDynamic!");
            }

            // 2. Hook MyCubeGrid.OnConvertedToShipRequest (Prefix) for Terminal auto-rescue
            try
            {
                MethodInfo shipRequestMethod = typeof(MyCubeGrid).GetMethod("OnConvertedToShipRequest", BindingFlags.Instance | BindingFlags.NonPublic);
                if (shipRequestMethod != null)
                {
                    MethodInfo prefix = typeof(PhysicsOptimizerPatches).GetMethod(nameof(Prefix_OnConvertedToShipRequest), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    ctx.GetPattern(shipRequestMethod).Prefixes.Add(prefix);
                    PatchConflictAudit.RegisterTarget(shipRequestMethod);
                    Log.Info(LogSource, "Registered MyCubeGrid.OnConvertedToShipRequest Prefix hook.");
                }
                else
                {
                    Log.Warn(LogSource, "Could not find MyCubeGrid.OnConvertedToShipRequest method to patch!");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyCubeGrid.OnConvertedToShipRequest!");
            }
        }

        /// <summary>
        /// Postfix executed whenever ANY grid converts from static to dynamic across the server.
        /// Universally forces Havok rigid body activation to resolve Keen's dynamic-sleep bug,
        /// unmarks fixed root status, and synchronizes module tracking state.
        /// </summary>
        public static void Postfix_OnConvertToDynamic(MyCubeGrid __instance)
        {
            if (!Sync.IsServer || __instance == null || __instance.MarkedForClose || __instance.Closed) return;

            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin == null || plugin.Config == null || !plugin.Config.Enabled) return;

            // 1. Force activate Havok physics and rigid body
            if (__instance.Physics != null)
            {
                __instance.Physics.ForceActivate();
                __instance.Physics.RigidBody?.Activate();
            }

            // 2. Clear fixed root marker if left behind
            MyFixedGrids.UnmarkGridRoot(__instance);
            MyGridPhysicalHierarchy.Static?.UpdateRoot(__instance);

            // 3. Module state synchronization (each gated behind its own master/module IsEnabled check)
            if (plugin.Config.EnablePhysicsOptimizations)
            {
                if (plugin.AdaptiveCollision != null && plugin.AdaptiveCollision.IsEnabled)
                {
                    plugin.AdaptiveCollision.ResetGridQuality(__instance.EntityId);
                }

                if (plugin.RigidBodySleep != null && plugin.RigidBodySleep.IsEnabled)
                {
                    plugin.RigidBodySleep.WakeGrid(__instance, "DynamicConversion");
                }

                if (plugin.WheelOptimizer != null && plugin.WheelOptimizer.IsEnabled)
                {
                    plugin.WheelOptimizer.WakeRover(__instance.EntityId, "DynamicConversion");
                }
            }

            if (plugin.GridDefender != null && plugin.GridDefender.IsEnabled)
            {
                plugin.GridDefender.ResetGridAttempts(__instance.EntityId);
            }
        }

        /// <summary>
        /// Prefix executed when a player requests "Convert to Ship" via the in-game Terminal.
        /// If vanilla would fail solely due to ~1m voxel clearance on a mobile rover or aircraft,
        /// automatically performs a one-shot rescue lift (+2.5m) and converts it dynamically.
        /// Genuine static stations, underground bunkers, and grids in combat run vanilla.
        /// Note: parameter 'reason' matches MyTestDynamicReason.ConvertToShip (int 4).
        /// </summary>
        public static bool Prefix_OnConvertedToShipRequest(MyCubeGrid __instance, int reason)
        {
            if (!Sync.IsServer) return true;

            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin == null || plugin.Config == null || !plugin.Config.Enabled) return true;
            if (!plugin.Config.EnableTerminalConvertToShipAutoRescue) return true;

            if (__instance == null || __instance.MarkedForClose || __instance.Closed) return true;
            if (!__instance.IsStatic || __instance.Physics == null || __instance.BlocksCount == 0) return true;
            if (reason != 4) return true; // 4 = MyTestDynamicReason.ConvertToShip

            // Verify sender identity etiquette: never swallow clicks if sender identity cannot be resolved
            ulong senderSteamId = MyEventContext.Current.Sender.Value;
            if (senderSteamId == 0L) return true;

            long senderIdentityId = Sync.Players.TryGetIdentityId(senderSteamId);
            if (senderIdentityId == 0L) return true;

            // Fail-fast gate 1: Must be classified as a rover, aircraft, or small-grid vehicle
            if (!GridUtils.IsRoverOrAircraftOrVehicle(__instance))
            {
                return true; // Genuine static stations or bases run vanilla
            }

            // Fail-fast gate 2: If vanilla would already succeed, let vanilla convert it in place without pushing
            if (_shouldBeStaticMethod != null && _testDynamicReasonType != null)
            {
                try
                {
                    object reasonEnum = Enum.ToObject(_testDynamicReasonType, reason);
                    bool shouldBeStatic = (bool)_shouldBeStaticMethod.Invoke(null, new object[] { __instance, reasonEnum, false });
                    if (!shouldBeStatic)
                    {
                        return true; // Vanilla succeeds naturally in place
                    }
                }
                catch
                {
                    // Fall back to bounding box check
                    BoundingBoxD box = __instance.PositionComp.WorldAABB;
                    if (!MyGamePruningStructure.AnyVoxelMapInBox(ref box))
                    {
                        return true;
                    }
                }
            }
            else
            {
                BoundingBoxD box = __instance.PositionComp.WorldAABB;
                if (!MyGamePruningStructure.AnyVoxelMapInBox(ref box))
                {
                    return true; // No voxels present, vanilla succeeds naturally
                }
            }

            // Gate 3: Combat damage cooldown check (do not rescue during active combat)
            if (PlayerRescueService.IsInCombat(__instance, plugin.Config.PlayerRescueCombatCooldownSeconds, out _))
            {
                return true; // Let vanilla reject with OnConvertToShipFailed
            }

            // Gate 4: Heightmap center check (reject deeply submerged or subterranean structures)
            Vector3D center = __instance.PositionComp.WorldVolume.Center;
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(center);
            if (planet != null)
            {
                Vector3D planetCore = planet.PositionComp.WorldVolume.Center;
                Vector3D surfacePoint = planet.GetClosestSurfacePointGlobal(ref center);
                double centerDist = (center - planetCore).Length();
                double surfaceDist = (surfacePoint - planetCore).Length();
                if (centerDist < surfaceDist)
                {
                    // Center below terrain heightmap: let vanilla fail
                    return true;
                }
            }

            // Gate 5: Perform the one-shot rescue push (+2.5m lift, landing gear unlock, and dynamic conversion)
            var defender = plugin.GridDefender;
            if (defender == null || !defender.IsEnabled) return true;

            double rescueDist = plugin.Config.PlayerRescuePushDistance;
            bool upright = plugin.Config.PlayerRescueUprightFlipped;

            bool success = defender.ExecuteRescuePush(__instance, rescueDist, upright, isAdmin: false, out string failureReason);
            if (success)
            {
                // Successfully queued rescue lift and dynamic conversion!
                // Return false to skip vanilla's OnConvertToShipFailed event.
                return false;
            }

            // If rescue push failed (e.g. OBB penetration > 4.0m ceiling), let vanilla run
            return true;
        }
    }
}
