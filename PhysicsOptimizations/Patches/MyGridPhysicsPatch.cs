using System;
using System.Reflection;
using Havok;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRageMath;
using Sandbox.Game.Entities.Planet;
using PhysicsOptimizations.Utils;

namespace PhysicsOptimizations.Patches
{
    public static class MyGridPhysicsPatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("PhysicsOptimizer.MyGridPhysicsPatch");
        [ThreadStatic]
        private static System.Collections.Generic.List<MyPhysics.HitInfo> _hitsCache;

        public static void Patch(PatchContext ctx)
        {
            try
            {
                var targetMethod = typeof(MyGridPhysics).GetMethod("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (targetMethod != null)
                {
                    var prefixMethod = typeof(MyGridPhysicsPatch).GetMethod(nameof(Prefix_PerformDeformation), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(targetMethod).Prefixes.Add(prefixMethod);
                    PatchConflictAudit.RegisterTarget(targetMethod);
                }

                var contactMethod = typeof(MyGridPhysics).GetMethod("RigidBody_ContactPointCallbackImpl", BindingFlags.Instance | BindingFlags.NonPublic);
                if (contactMethod != null)
                {
                    var contactPrefix = typeof(MyGridPhysicsPatch).GetMethod(nameof(Prefix_RigidBody_ContactPointCallbackImpl), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(contactMethod).Prefixes.Add(contactPrefix);
                    PatchConflictAudit.RegisterTarget(contactMethod);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to patch MyGridPhysics methods.");
            }
        }

        public static bool Prefix_PerformDeformation(MyGridPhysics __instance, MyEntity otherEntity, ref float separatingVelocity)
        {
            var plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin?.Engine == null) return true;

            return plugin.Engine.ShouldAllowDeformation(__instance, otherEntity, ref separatingVelocity);
        }

        public static bool Prefix_RigidBody_ContactPointCallbackImpl(MyGridPhysics __instance, ref HkContactPointEvent value)
        {
            if (__instance.Entity is MyCubeGrid gridLocal)
            {
                DeformationOcclusionPatch.UpdateCollisionContext(gridLocal.EntityId, value.ContactPoint.Position);
            }
            
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.EnableVoxelNormalArbitrator)
                return true;

            if (__instance.Entity is not MyCubeGrid grid || grid.MarkedForClose || grid.Closed)
                return true;

            var rb = value.GetPhysicsBody(0);
            var otherRb = value.GetPhysicsBody(1);
            if (rb == null || otherRb == null) return true;

            bool isVoxel = false;
            var otherEnt = otherRb.Entity;
            if (otherEnt is Sandbox.Game.Entities.MyVoxelBase)
            {
                isVoxel = true;
            }
            else
            {
                var selfEnt = rb.Entity;
                if (selfEnt is Sandbox.Game.Entities.MyVoxelBase)
                {
                    isVoxel = true;
                }
            }

            if (isVoxel)
            {
                var gridPos = grid.PositionComp.GetPosition();
                var gravity = __instance.Gravity;
                if (gravity.LengthSquared() < 0.01f) return true;

                var upVector = -Vector3.Normalize((Vector3)gravity);
                float upDot = Vector3.Dot(value.ContactPoint.Normal, upVector);

                if (upDot < 0f)
                {
                    var contactPos = value.ContactPoint.Position;
                    var rayStart = contactPos;
                    var rayEnd = rayStart + upVector * 1.5f;

                    bool hitAir = true;
                    _hitsCache ??= [];
                    _hitsCache.Clear();
                    MyPhysics.CastRay(rayStart, rayEnd, _hitsCache, MyPhysics.CollisionLayers.VoxelCollisionLayer);
                    foreach(var hit in _hitsCache)
                    {
                        var hitEnt = hit.HkHitInfo.GetHitEntity();
                        if (hitEnt is Sandbox.Game.Entities.MyVoxelBase)
                        {
                            hitAir = false;
                            break;
                        }
                    }
                    _hitsCache.Clear();

                    if (hitAir)
                    {
                        var cp = value.ContactPoint;
                        cp.Normal = -cp.Normal;
                        
                        PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelNormalsInverted();

                        if (config.EnableDebugLogging && config.LogVoxelNormals)
                        {
                            Log.Info($"[Voxel Arbitrator] Inverted downward normal for grid '{grid.DisplayName}' at {contactPos}.");
                        }
                    }
                }
            }

            return true;
        }
    }
}
