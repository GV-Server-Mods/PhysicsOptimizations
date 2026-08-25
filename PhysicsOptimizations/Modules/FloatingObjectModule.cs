using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using VRage;
using VRage.Game.Entity;
using VRageMath;
using GVK.PhysicsOptimizations.Config;

namespace GVK.PhysicsOptimizations.Modules
{
    public class FloatingObjectModule : IPhysicsModule
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.Ore");

        public string Name => "Floating Object & Ore Optimizer";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnableFloatingObjectOptimizer;

        private PhysicsOptimizerPlugin _plugin;

        private readonly List<MyFloatingObject> _floatingObjectsBuffer = new List<MyFloatingObject>();
        private readonly HashSet<long> _processedEntities = new HashSet<long>();

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            _floatingObjectsBuffer.Clear();
            _processedEntities.Clear();
            Log.Info("[FloatingObjectModule] Initialized successfully.");
        }

        public void Update(ulong frameCounter)
        {
            if (!IsEnabled || !Sync.IsServer || _plugin?.Config == null || !_plugin.Config.AutoMergeNearbyOre)
            {
                return;
            }

            // Run proximity merge on configured interval (default: 120 ticks = 2s)
            int interval = Math.Max(30, _plugin.Config.OreMergeIntervalTicks);
            if ((int)(frameCounter % (ulong)interval) != 0)
            {
                return;
            }

            MergeProximityFloatingObjects();
        }

        public int MergeProximityFloatingObjects()
        {
            if (!Sync.IsServer) return 0;

            int mergedCount = 0;
            int eliminatedCount = 0;

            try
            {
                var config = _plugin.Config;
                double mergeRadiusSq = config.OreMergeRadiusMeters * config.OreMergeRadiusMeters;

                _floatingObjectsBuffer.Clear();
                _processedEntities.Clear();

                // Gather active floating objects
                var entities = MyEntities.GetEntities();
                foreach (var entity in entities)
                {
                    if (entity is MyFloatingObject floatingObj && !floatingObj.MarkedForClose && !floatingObj.Closed && floatingObj.Item.Content != null)
                    {
                        _floatingObjectsBuffer.Add(floatingObj);
                    }
                }

                int totalCount = _floatingObjectsBuffer.Count;
                if (totalCount < 2)
                {
                    return 0;
                }

                // Spatial proximity clustering
                for (int i = 0; i < totalCount; i++)
                {
                    var primary = _floatingObjectsBuffer[i];
                    if (primary.MarkedForClose || primary.Closed || _processedEntities.Contains(primary.EntityId))
                    {
                        continue;
                    }

                    var primaryContent = primary.Item.Content;
                    Vector3D primaryPos = primary.PositionComp.GetPosition();

                    for (int j = i + 1; j < totalCount; j++)
                    {
                        var secondary = _floatingObjectsBuffer[j];
                        if (secondary.MarkedForClose || secondary.Closed || _processedEntities.Contains(secondary.EntityId))
                        {
                            continue;
                        }

                        var secondaryContent = secondary.Item.Content;

                        // Verify same item type definition and subtype
                        if (primaryContent.TypeId != secondaryContent.TypeId ||
                            primaryContent.SubtypeName != secondaryContent.SubtypeName)
                        {
                            continue;
                        }

                        // Check spatial distance
                        Vector3D secondaryPos = secondary.PositionComp.GetPosition();
                        if (Vector3D.DistanceSquared(primaryPos, secondaryPos) <= mergeRadiusSq)
                        {
                            // Merge amounts into primary stack
                            MyFixedPoint addedAmount = secondary.Amount.Value;
                            primary.Amount.Value += addedAmount;
                            primary.Item.Amount = primary.Amount.Value;
                            primary.RefreshDisplayName();

                            _processedEntities.Add(secondary.EntityId);
                            secondary.Close();

                            mergedCount++;
                            eliminatedCount++;
                        }
                    }
                }

                if (mergedCount > 0)
                {
                    _plugin.Telemetry?.IncrementOreMerged(mergedCount);
                    _plugin.Telemetry?.IncrementOreEntitiesEliminated(eliminatedCount);

                    if (config.EnableDebugLogging)
                    {
                        Log.Debug($"[OreOptimizer] Proximity merge complete: merged {mergedCount} stacks, eliminated {eliminatedCount} floating objects.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FloatingObjectModule] Error during floating object proximity merge!");
            }
            finally
            {
                _floatingObjectsBuffer.Clear();
                _processedEntities.Clear();
            }

            return eliminatedCount;
        }

        public void Dispose()
        {
            _floatingObjectsBuffer.Clear();
            _processedEntities.Clear();
            _plugin = null;
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
        }
    }
}

