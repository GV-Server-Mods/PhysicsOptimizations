using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox;
using Sandbox.Game;
using Sandbox.Engine.Utils;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;
using VRage.Game;
using VRageMath;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Services;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Thruster Clearance: replaces Keen's volumetric thruster flame damage shape-casts with tiered
    /// 1/5/9-ray nozzle raycasts. Own-construct obstructions vaporize (Optimized mode) or take
    /// vanilla-rate thermal damage (VanillaLike); external grids are immune in Optimized mode.
    /// </summary>
    public static class ThrusterClearance
    {
        private const string LogSource = "ThrusterClearance";

        [ThreadStatic]
        private static List<MyPhysics.HitInfo> _thrusterHitList;

        // Per-flame dedupe of blocks/characters already damaged - parallel rays must not multi-hit the same target.
        [ThreadStatic]
        private static HashSet<object> _thrusterDamageTargets;

        // Large but finite: float.MaxValue overflows Keen's damage pipeline into Infinity/NaN.
        private const float MaxVaporizeDamage = 1e9f;

        public static void RegisterPatches(PatchContext ctx)
        {
            try
            {
                var targetMethod = typeof(MyThrust).GetMethod("ThrustDamageAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? typeof(MyThrust).GetMethod("DamageGrid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (targetMethod != null)
                {
                    var prefixMethod = typeof(ThrusterClearance).GetMethod(nameof(ThrustDamagePrefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(targetMethod).Prefixes.Add(prefixMethod);
                    PatchConflictAudit.RegisterTarget(targetMethod);
                    Log.Info(LogSource, $"Registered {targetMethod.Name} for Thruster Clearance Optimizer.");
                }
                else
                {
                    Log.Warn(LogSource, "No thruster damage method found (ThrustDamageAsync/DamageGrid); Thruster Clearance inactive. Keen renamed it?");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyThrust for Thruster Clearance Optimizer!");
            }
        }

        public static bool ThrustDamagePrefix(MyThrust __instance)
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.Enabled || !config.EnablePhysicsOptimizations || !config.EnableThrusterClearance) return true;

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

                    (_thrusterDamageTargets ??= []).Clear();

                    Vector3D flameStart = Vector3D.Transform(flame.Position, worldMatrix);
                    Vector3D flameDir = Vector3D.TransformNormal(flame.Direction, worldMatrix);
                    if (flameDir.LengthSquared() < 0.0001)
                        flameDir = worldMatrix.Forward;
                    else
                        flameDir.Normalize();

                    float length = flame.Radius * flameDamageScale * 2.5f;
                    if (length <= 0f) length = 2.5f;

                    float radius = flame.Radius;

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
                        // Giant / Titan nozzle (> 4.0m diameter): 9-ray radial fan
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
                Log.Warn(ex, LogSource, "[ThrusterClearance] Error in raycast clearance; falling back to vanilla.");
                return true;
            }
        }

        private static void CastThrusterRay(Vector3D start, Vector3D dir, float length, MyThrust thruster, PhysicsOptimizerConfig config)
        {
            _thrusterHitList ??= [];
            _thrusterHitList.Clear();

            Vector3D end = start + dir * length;
            MyPhysics.CastRay(start, end, _thrusterHitList, MyPhysics.CollisionLayers.DefaultCollisionLayer);

            for (int i = 0; i < _thrusterHitList.Count; i++)
            {
                var hit = _thrusterHitList[i];
                var hitEntity = hit.HkHitInfo.GetHitEntity();
                if (hitEntity == null) continue;

                if (hitEntity is IMyCharacter character)
                {
                    if (!_thrusterDamageTargets.Add(character)) continue;
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

                    if (!_thrusterDamageTargets.Add(block)) continue;

                    if (isOwnConstruct)
                    {
                        if (config.ThrusterDamageMode == ThrusterDamageMode.Optimized)
                        {
                            // Anti-exploit: Instantly vaporize buried internal blocks on own construct
                            block.DoDamage(MaxVaporizeDamage, MyDamageType.Deformation, true, null, thruster.EntityId);
                            PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementThrusterObstructionsVaporized();

                            if (config.EnableDebugLogging && config.LogThrusterClearance)
                            {
                                Log.Info(LogSource, $"[ThrusterClearance] Vaporized own-construct obstruction on '{hitGrid.DisplayName}'.");
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
                                Log.Info(LogSource, $"[ThrusterClearance] Applied vanilla-rate thermal damage ({dmg:F1}) to external grid '{hitGrid.DisplayName}'.");
                            }
                        }
                        // If Optimized mode: External grids are 100% immune (0 damage, 0 allocations)
                    }
                }
            }

            _thrusterHitList.Clear();
        }


    }
}
