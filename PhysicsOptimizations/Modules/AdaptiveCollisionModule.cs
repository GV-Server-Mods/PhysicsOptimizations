using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Havok;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;
using GVK.PhysicsOptimizations.Config;

namespace GVK.PhysicsOptimizations.Modules
{
    public class AdaptiveCollisionModule : IPhysicsModule
    {
        private static readonly ILogger Log = LogManager.GetLogger("GVK.PhysicsOptimizer.AdaptiveTOI");

        public string Name => "Adaptive TOI & Collision Pruner";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnableAdaptiveTOI;

        private PhysicsOptimizerPlugin _plugin;

        public class GridQualityState
        {
            public long GridEntityId;
            public WeakReference<MyCubeGrid> GridRef;
            public HkCollidableQualityType OriginalQuality;
            public bool IsDiscrete;
        }

        private readonly ConcurrentDictionary<long, GridQualityState> _trackedQualities = new();
        private readonly List<long> _cleanupBuffer = [];

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            _trackedQualities.Clear();
            Log.Info("[AdaptiveCollisionModule] Initialized successfully.");
        }

        public void Update(ulong frameCounter)
        {
            if (!IsEnabled || _plugin?.Config == null)
            {
                return;
            }

            // Run evaluation every 30 frames (~0.5s)
            if (frameCounter % 30 != 0)
            {
                return;
            }

            EvaluateGridCollisions(frameCounter);
        }

        private void EvaluateGridCollisions(ulong frameCounter)
        {
            try
            {
                var config = _plugin.Config;
                float discreteThreshSq = config.DiscreteCollisionSpeedThreshold * config.DiscreteCollisionSpeedThreshold;
                float continuousThreshSq = config.ContinuousCollisionSpeedThreshold * config.ContinuousCollisionSpeedThreshold;

                int discreteCount = 0;
                _cleanupBuffer.Clear();

                var entities = MyEntities.GetEntities();
                foreach (var entity in entities)
                {
                    if (entity is MyCubeGrid grid && !grid.IsStatic && !grid.MarkedForClose && !grid.Closed && grid.Physics?.RigidBody != null)
                    {
                        var rb = grid.Physics.RigidBody;
                        float speedSq = (float)grid.Physics.LinearVelocity.LengthSquared();

                        if (!_trackedQualities.TryGetValue(grid.EntityId, out var state))
                        {
                            state = new()
                            {
                                GridEntityId = grid.EntityId,
                                GridRef = new(grid),
                                OriginalQuality = rb.Quality,
                                IsDiscrete = false
                            };
                            _trackedQualities[grid.EntityId] = state;
                        }

                        // Slow grid: switch to Debris (discrete) quality
                        if (speedSq <= discreteThreshSq)
                        {
                            if (!state.IsDiscrete && rb.Quality != HkCollidableQualityType.Debris)
                            {
                                state.OriginalQuality = rb.Quality;
                                rb.Quality = HkCollidableQualityType.Debris;
                                state.IsDiscrete = true;

                                if (config.EnableDebugLogging)
                                {
                                    Log.Info($"[AdaptiveTOI] Set grid '{grid.DisplayName}' to DISCRETE collision quality (Speed: {Math.Sqrt(speedSq):F1} m/s).");
                                }
                            }
                        }
                        // Fast grid or missile: restore full continuous Moving/Critical TOI
                        else if (speedSq >= continuousThreshSq)
                        {
                            if (state.IsDiscrete)
                            {
                                rb.Quality = state.OriginalQuality != HkCollidableQualityType.Invalid ? state.OriginalQuality : HkCollidableQualityType.Moving;
                                state.IsDiscrete = false;

                                if (config.EnableDebugLogging)
                                {
                                    Log.Info($"[AdaptiveTOI] Restored grid '{grid.DisplayName}' to CONTINUOUS TOI collision quality (Speed: {Math.Sqrt(speedSq):F1} m/s).");
                                }
                            }
                        }

                        if (state.IsDiscrete)
                        {
                            discreteCount++;
                        }
                    }
                }

                // Cold-path cleanup of stale trackers (entity eviction handles immediate removals)
                if (frameCounter % 300 == 0)
                {
                    foreach (var kvp in _trackedQualities)
                    {
                        if (!kvp.Value.GridRef.TryGetTarget(out var g) || g.MarkedForClose || g.Closed)
                        {
                            _cleanupBuffer.Add(kvp.Key);
                        }
                    }

                    for (int i = 0; i < _cleanupBuffer.Count; i++)
                    {
                        _trackedQualities.TryRemove(_cleanupBuffer[i], out _);
                    }
                    _cleanupBuffer.Clear();
                }

                int totalTracked = _trackedQualities.Count;
                _plugin?.Telemetry?.UpdateTOITelemetry(totalTracked, discreteCount, Math.Max(0, totalTracked - discreteCount));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AdaptiveCollisionModule] Error during adaptive TOI evaluation!");
            }
        }

        public void OnEntityAdded(MyEntity entity)
        {
        }

        public void OnEntityRemoved(MyEntity entity)
        {
            if (entity == null) return;
            if (_trackedQualities.TryRemove(entity.EntityId, out var state))
            {
                if (state.IsDiscrete && state.GridRef.TryGetTarget(out var grid) && grid.Physics?.RigidBody != null)
                {
                    grid.Physics.RigidBody.Quality = state.OriginalQuality != HkCollidableQualityType.Invalid ? state.OriginalQuality : HkCollidableQualityType.Moving;
                }
            }
        }

        public void RestoreAllGridQualities()
        {
            foreach (var kvp in _trackedQualities)
            {
                if (kvp.Value.GridRef.TryGetTarget(out var grid) && grid.Physics?.RigidBody != null)
                {
                    if (kvp.Value.IsDiscrete)
                    {
                        grid.Physics.RigidBody.Quality = kvp.Value.OriginalQuality != HkCollidableQualityType.Invalid ? kvp.Value.OriginalQuality : HkCollidableQualityType.Moving;
                    }
                }
            }
            _trackedQualities.Clear();
        }

        public void Dispose()
        {
            RestoreAllGridQualities();
            _cleanupBuffer.Clear();
            _plugin = null;
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
        }
    }
}

