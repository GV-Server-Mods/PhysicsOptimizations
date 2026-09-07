using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using Sandbox.Game.Entities.Cube;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using PhysicsOptimizations;
using PhysicsOptimizations.Config;
using PhysicsOptimizations.Utils;

namespace PhysicsOptimizations.Patches
{
    public static class ThrusterDamagePatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("PhysicsOptimizations.ThrusterDamage");

        [ThreadStatic]
        private static List<MyPhysics.HitInfo> _hitList;

        public static void Patch(PatchContext ctx)
        {
            var targetMethod = typeof(MyThrust).GetMethod("ThrustDamageAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? typeof(MyThrust).GetMethod("DamageGrid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            if (targetMethod != null)
            {
                var prefixMethod = typeof(ThrusterDamagePatch).GetMethod(nameof(ThrustDamagePrefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                ctx.GetPattern(targetMethod).Prefixes.Add(prefixMethod);
                PatchConflictAudit.RegisterTarget(targetMethod);
                Log.Info($"[PhysicsOptimizer] Successfully patched {targetMethod.Name} for Thruster Clearance Optimizer.");
            }
            else
            {
                Log.Error("[PhysicsOptimizer] Could not find MyThrust.ThrustDamageAsync or MyThrust.DamageGrid method to patch!");
            }
        }

        public static bool ThrustDamagePrefix(MyThrust __instance)
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.Enabled || !config.EnablePhysicsOptimizations || !config.EnableThrusterClearanceEngine) return true;

            if (__instance == null || __instance.Closed || __instance.CubeGrid == null || __instance.BlockDefinition == null) return false;

            // Inactive thrusters deal no damage
            if (__instance.CurrentStrength <= 0f && !MyFakes.INACTIVE_THRUSTER_DMG) return false;

            try
            {
                var flames = __instance.Flames;
                if (flames.Count == 0)
                {
                    // Fallback to vanilla if block definition has no recognized flame dummies
                    return true;
                }

                MatrixD worldMatrix = __instance.WorldMatrix;
                float flameDamageScale = __instance.BlockDefinition.FlameDamageLengthScale;
                if (flameDamageScale <= 0f) flameDamageScale = 1.0f;

                for (int f = 0; f < flames.Count; f++)
                {
                    var flame = flames[f];
                    if (!flame.HasDamage) continue;

                    Vector3D flameStart = Vector3D.Transform(flame.Position, worldMatrix);
                    Vector3D flameDir = Vector3D.TransformNormal(flame.Direction, worldMatrix);
                    if (flameDir.LengthSquared() < 0.0001)
                        flameDir = worldMatrix.Forward;
                    else
                        flameDir.Normalize();

                    float length = flame.Radius * flameDamageScale * 2.5f;
                    if (length <= 0f) length = 2.5f;

                    float radius = flame.Radius;

                    // Multi-nozzle / radial coverage based on nozzle radius:
                    if (radius <= 0.75f)
                    {
                        // Small nozzle (<= 1.5m diameter): Single center ray
                        CastThrusterRay(flameStart, flameDir, length, __instance, config);
                    }
                    else if (radius <= 2.0f)
                    {
                        // Medium nozzle (1.5m - 4.0m diameter): 5-ray crosshair (center + 4 cardinal offsets)
                        Vector3D right = Vector3D.CalculatePerpendicularVector(flameDir);
                        Vector3D up = Vector3D.Cross(flameDir, right);
                        float offset = 0.7f * radius;

                        CastThrusterRay(flameStart, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + right * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart - right * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + up * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart - up * offset, flameDir, length, __instance, config);
                    }
                    else
                    {
                        // Giant / Titan nozzle (> 4.0m diameter, e.g. 5x5, 7x7):
                        // 9-ray radial fan: Center + 8 outer rays (4 cardinal + 4 diagonal)
                        // Eliminates all diagonal corner blind spots on square/rectangular thrusters
                        Vector3D right = Vector3D.CalculatePerpendicularVector(flameDir);
                        Vector3D up = Vector3D.Cross(flameDir, right);
                        float offset = 0.75f * radius;
                        float diagOffset = offset * 0.7071f;

                        CastThrusterRay(flameStart, flameDir, length, __instance, config);
                        
                        // Cardinal rays
                        CastThrusterRay(flameStart + right * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart - right * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + up * offset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart - up * offset, flameDir, length, __instance, config);

                        // Diagonal rays
                        CastThrusterRay(flameStart + (right + up) * diagOffset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + (right - up) * diagOffset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + (-right + up) * diagOffset, flameDir, length, __instance, config);
                        CastThrusterRay(flameStart + (-right - up) * diagOffset, flameDir, length, __instance, config);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "[ThrusterClearance] Error in raycast clearance; falling back to vanilla.");
                return true;
            }
        }

        private static void CastThrusterRay(Vector3D start, Vector3D dir, float length, MyThrust thruster, PhysicsOptimizerConfig config)
        {
            _hitList ??= [];
            _hitList.Clear();

            Vector3D end = start + dir * length;
            MyPhysics.CastRay(start, end, _hitList, MyPhysics.CollisionLayers.DefaultCollisionLayer);

            for (int i = 0; i < _hitList.Count; i++)
            {
                var hit = _hitList[i];
                var hitEntity = hit.HkHitInfo.GetHitEntity();
                if (hitEntity == null) continue;

                if (hitEntity is IMyCharacter character)
                {
                    character.DoDamage(50f, MyDamageType.Environment, true, null, thruster.EntityId);
                    continue;
                }

                if (hitEntity is MyCubeGrid hitGrid)
                {
                    bool isOwnConstruct = hitGrid == thruster.CubeGrid || 
                        (thruster.CubeGrid.GridSystems != null && hitGrid.GridSystems != null && 
                         MyCubeGridGroups.Static.Physical.GetGroup(hitGrid) == MyCubeGridGroups.Static.Physical.GetGroup(thruster.CubeGrid));

                    Vector3I blockPos = hitGrid.WorldToGridInteger(hit.Position + dir * 0.1f);
                    var block = hitGrid.GetCubeBlock(blockPos);

                    if (block == null) continue;

                    // Critical safety guard: NEVER damage the thruster itself
                    if (ReferenceEquals(block, thruster.SlimBlock)) continue;

                    if (isOwnConstruct)
                    {
                        if (config.ThrusterDamageMode == ThrusterDamageMode.Optimized)
                        {
                            // Anti-exploit: Instantly vaporize buried internal blocks on own construct
                            block.DoDamage(float.MaxValue, MyDamageType.Deformation, true, null, thruster.EntityId);
                            PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementThrusterObstructionsVaporized();

                            if (config.EnableDebugLogging && config.LogThrusterClearance)
                            {
                                Log.Info($"[ThrusterClearance] Vaporized own-construct obstruction on '{hitGrid.DisplayName}'.");
                            }
                        }
                        else
                        {
                            // Vanilla-like mode: Gradual thermal damage on own construct
                            float dmg = thruster.BlockDefinition.FlameDamage * thruster.CurrentStrength;
                            block.DoDamage(dmg, MyDamageType.Thruster, true, null, thruster.EntityId);
                        }
                    }
                    else
                    {
                        // External Grid (Carrier Deck, Landing Pad, Enemy Hull)
                        if (config.ThrusterDamageMode == ThrusterDamageMode.VanillaLike)
                        {
                            // Vanilla-like mode: Gradual thermal damage to carrier deck / external grid
                            float dmg = thruster.BlockDefinition.FlameDamage * thruster.CurrentStrength;
                            block.DoDamage(dmg, MyDamageType.Thruster, true, null, thruster.EntityId);

                            if (config.EnableDebugLogging && config.LogThrusterClearance)
                            {
                                Log.Info($"[ThrusterClearance] Applied vanilla-rate thermal damage ({dmg:F1}) to external grid '{hitGrid.DisplayName}'.");
                            }
                        }
                        // If Optimized mode: External grids are 100% immune (0 damage, 0 allocations)
                    }
                }
            }

            _hitList.Clear();
        }
    }
}
