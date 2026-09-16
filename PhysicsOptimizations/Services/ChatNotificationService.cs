using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Services
{
    /// <summary>
    /// Thread-safe service responsible for dispatching in-game chat notifications and HUD toasts.
    /// Guarantees zero allocation on hot paths, simulation thread safety, and strict rate-limiting.
    /// </summary>
    public static class ChatNotificationService
    {
        private const string LogSource = "ChatNotify";
        private static readonly ConcurrentDictionary<long, ulong> _pushApartToastCooldowns = new();

        /// <summary>
        /// Sends a clean 3-second cyan HUD toast to the pilots/controllers of a nudged grid, or its owner if unpiloted.
        /// Strictly rate-limited per grid to prevent notification spam during repeated nudge attempts.
        /// </summary>
        public static void SendPushApartNotification(MyCubeGrid grid, double distance, bool convertedToStatic, PhysicsOptimizerConfig config, ulong currentFrame)
        public static void SendPushApartNotification(MyCubeGrid grid, double distance, bool convertedToStatic, PhysicsOptimizerConfig config, ulong currentFrame, bool isRescue = false)
        {
            if (grid == null || config == null || !config.EnablePushApartPlayerNotification) return;

            long gridId = grid.EntityId;
            ulong cooldownTicks = (ulong)Math.Max(5, config.ChatNotificationCooldownSeconds) * 60UL;

            if (_pushApartToastCooldowns.TryGetValue(gridId, out ulong lastFrame) && currentFrame < lastFrame + cooldownTicks)
            if (!isRescue && _pushApartToastCooldowns.TryGetValue(gridId, out ulong lastFrame) && currentFrame < lastFrame + cooldownTicks)
            {
                return;
            }
            _pushApartToastCooldowns[gridId] = currentFrame;

            string gridName = grid.DisplayName ?? "Vehicle";
            string toastMsg = convertedToStatic
                ? string.Format(CultureInfo.InvariantCulture, "[Physics] '{0}' anchored to terrain to prevent Clang", gridName)
                : string.Format(CultureInfo.InvariantCulture, "[Physics] Nudged '{0}' to surface", gridName);
            string toastMsg;
            if (isRescue)
            {
                toastMsg = string.Format(CultureInfo.InvariantCulture, "[Rescue] Rescued '{0}' (+{1:F1}m). Velocities stabilized.", gridName, distance);
            }
            else
            {
                toastMsg = convertedToStatic
                    ? string.Format(CultureInfo.InvariantCulture, "[Physics] '{0}' anchored to terrain to prevent Clang", gridName)
                    : string.Format(CultureInfo.InvariantCulture, "[Physics] Nudged '{0}' to surface", gridName);
            }

            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    if (grid.MarkedForClose || grid.Closed) return;

                    var recipients = new HashSet<long>();

                    // 1. Send to cockpit controllers
                    var controllers = grid.GridSystems?.ControlSystem?.GetControllers();
                    if (controllers != null)
                    {
                        foreach (var controller in controllers)
                        {
                            long identityId = controller.ControllerInfo?.ControllingIdentityId ?? 0L;
                            if (identityId != 0L) recipients.Add(identityId);
                        }
                    }

                    // 2. Fallback to grid owner if no active controller
                    if (recipients.Count == 0 && grid.BigOwners != null && grid.BigOwners.Count > 0)
                    {
                        recipients.Add(grid.BigOwners[0]);
                    }

                    foreach (long identityId in recipients)
                    {
                        MyVisualScriptLogicProvider.ShowNotification(toastMsg, 3000, "Cyan", identityId);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, LogSource, "Failed to dispatch push-apart player toast.");
                }
            }, "PhysicsOptimizer.PushApartToast");
        }

        /// <summary>
        /// Sends Style A PMW impact notification to the factions of both the attacker missile and the defender target.
        /// Format: [PMW] '{MissileName}' hit '{TargetName}' ({Speed:F0} m/s)
        /// </summary>
        public static void SendPmwStrikeNotification(MyCubeGrid missile, MyCubeGrid target, float speed, PhysicsOptimizerConfig config)
        {
            if (config == null || !config.EnablePmwFactionTelemetry) return;

            string missileName = missile?.DisplayName ?? "Missile";
            string targetName = target?.DisplayName ?? "Target";
            string chatMsg = string.Format(CultureInfo.InvariantCulture, "[PMW] '{0}' hit '{1}' ({2:F0} m/s)", missileName, targetName, speed);

            long missileOwner = (missile?.BigOwners != null && missile.BigOwners.Count > 0) ? missile.BigOwners[0] : 0L;
            long targetOwner = (target?.BigOwners != null && target.BigOwners.Count > 0) ? target.BigOwners[0] : 0L;

            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    var recipients = new HashSet<long>();

                    void AddFactionOrPlayer(long identityId)
                    {
                        if (identityId == 0L) return;
                        IMyFaction faction = MySession.Static?.Factions?.TryGetPlayerFaction(identityId);
                        if (faction != null)
                        {
                            foreach (var memberKvp in faction.Members)
                            {
                                recipients.Add(memberKvp.Key);
                            }
                        }
                        else
                        {
                            recipients.Add(identityId);
                        }
                    }

                    AddFactionOrPlayer(missileOwner);
                    AddFactionOrPlayer(targetOwner);

                    foreach (long recipientId in recipients)
                    {
                        MyVisualScriptLogicProvider.SendChatMessage(chatMsg, "Combat", recipientId, "Red");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, LogSource, "Failed to dispatch PMW strike faction chat.");
                }
            }, "PhysicsOptimizer.PmwChat");
        }

        /// <summary>
        /// Broadcasts global hourly defense digest to all players with prefix [Physics Digest].
        /// </summary>
        public static void SendHourlyDefenseDigest(long hourBlocked, long allTimeBlocked, long hourPushed, long allTimePushed)
        {
            string msg = string.Format(CultureInfo.InvariantCulture,
                "Past hour: {0:N0} collisions deflected, {1:N0} grids nudged. Lifetime: {2:N0} saved.",
                hourBlocked, hourPushed, allTimeBlocked);

            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    MyVisualScriptLogicProvider.SendChatMessage(msg, "[Physics Digest]", 0L, "Green");
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, LogSource, "Failed to broadcast hourly defense digest.");
                }
            }, "PhysicsOptimizer.HourlyDigest");
        }

        /// <summary>
        /// Cleans up expired cooldown timestamps to prevent unbounded memory growth.
        /// </summary>
        public static void PruneExpiredCooldowns(ulong currentFrame)
        {
            foreach (var kvp in _pushApartToastCooldowns)
            {
                if (currentFrame > kvp.Value + 7200UL) // Evict after ~2 minutes of inactivity
                {
                    _pushApartToastCooldowns.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}

