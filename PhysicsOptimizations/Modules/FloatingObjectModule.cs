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

        private readonly List<MyFloatingObject> _floatingObjectsBuffer = [];
        private readonly HashSet<long> _processedEntities = [];
        private readonly Dictionary<Vector3I, List<MyFloatingObject>> _cellBuckets = [];
        private readonly List<List<MyFloatingObject>> _listPool = [];

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            _floatingObjectsBuffer.Clear();
            _processedEntities.Clear();
            _cellBuckets.Clear();
            _listPool.Clear();
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

        public void OnEntityAdded(MyEntity entity)
        {
        }

        public void OnEntityRemoved(MyEntity entity)
        {
            // Transient entities cleaned up in merge passes
        }

        private List<MyFloatingObject> GetPooledList()
        {
            if (_listPool.Count > 0)
            {
                int lastIdx = _listPool.Count - 1;
                var list = _listPool[lastIdx];
                _listPool.RemoveAt(lastIdx);
                return list;
            }
            return new(8);
        }

        private void RecycleBuckets()
        {
            foreach (var kvp in _cellBuckets)
            {
                kvp.Value.Clear();
                _listPool.Add(kvp.Value);
            }
            _cellBuckets.Clear();
        }

        public int MergeProximityFloatingObjects()
        {
            if (!Sync.IsServer) return 0;

            int mergedCount = 0;
            int eliminatedCount = 0;

            try
            {
                var config = _plugin.Config;
                double mergeRadius = Math.Max(0.5, (double)config.OreMergeRadiusMeters);
                double mergeRadiusSq = mergeRadius * mergeRadius;
                double cellSize = mergeRadius; // Grid cell size matches merge radius

                _floatingObjectsBuffer.Clear();
                _processedEntities.Clear();
                RecycleBuckets();

                // 1. Gather active floating objects
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

                // 2. Spatial Grid Hashing (O(N) bucketing)
                for (int i = 0; i < totalCount; i++)
                {
                    var item = _floatingObjectsBuffer[i];
                    Vector3D pos = item.PositionComp.GetPosition();
                    var cell = new Vector3I(
                        (int)Math.Floor(pos.X / cellSize),
                        (int)Math.Floor(pos.Y / cellSize),
                        (int)Math.Floor(pos.Z / cellSize)
                    );

                    if (!_cellBuckets.TryGetValue(cell, out var list))
                    {
                        list = GetPooledList();
                        _cellBuckets[cell] = list;
                    }
                    list.Add(item);
                }

                // 3. Local proximity clustering within adjacent cells
                foreach (var cellKvp in _cellBuckets)
                {
                    var currentCell = cellKvp.Key;
                    var currentList = cellKvp.Value;

                    for (int i = 0; i < currentList.Count; i++)
                    {
                        var primary = currentList[i];
                        if (primary.MarkedForClose || primary.Closed || _processedEntities.Contains(primary.EntityId))
                        {
                            continue;
                        }

                        var primaryContent = primary.Item.Content;
                        Vector3D primaryPos = primary.PositionComp.GetPosition();

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dz = -1; dz <= 1; dz++)
                                {
                                    var neighborCell = new Vector3I(currentCell.X + dx, currentCell.Y + dy, currentCell.Z + dz);

                                    // Check ordered coordinate pairs to avoid duplicate evaluation
                                    if (neighborCell.X < currentCell.X) continue;
                                    if (neighborCell.X == currentCell.X && neighborCell.Y < currentCell.Y) continue;
                                    if (neighborCell.X == currentCell.X && neighborCell.Y == currentCell.Y && neighborCell.Z < currentCell.Z) continue;

                                    if (_cellBuckets.TryGetValue(neighborCell, out var neighborList))
                                    {
                                        int startIdx = (neighborCell == currentCell) ? i + 1 : 0;
                                        for (int j = startIdx; j < neighborList.Count; j++)
                                        {
                                            TryMerge(primary, neighborList[j], primaryPos, primaryContent, mergeRadiusSq, ref mergedCount, ref eliminatedCount);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (mergedCount > 0)
                {
                    _plugin.Telemetry?.IncrementOreMerged(mergedCount);
                    _plugin.Telemetry?.IncrementOreEntitiesEliminated(eliminatedCount);

                    if (config.EnableDebugLogging)
                    {
                        Log.Debug($"[OreOptimizer] Spatial merge complete: merged {mergedCount} stacks, eliminated {eliminatedCount} floating objects.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FloatingObjectModule] Error during floating object spatial merge!");
            }
            finally
            {
                _floatingObjectsBuffer.Clear();
                _processedEntities.Clear();
                RecycleBuckets();
            }

            return eliminatedCount;
        }

        private void TryMerge(
            MyFloatingObject primary,
            MyFloatingObject secondary,
            Vector3D primaryPos,
            VRage.Game.MyObjectBuilder_PhysicalObject primaryContent,
            double mergeRadiusSq,
            ref int mergedCount,
            ref int eliminatedCount)
        {
            if (secondary.MarkedForClose || secondary.Closed || _processedEntities.Contains(secondary.EntityId))
            {
                return;
            }

            var secondaryContent = secondary.Item.Content;
            if (secondaryContent == null ||
                primaryContent.TypeId != secondaryContent.TypeId ||
                primaryContent.SubtypeName != secondaryContent.SubtypeName)
            {
                return;
            }

            Vector3D secondaryPos = secondary.PositionComp.GetPosition();
            if (Vector3D.DistanceSquared(primaryPos, secondaryPos) <= mergeRadiusSq)
            {
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

        public void Dispose()
        {
            _floatingObjectsBuffer.Clear();
            _processedEntities.Clear();
            RecycleBuckets();
            _listPool.Clear();
            _plugin = null;
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
        }
    }
}


