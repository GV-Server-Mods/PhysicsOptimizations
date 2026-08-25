using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Havok;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
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

        private readonly ConcurrentDictionary<long, GridQualityState> _trackedQualities = new ConcurrentDictionary<long, GridQualityState>();
        private readonly List<long> _cleanupBuffer = new List<long>();

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

            EvaluateGridCollisions();
        }

        private void EvaluateGridCollisions()
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
                    if (entity is MyCubeGrid grid && !grid.IsStatic && !grid.MarkedForClose && grid.Physics?.RigidBody != null)
                    {
                        var rb = grid.Physics.RigidBody;
                        float speedSq = (float)grid.Physics.LinearVelocity.LengthSquared();

                        var state = _trackedQualities.GetOrAdd(grid.EntityId, id => new GridQualityState
                        {
                            GridEntityId = id,
                            GridRef = new WeakReference<MyCubeGrid>(grid),
                            OriginalQuality = rb.Quality,
                            IsDiscrete = false
                        });

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
                                    Log.Debug($"[AdaptiveTOI] Set grid '{grid.DisplayName}' to DISCRETE collision quality (Speed: {Math.Sqrt(speedSq):F1} m/s).");
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
                                    Log.Debug($"[AdaptiveTOI] Restored grid '{grid.DisplayName}' to CONTINUOUS TOI collision quality (Speed: {Math.Sqrt(speedSq):F1} m/s).");
                                }
                            }
                        }

                        if (state.IsDiscrete)
                        {
                            discreteCount++;
                        }
                    }
                }

                // Cleanup dead references
                foreach (var kvp in _trackedQualities)
                {
                    if (!kvp.Value.GridRef.TryGetTarget(out var g) || g.MarkedForClose)
                    {
                        _cleanupBuffer.Add(kvp.Key);
                    }
                }

                for (int i = 0; i < _cleanupBuffer.Count; i++)
                {
                    _trackedQualities.TryRemove(_cleanupBuffer[i], out _);
                }

                if (_plugin.Telemetry != null)
                {
                    _plugin.Telemetry.DiscreteTOIGridsCount = discreteCount;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AdaptiveCollisionModule] Error during adaptive TOI evaluation!");
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

