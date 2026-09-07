using System;
using Sandbox.Game.Gui;
using Torch;
using Torch.API;
using Torch.Mod;
using Torch.Mod.Messages;
using VRage.Game;
using VRageMath;

namespace PhysicsOptimizer.Utils
{
    /// <summary>
    /// Utility methods for sending in-game notifications to players.
    /// </summary>
    public static class ChatUtils
    {
        public const string Prefix = "GridDefender";

        /// <summary>
        /// Sends an on-screen notification message to a specific player via Torch ModCommunication.
        /// </summary>
        public static void SendMessageToPlayer(ulong steamId, string message, Color color)
        {
            if (steamId == 0) return;
            TorchBase.Instance?.Invoke(() =>
            {
                var msg = new NotificationMessage(message, 5000, MyFontEnum.White);
                ModCommunication.SendMessageTo(msg, steamId);
            });
        }
    }
}

