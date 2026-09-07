using System;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace PhysicsOptimizations.Utils
{
    /// <summary>
    /// Utility methods for player identity and admin status checks.
    /// </summary>
    public static class PlayerUtils
    {
        /// <summary>
        /// Checks if the given Steam ID has server administrator privileges.
        /// </summary>
        public static bool IsAdmin(ulong steamId)
        {
            if (steamId == 0) return true; // Server console
            return MySession.Static?.IsUserAdmin(steamId) ?? false;
        }

        /// <summary>
        /// Gets the player's display name from Steam ID, falling back to a formatted default.
        /// </summary>
        public static string GetPlayerName(ulong steamId)
        {
            if (steamId == 0) return "Server Console";
            return Sync.Players?.TryGetIdentityNameFromSteamId(steamId) ?? $"Player_{steamId}";
        }

        /// <summary>
        /// Finds the online player instance corresponding to the given Steam ID.
        /// </summary>
        public static IMyPlayer GetPlayer(ulong steamId)
        {
            if (Sync.Players == null) return null;
            foreach (var p in Sync.Players.GetOnlinePlayers())
            {
                if (p.Id.SteamId == steamId) return p;
            }
            return null;
        }
    }
}

