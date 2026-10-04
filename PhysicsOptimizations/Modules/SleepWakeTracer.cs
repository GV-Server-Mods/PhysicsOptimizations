using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Havok;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Debug-only diagnostic for forced-sleep grids that keep waking. Havok defers activation to the
    /// next physics step, so the RigidBodySleep wake probe only sees the step itself. These prefixes
    /// catch the managed call that queued the wake (activate, velocity/impulse/force writes, shape
    /// update, collision filter refresh, physics-changed) and log its caller.
    /// Patches are applied at runtime while debug logging is on and removed when it is turned off
    /// (zero cost otherwise).
    /// </summary>
    public static class SleepWakeTracer
    {
        private const string LogSource = "SleepWakeTracer";
        private const ulong RelogFrames = 600; // same grid + call logged at most every 10s

        private static readonly ConcurrentDictionary<string, ulong> _lastLogged = new();
        private static PatchManager _patchManager;
        private static PatchContext _ctx;

        public static void Init(PatchManager patchManager) => _patchManager = patchManager;

        /// <summary>Applies or removes the tracer patches to match the debug logging toggle. Game thread only.</summary>
        public static void Sync(bool debugEnabled)
        {
            if (_patchManager == null || debugEnabled == (_ctx != null)) return;

            try
            {
                if (debugEnabled)
                {
                    _ctx = _patchManager.AcquireContext();
                    RegisterPatches(_ctx);
                    _patchManager.Commit();
                    Log.Info(LogSource, "Debug logging on: wake tracer patches applied.");
                }
                else
                {
                    _patchManager.FreeContext(_ctx);
                    _ctx = null;
                    _patchManager.Commit();
                    _lastLogged.Clear();
                    Log.Info(LogSource, "Debug logging off: wake tracer patches removed.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to sync wake tracer patches!");
                _patchManager = null; // stop retrying every tick
            }
        }

        private static void RegisterPatches(PatchContext ctx)
        {
            {
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public;
                var vec = new[] { typeof(VRageMath.Vector3) };

                Prefix(ctx, typeof(HkEntity).GetMethod("Activate", inst, null, Type.EmptyTypes, null), nameof(ActivatePrefix));
                Prefix(ctx, typeof(HkEntity).GetProperty("LinearVelocity", inst)?.GetSetMethod(), nameof(LinearVelocityPrefix));
                Prefix(ctx, typeof(HkEntity).GetProperty("AngularVelocity", inst)?.GetSetMethod(), nameof(AngularVelocityPrefix));
                Prefix(ctx, typeof(HkRigidBody).GetMethod("UpdateShape", inst, null, Type.EmptyTypes, null), nameof(UpdateShapePrefix));
                Prefix(ctx, typeof(HkRigidBody).GetMethod("ApplyLinearImpulse", inst, null, vec, null), nameof(ImpulsePrefix));
                Prefix(ctx, typeof(HkRigidBody).GetMethod("ApplyAngularImpulse", inst, null, vec, null), nameof(ImpulsePrefix));
                Prefix(ctx, typeof(HkRigidBody).GetMethod("ApplyPointImpulse", inst, null, new[] { typeof(VRageMath.Vector3), typeof(VRageMath.Vector3) }, null), nameof(ImpulsePrefix));
                Prefix(ctx, typeof(HkRigidBody).GetMethod("ApplyForce", inst, null, new[] { typeof(float), typeof(VRageMath.Vector3) }, null), nameof(ForcePrefix));
                Prefix(ctx, typeof(HkRigidBody).GetMethod("ApplyForce", inst, null, new[] { typeof(float), typeof(VRageMath.Vector3), typeof(VRageMath.Vector3) }, null), nameof(ForcePrefix));
                Prefix(ctx, typeof(HkRigidBody).GetMethod("ApplyTorque", inst, null, new[] { typeof(float), typeof(VRageMath.Vector3) }, null), nameof(ForcePrefix));
                Prefix(ctx, typeof(HkRigidBody).GetMethod("SetCollisionFilterInfo", inst, null, new[] { typeof(uint) }, null), nameof(FilterInfoPrefix));
                Prefix(ctx, typeof(MyPhysics).GetMethod("RefreshCollisionFilter", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(MyPhysicsBody) }, null), nameof(RefreshFilterPrefix));
                Prefix(ctx, typeof(MyCubeGrid).GetMethod("RaisePhysicsChanged", inst | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null), nameof(PhysicsChangedPrefix));
            }
        }

        private static void Prefix(PatchContext ctx, MethodBase target, string prefixName)
        {
            if (target == null)
            {
                Log.Warn(LogSource, $"Wake tracer target for {prefixName} not found; skipped.");
                return;
            }
            ctx.GetPattern(target).Prefixes.Add(typeof(SleepWakeTracer).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void ActivatePrefix(HkEntity __instance) => Trace(__instance?.GetBody()?.Entity as MyCubeGrid, "HkEntity.Activate");
        private static void LinearVelocityPrefix(HkEntity __instance) => Trace(__instance?.GetBody()?.Entity as MyCubeGrid, "set LinearVelocity");
        private static void AngularVelocityPrefix(HkEntity __instance) => Trace(__instance?.GetBody()?.Entity as MyCubeGrid, "set AngularVelocity");
        private static void UpdateShapePrefix(HkRigidBody __instance) => Trace(__instance?.GetBody()?.Entity as MyCubeGrid, "HkRigidBody.UpdateShape");
        private static void ImpulsePrefix(HkRigidBody __instance) => Trace(__instance?.GetBody()?.Entity as MyCubeGrid, "Impulse");
        private static void ForcePrefix(HkRigidBody __instance) => Trace(__instance?.GetBody()?.Entity as MyCubeGrid, "Force/Torque");
        private static void FilterInfoPrefix(HkRigidBody __instance) => Trace(__instance?.GetBody()?.Entity as MyCubeGrid, "SetCollisionFilterInfo");
        private static void RefreshFilterPrefix(MyPhysicsBody physicsBody) => Trace(physicsBody?.Entity as MyCubeGrid, "MyPhysics.RefreshCollisionFilter");
        private static void PhysicsChangedPrefix(MyCubeGrid __instance) => Trace(__instance, "MyCubeGrid.RaisePhysicsChanged");

        /// <summary>Logs the caller when the touched grid (or the chassis of a touched wheel) is forced asleep.</summary>
        private static void Trace(MyCubeGrid grid, string call)
        {
            try
            {
                if (grid == null) return;
                var sleep = PhysicsOptimizerPlugin.Instance?.RigidBodySleep;
                if (sleep == null) return;

                long id = grid.EntityId;
                string via = string.Empty;
                if (!sleep.IsGridSleeping(id))
                {
                    if (!GridDefender.IsWheelSubgrid(grid, out long baseId) || baseId <= 0L || !sleep.IsGridSleeping(baseId)) return;
                    via = $" via wheel '{grid.DisplayName}'";
                    id = baseId;
                }

                ulong now = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                string key = id + "|" + call + via;
                if (_lastLogged.TryGetValue(key, out ulong last) && now >= last && now - last < RelogFrames) return;
                _lastLogged[key] = now;

                string name = (MyEntities.GetEntityById(id) as MyCubeGrid)?.DisplayName ?? id.ToString();
                Log.Info(LogSource, $"WAKE TRACE: {call} on forced-sleep grid '{name}'{via} (frame {now}). Caller:\n{TrimStack(Environment.StackTrace)}");
            }
            catch
            {
                // Diagnostic only - never break the patched call
            }
        }

        private static string TrimStack(string stack)
        {
            var sb = new StringBuilder();
            int kept = 0;
            foreach (var raw in stack.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.Contains("System.Environment") || line.Contains("SleepWakeTracer")) continue;
                sb.Append("    ").AppendLine(line);
                if (++kept >= 18) break;
            }
            return sb.ToString();
        }
    }
}
