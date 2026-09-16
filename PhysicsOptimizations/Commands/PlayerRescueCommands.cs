using System;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using PhysicsOptimizer.Services;

namespace PhysicsOptimizer.Commands
{
    /// <summary>
    /// Player and admin root commands for vehicle self-rescue (!unstuckit, !rescue, !unstuck).
    /// Dispatches to PlayerRescueService with context-aware seated or on-foot crosshair handling.
    /// </summary>
    public class PlayerRescueCommands : CommandModule
    {
        [Command("unstuckit", "Self-rescue for your stuck vehicle (!unstuckit). Crosshair-aim at your vehicle if on foot, or run while seated.")]
        [Permission(MyPromoteLevel.None)]
        public void UnstuckIt(string targetFilter = null)
        {
            PlayerRescueService.RequestRescue(Context, targetFilter);
        }

        [Command("rescue", "Self-rescue for your stuck vehicle (!rescue). Crosshair-aim at your vehicle if on foot, or run while seated.")]
        [Permission(MyPromoteLevel.None)]
        public void Rescue(string targetFilter = null)
        {
            PlayerRescueService.RequestRescue(Context, targetFilter);
        }

        [Command("unstuck", "Self-rescue for your stuck vehicle (!unstuck). Crosshair-aim at your vehicle if on foot, or run while seated.")]
        [Permission(MyPromoteLevel.None)]
        public void Unstuck(string targetFilter = null)
        {
            PlayerRescueService.RequestRescue(Context, targetFilter);
        }
    }
}

