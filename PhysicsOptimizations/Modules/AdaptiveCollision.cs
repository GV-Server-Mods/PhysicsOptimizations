using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Havok;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;
using VRageMath;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Utils;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Adaptive collision pruner: flips dynamic grids between Continuous and Discrete (Debris)
    /// Havok quality based on speed, grid size, terrain altitude, and nearby-grid safety overrides.
    /// </summary>
    public class AdaptiveCollision : IPhysicsModule
    {
        private const string LogSource = "AdaptiveCollision";

        public string Name => "Adaptive TOI & Collision Pruner";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnablePhysicsOptimizations && _plugin.Config.EnableAdaptiveCollision;

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
        private readonly List<MyEntity> _nearbyBuffer = [];

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            _trackedQualities.Clear();
            Log.Info(LogSource, "Initialized successfully.");
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
            if (_plugin?.Config == null) return;
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

                    if (!rb.IsActive)
                    {
                        continue; // Zero-Touch Sleep Guard
                    }

                    float speedSq = (float)grid.Physics.LinearVelocity.LengthSquared();

                    if (!_trackedQualities.TryGetValue(grid.EntityId, out var state))
                    {
                        state = new GridQualityState
                        {
                            GridEntityId = grid.EntityId,
                            GridRef = new WeakReference<MyCubeGrid>(grid),
                            OriginalQuality = rb.Quality,
                            IsDiscrete = false
                        };
                        _trackedQualities[grid.EntityId] = state;
                    }

                    float speed = (float)Math.Sqrt(speedSq);
                    bool isMissile = _plugin.GridDefender != null && _plugin.Config.AllowMissileDamage && _plugin.GridDefender.IsMissile(grid, speed);

                    // Grid-Size Discrete Architecture Override (PMW missiles always bypass this to retain Continuous TOI)
                    bool forceDiscrete = false;
                    if (!isMissile)
                    {
                        if (grid.GridSizeEnum == VRage.Game.MyCubeSize.Large && config.EnforceDiscreteLargeGrids && grid.BlocksCount >= config.DiscreteLargeGridMinBlocks)
                        {
                            forceDiscrete = true;
                        }
                        else if (grid.GridSizeEnum == VRage.Game.MyCubeSize.Small && config.EnforceDiscreteSmallGrids && grid.BlocksCount >= config.DiscreteSmallGridMinBlocks)
                        {
                            forceDiscrete = true;
                        }
                    }

                    if (forceDiscrete)
                    {
                        if (!state.IsDiscrete && rb.Quality != HkCollidableQualityType.Debris)
                        {
                            state.OriginalQuality = rb.Quality;
                            rb.Quality = HkCollidableQualityType.Debris;
                            state.IsDiscrete = true;
                        }
                    }
                    else
                    {
                        bool forceContinuous = isMissile;

                        if (!forceContinuous && config.EnableSpeedThresholds)
                        {
                            // 1. High-Speed Threshold:
                            if (speedSq >= continuousThreshSq)
                            {
                                forceContinuous = true;
                            }
                            // 2. Planetary Terrain Altitude Safety Override:
                            else if (config.EnableAltitudeTOIReversion)
                            {
                                var pos = grid.PositionComp.GetPosition();
                                var planet = MyGamePruningStructure.GetClosestPlanet(pos);
                                if (planet != null)
                                {
                                    var surfacePoint = planet.GetClosestSurfacePointGlobal(pos);
                                    if (Vector3D.DistanceSquared(pos, surfacePoint) < config.ContinuousAltitudeThreshold * config.ContinuousAltitudeThreshold)
                                    {
                                        forceContinuous = true;
                                    }
                                }
                            }

                            // 3. Dynamic Grid Proximity Safety Override:
                            if (!forceContinuous && config.RevertNearOtherDynamicGrids)
                            {
                                double proxDist = config.DynamicGridProximityRevertDistanceMeters;
                                var sphere = new BoundingSphereD(grid.PositionComp.GetPosition(), proxDist);
                                _nearbyBuffer.Clear();
                                MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, _nearbyBuffer, MyEntityQueryType.Both);
                                foreach (var near in _nearbyBuffer)
                                {
                                    if (near is MyCubeGrid other && other != grid && !other.IsStatic && !other.Closed)
                                    {
                                        forceContinuous = true;
                                        break;
                                    }
                                }
                                _nearbyBuffer.Clear();
                            }

                            // 4. Small Grid / Torpedo Safety: Small crafts (<= 40 blocks) retain Continuous TOI when speed thresholds are active
                            if (!forceContinuous && grid.BlocksCount <= 40)
                            {
                                forceContinuous = true;
                            }
                        }

                        if (!forceContinuous)
                        {
                            if (config.EnableSpeedThresholds)
                            {
                                if (speedSq <= discreteThreshSq && !state.IsDiscrete && rb.Quality != HkCollidableQualityType.Debris)
                                {
                                    state.OriginalQuality = rb.Quality;
                                    rb.Quality = HkCollidableQualityType.Debris;
                                    state.IsDiscrete = true;
                                }
                            }
                            else if (state.IsDiscrete)
                            {
                                // Speed thresholds disabled and not covered by forced discrete: restore original quality
                                rb.Quality = state.OriginalQuality != HkCollidableQualityType.Invalid ? state.OriginalQuality : HkCollidableQualityType.Moving;
                                state.IsDiscrete = false;
                            }
                        }
                        else
                        {
                            if (state.IsDiscrete)
                            {
                                rb.Quality = state.OriginalQuality != HkCollidableQualityType.Invalid ? state.OriginalQuality : HkCollidableQualityType.Moving;
                                state.IsDiscrete = false;
                            }
                        }
                    }

                    if (state.IsDiscrete)
                    {
                        discreteCount++;
                    }
                }
            }

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
