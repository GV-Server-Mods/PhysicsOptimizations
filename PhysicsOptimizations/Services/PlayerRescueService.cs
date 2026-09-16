using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Commands;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Modules;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Services
{
    /// <summary>
    /// Coordinates player and admin vehicle self-rescues (!unstuckit, !rescue, !unstuck).
    /// Enforces rigorous anti-exploit validations (stationary thresholds with Clang-jitter exemption,
    /// combat damage cooldown, hostile proximity, terrain proximity, and per-player/grid rate limits).
    /// Dispatches one-shot macroscopic terrain normal push-apart via GridDefender.
    /// </summary>
    public static class PlayerRescueService
    {
        private const string LogSource = "RescueService";

        private struct PendingRescue
        {
            public ulong SteamId;
            public long IdentityId;
            public long GridId;
            public bool IsOnFoot;
            public Vector3D StartPosition;
            public ulong StartFrame;
            public ulong FinishFrame;
            public int LastSecondReported;
        }

        private static readonly ConcurrentDictionary<long, DateTime> _lastGridDamageTimesUtc = new();
        private static readonly ConcurrentDictionary<ulong, DateTime> _playerCooldownsUtc = new();
        private static readonly ConcurrentDictionary<long, DateTime> _gridCooldownsUtc = new();
        private static readonly ConcurrentDictionary<ulong, PendingRescue> _pendingRescues = new();

        /// <summary>
        /// Zero-allocation hot-path damage hook called directly from RaiseAfterDamageApplied.
        /// Records combat damage timestamp to enforce combat delay gates.
        /// </summary>
        public static void RecordGridDamage(long gridId)
        {
            if (gridId != 0)
            {
                _lastGridDamageTimesUtc[gridId] = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Evicts tracking records when a grid entity is removed from the world.
        /// </summary>
        public static void OnEntityRemoved(long entityId)
        {
            _lastGridDamageTimesUtc.TryRemove(entityId, out _);
            _gridCooldownsUtc.TryRemove(entityId, out _);
        }

        /// <summary>
        /// Attempts to find the grid the player is currently piloting or sitting in.
        /// Supports cockpits, passenger seats, cryo chambers, and remote controls.
        /// </summary>
        public static bool TryGetPilotedOrOccupiedGrid(MyPlayer player, out MyCubeGrid grid)
        {
            grid = null;
            if (player == null) return false;

            var controlled = player.Controller?.ControlledEntity;
            if (controlled is MyShipController shipController && shipController.CubeGrid != null)
            {
                grid = shipController.CubeGrid;
                return true;
            }
            if (controlled?.Entity is MyShipController sc && sc.CubeGrid != null)
            {
                grid = sc.CubeGrid;
                return true;
            }
            if (controlled?.Entity is MyCubeBlock block && block.CubeGrid != null)
            {
                grid = block.CubeGrid;
                return true;
            }

            var character = player.Character;
            if (character != null)
            {
                if (character.Parent is MyCubeBlock parentBlock && parentBlock.CubeGrid != null)
                {
                    grid = parentBlock.CubeGrid;
                    return true;
                }
                if (character.Parent is MyCubeGrid parentGrid)
                {
                    grid = parentGrid;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Casts a physical ray along the player's crosshairs line of sight to select the first grid construct.
        /// </summary>
        public static bool TryRaycastTargetGrid(MyPlayer player, float maxDistance, out MyCubeGrid hitGrid)
        {
            hitGrid = null;
            var character = player?.Character;
            if (character == null || character.MarkedForClose || character.Closed) return false;

            MatrixD headMatrix = character.GetHeadMatrix(true, true, false, false, false);
            Vector3D from = headMatrix.Translation;
            Vector3D dir = headMatrix.Forward;
            Vector3D to = from + (dir * maxDistance);

            var hits = new List<MyPhysics.HitInfo>();
            try
            {
                MyPhysics.CastRay(from, to, hits, 0);
                foreach (var hit in hits)
                {
                    var ent = hit.HkHitInfo.GetHitEntity();
                    if (ent is MyCubeGrid g && !g.MarkedForClose && !g.Closed)
                    {
                        hitGrid = GridUtils.GetMainGrid(g) ?? g;
                        return true;
                    }
                    if (ent is MyCubeBlock b && b.CubeGrid != null && !b.CubeGrid.MarkedForClose && !b.CubeGrid.Closed)
                    {
                        hitGrid = GridUtils.GetMainGrid(b.CubeGrid) ?? b.CubeGrid;
                        return true;
                    }
                }
            }
            finally
            {
                hits.Clear();
            }

            return false;
        }

        /// <summary>
        /// Validates that the player has ownership or shared faction rights on the grid.
        /// </summary>
        public static bool HasOwnershipOrFactionAccess(MyCubeGrid grid, MyPlayer player)
        {
            if (grid == null || player?.Identity == null) return false;
            long identityId = player.Identity.IdentityId;

            if (grid.BigOwners != null && grid.BigOwners.Count > 0)
            {
                foreach (long ownerId in grid.BigOwners)
                {
                    if (ownerId == identityId) return true;
                    var rel = player.GetRelationTo(ownerId);
                    if (rel == MyRelationsBetweenPlayerAndBlock.FactionShare || rel == MyRelationsBetweenPlayerAndBlock.Owner)
                    {
                        return true;
                    }
                }
                return false;
            }

            if (grid.SmallOwners != null && grid.SmallOwners.Contains(identityId))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if any member of the construct took damage within the configured combat cooldown window.
        /// </summary>
        public static bool IsInCombat(MyCubeGrid topGrid, int combatCooldownSeconds, out double remainingSeconds)
        {
            remainingSeconds = 0;
            if (topGrid == null || combatCooldownSeconds <= 0) return false;

            DateTime now = DateTime.UtcNow;
            TimeSpan cooldown = TimeSpan.FromSeconds(combatCooldownSeconds);

            var members = new List<MyCubeGrid>();
            try
            {
                GridUtils.GetMechanicalGroupMembers(topGrid, members);
                foreach (var member in members)
                {
                    if (member == null) continue;
                    if (_lastGridDamageTimesUtc.TryGetValue(member.EntityId, out DateTime dmgTime))
                    {
                        TimeSpan elapsed = now - dmgTime;
                        if (elapsed < cooldown)
                        {
                            remainingSeconds = Math.Max(remainingSeconds, (cooldown - elapsed).TotalSeconds);
                        }
                    }
                }
            }
            finally
            {
                members.Clear();
            }

            return remainingSeconds > 0;
        }

        /// <summary>
        /// Checks if hostile enemy players or armed hostile grids are within the specified proximity.
        /// </summary>
        public static bool HasHostilesInProximity(MyPlayer player, Vector3D position, float enemyRadiusMeters)
        {
            if (player == null || enemyRadiusMeters <= 0f) return false;

            double radiusSq = (double)enemyRadiusMeters * enemyRadiusMeters;

            // 1. Check online players
            var players = MySession.Static?.Players?.GetOnlinePlayers();
            if (players != null)
            {
                foreach (var other in players)
                {
                    if (other == player || other.Identity == null) continue;
                    if (player.GetRelationTo(other.Identity.IdentityId) == MyRelationsBetweenPlayerAndBlock.Enemies)
                    {
                        if (Vector3D.DistanceSquared(position, other.GetPosition()) <= radiusSq)
                        {
                            return true;
                        }
                    }
                }
            }

            // 2. Check nearby enemy grids
            BoundingSphereD sphere = new BoundingSphereD(position, enemyRadiusMeters);
            List<MyEntity> entities = null;
            try
            {
                entities = MyEntities.GetEntitiesInSphere(ref sphere);
                if (entities != null)
                {
                    foreach (var ent in entities)
                    {
                        if (ent is MyCubeGrid otherGrid && !otherGrid.MarkedForClose && !otherGrid.Closed)
                        {
                            if (otherGrid.BigOwners != null && otherGrid.BigOwners.Count > 0)
                            {
                                long ownerId = otherGrid.BigOwners[0];
                                if (player.GetRelationTo(ownerId) == MyRelationsBetweenPlayerAndBlock.Enemies)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                entities?.Clear();
            }

            return false;
        }

        /// <summary>
        /// Verifies that the grid is near surface terrain or experiencing active contacts,
        /// preventing mid-air flight or orbit abuse.
        /// </summary>
        public static bool IsNearTerrainOrColliding(MyCubeGrid topGrid)
        {
            if (topGrid == null) return false;

            Vector3D center = topGrid.PositionComp.WorldVolume.Center;
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(center);
            if (planet != null)
            {
                Vector3D surfacePoint = planet.GetClosestSurfacePointGlobal(ref center);
                double dist = Vector3D.Distance(center, surfacePoint);
                if (dist <= topGrid.PositionComp.WorldVolume.Radius + 15.0)
                {
                    return true;
                }
            }

            if (PhysicsOptimizerPlugin.Instance?.GridDefender?.HasRecentContacts(topGrid.EntityId) == true)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks whether the player or grid is currently on rescue cooldown.
        /// </summary>
        public static bool IsOnCooldown(ulong steamId, long gridId, int cooldownSeconds, out double remainingSeconds)
        {
            remainingSeconds = 0;
            if (cooldownSeconds <= 0) return false;

            DateTime now = DateTime.UtcNow;
            TimeSpan cd = TimeSpan.FromSeconds(cooldownSeconds);

            if (_playerCooldownsUtc.TryGetValue(steamId, out DateTime pTime))
            {
                TimeSpan elapsed = now - pTime;
                if (elapsed < cd)
                {
                    remainingSeconds = Math.Max(remainingSeconds, (cd - elapsed).TotalSeconds);
                }
            }

            if (_gridCooldownsUtc.TryGetValue(gridId, out DateTime gTime))
            {
                TimeSpan elapsed = now - gTime;
                if (elapsed < cd)
                {
                    remainingSeconds = Math.Max(remainingSeconds, (cd - elapsed).TotalSeconds);
                }
            }

            return remainingSeconds > 0;
        }

        /// <summary>
        /// Main command handler for !unstuckit, !rescue, and !unstuck.
        /// Evaluates player context, performs crosshairs raycast or seated vehicle detection,
        /// and initiates warm-up countdown or executes admin bypass immediately.
        /// </summary>
        public static void RequestRescue(CommandContext context, string targetFilter = null)
        {
            var plugin = PhysicsOptimizerPlugin.Instance;
            var config = plugin?.Config;
            if (config == null || !config.Enabled)
            {
                context.Respond("[Rescue] Physics Optimizer plugin is currently disabled.");
                return;
            }

            bool isAdmin = context.Player == null || context.Player.PromoteLevel >= MyPromoteLevel.Admin;

            if (!isAdmin && !config.EnablePlayerRescue)
            {
                context.Respond("[Rescue] Player vehicle rescue is currently disabled on this server.");
                return;
            }

            MyPlayer player = null;
            if (context.Player != null)
            {
                MySession.Static?.Players?.TryGetPlayerBySteamId(context.Player.SteamUserId, out player);
            }

            MyCubeGrid targetGrid = null;
            bool isOnFoot = false;

            // 1. Try seated vehicle first
            if (player != null && TryGetPilotedOrOccupiedGrid(player, out MyCubeGrid seatedGrid))
            {
                targetGrid = seatedGrid;
                isOnFoot = false;
            }
            // 2. Try on-foot crosshairs raycast if not seated
            else if (player != null)
            {
                float raycastDist = isAdmin ? 1000.0f : config.PlayerRescueOnFootMaxDistanceMeters;
                if (TryRaycastTargetGrid(player, raycastDist, out MyCubeGrid raycastGrid))
                {
                    targetGrid = raycastGrid;
                    isOnFoot = true;
                }
            }
            // 3. Admin filter lookup (e.g. from console or by name/id)
            if (targetGrid == null && !string.IsNullOrWhiteSpace(targetFilter) && isAdmin)
            {
                if (long.TryParse(targetFilter, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                {
                    if (MyEntities.TryGetEntityById(id, out MyEntity ent) && ent is MyCubeGrid g)
                    {
                        targetGrid = g;
                    }
                }
                else
                {
                    foreach (var ent in MyEntities.GetEntities())
                    {
                        if (ent is MyCubeGrid g && !g.MarkedForClose && !g.Closed)
                        {
                            if (string.Equals(g.DisplayName, targetFilter, StringComparison.OrdinalIgnoreCase))
                            {
                                targetGrid = g;
                                break;
                            }
                        }
                    }
                }
            }

            if (targetGrid == null)
            {
                if (player == null)
                {
                    context.Respond("[Rescue] Specify a valid grid name or EntityId when calling from server console.");
                    return;
                }
                if (isOnFoot || player.Character != null)
                {
                    context.Respond(string.Format(CultureInfo.InvariantCulture,
                        "[Rescue] No vehicle detected in your crosshairs within {0:F0}m. Enter a cockpit or look directly at your vehicle.",
                        isAdmin ? 1000.0f : config.PlayerRescueOnFootMaxDistanceMeters));
                }
                else
                {
                    context.Respond("[Rescue] You must be seated in a vehicle or aim your crosshairs at one.");
                }
                return;
            }

            MyCubeGrid topGrid = GridUtils.GetMainGrid(targetGrid) ?? targetGrid;
            if (topGrid.MarkedForClose || topGrid.Closed)
            {
                context.Respond("[Rescue] Target vehicle is closed or invalid.");
                return;
            }

            if (topGrid.IsStatic)
            {
                context.Respond("[Rescue] Static stations cannot be rescued.");
                return;
            }

            // ADMIN BYPASS: execute immediately without barriers
            if (isAdmin)
            {
                ExecuteRescueNow(topGrid, isAdmin: true, context);
                return;
            }

            // PLAYER VALIDATION GATES:
            long identityId = player?.Identity?.IdentityId ?? 0L;
            ulong steamId = context.Player.SteamUserId;

            // 1. Ownership & faction access check
            if (!HasOwnershipOrFactionAccess(topGrid, player))
            {
                context.Respond("[Rescue] You do not have ownership or faction authorization for this vehicle.");
                return;
            }

            // 2. Cooldown check
            if (IsOnCooldown(steamId, topGrid.EntityId, config.PlayerRescueCooldownSeconds, out double cdRem))
            {
                int mins = (int)(cdRem / 60);
                int secs = (int)(cdRem % 60);
                context.Respond(string.Format(CultureInfo.InvariantCulture, "[Rescue] Rescue is on cooldown. Please wait {0}m {1}s.", mins, secs));
                return;
            }

            // 3. Combat damage cooldown check
            if (IsInCombat(topGrid, config.PlayerRescueCombatCooldownSeconds, out double combatRem))
            {
                context.Respond(string.Format(CultureInfo.InvariantCulture, "[Rescue] Cannot rescue while in combat. Please wait {0:F0}s after taking damage.", combatRem));
                return;
            }

            // 4. Speed gate with Clang-jitter exemption
            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            float speed = topGrid.Physics?.LinearVelocity.Length() ?? 0f;
            bool isClanging = GridDefender.IsConstructClangingOrJittering(topGrid.EntityId, currentFrame);

            if (speed > config.PlayerRescueMaxSpeed && !isClanging)
            {
                context.Respond(string.Format(CultureInfo.InvariantCulture,
                    "[Rescue] Vehicle must be stationary (speed < {0:F1} m/s) to initiate rescue.", config.PlayerRescueMaxSpeed));
                return;
            }

            // 5. Hostile enemy proximity check
            Vector3D checkPos = isOnFoot && player?.Character != null
                ? player.Character.PositionComp.GetPosition()
                : topGrid.PositionComp.WorldVolume.Center;

            if (HasHostilesInProximity(player, checkPos, config.PlayerRescueEnemyProximityMeters))
            {
                context.Respond("[Rescue] Cannot rescue while hostile forces are detected within proximity.");
                return;
            }

            // 6. Terrain / contact proximity check (anti-aerial abuse)
            if (config.PlayerRescueRequireNearTerrainOrContact && !IsNearTerrainOrColliding(topGrid))
            {
                context.Respond("[Rescue] Vehicle is not stuck in terrain or colliding with another body.");
                return;
            }

            // 7. Warm-up countdown registration
            if (config.PlayerRescueWarmupSeconds <= 0)
            {
                ExecuteRescueNow(topGrid, isAdmin: false, context);
                _playerCooldownsUtc[steamId] = DateTime.UtcNow;
                _gridCooldownsUtc[topGrid.EntityId] = DateTime.UtcNow;
                return;
            }

            ulong warmupFrames = (ulong)config.PlayerRescueWarmupSeconds * 60UL;
            _pendingRescues[steamId] = new PendingRescue
            {
                SteamId = steamId,
                IdentityId = identityId,
                GridId = topGrid.EntityId,
                IsOnFoot = isOnFoot,
                StartPosition = isOnFoot && player?.Character != null ? player.Character.PositionComp.GetPosition() : topGrid.PositionComp.WorldVolume.Center,
                StartFrame = currentFrame,
                FinishFrame = currentFrame + warmupFrames,
                LastSecondReported = config.PlayerRescueWarmupSeconds
            };

            string initMsg = string.Format(CultureInfo.InvariantCulture,
                "[Rescue] Rescue initiating in {0}s... Remain stationary and avoid taking damage.", config.PlayerRescueWarmupSeconds);
            MyVisualScriptLogicProvider.ShowNotification(initMsg, 3000, "Yellow", identityId);
            context.Respond(initMsg);
        }

        private static void ExecuteRescueNow(MyCubeGrid topGrid, bool isAdmin, CommandContext context)
        {
            var plugin = PhysicsOptimizerPlugin.Instance;
            var defender = plugin?.GridDefender;
            var config = plugin?.Config;
            if (defender == null)
            {
                context?.Respond("[Rescue] Grid Defender physics engine is not available.");
                return;
            }

            double dist = config?.PlayerRescuePushDistance ?? 2.5f;
            bool upright = config?.PlayerRescueUprightFlipped ?? false;

            bool success = defender.ExecuteRescuePush(topGrid, dist, upright, isAdmin, out string failureReason);
            if (success)
            {
                string msg = isAdmin
                    ? string.Format(CultureInfo.InvariantCulture, "[Rescue] Admin rescue push applied to '{0}' (+{1:F1}m).", topGrid.DisplayName, dist)
                    : string.Format(CultureInfo.InvariantCulture, "[Rescue] Rescue push applied to '{0}' (+{1:F1}m). Velocities stabilized.", topGrid.DisplayName, dist);
                context?.Respond(msg);
            }
            else
            {
                context?.Respond(string.Format(CultureInfo.InvariantCulture, "[Rescue] Failed to rescue vehicle: {0}", failureReason ?? "Unknown error"));
            }
        }

        /// <summary>
        /// Simulation frame step called from Plugin.Update.
        /// Evaluates pending rescue warm-up countdowns and checks abort conditions.
        /// </summary>
        public static void Update(ulong frameCounter)
        {
            if (_pendingRescues.IsEmpty)
            {
                if (frameCounter % 600UL == 0)
                {
                    PruneExpiredCooldowns();
                }
                return;
            }

            var plugin = PhysicsOptimizerPlugin.Instance;
            var config = plugin?.Config;
            if (config == null) return;

            DateTime now = DateTime.UtcNow;

            foreach (var kvp in _pendingRescues)
            {
                ulong steamId = kvp.Key;
                PendingRescue pending = kvp.Value;

                if (!MySession.Static.Players.TryGetPlayerBySteamId(steamId, out MyPlayer player) || player == null)
                {
                    _pendingRescues.TryRemove(steamId, out _);
                    continue;
                }

                if (!MyEntities.TryGetEntityById(pending.GridId, out MyEntity ent) || ent is not MyCubeGrid grid || grid.MarkedForClose || grid.Closed)
                {
                    MyVisualScriptLogicProvider.ShowNotification("[Rescue] Rescue aborted: Vehicle was removed or closed.", 3000, "Red", pending.IdentityId);
                    _pendingRescues.TryRemove(steamId, out _);
                    continue;
                }

                // Abort check 1: Combat damage
                if (_lastGridDamageTimesUtc.TryGetValue(grid.EntityId, out DateTime lastDmg) && (now - lastDmg).TotalSeconds < config.PlayerRescueCombatCooldownSeconds)
                {
                    MyVisualScriptLogicProvider.ShowNotification("[Rescue] Rescue aborted: Vehicle took damage.", 4000, "Red", pending.IdentityId);
                    _pendingRescues.TryRemove(steamId, out _);
                    continue;
                }

                // Abort check 2: Movement / speed
                float speed = grid.Physics?.LinearVelocity.Length() ?? 0f;
                bool isClanging = GridDefender.IsConstructClangingOrJittering(grid.EntityId, frameCounter);

                if (pending.IsOnFoot)
                {
                    if (player.Character == null || player.Character.MarkedForClose || player.Character.Closed)
                    {
                        _pendingRescues.TryRemove(steamId, out _);
                        continue;
                    }
                    double movedDist = Vector3D.Distance(player.Character.PositionComp.GetPosition(), pending.StartPosition);
                    if (movedDist > 4.0)
                    {
                        MyVisualScriptLogicProvider.ShowNotification("[Rescue] Rescue aborted: Character moved.", 3000, "Red", pending.IdentityId);
                        _pendingRescues.TryRemove(steamId, out _);
                        continue;
                    }
                }
                else
                {
                    if (speed > config.PlayerRescueMaxSpeed && !isClanging)
                    {
                        MyVisualScriptLogicProvider.ShowNotification("[Rescue] Rescue aborted: Vehicle moved.", 3000, "Red", pending.IdentityId);
                        _pendingRescues.TryRemove(steamId, out _);
                        continue;
                    }
                }

                // Abort check 3: Hostile forces entered proximity
                Vector3D checkPos = pending.IsOnFoot && player.Character != null
                    ? player.Character.PositionComp.GetPosition()
                    : grid.PositionComp.WorldVolume.Center;

                if (HasHostilesInProximity(player, checkPos, config.PlayerRescueEnemyProximityMeters))
                {
                    MyVisualScriptLogicProvider.ShowNotification("[Rescue] Rescue aborted: Hostiles detected nearby.", 4000, "Red", pending.IdentityId);
                    _pendingRescues.TryRemove(steamId, out _);
                    continue;
                }

                // Countdown HUD tick
                if (frameCounter < pending.FinishFrame)
                {
                    int secondsRemaining = (int)Math.Ceiling((pending.FinishFrame - frameCounter) / 60.0);
                    if (secondsRemaining != pending.LastSecondReported && secondsRemaining > 0)
                    {
                        pending.LastSecondReported = secondsRemaining;
                        _pendingRescues[steamId] = pending;
                        string tickMsg = string.Format(CultureInfo.InvariantCulture, "[Rescue] Rescue initiating in {0}s...", secondsRemaining);
                        MyVisualScriptLogicProvider.ShowNotification(tickMsg, 1100, "Yellow", pending.IdentityId);
                    }
                    continue;
                }

                // Warmup countdown complete: execute rescue push!
                _pendingRescues.TryRemove(steamId, out _);

                var defender = plugin.GridDefender;
                if (defender != null)
                {
                    double dist = config.PlayerRescuePushDistance;
                    bool upright = config.PlayerRescueUprightFlipped;

                    bool success = defender.ExecuteRescuePush(grid, dist, upright, isAdmin: false, out string failureReason);
                    if (success)
                    {
                        _playerCooldownsUtc[steamId] = DateTime.UtcNow;
                        _gridCooldownsUtc[grid.EntityId] = DateTime.UtcNow;
                        MyVisualScriptLogicProvider.ShowNotification("[Rescue] Vehicle rescue complete. Velocities stabilized.", 4000, "Green", pending.IdentityId);
                    }
                    else
                    {
                        string err = string.Format(CultureInfo.InvariantCulture, "[Rescue] Rescue failed: {0}", failureReason ?? "Physics solver error");
                        MyVisualScriptLogicProvider.ShowNotification(err, 4000, "Red", pending.IdentityId);
                    }
                }
            }
        }

        private static void PruneExpiredCooldowns()
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan maxAge = TimeSpan.FromHours(1);

            foreach (var kvp in _playerCooldownsUtc)
            {
                if (now - kvp.Value > maxAge) _playerCooldownsUtc.TryRemove(kvp.Key, out _);
            }
            foreach (var kvp in _gridCooldownsUtc)
            {
                if (now - kvp.Value > maxAge) _gridCooldownsUtc.TryRemove(kvp.Key, out _);
            }
            foreach (var kvp in _lastGridDamageTimesUtc)
            {
                if (now - kvp.Value > maxAge) _lastGridDamageTimesUtc.TryRemove(kvp.Key, out _);
            }
        }
    }
}
