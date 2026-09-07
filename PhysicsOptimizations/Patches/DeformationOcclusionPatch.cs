using System;
using System.Reflection;
using System.Collections.Concurrent;
using NLog;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities;
using Sandbox.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using Sandbox.Game.SessionComponents;
using VRage.Game.Components;
using Sandbox.Engine.Physics;
using PhysicsOptimizations;
using Sandbox.Game.GameSystems;
using PhysicsOptimizations.Utils;

namespace PhysicsOptimizations.Patches
{
    public static class DeformationOcclusionPatch
    {
        private static readonly ILogger Log = LogManager.GetLogger("PhysicsOptimizations.DeformationOcclusion");

        // Key: Grid EntityId, Value: Global impact position
        private static readonly ConcurrentDictionary<long, Vector3D> _lastImpactPositions = new();

        public static void UpdateCollisionContext(long gridId, Vector3D position)
        {
            _lastImpactPositions[gridId] = position;
        }

        public static void RemoveCollisionContext(long gridId)
        {
            _lastImpactPositions.TryRemove(gridId, out _);
        }

        public static void Patch(PatchContext ctx)
        {
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            
            var initMethod = typeof(MyDamageSystem).GetMethod("LoadData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) 
                          ?? typeof(MyDamageSystem).GetMethod("Init", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            if (initMethod != null)
            {
                var initPostfix = typeof(DeformationOcclusionPatch).GetMethod(nameof(InitPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                ctx.GetPattern(initMethod).Suffixes.Add(initPostfix);
                PatchConflictAudit.RegisterTarget(initMethod);
                Log.Info("[PhysicsOptimizer] Patched MyDamageSystem to register DeformationOcclusion.");
            }
            else
            {
                if (MyDamageSystem.Static != null)
                {
                    MyDamageSystem.Static.RegisterBeforeDamageHandler(100, OnBeforeDamageApplied);
                    Log.Info("[PhysicsOptimizer] Registered Layered Armor Occlusion damage handler directly.");
                }
                else
                {
                    Log.Error("[PhysicsOptimizer] Could not patch MyDamageSystem for Layered Armor Occlusion!");
                }
            }
        }

        public static void InitPostfix(MyDamageSystem __instance)
        {
            if (__instance != null)
            {
                __instance.RegisterBeforeDamageHandler(100, OnBeforeDamageApplied);
                Log.Info("[PhysicsOptimizer] Registered Layered Armor Occlusion damage handler via Postfix.");
            }
        }

        private static void OnBeforeDamageApplied(object target, ref MyDamageInformation info)
        {
            if (info.Amount <= 0f || info.Type != MyDamageType.Deformation) return;
            
            var config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.EnableLayeredArmorOcclusion) return;

            if (target is MySlimBlock slimBlock && slimBlock.CubeGrid != null)
            {
                var grid = slimBlock.CubeGrid;
                
                Vector3D globalHitPos;
                if (_lastImpactPositions.TryGetValue(grid.EntityId, out var pos))
                {
                    globalHitPos = pos;
                }
                else
                {
                    return; // Can't determine direction without a collision point
                }

                Vector3D localHitPos = Vector3D.Transform(globalHitPos, grid.PositionComp.WorldMatrixNormalizedInv);
                Vector3D blockCenterLocal = slimBlock.Position * grid.GridSize;
                Vector3D D = localHitPos - blockCenterLocal;
                
                if (D.LengthSquared() < 0.001f) return;
                
                Vector3I step = Vector3I.Round(Vector3D.Normalize(D));
                Vector3I neighborCoord = slimBlock.Position + step;
                
                var occluder = grid.GetCubeBlock(neighborCoord);
                
                if (occluder == null || ReferenceEquals(occluder, slimBlock)) return;
                
                bool isValidOccluder = true;
                if (config.EnforceStructuralArmorCheck)
                {
                    isValidOccluder = (occluder.FatBlock == null) || (occluder.DeformationRatio < 0.5f);
                }
                
                if (isValidOccluder && !occluder.IsDestroyed)
                {
                    info.Amount = 0f; // Shielded!
                    PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementArmorHitsOccluded();
                }
            }
        }
    }
}
