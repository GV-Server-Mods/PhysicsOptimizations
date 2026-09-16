using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Havok;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using PhysicsOptimizer.Config;
using PhysicsOptimizer.Services;
using PhysicsOptimizer.Utils;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage.Collections;

namespace PhysicsOptimizer.Modules
{
    /// <summary>
    /// Grid Defender: zero-cost collision defense, kinetic damage interception, player-made missile
    /// tracking, anti-clang damping, push-apart separation, voxel cut-out suppression, and layered
    /// armor occlusion.
    /// </summary>
    public class GridDefender : IPhysicsOptimizer
    {
        private const string LogSource = "GridDefender";

        public string Name => "Grid Defender";
        public bool IsEnabled => _plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.EnableGridDefender;

        private PhysicsOptimizerPlugin _plugin;

        private class MissileEngagement
        {
            public long GroupId;
            public ulong ExpireFrame;
            public string MissileName;
            public string TargetName;
            public float InitialSpeed;
            public int ImpactCount;
            public bool NotificationSent;
        }

        private struct PushApartAction
        {
            public long GridId;
            public Vector3D SeparationDir;
            public float Distance;
            public Vector3D StartPos;
            public bool VoxelPush;
            public bool ConvertToStatic;
            public bool IsRescue;
            public bool IsAdminRescue;
        }

        private sealed class EscapeRecord
        {
            public Vector3D Direction;
            public ulong Frame;
            public int Level;
        }

        public sealed class ClangOffender
        {
            public long ConstructId { get; set; }
            public string DisplayName { get; set; }
            public int TotalClangs { get; set; }
            public int PeakRate { get; set; }
            public int CurrentRate { get; set; }
            public DateTime LastClangUtc { get; set; }
            public Vector3D LastPosition { get; set; }
            public string LocationDisplay { get; set; }
            public const int ActiveClangThreshold = 5;
            public bool IsActive => CurrentRate >= ActiveClangThreshold;
            public string StatusDisplay
            {
                get
                {
                    if (CurrentRate >= 15) return $"🔥 CRITICAL: {CurrentRate}/s";
                    if (CurrentRate >= ActiveClangThreshold) return $"⚡ CLANG: {CurrentRate}/s";
                    if (CurrentRate > 0) return $"🟢 CONTACT: {CurrentRate}/s";
                    return $"💤 IDLE ({FormatTimeAgo(LastClangUtc)})";
                }
            }
            public string StatusColor
            {
                get
                {
                    if (CurrentRate >= 15) return "#EF4444";
                    if (CurrentRate >= ActiveClangThreshold) return "#F59E0B";
                    if (CurrentRate > 0) return "#10B981";
                    return "#9CA3AF";
                }
            }
            public string SummaryText => $"{TotalClangs:N0} clangs | Peak: {PeakRate:N0}/s | {LocationDisplay}";

            private static string FormatTimeAgo(DateTime dt)
            {
                var diff = DateTime.UtcNow - dt;
                if (diff.TotalSeconds < 60) return $"{Math.Max(1, (int)diff.TotalSeconds)}s ago";
                if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
                return $"{(int)diff.TotalHours}h ago";
            }
        }

        public sealed class PhysicsIncident
        {
            public DateTime UtcTime { get; set; }
            public string Icon { get; set; }
            public string Message { get; set; }
            public string FormattedTime => UtcTime.ToLocalTime().ToString("HH:mm:ss");
            public string FullText => $"{FormattedTime} {Icon} {Message}";
        }

        private sealed class ConstructClangRecord
        {
            public long ConstructId;
            public string DisplayName;
            public int TotalClangs;
            public int PeakRate;
            public int CurrentSecondClangs;
            public int PreviousSecondClangs;
            public ulong CurrentSecondStartFrame;
            public ulong LastClangFrame;
            public DateTime LastClangUtc;
            public Vector3D LastPosition;
        }

        private struct VoxelArbDebugMarker
        {
            public string GridName;
            public Vector3D OldForcePos;
            public Vector3D NewForcePos;
        }

        private static long _nextMissileGroupId = 0;

        private readonly ConcurrentQueue<PushApartAction> _pushQueue = new();
        private static readonly ConcurrentDictionary<long, VoxelArbDebugMarker> _pendingVoxelArbDebugGps = new();
        private static readonly ConcurrentDictionary<long, ConstructClangRecord> _constructClangRecords = new();
        private readonly ConcurrentDictionary<long, MissileEngagement> _activeMissiles = new();
        private readonly ConcurrentDictionary<long, ulong> _lastDeformationFrames = new();
        private readonly ConcurrentDictionary<long, int> _consecutiveContactFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastContactFrameTracker = new();
        private readonly ConcurrentDictionary<long, ulong> _lastRammingLogFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastVoxelLogFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastStationLogFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastSubgridLogFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastExtremeSpeedLogFrames = new();
        private static readonly ConcurrentDictionary<long, ulong> _lastVoxelArbitratorLogFrames = new();

        private static readonly ConcurrentDictionary<long, ulong> _lastVoxelContactFrames = new();
        private readonly ConcurrentDictionary<long, int> _consecutiveVoxelContactFrames = new();
        private readonly ConcurrentDictionary<long, ulong> _lastVoxelContactFrameTracker = new();
        private readonly ConcurrentDictionary<long, Vector3D> _voxelContactStartPositions = new();
        private readonly ConcurrentDictionary<long, ulong> _lastPushApartGateLogFrames = new();
        private readonly ConcurrentDictionary<long, int> _pushApartAttempts = new();
        private readonly ConcurrentDictionary<long, EscapeRecord> _lastEscapes = new();

        // Wheel subgrid cache: key=wheel subgrid id, value=base grid id (negative sentinel = not a wheel).
        private static readonly ConcurrentDictionary<long, long> _wheelBaseGridIds = new();
        private readonly ConcurrentDictionary<long, Vector3D> _contactStartPositions = new();
        private readonly ConcurrentDictionary<long, ulong> _lastPushApartGiveUpLogFrames = new();

        // Layered Armor Occlusion context tracking: Key: Grid EntityId, Value: Global impact position
        private static readonly ConcurrentDictionary<long, Vector3D> _lastImpactPositions = new();

        // Push-apart escape direction source: raw voxel contact normal cached per grid, independent of the arbitrator toggle
        private static readonly ConcurrentDictionary<long, Vector3D> _lastVoxelContactNormals = new();

        // Push-apart context tracking: latest Havok contact penetration depth per grid (distance < 0 means penetrating)
        private static readonly ConcurrentDictionary<long, float> _lastVoxelPenetrations = new();

        // Macroscopic terrain normal and altitude cache per colliding grid per frame: key = grid EntityId
        private struct MacroTerrainCacheEntry
        {
            public ulong Frame;
            public Vector3D SurfaceNormal;
            public Vector3D SurfaceCenter;
            public double CenterAltDiff;
            public bool IsSubmerged;
        }

        private static readonly ConcurrentDictionary<long, MacroTerrainCacheEntry> _macroTerrainCache = new();

        // Per-voxel-map coarse modification map: Key: voxel EntityId, Value: set of modified 32m region buckets.
        // Tracked from MyVoxelBase.RangeChanged (fires per carve operation), so a drilled planet only records
        // the carved regions - pristine terrain everywhere else keeps the raycast-free push direction fast path.
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _modifiedVoxelRegions = new();
        private static readonly ConcurrentDictionary<long, MyVoxelBase.StorageChanged> _voxelRangeHandlers = new();
        private const int VoxelRegionBucketShift = 5; // 32m buckets (voxel storage cells are 1m)

        private static bool _damageHandlerRegistered;

        [ThreadStatic]
        private static List<MyPhysics.HitInfo> _voxelHitsCache;

        [ThreadStatic]
        private static List<MyCubeGrid> _wheelResolveBuffer;

        [ThreadStatic]
        private static List<MyCubeGrid> _pushMechanicalGroupBuffer;

        public static void UpdateCollisionContext(long gridId, Vector3D position)
        {
            _lastImpactPositions[gridId] = position;
        }

        public static void RemoveCollisionContext(long gridId)
        {
            _lastImpactPositions.TryRemove(gridId, out _);
            _lastVoxelContactNormals.TryRemove(gridId, out _);
            _lastVoxelPenetrations.TryRemove(gridId, out _);
            _macroTerrainCache.TryRemove(gridId, out _);
        }

        public void Init(PhysicsOptimizerPlugin plugin)
        {
            _plugin = plugin;
            ClearCollections();
            SyncVoxelFakes();
            Log.Info(LogSource, "Initialized successfully.");
        }

        public void Update(ulong frameCounter)
        {
            SweepCaches(frameCounter);
        }

        public void UpdateConfig(PhysicsOptimizerConfig config)
        {
            SyncVoxelFakes();
        }

        public void OnEntityAdded(MyEntity entity)
        {
            if (entity is MyVoxelBase voxel && voxel.Storage != null) HookVoxelStorage(voxel);
        }

        private static void HookVoxelStorage(MyVoxelBase voxel)
        {
            MyVoxelBase.StorageChanged handler = (v, min, max, flags) => MarkVoxelRange(v, min, max);
            if (_voxelRangeHandlers.TryAdd(voxel.EntityId, handler))
            {
                voxel.RangeChanged += handler;
            }
        }

        private static void MarkVoxelRange(MyVoxelBase voxel, Vector3I min, Vector3I max)
        {
            ConcurrentDictionary<long, byte> buckets = _modifiedVoxelRegions.GetOrAdd(voxel.EntityId, _ => new ConcurrentDictionary<long, byte>());
            int bMinX = min.X >> VoxelRegionBucketShift, bMinY = min.Y >> VoxelRegionBucketShift, bMinZ = min.Z >> VoxelRegionBucketShift;
            int bMaxX = max.X >> VoxelRegionBucketShift, bMaxY = max.Y >> VoxelRegionBucketShift, bMaxZ = max.Z >> VoxelRegionBucketShift;
            long cellCount = (long)(bMaxX - bMinX + 1) * (bMaxY - bMinY + 1) * (bMaxZ - bMinZ + 1);
            if (cellCount > 4096)
            {
                // Huge carve (voxel clear/removal tool) - mark the whole map modified instead of iterating
                buckets[-1L] = 1;
                return;
            }
            for (int x = bMinX; x <= bMaxX; x++)
            {
                for (int y = bMinY; y <= bMaxY; y++)
                {
                    for (int z = bMinZ; z <= bMaxZ; z++)
                    {
                        buckets[PackBucket(x, y, z)] = 1;
                    }
                }
            }
        }

        private static long PackBucket(int x, int y, int z)
        {
            return ((long)(uint)x << 42) | ((long)(uint)y << 21) | (uint)z;
        }

        public static int CachedModifiedVoxelBucketsCount
        {
            get
            {
                int total = 0;
                foreach (var dict in _modifiedVoxelRegions.Values)
                {
                    total += dict.Count;
                }
                return total;
            }
        }

        public static int CachedMacroTerrainCount => _macroTerrainCache.Count;

        /// <summary>True if the 32m region around worldPos was carved/drilled at some point (per RangeChanged tracking).</summary>
        private static bool IsVoxelRegionModified(MyVoxelBase voxel, Vector3D worldPos)
        {
            if (!_modifiedVoxelRegions.TryGetValue(voxel.EntityId, out ConcurrentDictionary<long, byte> buckets) || buckets.IsEmpty) return false;
            if (buckets.ContainsKey(-1L)) return true;
            Vector3D local = (worldPos - voxel.PositionLeftBottomCorner) / voxel.VoxelSize;
            int cx = (int)Math.Floor(local.X), cy = (int)Math.Floor(local.Y), cz = (int)Math.Floor(local.Z);
            long bucket = PackBucket(cx >> VoxelRegionBucketShift, cy >> VoxelRegionBucketShift, cz >> VoxelRegionBucketShift);
            return buckets.ContainsKey(bucket);
        }

        public void OnEntityRemoved(MyEntity entity)
        {
            if (entity is MyCubeGrid grid)
            {
                EvictGrid(grid.EntityId);
                RemoveCollisionContext(grid.EntityId);
            }
            else if (entity is MyVoxelBase voxel)
            {
                if (_voxelRangeHandlers.TryRemove(voxel.EntityId, out var handler))
                {
                    voxel.RangeChanged -= handler;
                }
                _modifiedVoxelRegions.TryRemove(voxel.EntityId, out _);
            }
        }

        public void Dispose()
        {
            RestoreVoxelFakes();
            UnregisterDamageHandler();
            ClearCollections();
            ClearAllDebugGps();
            _plugin = null;
        }

        private static void UnregisterDamageHandler()
        {
            if (!_damageHandlerRegistered) return;
            try
            {
                if (MyDamageSystem.Static != null)
                {
                    FieldInfo beforeField = typeof(MyDamageSystem).GetField("m_beforeDamageHandlers", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (beforeField?.GetValue(MyDamageSystem.Static) is List<Tuple<int, BeforeDamageApplied>> handlers)
                    {
                        handlers.RemoveAll(t => t.Item2 == OnBeforeDamageApplied);
                        Log.Info(LogSource, "Unregistered Layered Armor Occlusion damage handler on Dispose.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, LogSource, "Failed to unregister damage handler on Dispose.");
            }
            finally
            {
                _damageHandlerRegistered = false;
            }
        }

        private void ClearCollections()
        {
            while (_pushQueue.TryDequeue(out _)) { }
            _pendingVoxelArbDebugGps.Clear();
            foreach (var id in _activeMissiles.Keys)
            {
                if (MyEntities.TryGetEntityById(id, out MyEntity ent) && ent is MyCubeGrid g)
                {
                    g.OnClose -= OnTrackedGridClosed;
                    g.OnGridSplit -= OnTrackedGridSplit;
                }
            }
            _activeMissiles.Clear();
            _lastDeformationFrames.Clear();
            _consecutiveContactFrames.Clear();
            _lastContactFrameTracker.Clear();
            _lastRammingLogFrames.Clear();
            _lastVoxelLogFrames.Clear();
            _lastStationLogFrames.Clear();
            _lastSubgridLogFrames.Clear();
            _lastExtremeSpeedLogFrames.Clear();
            _lastImpactPositions.Clear();
            _lastVoxelContactNormals.Clear();
            _lastVoxelPenetrations.Clear();
            _macroTerrainCache.Clear();
            _pushApartAttempts.Clear();
            _lastPushApartGiveUpLogFrames.Clear();
            _wheelBaseGridIds.Clear();
            _lastEscapes.Clear();
            _contactStartPositions.Clear();
            _lastVoxelContactFrames.Clear();
            _consecutiveVoxelContactFrames.Clear();
            _lastVoxelContactFrameTracker.Clear();
            _voxelContactStartPositions.Clear();
            _lastPushApartGateLogFrames.Clear();
            _constructClangRecords.Clear();
            _lastVoxelArbitratorLogFrames.Clear();
            _pendingVoxelArbDebugGps.Clear();

            foreach (var kvp in _voxelRangeHandlers)
            {
                if (MyEntities.TryGetEntityById(kvp.Key, out MyEntity ent) && ent is MyVoxelBase v)
                {
                    v.RangeChanged -= kvp.Value;
                }
            }
            _voxelRangeHandlers.Clear();
            _modifiedVoxelRegions.Clear();
        }

        public void SyncVoxelFakes()
        {
            if (_plugin?.Config != null && _plugin.Config.Enabled && _plugin.Config.SuppressAllVoxelExplosionDamage)
            {
                MyFakes.DEFORMATION_EXPLOSIONS = false; // Disables voxel cutouts from collision deformation
            }
        }

        public static void RestoreVoxelFakes()
        {
            MyFakes.DEFORMATION_EXPLOSIONS = true;
        }

        /// <summary>
        /// Evaluates whether a given grid qualifies as a player-made missile based on size and speed.
        /// </summary>
        public bool IsMissile(MyCubeGrid testGrid, float speed)
        {
            if (_plugin?.Config == null) return false;
            PhysicsOptimizerConfig config = _plugin.Config;

            if (testGrid == null || testGrid.MarkedForClose || testGrid.Closed || testGrid.IsStatic) return false;
            if (speed < config.MissileMinVelocity) return false;
            if (config.ExemptPilotedFromMissileStatus && (testGrid.GridSystems?.ControlSystem?.IsControlled ?? false))
            {
                int b = testGrid.BlocksCount;
                bool wouldBeMissile = testGrid.IsLargeGrid()
                    ? (b >= config.LargeGridMissileMinBlocks && b <= config.LargeGridMissileMaxBlocks)
                    : (b >= config.SmallGridMissileMinBlocks && b <= config.SmallGridMissileMaxBlocks);
                if (wouldBeMissile)
                {
                    _plugin?.DefenseStats?.IncrementPilotedBuggySaves();
                }
                return false;
            }

            int blocks = testGrid.BlocksCount;
            if (testGrid.IsLargeGrid())
            {
                return blocks >= config.LargeGridMissileMinBlocks && blocks <= config.LargeGridMissileMaxBlocks;
            }
            if (testGrid.IsSmallGrid())
            {
                return blocks >= config.SmallGridMissileMinBlocks && blocks <= config.SmallGridMissileMaxBlocks;
            }
            return false;
        }

        public static string GetUniversalLocation(Vector3D pos)
        {
            var planet = MyGamePruningStructure.GetClosestPlanet(pos);
            if (planet != null && planet.PositionComp != null)
            {
                Vector3D planetCenter = planet.PositionComp.GetPosition();
                double distCenter = Vector3D.Distance(pos, planetCenter);
                if (distCenter <= planet.MaximumRadius * 1.5)
                {
                    double altKm = Math.Max(0.0, (distCenter - planet.AverageRadius) / 1000.0);
                    string planetName = planet.Generator?.Id.SubtypeName;
                    if (string.IsNullOrEmpty(planetName)) planetName = planet.StorageName ?? "Planet";
                    return $"{planetName} ({altKm:F1} km alt)";
                }
            }

            double distOriginKm = pos.Length() / 1000.0;
            if (distOriginKm < 1.0)
            {
                return "Deep Space (Origin)";
            }
            return $"Deep Space ({distOriginKm:N0} km)";
        }

        private static readonly ConcurrentQueue<PhysicsIncident> _incidentQueue = new();
        private const int MaxIncidentHistory = 10;

        public static void RecordIncident(string icon, string message)
        {
            _incidentQueue.Enqueue(new PhysicsIncident
            {
                UtcTime = DateTime.UtcNow,
                Icon = icon,
                Message = message
            });
            while (_incidentQueue.Count > MaxIncidentHistory)
            {
                _incidentQueue.TryDequeue(out _);
            }
        }

        public static List<PhysicsIncident> GetRecentIncidents(int maxResults = 5)
        {
            var arr = _incidentQueue.ToArray();
            var list = new List<PhysicsIncident>(arr.Length);
            for (int i = arr.Length - 1; i >= 0 && list.Count < maxResults; i--)
            {
                list.Add(arr[i]);
            }
            return list;
        }

        private static void RecordBlockedClang(MyCubeGrid grid, ulong currentFrame)
        {
            if (grid == null || currentFrame == 0) return;
            MyCubeGrid topGrid = GridUtils.GetMainGrid(grid) ?? grid;
            long constructId = topGrid.EntityId;
            ConstructClangRecord record = _constructClangRecords.GetOrAdd(constructId, id => new ConstructClangRecord
            {
                ConstructId = id,
                DisplayName = topGrid.DisplayName,
                CurrentSecondStartFrame = currentFrame,
                LastClangFrame = currentFrame,
                LastClangUtc = DateTime.UtcNow,
                LastPosition = topGrid.PositionComp?.GetPosition() ?? Vector3D.Zero
            });

            record.DisplayName = topGrid.DisplayName;
            record.LastClangFrame = currentFrame;
            record.LastClangUtc = DateTime.UtcNow;
            if (topGrid.PositionComp != null)
            {
                record.LastPosition = topGrid.PositionComp.GetPosition();
            }
            Interlocked.Increment(ref record.TotalClangs);

            if (currentFrame >= record.CurrentSecondStartFrame + 60)
            {
                if (currentFrame >= record.CurrentSecondStartFrame + 120)
                {
                    record.PreviousSecondClangs = 0;
                    record.CurrentSecondClangs = 1;
                }
                else
                {
                    record.PreviousSecondClangs = record.CurrentSecondClangs;
                    record.CurrentSecondClangs = 1;
                }
                record.CurrentSecondStartFrame = currentFrame;
            }
            else
            {
                record.CurrentSecondClangs++;
            }

            if (record.CurrentSecondClangs > record.PeakRate)
            {
                record.PeakRate = record.CurrentSecondClangs;
            }

            if (record.CurrentSecondClangs == 10)
            {
                string loc = GetUniversalLocation(record.LastPosition);
                RecordIncident("🚨", $"Clang surge on '{record.DisplayName}' (10/s at {loc}).");
            }
        }

        private bool BlockDeformation(MyCubeGrid grid, ulong currentFrame, DefenseStatistics stats, bool isRamming = false, bool isVoxel = false, bool isSubgrid = false, bool isCooldown = false, bool isLowSpeed = false, bool isStation = false, bool isDebris = false)
        {
            RecordBlockedClang(grid, currentFrame);
            stats?.IncrementBlocked(isRamming, isVoxel, isSubgrid, isCooldown, isLowSpeed, isStation, isDebris);
            return false;
        }

        public static List<(string Name, long Id, int Rate, int Total)> GetActiveClangers(int minRate = 1, int maxResults = 5)
        {
            if (_constructClangRecords.IsEmpty) return null;
            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            List<(string Name, long Id, int Rate, int Total)> results = null;

            foreach (var kvp in _constructClangRecords)
            {
                ConstructClangRecord rec = kvp.Value;
                if (rec == null) continue;

                int rate = GetConstructClangRate(kvp.Key, currentFrame);
                if (rate >= minRate)
                {
                    results ??= new List<(string Name, long Id, int Rate, int Total)>();
                    results.Add((rec.DisplayName ?? "Unknown", kvp.Key, rate, rec.TotalClangs));
                }
            }

            if (results != null && results.Count > 1)
            {
                results.Sort((a, b) => b.Rate.CompareTo(a.Rate));
                if (results.Count > maxResults)
                {
                    results.RemoveRange(maxResults, results.Count - maxResults);
                }
            }

            return results;
        }

        /// <summary>
        /// Instantly dampens linear and angular velocity and deactivates physics across an entire mechanical construct.
        /// Must be invoked on the game simulation thread.
        /// </summary>
        public static List<ClangOffender> GetSessionTopClangers(int maxResults = 20)
        {
            if (_constructClangRecords.IsEmpty) return new List<ClangOffender>(0);
            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            var list = new List<ClangOffender>(_constructClangRecords.Count);

            foreach (var kvp in _constructClangRecords)
            {
                ConstructClangRecord rec = kvp.Value;
                if (rec == null || rec.TotalClangs <= 0) continue;

                int currentRate = GetConstructClangRate(kvp.Key, currentFrame);
                list.Add(new ClangOffender
                {
                    ConstructId = rec.ConstructId,
                    DisplayName = rec.DisplayName ?? "Unknown Construct",
                    TotalClangs = rec.TotalClangs,
                    PeakRate = Math.Max(rec.PeakRate, currentRate),
                    CurrentRate = currentRate,
                    LastClangUtc = rec.LastClangUtc,
                    LastPosition = rec.LastPosition,
                    LocationDisplay = GetUniversalLocation(rec.LastPosition)
                });
            }

            list.Sort((a, b) =>
            {
                if (a.IsActive != b.IsActive) return a.IsActive ? -1 : 1;
                if (a.IsActive) return b.CurrentRate.CompareTo(a.CurrentRate);
                return b.TotalClangs.CompareTo(a.TotalClangs);
            });

            if (list.Count > maxResults)
            {
                list.RemoveRange(maxResults, list.Count - maxResults);
            }

            return list;
        }

        public static void ClearClangRecords()
        {
            _constructClangRecords.Clear();
        }

        public static bool DampenConstruct(long constructId)
        {
            if (!MyEntities.TryGetEntityById(constructId, out MyEntity ent) || ent is not MyCubeGrid grid)
            {
                return false;
            }

            var group = new List<MyCubeGrid>();
            GridUtils.GetMechanicalGroupMembers(grid, group);

            foreach (var member in group)
            {
                if (member == null || member.MarkedForClose || member.Closed) continue;
                if (member.Physics != null)
                {
                    member.Physics.LinearVelocity = Vector3.Zero;
                    member.Physics.AngularVelocity = Vector3.Zero;
                    if (member.Physics.RigidBody != null)
                    {
                        member.Physics.RigidBody.LinearVelocity = Vector3.Zero;
                        member.Physics.RigidBody.AngularVelocity = Vector3.Zero;
                        member.Physics.RigidBody.Deactivate();
                    }
                }
            }

            ResetConstructClangTracking(constructId, MySandboxGame.Static?.SimulationFrameCounter ?? 0);
            RecordIncident("🛑", $"Dampened construct '{grid.DisplayName}' and deactivated rigid bodies.");
            return true;
        }

        /// <summary>
        /// Dampens all active clangers currently detected by the defense engine.
        /// Must be invoked on the game simulation thread.
        /// </summary>
        public static bool NudgeConstruct(long constructId, float distanceMeters = 1.0f)
        {
            if (!MyEntities.TryGetEntityById(constructId, out MyEntity ent) || ent is not MyCubeGrid grid)
            {
                return false;
            }

            MyCubeGrid topGrid = GridUtils.GetMainGrid(grid) ?? grid;
            Vector3D pos = topGrid.PositionComp.GetPosition();

            Vector3D upDir = Vector3D.Up;
            var planet = MyGamePruningStructure.GetClosestPlanet(pos);
            if (planet != null)
            {
                Vector3D grav = planet.Components?.Get<MyGravityProviderComponent>()?.GetWorldGravity(pos) ?? Vector3D.Zero;
                if (grav.LengthSquared() > 0.0001)
                {
                    upDir = -Vector3D.Normalize(grav);
                }
            }

            Vector3D offset = upDir * distanceMeters;
            var group = new List<MyCubeGrid>();
            GridUtils.GetMechanicalGroupMembers(topGrid, group);

            foreach (var member in group)
            {
                if (member == null || member.MarkedForClose || member.Closed) continue;
                var currentPos = member.PositionComp.GetPosition();
                member.PositionComp.SetPosition(currentPos + offset);
                if (member.Physics != null)
                {
                    member.Physics.LinearVelocity = Vector3.Zero;
                    member.Physics.AngularVelocity = Vector3.Zero;
                    if (member.Physics.RigidBody != null)
                    {
                        member.Physics.RigidBody.LinearVelocity = Vector3.Zero;
                        member.Physics.RigidBody.AngularVelocity = Vector3.Zero;
                    }
                }
            }

            RecordIncident("🚀", $"Nudged '{topGrid.DisplayName}' +{distanceMeters:F1}m along gravity up-vector.");
            return true;
        }

        public static bool AnchorConstruct(long constructId)
        {
            if (!MyEntities.TryGetEntityById(constructId, out MyEntity ent) || ent is not MyCubeGrid grid)
            {
                return false;
            }

            MyCubeGrid topGrid = GridUtils.GetMainGrid(grid) ?? grid;
            if (topGrid.IsStatic) return false;

            topGrid.OnConvertedToStationRequest();
            if (topGrid.Physics != null)
            {
                topGrid.Physics.LinearVelocity = Vector3.Zero;
                topGrid.Physics.AngularVelocity = Vector3.Zero;
            }

            RecordIncident("⚓", $"Converted base grid '{topGrid.DisplayName}' to Static Station.");
            return true;
        }

        public static bool DepowerConstruct(long constructId)
        {
            if (!MyEntities.TryGetEntityById(constructId, out MyEntity ent) || ent is not MyCubeGrid grid)
            {
                return false;
            }

            var group = new List<MyCubeGrid>();
            GridUtils.GetMechanicalGroupMembers(grid, group);
            int shutOffCount = 0;

            foreach (var member in group)
            {
                if (member == null || member.MarkedForClose || member.Closed) continue;
                foreach (var block in member.GetFatBlocks<Sandbox.Game.Entities.MyCubeBlock>())
                {
                    if (block is Sandbox.ModAPI.IMyPowerProducer power && power.Enabled)
                    {
                        power.Enabled = false;
                        shutOffCount++;
                    }
                }
            }

            RecordIncident("🔌", $"Depowered '{grid.DisplayName}' ({shutOffCount} power producers shut off).");
            return true;
        }

        public static int DampenAllClangers()
        {
            var clangers = GetActiveClangers(minRate: 1, maxResults: 50);
            if (clangers == null || clangers.Count == 0) return 0;

            int dampenedCount = 0;
            foreach (var clanger in clangers)
            {
                if (DampenConstruct(clanger.Id))
                {
                    dampenedCount++;
                }
            }
            return dampenedCount;
        }

        private static int GetConstructClangRate(long constructId, ulong currentFrame)
        {
            if (_constructClangRecords.TryGetValue(constructId, out ConstructClangRecord cRec) && cRec != null)
            {
                if (currentFrame == 0 || cRec.LastClangFrame == 0) return 0;

                // If no clangs occurred in the last 60 frames (1 second), rate drops to 0
                if (currentFrame > cRec.LastClangFrame + 60)
                {
                    return 0;
                }

                ulong framesSinceStart = currentFrame - cRec.CurrentSecondStartFrame;
                if (framesSinceStart >= 120)
                {
                    return 0;
                }
                if (framesSinceStart >= 60)
                {
                    return cRec.CurrentSecondClangs;
                }

                int prevPortion = (int)((cRec.PreviousSecondClangs * (60L - (long)framesSinceStart)) / 60L);
                return prevPortion + cRec.CurrentSecondClangs;
            }
            return 0;
        }

        private static int GetConstructClangTotal(long constructId)
        {
            return _constructClangRecords.TryGetValue(constructId, out ConstructClangRecord cRec) ? cRec.TotalClangs : 0;
        }

        private static void ResetConstructClangTracking(long constructId, ulong currentFrame)
        {
            if (_constructClangRecords.TryGetValue(constructId, out ConstructClangRecord rec) && rec != null)
            {
                rec.CurrentSecondClangs = 0;
                rec.PreviousSecondClangs = 0;
                rec.CurrentSecondStartFrame = currentFrame;
            }
        }

        /// <summary>
        /// Main collision decision hook. Filters deformation across the sequential defense pipeline.
        /// </summary>
        public bool ShouldAllowDeformation(MyGridPhysics physics, MyEntity otherEntity, ref float separatingVelocity)
        {
            if (_plugin?.Config == null || !_plugin.Config.Enabled || !_plugin.Config.EnableGridDefender) return true;
            PhysicsOptimizerConfig config = _plugin.Config;
            DefenseStatistics stats = _plugin.DefenseStats;

            if (physics?.Entity is not MyCubeGrid grid || grid.MarkedForClose || grid.Closed) return true;
            if (otherEntity == null || otherEntity.MarkedForClose || otherEntity.Closed) return true;

            stats?.IncrementEvaluated();

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;

            // 1. Subgrid / Mechanicals (Pistons, Rotors, Hinges, Connectors) Protection
            bool isConnectedSubgrid = false;
            if (otherEntity is MyCubeGrid otherGrid)
            {
                isConnectedSubgrid = GridUtils.AreInSameMechanicalGroup(grid, otherGrid) || GridUtils.AreInSameLogicalGroup(grid, otherGrid);
                if (config.ProtectSubgrids && isConnectedSubgrid)
                {
                    if (config.EnableDebugLogging && ShouldLog(_lastSubgridLogFrames, grid.EntityId ^ otherGrid.EntityId, currentFrame, 120))
                    {
                        Log.Info(LogSource, $"[SUBGRID] Protected: Blocked collision between '{grid.DisplayName}' and '{otherGrid.DisplayName}'.");
                    }
                    return BlockDeformation(grid, currentFrame, stats, isSubgrid: true);
                }
            }

            // 2. Floating Objects / Ores / Loose Debris Protection
            if (otherEntity is MyFloatingObject && config.ProtectAgainstFloatingObjects)
            {
                return BlockDeformation(grid, currentFrame, stats, isDebris: true);
            }

            // 3. Speed calculations (hoisted after subgrid/debris early exits; pre-squared to minimize sqrt calls)
            float gridSpeedSq = grid.GetSpeedSquared();
            float otherSpeedSq = (otherEntity as MyCubeGrid)?.GetSpeedSquared() ?? 0f;
            float absSepVelocity = Math.Abs(separatingVelocity);
            float impactSpeedSq = Math.Max(absSepVelocity * absSepVelocity, Math.Max(gridSpeedSq, otherSpeedSq));
            float impactSpeed = (float)Math.Sqrt(impactSpeedSq);

            // 4. Missile (PMW) Evaluation & Engagement Tracking (Targets grids only, never voxels)
            if (otherEntity is MyCubeGrid targetGrid)
            {
                bool gridInMissile = _activeMissiles.TryGetValue(grid.EntityId, out MissileEngagement gridEngage) && currentFrame <= gridEngage.ExpireFrame;
                bool otherInMissile = _activeMissiles.TryGetValue(targetGrid.EntityId, out MissileEngagement otherEngage) && currentFrame <= otherEngage.ExpireFrame;

                // Friendly-fire shield: Suppress self-damage between splits of the same missile
                if (gridInMissile && otherInMissile && gridEngage.GroupId == otherEngage.GroupId)
                {
                    return BlockDeformation(grid, currentFrame, stats);
                }

                bool gridQualifies = gridInMissile || (config.AllowMissileDamage && IsMissile(grid, impactSpeed));
                bool otherQualifies = otherInMissile || (config.AllowMissileDamage && IsMissile(targetGrid, impactSpeed));

                if (gridQualifies || otherQualifies)
                {
                    MyCubeGrid missileObj = gridQualifies ? grid : targetGrid;
                    MyCubeGrid targetObj = ReferenceEquals(missileObj, grid) ? targetGrid : grid;

                    MissileEngagement engagement;
                    if (gridInMissile)
                    {
                        engagement = gridEngage;
                        engagement.ExpireFrame = Math.Max(engagement.ExpireFrame, currentFrame + 30);
                    }
                    else if (otherInMissile)
                    {
                        engagement = otherEngage;
                        engagement.ExpireFrame = Math.Max(engagement.ExpireFrame, currentFrame + 30);
                    }
                    else
                    {
                        long groupId = Interlocked.Increment(ref _nextMissileGroupId);
                        ulong expire = currentFrame + 60; // 60 frames (~1.0s) active penetration window

                        engagement = new MissileEngagement
                        {
                            GroupId = groupId,
                            ExpireFrame = expire,
                            MissileName = missileObj.DisplayName,
                            TargetName = targetObj.DisplayName,
                            InitialSpeed = impactSpeed,
                            ImpactCount = 0
                        };

                        if (config.EnableDebugLogging && config.LogMissileDefense)
                        {
                            if (gridQualifies && otherQualifies)
                            {
                                Log.Info(LogSource, $"[MISSILE] Head-on missile collision ALLOWED: '{grid.DisplayName}' and '{targetGrid.DisplayName}' collided at {impactSpeed:F1} m/s.");
                            }
                            else
                            {
                                Log.Info(LogSource, $"[MISSILE] Impact ALLOWED: '{engagement.MissileName}' struck '{engagement.TargetName}' at {engagement.InitialSpeed:F1} m/s.");
                            }
                        }
                    }

                    if (Interlocked.Increment(ref engagement.ImpactCount) == 1 && !engagement.NotificationSent)
                    {
                        engagement.NotificationSent = true;
                        ChatNotificationService.SendPmwStrikeNotification(missileObj, targetObj, engagement.InitialSpeed, config);
                    }

                    if (gridQualifies && !gridInMissile) RegisterActiveMissile(grid, engagement);
                    if (otherQualifies && !otherInMissile) RegisterActiveMissile(targetGrid, engagement);

                    return AllowOrScale(grid, ref separatingVelocity, isMissile: true);
                }
            }

            // 5. Safe Docking, Parking, and Slow Driving Check (Low-speed floor)
            if (impactSpeed < config.MinDrivingVelocity)
            {
                if (config.EnableDebugLogging && impactSpeed >= 1.5f && ShouldLog(_lastRammingLogFrames, grid.EntityId, currentFrame, 120))
                {
                    if (otherEntity is MyVoxelBase)
                        Log.Info(LogSource, $"[DRIVING] Low-speed voxel contact on '{grid.DisplayName}' - normal driving, suppression harmless.");
                    else
                        Log.Info(LogSource, $"[DOCKING] Safe Docking: Suppressed low-speed bump on '{grid.DisplayName}'.");
                }
                return BlockDeformation(grid, currentFrame, stats, isLowSpeed: true);
            }

            // 6. Extreme Velocity Anti-Freeze Limit (Non-missiles only)
            if (config.MaxDeformationVelocity > 0 && impactSpeed > config.MaxDeformationVelocity)
            {
                if (config.EnableDebugLogging && ShouldLog(_lastExtremeSpeedLogFrames, grid.EntityId, currentFrame, 60))
                {
                    Log.Warn(LogSource, $"[SPEED] Limit: Suppressed collision on '{grid.DisplayName}' ({impactSpeed:F1} m/s > {config.MaxDeformationVelocity:F1} m/s limit).");
                }
                return BlockDeformation(grid, currentFrame, stats, isRamming: otherEntity is MyCubeGrid, isVoxel: otherEntity is MyVoxelBase);
            }

            // 7. Non-Missile Collisions (Ships, Rovers, Stations, Voxels)
            // A. Static Station Protection
            bool otherIsStaticGrid = otherEntity is MyCubeGrid { IsStatic: true };
            if ((grid.IsStatic || otherIsStaticGrid) && config.ProtectStaticGrids)
            {
                if (config.EnableDebugLogging && ShouldLog(_lastStationLogFrames, grid.EntityId ^ otherEntity.EntityId, currentFrame, 60))
                {
                    string stationName = grid.IsStatic ? grid.DisplayName : otherEntity.DisplayName;
                    string strikingName = grid.IsStatic ? otherEntity.DisplayName : grid.DisplayName;
                    Log.Info(LogSource, $"[STATION] Protected: '{strikingName}' struck static station '{stationName}' at {impactSpeed:F1} m/s (damage suppressed).");
                }
                return BlockDeformation(grid, currentFrame, stats, isStation: true);
            }

            // B. Ship vs Voxel Protection (Asteroids, terrain, and off-target missiles hitting dirt)
            if (otherEntity is MyVoxelBase)
            {
                if (config.ProtectShipsAgainstVoxels)
                {
                    if (config.EnableDebugLogging && ShouldLog(_lastVoxelLogFrames, grid.EntityId, currentFrame, 60))
                    {
                        Log.Info(LogSource, $"[VOXEL] Terrain Crash Blocked: '{grid.DisplayName}' ({grid.BlocksCount} blocks) hit voxels at {impactSpeed:F1} m/s (damage suppressed).");
                    }
                    return BlockDeformation(grid, currentFrame, stats, isVoxel: true);
                }
                return AllowOrScale(grid, ref separatingVelocity, isMissile: false);
            }

            // C. Ship vs Ship Ramming Protection
            if (otherEntity is MyCubeGrid && config.ProtectShipsAgainstRamming)
            {
                if (config.EnableDebugLogging && ShouldLog(_lastRammingLogFrames, grid.EntityId ^ otherEntity.EntityId, currentFrame, 60))
                {
                    Log.Info(LogSource, $"[RAMMING] Blocked: '{grid.DisplayName}' ({grid.BlocksCount} blocks) hit '{otherEntity.DisplayName}' at {impactSpeed:F1} m/s (damage suppressed).");
                }
                return BlockDeformation(grid, currentFrame, stats, isRamming: true);
            }

            // 8. Rate Limiting / Cooldown for any unprotected continuous deformations
            if (config.DeformationCooldownFrames > 0 && currentFrame > 0)
            {
                if (_lastDeformationFrames.TryGetValue(grid.EntityId, out ulong lastFrame))
                {
                    if (currentFrame >= lastFrame && (currentFrame - lastFrame) < (ulong)config.DeformationCooldownFrames)
                    {
                        return BlockDeformation(grid, currentFrame, stats, isCooldown: true);
                    }
                }
            }

            return AllowOrScale(grid, ref separatingVelocity, isMissile: false);
        }

        private void RegisterActiveMissile(MyCubeGrid missileGrid, MissileEngagement engagement)
        {
            if (missileGrid == null || missileGrid.MarkedForClose || missileGrid.Closed || engagement == null) return;

            long id = missileGrid.EntityId;
            _activeMissiles[id] = engagement;

            missileGrid.OnClose -= OnTrackedGridClosed;
            missileGrid.OnClose += OnTrackedGridClosed;
            missileGrid.OnGridSplit -= OnTrackedGridSplit;
            missileGrid.OnGridSplit += OnTrackedGridSplit;
        }

        private void EvictGrid(long id)
        {
            if (_activeMissiles.TryRemove(id, out _) && MyEntities.TryGetEntityById(id, out MyEntity ent) && ent is MyCubeGrid grid)
            {
                grid.OnClose -= OnTrackedGridClosed;
                grid.OnGridSplit -= OnTrackedGridSplit;
            }
            _lastDeformationFrames.TryRemove(id, out _);
            _consecutiveContactFrames.TryRemove(id, out _);
            _lastContactFrameTracker.TryRemove(id, out _);
            _lastRammingLogFrames.TryRemove(id, out _);
            _lastVoxelLogFrames.TryRemove(id, out _);
            _lastStationLogFrames.TryRemove(id, out _);
            _lastSubgridLogFrames.TryRemove(id, out _);
            _lastExtremeSpeedLogFrames.TryRemove(id, out _);
            _lastVoxelArbitratorLogFrames.TryRemove(id, out _);
            _lastVoxelContactFrames.TryRemove(id, out _);
            _lastVoxelContactNormals.TryRemove(id, out _);
            _lastVoxelPenetrations.TryRemove(id, out _);
            _macroTerrainCache.TryRemove(id, out _);
            _pushApartAttempts.TryRemove(id, out _);
            _lastPushApartGiveUpLogFrames.TryRemove(id, out _);
            _wheelBaseGridIds.TryRemove(id, out _);
            _lastEscapes.TryRemove(id, out _);
            _contactStartPositions.TryRemove(id, out _);
            _consecutiveVoxelContactFrames.TryRemove(id, out _);
            _lastVoxelContactFrameTracker.TryRemove(id, out _);
            _voxelContactStartPositions.TryRemove(id, out _);
            _lastPushApartGateLogFrames.TryRemove(id, out _);
            _constructClangRecords.TryRemove(id, out _);
            _pendingVoxelArbDebugGps.TryRemove(id, out _);
        }

        private void OnTrackedGridClosed(IMyEntity entity)
        {
            if (entity == null) return;
            if (entity is MyCubeGrid grid)
            {
                grid.OnClose -= OnTrackedGridClosed;
                grid.OnGridSplit -= OnTrackedGridSplit;
            }
            EvictGrid(entity.EntityId);
        }

        private void OnTrackedGridSplit(MyCubeGrid originalGrid, MyCubeGrid newGrid)
        {
            if (originalGrid == null || newGrid == null || newGrid.MarkedForClose || newGrid.Closed) return;

            if (_activeMissiles.TryGetValue(originalGrid.EntityId, out MissileEngagement engagement))
            {
                RegisterActiveMissile(newGrid, engagement);
            }
        }

        /// <summary>
        /// Cached wheel subgrid test. Wheels are single-block subgrids, so the scan is trivial.
        /// Returns true with baseGridId set when the grid hosts a MyWheel attached to a suspension stator.
        /// </summary>
        private static bool IsWheelSubgrid(MyCubeGrid grid, out long baseGridId)
        {
            baseGridId = -1L;
            if (grid == null || grid.MarkedForClose || grid.Closed) return false;

            if (_wheelBaseGridIds.TryGetValue(grid.EntityId, out long cached))
            {
                baseGridId = cached > 0L ? cached : -1L;
                return cached > 0L;
            }

            long resolved = TryResolveWheelBaseGrid(grid);
            _wheelBaseGridIds[grid.EntityId] = resolved;
            baseGridId = resolved > 0L ? resolved : -1L;
            return resolved > 0L;
        }

        private static long TryResolveWheelBaseGrid(MyCubeGrid grid)
        {
            foreach (MyCubeBlock fat in grid.GetFatBlocks())
            {
                if (fat is MyMotorRotor rotor && rotor.Stator is MyMotorSuspension)
                {
                    MyMechanicalConnectionBlockBase stator = rotor.Stator;
                    if (stator?.CubeGrid != null && !stator.CubeGrid.MarkedForClose && !stator.CubeGrid.Closed)
                    {
                        return stator.CubeGrid.EntityId;
                    }
                }
            }

            // Fallback: mechanical group scan for the base grid (wheels should always have a stator,
            // but if the stator reference is not yet wired, pick the first non-rotor member).
            _wheelResolveBuffer ??= new List<MyCubeGrid>();
            _wheelResolveBuffer.Clear();
            try
            {
                GridUtils.GetMechanicalGroupMembers(grid, _wheelResolveBuffer);
                foreach (MyCubeGrid member in _wheelResolveBuffer)
                {
                    if (member == null || member.MarkedForClose || member.Closed || member.EntityId == grid.EntityId) continue;
                    foreach (MyCubeBlock fat in member.GetFatBlocks())
                    {
                        if (fat != null && !(fat is MyMotorRotor)) return member.EntityId;
                    }
                }
                return -1L;
            }
            finally
            {
                _wheelResolveBuffer.Clear();
            }
        }

        public void RecordGridContactFrame(MyCubeGrid grid, MyCubeGrid otherGrid, PhysicsOptimizerConfig config)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed || grid.IsStatic || otherGrid == null || otherGrid.MarkedForClose || otherGrid.Closed || otherGrid.IsStatic) return;

            long gridEntityId = grid.EntityId;
            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (currentFrame == 0) return;

            int contactCount;
            if (_lastContactFrameTracker.TryGetValue(gridEntityId, out ulong lastFrame))
            {
                if (currentFrame == lastFrame)
                {
                    contactCount = _consecutiveContactFrames.TryGetValue(gridEntityId, out int c) ? c : 1;
                }
                else if (currentFrame == lastFrame + 1)
                {
                    contactCount = _consecutiveContactFrames.AddOrUpdate(gridEntityId, 1, (k, v) => v + 1);
                    _lastContactFrameTracker[gridEntityId] = currentFrame;
                }
                else
                {
                    contactCount = 1;
                    _consecutiveContactFrames[gridEntityId] = 1;
                    _lastContactFrameTracker[gridEntityId] = currentFrame;
                    _contactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
                }
            }
            else
            {
                contactCount = 1;
                _consecutiveContactFrames[gridEntityId] = 1;
                _lastContactFrameTracker[gridEntityId] = currentFrame;
                _contactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
            }

            bool areConnectedSubgrids = GridUtils.AreInSameMechanicalGroup(grid, otherGrid) || GridUtils.AreInSameLogicalGroup(grid, otherGrid);
            if (areConnectedSubgrids) return;

            // Resolve construct top grid and dynamic drift radius
            MyCubeGrid topGrid = GridUtils.GetMainGrid(grid) ?? grid;
            double constructRadius = topGrid.PositionComp.WorldVolume.Radius;
            double effectiveMaxDrift = Math.Max((double)config.PushApartMaxDrift, constructRadius * 0.10);

            // Clang gate: resting or docking grids never arm pushes;
            // only active Clang loops (>= ClangRateThreshold clangs/s) qualify
            bool isClanging = config.PushApartClangRateThreshold > 0 && GetConstructClangRate(topGrid.EntityId, currentFrame) >= config.PushApartClangRateThreshold;
            bool impactGateOpen = isClanging;

            // Drift gate: a grid covering ground during the contact window is driving/flying, not stuck.
            // Clanging grids bypass the drift gate.
            bool driftGateOpen = config.PushApartMaxDrift <= 0f || isClanging;
            if (!driftGateOpen && _contactStartPositions.TryGetValue(gridEntityId, out Vector3D startPos))
            {
                Vector3D drift = grid.PositionComp.WorldVolume.Center - startPos;
                driftGateOpen = drift.LengthSquared() <= effectiveMaxDrift * effectiveMaxDrift;
            }

            bool isWheelGrid = IsWheelSubgrid(grid, out _);
            bool pushApartAllowed = !isWheelGrid || !config.ExcludeWheelSubgridsFromPushApart;
            if (pushApartAllowed && impactGateOpen && driftGateOpen && config.EnablePushApart && contactCount >= config.PushApartThreshold)
            {
                TryPushApart(grid, otherGrid);
                _consecutiveContactFrames[gridEntityId] = 0;
            }
        }

        public void RecordVoxelContactFrame(MyCubeGrid grid, MyEntity otherEntity, PhysicsOptimizerConfig config, float distance, Vector3 gridForceDir)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed || grid.IsStatic || otherEntity == null) return;

            long gridEntityId = grid.EntityId;
            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (currentFrame == 0) return;

            bool isWheelVoxel = otherEntity is MyVoxelBase && IsWheelSubgrid(grid, out _);

            int contactCount;
            if (_lastVoxelContactFrameTracker.TryGetValue(gridEntityId, out ulong lastFrame))
            {
                if (currentFrame == lastFrame)
                {
                    if (!isWheelVoxel)
                    {
                        _lastVoxelPenetrations.AddOrUpdate(gridEntityId, distance, (k, old) => Math.Min(old, distance));
                    }
                    contactCount = _consecutiveVoxelContactFrames.TryGetValue(gridEntityId, out int c) ? c : 0;
                    if (contactCount == 0) return;
                }
                else if (currentFrame == lastFrame + 1)
                {
                    contactCount = _consecutiveVoxelContactFrames.AddOrUpdate(gridEntityId, 1, (k, v) => v + 1);
                    _lastVoxelContactFrameTracker[gridEntityId] = currentFrame;
                    if (!isWheelVoxel) _lastVoxelPenetrations[gridEntityId] = distance;

                    // Rolling drift window: reset start position every threshold cycle so drift doesn't monotonically accumulate forever
                    if (config.PushApartThreshold > 0 && contactCount % config.PushApartThreshold == 0)
                    {
                        _voxelContactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
                    }
                }
                else
                {
                    contactCount = 1;
                    _consecutiveVoxelContactFrames[gridEntityId] = 1;
                    _lastVoxelContactFrameTracker[gridEntityId] = currentFrame;
                    _voxelContactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
                    if (!isWheelVoxel) _lastVoxelPenetrations[gridEntityId] = distance;
                }
            }
            else
            {
                contactCount = 1;
                _consecutiveVoxelContactFrames[gridEntityId] = 1;
                _lastVoxelContactFrameTracker[gridEntityId] = currentFrame;
                _voxelContactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
                if (!isWheelVoxel) _lastVoxelPenetrations[gridEntityId] = distance;
            }

            bool pushApartAllowed = !isWheelVoxel || !config.ExcludeWheelSubgridsFromPushApart;

            if (pushApartAllowed && config.EnablePushApart && contactCount >= config.PushApartThreshold)
            {
                MyCubeGrid topGrid = GridUtils.GetMainGrid(grid) ?? grid;
                long constructId = topGrid.EntityId;
                double constructRadius = topGrid.PositionComp.WorldVolume.Radius;
                double effectiveMaxDrift = Math.Max((double)config.PushApartMaxDrift, constructRadius * 0.10);

                Vector3D gridCenter = grid.PositionComp.WorldVolume.Center;
                MyPlanet planet = otherEntity as MyPlanet ?? MyGamePruningStructure.GetClosestPlanet(gridCenter);
                bool isUnderSurface = false;
                bool isMacroSubmerged = false;
                if (planet != null)
                {
                    Vector3D core = planet.PositionComp.WorldVolume.Center;
                    Vector3D surfacePt = planet.GetClosestSurfacePointGlobal(ref gridCenter);
                    isUnderSurface = (gridCenter - core).LengthSquared() < (surfacePt - core).LengthSquared();
                    isMacroSubmerged = (gridCenter - core).LengthSquared() < (surfacePt - core).LengthSquared();
                }

                float effectiveDistance = distance;
                if (_lastVoxelPenetrations.TryGetValue(gridEntityId, out float recordedDist))
                {
                    effectiveDistance = Math.Min(distance, recordedDist);
                }

                // A grid is physically embedded if Havok reports contact penetration exceeding the configured threshold
                // (default 0.20m), or its center of mass is submerged below the planet heightmap surface.
                bool isPhysicallyEmbedded = effectiveDistance < -config.PushApartEmbeddedDepth;
                bool isEmbedded = isPhysicallyEmbedded || isUnderSurface;
                float recordedPenetration = effectiveDistance < -0.001f ? -effectiveDistance : 0f;
                bool isMeshPenetrated = config.PushApartEmbeddedDepth <= 0f
                    ? recordedPenetration > 0.001f
                    : recordedPenetration >= config.PushApartEmbeddedDepth;

                float speed = grid.Physics?.LinearVelocity.Length() ?? 0f;
                int clangTotal = GetConstructClangTotal(constructId);
                int clangRate = GetConstructClangRate(constructId, currentFrame);
                bool isClanging = config.PushApartClangRateThreshold > 0 && clangRate >= config.PushApartClangRateThreshold;

                // Drift gate: normal driving rovers move across the map (drift > max drift), preventing micro-teleports.
                // However, submerged grids or grids actively clanging bypass the drift gate
                // because stationary/jittering clanging rovers or rovers slowly sliding down slopes need rescue.
                double driftDist = 0.0;
                bool driftGateOpen = config.PushApartMaxDrift <= 0f || isClanging || isMacroSubmerged;
                if (_voxelContactStartPositions.TryGetValue(gridEntityId, out Vector3D startPos))
                {
                    Vector3D drift = grid.PositionComp.WorldVolume.Center - startPos;
                    driftDist = drift.Length();
                    if (!driftGateOpen)
                    {
                        driftGateOpen = driftDist <= effectiveMaxDrift;
                    }
                }
                else if (!driftGateOpen)
                {
                    _voxelContactStartPositions[gridEntityId] = grid.PositionComp.WorldVolume.Center;
                    driftGateOpen = true;
                }

                // Non-wheel chassis body is wedged if experiencing sustained mesh penetration within the drift envelope
                bool isChassisWedged = !isWheelVoxel && isMeshPenetrated && driftGateOpen;
                bool qualifyForRescue = isMacroSubmerged || isClanging || isChassisWedged;

                if (driftGateOpen && qualifyForRescue)
                {
                    TryPushApart(grid, otherEntity);
                    _consecutiveVoxelContactFrames[gridEntityId] = 0;
                }
                else if (config.LogPushApartDiagnostics && ShouldLog(_lastPushApartGateLogFrames, gridEntityId, currentFrame, 60))
                {
                    string clangMin = config.PushApartClangRateThreshold > 0 ? string.Format(CultureInfo.InvariantCulture, "{0}/s", config.PushApartClangRateThreshold) : "Off";
                    string closedReason = !driftGateOpen ? "Drift Exceeded" : "No Penetration or Clang";
                    Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                        "[PUSH-APART GATE] '{0}' ({1}) | Contacts: {2}/{3} | Gate: CLOSED ({4}) | Drift: {5:F2}m (Max: {6:F2}m, Radius: {7:F1}m) | Clangs: {8}/s (Min: {9}, Total: {10}) | Penetration: {11:F3}m (Threshold: {12:F2}m) | Speed: {13:F2} m/s | Submerged: {14}",
                        grid.DisplayName, gridEntityId, contactCount, config.PushApartThreshold, closedReason,
                        driftDist, effectiveMaxDrift, constructRadius, clangRate, clangMin, clangTotal,
                        recordedPenetration, config.PushApartEmbeddedDepth, speed, isMacroSubmerged));
                }
            }
        }

        /// <summary>True if the grid recorded a voxel contact within the last 30 sim frames.</summary>
        public static bool HadRecentVoxelContact(long gridId) => HadVoxelContactWithin(gridId, 30);

        /// <summary>True if the grid recorded a voxel contact within the given number of sim frames.</summary>
        private static bool HadVoxelContactWithin(long gridId, ulong frames)
        {
            ulong frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            return frame > 0 && _lastVoxelContactFrames.TryGetValue(gridId, out ulong last) && frame >= last && (frame - last) <= frames;
        }

        /// <summary>
        /// Samples macroscopic terrain normal around the given center on a planet surface using a 5-point cross pattern.
        /// Performs single quadtree elevation sampling to return both the smooth outward normal and heightmap surface center.
        /// </summary>
        private static Vector3D SampleMacroTerrainNormal(MyPlanet planet, Vector3D center, Vector3D up, double sampleRadius, out Vector3D surfaceCenter)
        {
            surfaceCenter = planet.GetClosestSurfacePointGlobal(ref center);
            Vector3D t1 = Vector3D.CalculatePerpendicularVector(up);
            Vector3D t2 = Vector3D.Normalize(Vector3D.Cross(up, t1));

            Vector3D s1 = center + t1 * sampleRadius;
            Vector3D s2 = center - t1 * sampleRadius;
            Vector3D s3 = center + t2 * sampleRadius;
            Vector3D s4 = center - t2 * sampleRadius;

            Vector3D p1 = planet.GetClosestSurfacePointGlobal(ref s1);
            Vector3D p2 = planet.GetClosestSurfacePointGlobal(ref s2);
            Vector3D p3 = planet.GetClosestSurfacePointGlobal(ref s3);
            Vector3D p4 = planet.GetClosestSurfacePointGlobal(ref s4);

            Vector3D v1 = p1 - p2;
            Vector3D v2 = p3 - p4;
            Vector3D cross = Vector3D.Cross(v1, v2);
            Vector3D normal = cross.LengthSquared() > 1e-6 ? Vector3D.Normalize(cross) : up;
            return Vector3D.Dot(normal, up) >= 0.0 ? normal : -normal;
        }

        /// <summary>
        /// Resolves the push-apart escape direction for a grid embedded in or colliding with voxels.
        /// Uses the raw collision impact normal parallel to the surface, aligned away from the voxel face into open air
        /// when above ground, or toward/above the heightmap surface when underground.
        /// Uses universal 5-point macroscopic terrain sampling on planets pointing perpendicularly outward into open air,
        /// or recorded impact normal / local up on asteroids.
        /// </summary>
        private bool TryResolveVoxelEscapeDirection(MyCubeGrid grid, PhysicsOptimizerConfig config, MyVoxelBase contactVoxel, out Vector3D dir, out Vector3D surfaceCenter)
        {
            dir = Vector3D.Zero;
            surfaceCenter = Vector3D.Zero;
            Vector3D center = grid.PositionComp.WorldVolume.Center;
            Vector3D up = grid.Physics != null && grid.Physics.Gravity.LengthSquared() > 0.1f
                ? -Vector3D.Normalize(grid.Physics.Gravity)
                : Vector3D.Up;

            MyPlanet planet = contactVoxel as MyPlanet ?? MyGamePruningStructure.GetClosestPlanet(center);
            if (planet != null)
            {
                Vector3D planetCore = planet.PositionComp.WorldVolume.Center;
                if (grid.Physics == null || grid.Physics.Gravity.LengthSquared() <= 0.1f)
                {
                    Vector3D radial = center - planetCore;
                    if (radial.LengthSquared() > 0.001) up = Vector3D.Normalize(radial);
                }

                ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                Vector3D terrainNormal;
                if (_macroTerrainCache.TryGetValue(grid.EntityId, out MacroTerrainCacheEntry cache) && cache.Frame == currentFrame)
                {
                    terrainNormal = cache.SurfaceNormal;
                    surfaceCenter = cache.SurfaceCenter;
                }
                else
                {
                    MyCubeGrid topGrid = GridUtils.GetMainGrid(grid) ?? grid;
                    double sampleRadius = Math.Max(topGrid.PositionComp.WorldVolume.Radius, 3.0);
                    terrainNormal = SampleMacroTerrainNormal(planet, center, up, sampleRadius, out surfaceCenter);
                }
                dir = terrainNormal;

                if (config.LogPushApartDiagnostics || config.EnablePushApartDebugDraw)
                {
                    double centerDist = (center - planetCore).Length();
                    double surfaceDist = (surfaceCenter - planetCore).Length();
                    bool isMacroSubmerged = centerDist < surfaceDist;
                    double dotUp = Vector3D.Dot(dir, up);
                    Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                        "[PUSH-APART DIAG] '{0}' ({1}) [VECTOR] | AltDiff: {2:F2}m | Submerged: {3} | MacroNormal: {4:F3} (SlopeDot: {5:F2}) | ResolvedDir: {6:F3}",
                        grid.DisplayName, grid.EntityId, centerDist - surfaceDist, isMacroSubmerged, terrainNormal, dotUp, dir));
                }
                return true;
            }

            // Non-planet voxels (asteroids): use contact normal pointing toward open air or local up
            surfaceCenter = center;
            Vector3D astNormal = Vector3D.Zero;
            if (!_lastVoxelContactNormals.TryGetValue(grid.EntityId, out astNormal) || astNormal.LengthSquared() <= 0.001)
            {
                _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
                _pushMechanicalGroupBuffer.Clear();
                try
                {
                    GridUtils.GetMechanicalGroupMembers(grid, _pushMechanicalGroupBuffer);
                    foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                    {
                        if (member != null && _lastVoxelContactNormals.TryGetValue(member.EntityId, out Vector3D memberNormal) && memberNormal.LengthSquared() > 0.001)
                        {
                            astNormal = memberNormal;
                            break;
                        }
                    }
                }
                finally
                {
                    _pushMechanicalGroupBuffer.Clear();
                }
            }

            if (astNormal.LengthSquared() > 0.001)
            {
                dir = Vector3D.Normalize(astNormal);
                return true;
            }

            dir = up;
            return true;
        }

        private bool TryResolveVoxelEscapeDirection(MyCubeGrid grid, PhysicsOptimizerConfig config, MyVoxelBase contactVoxel, out Vector3D dir)
        {
            return TryResolveVoxelEscapeDirection(grid, config, contactVoxel, out dir, out _);
        }

        private void TryPushApart(MyCubeGrid grid, MyEntity otherEntity)
        {
            PhysicsOptimizerConfig config = _plugin?.Config;
            if (config == null || grid == null || otherEntity == null || grid.MarkedForClose || grid.Closed || otherEntity.MarkedForClose || otherEntity.Closed) return;

            // Subgrids must delegate to the construct's main grid so the whole construct escapes together along one unified vector
            MyCubeGrid topGrid = GridUtils.GetMainGrid(grid) ?? grid;
            if (topGrid != grid)
            {
                // Suspension wheels never initiate or forward push-apart to the main chassis
                if (otherEntity is MyVoxelBase && IsWheelSubgrid(grid, out _))
                {
                    return;
                }

                if (_lastVoxelContactNormals.TryGetValue(grid.EntityId, out Vector3D subNormal) && subNormal.LengthSquared() > 0.001)
                {
                    if (!_lastVoxelContactNormals.TryGetValue(topGrid.EntityId, out Vector3D topNormal) || topNormal.LengthSquared() <= 0.001)
                    {
                        _lastVoxelContactNormals[topGrid.EntityId] = subNormal;
                    }
                }
                if (_lastVoxelPenetrations.TryGetValue(grid.EntityId, out float subPen))
                {
                    _lastVoxelPenetrations[topGrid.EntityId] = subPen;
                }
                if (_lastImpactPositions.TryGetValue(grid.EntityId, out Vector3D subImpact))
                {
                    _lastImpactPositions[topGrid.EntityId] = subImpact;
                }
                if (_consecutiveVoxelContactFrames.TryGetValue(grid.EntityId, out int subCnt))
                {
                    _consecutiveVoxelContactFrames[topGrid.EntityId] = Math.Max(_consecutiveVoxelContactFrames.TryGetValue(topGrid.EntityId, out int tc) ? tc : 0, subCnt);
                }
                if (_voxelContactStartPositions.TryGetValue(grid.EntityId, out Vector3D subStart))
                {
                    if (!_voxelContactStartPositions.ContainsKey(topGrid.EntityId))
                    {
                        _voxelContactStartPositions[topGrid.EntityId] = subStart;
                    }
                }
                TryPushApart(topGrid, otherEntity);
                return;
            }

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            // Cooldown: do not push the same construct more frequently than its contact threshold frames to allow Havok to settle
            int cooldownFrames = Math.Max(10, config.PushApartThreshold);
            if (_lastEscapes.TryGetValue(grid.EntityId, out EscapeRecord recentEsc) && currentFrame < recentEsc.Frame + (ulong)cooldownFrames)
            {
                return;
            }

            Vector3D separationDir;

            if (otherEntity is MyCubeGrid otherGrid)
            {
                MyCubeGrid otherTopGrid = GridUtils.GetMainGrid(otherGrid) ?? otherGrid;
                // If otherGrid belongs to the same mechanical construct, skip (anti-clang handles subgrids)
                if (otherTopGrid == grid) return;

                // Push along vector separating bounding centers; if co-located, fallback to recorded contact normal or up
                Vector3D centerDiff = grid.PositionComp.WorldVolume.Center - otherTopGrid.PositionComp.WorldVolume.Center;
                if (centerDiff.LengthSquared() > 0.001)
                {
                    separationDir = Vector3D.Normalize(centerDiff);
                }
                else if (_lastVoxelContactNormals.TryGetValue(grid.EntityId, out Vector3D normal) && normal.LengthSquared() > 0.001)
                {
                    separationDir = Vector3D.Normalize(normal);
                }
                else
                {
                    separationDir = Vector3D.Up;
                }

                // Prevent pushing lower grids downward into terrain:
                // If this grid is the lower body (separationDir points downward against gravity) and voxels are nearby,
                // deflect the push horizontally so the bottom grid slides sideways instead of getting shoved underground.
                if (grid.Physics != null && grid.Physics.Gravity.LengthSquared() > 0.1f)
                {
                    Vector3D up = -Vector3D.Normalize(grid.Physics.Gravity);
                    double downDot = Vector3D.Dot(separationDir, up);
                    if (downDot < -0.1)
                    {
                        // Project separation vector onto horizontal plane (remove downward component)
                        Vector3D horizontal = separationDir - (up * downDot);
                        if (horizontal.LengthSquared() > 0.01)
                        {
                            separationDir = Vector3D.Normalize(horizontal);
                        }
                        else
                        {
                            // Exactly vertical stack - do not push lower grid down; let top grid take the upward push
                            return;
                        }
                    }
                }
            }
            else if (otherEntity is MyVoxelBase contactVoxel)
            {
                long trackingId = grid.EntityId;
                ulong resetWindow = (ulong)Math.Max(10, config.PushApartThreshold * 2);
                bool hasPriorEscape = _lastEscapes.TryGetValue(grid.EntityId, out EscapeRecord esc) && currentFrame <= esc.Frame + resetWindow;

                if (!hasPriorEscape)
                {
                    _pushApartAttempts.TryRemove(trackingId, out _);
                }

                int attempts = _pushApartAttempts.TryGetValue(trackingId, out int a) ? a : 0;
                if (config.PushApartMaxAttempts > 0 && attempts >= config.PushApartMaxAttempts)
                {
                    // Escape-attempt cap: repeated failed pushes mean the direction is wrong (cliff face)
                    if (config.ConvertToStaticOnPushApartGiveUp)
                    {
                        if (config.LogPushApartStationConversion || ShouldLog(_lastPushApartGiveUpLogFrames, trackingId, currentFrame, 300))
                        {
                            Log.Warn(LogSource, $"[PUSH-APART GIVE-UP] '{grid.DisplayName}' ({trackingId}) reached attempt limit ({attempts}/{config.PushApartMaxAttempts}); enqueuing convert-to-station to save server sim.");
                        }
                        _pushQueue.Enqueue(new PushApartAction
                        {
                            GridId = trackingId,
                            ConvertToStatic = true
                        });
                    }
                    else
                    {
                        if (ShouldLog(_lastPushApartGiveUpLogFrames, trackingId, currentFrame, 300))
                        {
                            Log.Warn(LogSource, $"[PUSH-APART GIVE-UP] '{grid.DisplayName}' ({trackingId}) reached attempt limit ({attempts}/{config.PushApartMaxAttempts}); give-up attempt limit reached (stationing disabled).");
                        }
                    }
                    return;
                }

                // Heightmap submersion and macroscopic escape direction
                Vector3D gridCenter = grid.PositionComp.WorldVolume.Center;
                MyPlanet planet = contactVoxel as MyPlanet ?? MyGamePruningStructure.GetClosestPlanet(gridCenter);

                if (!TryResolveVoxelEscapeDirection(grid, config, contactVoxel, out Vector3D resolvedDir, out Vector3D surfaceCenter))
                {
                    if (hasPriorEscape && esc.Direction.LengthSquared() > 0.001)
                    {
                        resolvedDir = esc.Direction;
                    }
                    else
                    {
                        if (ShouldLog(_lastPushApartGiveUpLogFrames, grid.EntityId, currentFrame, 1200))
                        {
                            Log.Warn(LogSource, $"[PUSH-APART] No escape direction found for '{grid.DisplayName}' (all candidates blocked by voxels); retrying on later contacts.");
                        }
                        return;
                    }
                }

                bool isMacroSubmerged = false;
                double altDiff = 0.0;
                if (planet != null)
                {
                    Vector3D core = planet.PositionComp.WorldVolume.Center;
                    altDiff = (gridCenter - core).Length() - (surfaceCenter - core).Length();
                    isMacroSubmerged = altDiff < 0;
                }

                _lastVoxelPenetrations.TryGetValue(trackingId, out float contactDist);
                float recordedPenetration = contactDist < -0.001f ? -contactDist : 0f;
                bool isMeshPenetrated = config.PushApartEmbeddedDepth <= 0f
                    ? recordedPenetration > 0.001f
                    : recordedPenetration >= config.PushApartEmbeddedDepth;

                float pushSpeed = grid.Physics?.LinearVelocity.Length() ?? 0f;

                int clangTotal = GetConstructClangTotal(trackingId);
                int clangRate = GetConstructClangRate(trackingId, currentFrame);
                bool isClanging = config.PushApartClangRateThreshold > 0 && clangRate >= config.PushApartClangRateThreshold;
                string clangMin = config.PushApartClangRateThreshold > 0 ? string.Format(CultureInfo.InvariantCulture, "{0}/s", config.PushApartClangRateThreshold) : "Off";

                // Peaceful resting guard:
                // If the grid is not submerged underground, not clanging, and has not penetrated the mesh beyond threshold,
                // it is peacefully resting on the surface.
                if (!isMeshPenetrated && !isMacroSubmerged && !isClanging)
                {
                    if (config.LogPushApartDiagnostics && ShouldLog(_lastPushApartGateLogFrames, trackingId, currentFrame, 60))
                    {
                        string restingReason = recordedPenetration <= 0.001f ? "Zero Penetration" : "Shallow Penetration";
                        Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                            "[PUSH-APART DIAG] '{0}' ({1}) [SKIPPED - RESTING] | Reason: {2} | Penetration: {3:F3}m (Threshold: {4:F2}m) | Clangs: {5}/s (Min: {6}, Total: {7}) | Speed: {8:F2} m/s",
                            grid.DisplayName, trackingId, restingReason, recordedPenetration, config.PushApartEmbeddedDepth, clangRate, clangMin, clangTotal, pushSpeed));
                    }
                    return;
                }

                // Subterranean multi-subgrid OBB clearance:
                // Evaluates all mechanical group members (chassis, wheels, plows, tools) along -resolvedDir
                // so the lowest attached subgrid clears the terrain surface.
                double clearance = 0.0;
                if (isMacroSubmerged)
                {
                    double maxClearance = 0.0;
                    _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
                    _pushMechanicalGroupBuffer.Clear();
                    try
                    {
                        GridUtils.GetMechanicalGroupMembers(grid, _pushMechanicalGroupBuffer);
                        foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                        {
                            if (member == null || member.MarkedForClose || member.Closed) continue;
                            MatrixD worldMatrix = member.WorldMatrix;
                            MatrixD invWorld = member.PositionComp.WorldMatrixNormalizedInv;
                            Vector3D localDir = Vector3D.TransformNormal(-resolvedDir, invWorld);

                            BoundingBox localBox = member.PositionComp.LocalAABB;
                            Vector3D localSupport = new Vector3D(
                                localDir.X > 0 ? localBox.Max.X : localBox.Min.X,
                                localDir.Y > 0 ? localBox.Max.Y : localBox.Min.Y,
                                localDir.Z > 0 ? localBox.Max.Z : localBox.Min.Z);

                            Vector3D deepestPoint = Vector3D.Transform(localSupport, worldMatrix);
                            double pointClearance = Vector3D.Dot(surfaceCenter - deepestPoint, resolvedDir);
                            if (pointClearance > maxClearance) maxClearance = pointClearance;
                        }
                    }
                    finally
                    {
                        _pushMechanicalGroupBuffer.Clear();
                    }
                    clearance = maxClearance + config.PushApartDistance;
                }

                double maxNudge = config.PushApartMaxNudgeDistance > 0f ? config.PushApartMaxNudgeDistance : double.MaxValue;
                double distance;
                if (hasPriorEscape)
                {
                    esc.Level = Math.Min(esc.Level + 1, 1000);
                    esc.Frame = currentFrame;
                    // Further attempts: continue nudging with escalated distance
                    double escalated = config.PushApartDistance * (esc.Level + 1);
                    double surfaceDist = maxNudge < double.MaxValue ? Math.Min(escalated, maxNudge) : escalated;
                    distance = isMacroSubmerged ? Math.Max(clearance, surfaceDist) : surfaceDist;

                    // Rolling blend: 50% prior escape vector + 50% latest resolved vector
                    Vector3D blended = esc.Direction + resolvedDir;
                    separationDir = blended.LengthSquared() > 0.01 ? Vector3D.Normalize(blended) : resolvedDir;
                    esc.Direction = separationDir;
                    _lastEscapes[grid.EntityId] = esc;
                }
                else
                {
                    // First shot: clearance for subterranean grids bypasses maxNudge cap; surface grids use PushApartDistance capped by maxNudge
                    double targetDist = isMacroSubmerged ? Math.Max((double)config.PushApartDistance, clearance) : config.PushApartDistance;
                    distance = isMacroSubmerged ? targetDist : (maxNudge < double.MaxValue ? Math.Min(targetDist, maxNudge) : targetDist);

                    separationDir = resolvedDir;
                    _lastEscapes[grid.EntityId] = new EscapeRecord { Direction = separationDir, Frame = currentFrame, Level = 0 };
                }

                _pushApartAttempts[trackingId] = attempts + 1;
                if (config.LogPushApartDiagnostics)
                {
                    string underSurfaceInfo = planet != null
                        ? string.Format(CultureInfo.InvariantCulture, " | UnderSurface: {0} (AltDiff: {1:F2}m)", isMacroSubmerged, altDiff)
                        : "";
                    string lockInfo = hasPriorEscape ? string.Format(CultureInfo.InvariantCulture, "Blended (Lvl {0})", esc.Level) : "Attempt 1";

                    int contacts = _consecutiveVoxelContactFrames.TryGetValue(trackingId, out int vcnt) ? vcnt : (_consecutiveContactFrames.TryGetValue(trackingId, out int cnt) ? cnt : 0);
                    double drift = _voxelContactStartPositions.TryGetValue(trackingId, out Vector3D vsPos)
                        ? (grid.PositionComp.WorldVolume.Center - vsPos).Length()
                        : (_contactStartPositions.TryGetValue(trackingId, out Vector3D sPos) ? (grid.PositionComp.WorldVolume.Center - sPos).Length() : 0.0);

                    double constructRadius = grid.PositionComp.WorldVolume.Radius;
                    double effectiveMaxDrift = Math.Max((double)config.PushApartMaxDrift, constructRadius * 0.10);

                    Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                        "[PUSH-APART DIAG] '{0}' ({1}) [PUSHED] | Attempt: {2}/{3}{4} | Clangs: {5}/s (Min: {6}, Total: {7}) | Contacts: {8}/{9} | Drift: {10:F2}m (Max: {11:F2}m, Radius: {12:F1}m) | Penetration: {13:F3}m (Threshold: {14:F2}m) | OBB Clearance: {15:F2}m | Speed: {16:F2} m/s | Nudge: {17:F2}m (Base: {18:F2}m, Max: {19:F2}m) | Dir: {20:F3} | MacroNormal: {21:F3} | Mode: {22}",
                        grid.DisplayName, trackingId, attempts + 1, config.PushApartMaxAttempts, underSurfaceInfo,
                        clangRate, clangMin, clangTotal, contacts, config.PushApartThreshold, drift, effectiveMaxDrift, constructRadius,
                        recordedPenetration, config.PushApartEmbeddedDepth, clearance, pushSpeed,
                        distance, config.PushApartDistance, config.PushApartMaxNudgeDistance,
                        separationDir, resolvedDir, lockInfo));
                }

                // Reset clang rate tracking so that pre-push violent clanging does not echo into subsequent contacts
                ResetConstructClangTracking(trackingId, currentFrame);

                EnqueuePush(grid, separationDir, true, distance);
                return;
            }
            else
            {
                separationDir = Vector3D.Up;
            }

            EnqueuePush(grid, separationDir, false, config.PushApartDistance);
        }

        private void EnqueuePush(MyCubeGrid grid, Vector3D separationDir, bool voxelPush, double distance)
        {
            PhysicsOptimizerConfig config = _plugin?.Config;
            if (config == null || grid == null || grid.MarkedForClose || grid.Closed) return;

            _pushQueue.Enqueue(new PushApartAction
            {
                GridId = grid.EntityId,
                SeparationDir = separationDir,
                Distance = (float)distance,
                StartPos = grid.PositionComp.WorldVolume.Center,
                VoxelPush = voxelPush
                VoxelPush = voxelPush,
                IsRescue = false,
                IsAdminRescue = false
            });

            _plugin?.DefenseStats?.IncrementGridsSeparated();
            RecordIncident("🚨", $"Push-Apart nudged '{grid.DisplayName}' +{distance:F1}m out of voxels.");

            // Clear cached contact context for the pushed grid so old coordinates cannot poison future frames
            _lastImpactPositions.TryRemove(grid.EntityId, out _);
            _lastVoxelContactNormals.TryRemove(grid.EntityId, out _);
            _lastVoxelPenetrations.TryRemove(grid.EntityId, out _);

            if (config.EnableDebugLogging)
            {
                Log.Info(LogSource, $"[PUSH-APART] Queued push for '{grid.DisplayName}' {distance:F2}m along separation vector.");
            }
        }

        /// <summary>
        /// Executes a one-shot push-apart rescue on a stuck construct, resolving macroscopic terrain normal
        /// and multi-subgrid subterranean OBB support clearance using the exact same solver pipeline as Push-Apart.
        /// </summary>
        public bool ExecuteRescuePush(MyCubeGrid grid, double distance, bool uprightFlipped, bool isAdmin, out string failureReason)
        {
            failureReason = null;
            if (grid == null || grid.MarkedForClose || grid.Closed)
            {
                failureReason = "Target vehicle is invalid or closed.";
                return false;
            }

            MyCubeGrid topGrid = GridUtils.GetMainGrid(grid) ?? grid;
            if (topGrid.MarkedForClose || topGrid.Closed)
            {
                failureReason = "Target vehicle construct is closed.";
                return false;
            }

            if (topGrid.IsStatic)
            {
                failureReason = "Static stations cannot be rescued.";
                return false;
            }

            PhysicsOptimizerConfig config = _plugin?.Config;
            Vector3D gridCenter = topGrid.PositionComp.WorldVolume.Center;
            Vector3D up = topGrid.Physics != null && topGrid.Physics.Gravity.LengthSquared() > 0.1f
                ? -Vector3D.Normalize(topGrid.Physics.Gravity)
                : Vector3D.Up;

            Vector3D separationDir = up;
            double finalDistance = distance > 0 ? distance : (config?.PlayerRescuePushDistance ?? 2.5f);

            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(gridCenter);
            if (planet != null)
            {
                Vector3D planetCore = planet.PositionComp.WorldVolume.Center;
                if (topGrid.Physics == null || topGrid.Physics.Gravity.LengthSquared() <= 0.1f)
                {
                    Vector3D radial = gridCenter - planetCore;
                    if (radial.LengthSquared() > 0.001) up = Vector3D.Normalize(radial);
                }

                double sampleRadius = Math.Max(topGrid.PositionComp.WorldVolume.Radius, 3.0);
                Vector3D terrainNormal = SampleMacroTerrainNormal(planet, gridCenter, up, sampleRadius, out Vector3D surfaceCenter);
                separationDir = terrainNormal;

                double centerDist = (gridCenter - planetCore).Length();
                double surfaceDist = (surfaceCenter - planetCore).Length();
                bool isMacroSubmerged = centerDist < surfaceDist;

                double maxClearance = 0.0;
                _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
                _pushMechanicalGroupBuffer.Clear();
                try
                {
                    GridUtils.GetMechanicalGroupMembers(topGrid, _pushMechanicalGroupBuffer);
                    foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                    {
                        if (member == null || member.MarkedForClose || member.Closed) continue;
                        MatrixD worldMatrix = member.WorldMatrix;
                        MatrixD invWorld = member.PositionComp.WorldMatrixNormalizedInv;
                        Vector3D localDir = Vector3D.TransformNormal(-separationDir, invWorld);

                        BoundingBox localBox = member.PositionComp.LocalAABB;
                        Vector3D localSupport = new Vector3D(
                            localDir.X > 0 ? localBox.Max.X : localBox.Min.X,
                            localDir.Y > 0 ? localBox.Max.Y : localBox.Min.Y,
                            localDir.Z > 0 ? localBox.Max.Z : localBox.Min.Z);

                        Vector3D deepestPoint = Vector3D.Transform(localSupport, worldMatrix);
                        double pointClearance = Vector3D.Dot(surfaceCenter - deepestPoint, separationDir);
                        if (pointClearance > maxClearance) maxClearance = pointClearance;
                    }
                }
                finally
                {
                    _pushMechanicalGroupBuffer.Clear();
                }

                if (maxClearance > 0 || isMacroSubmerged)
                {
                    finalDistance = Math.Max(finalDistance, maxClearance + (config?.PushApartDistance ?? 0.5f));
                }

                if (uprightFlipped && topGrid.Physics != null && topGrid.Physics.Gravity.LengthSquared() > 0.1f)
                {
                    double orientationDot = Vector3D.Dot(topGrid.WorldMatrix.Up, up);
                    if (orientationDot < 0.2)
                    {
                        UprightConstruct(topGrid, up);
                    }
                }
            }
            else
            {
                if (_lastVoxelContactNormals.TryGetValue(topGrid.EntityId, out Vector3D astNormal) && astNormal.LengthSquared() > 0.001)
                {
                    separationDir = Vector3D.Normalize(astNormal);
                }
                else
                {
                    separationDir = topGrid.WorldMatrix.Up;
                }
            }

            // Clear old contact and penetration caches on all mechanical group members so Havok settles cleanly
            _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
            _pushMechanicalGroupBuffer.Clear();
            try
            {
                GridUtils.GetMechanicalGroupMembers(topGrid, _pushMechanicalGroupBuffer);
                foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                {
                    if (member == null) continue;
                    _lastImpactPositions.TryRemove(member.EntityId, out _);
                    _lastVoxelContactNormals.TryRemove(member.EntityId, out _);
                    _lastVoxelPenetrations.TryRemove(member.EntityId, out _);
                    _consecutiveContactFrames[member.EntityId] = 0;
                    _consecutiveVoxelContactFrames.TryRemove(member.EntityId, out _);
                    _pushApartAttempts.TryRemove(member.EntityId, out _);
                    _lastEscapes.TryRemove(member.EntityId, out _);
                }
            }
            finally
            {
                _pushMechanicalGroupBuffer.Clear();
            }

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            ResetConstructClangTracking(topGrid.EntityId, currentFrame);

            _pushQueue.Enqueue(new PushApartAction
            {
                GridId = topGrid.EntityId,
                SeparationDir = separationDir,
                Distance = (float)finalDistance,
                StartPos = topGrid.PositionComp.WorldVolume.Center,
                VoxelPush = true,
                IsRescue = true,
                IsAdminRescue = isAdmin
            });

            return true;
        }

        /// <summary>
        /// Smoothly aligns a flipped rover's Up vector with planetary gravity Up while preserving horizontal forward yaw heading.
        /// Transforms all mechanical subgrids synchronously via relative matrix multiplication.
        /// </summary>
        private static void UprightConstruct(MyCubeGrid mainGrid, Vector3D planetUp)
        {
            if (mainGrid == null || mainGrid.MarkedForClose || mainGrid.Closed) return;
            MatrixD oldMain = mainGrid.WorldMatrix;
            Vector3D forward = oldMain.Forward;
            Vector3D horizontalForward = forward - (planetUp * Vector3D.Dot(forward, planetUp));
            if (horizontalForward.LengthSquared() < 0.01)
            {
                Vector3D right = oldMain.Right;
                Vector3D horizontalRight = right - (planetUp * Vector3D.Dot(right, planetUp));
                if (horizontalRight.LengthSquared() > 0.01)
                {
                    horizontalForward = Vector3D.Cross(planetUp, Vector3D.Normalize(horizontalRight));
                }
                else
                {
                    horizontalForward = Vector3D.CalculatePerpendicularVector(planetUp);
                }
            }
            else
            {
                horizontalForward = Vector3D.Normalize(horizontalForward);
            }

            MatrixD newMain = MatrixD.CreateWorld(oldMain.Translation, horizontalForward, planetUp);
            MatrixD delta = MatrixD.Invert(oldMain) * newMain;

            var groupMembers = new List<MyCubeGrid>();
            try
            {
                GridUtils.GetMechanicalGroupMembers(mainGrid, groupMembers);
                foreach (var member in groupMembers)
                {
                    if (member == null || member.MarkedForClose || member.Closed) continue;
                    MatrixD m = member.WorldMatrix * delta;
                    member.PositionComp.SetWorldMatrix(ref m);
                    if (member.Physics != null)
                    {
                        member.Physics.LinearVelocity = Vector3.Zero;
                        member.Physics.AngularVelocity = Vector3.Zero;
                        member.Physics.RigidBody?.Activate();
                    }
                }
            }
            finally
            {
                groupMembers.Clear();
            }
        }

        /// <summary>
        /// Returns true if the construct is actively clanging or suffering Havok solver chatter,
        /// exempting it from velocity limits during rescue requests.
        /// </summary>
        public static bool IsConstructClangingOrJittering(long constructId, ulong currentFrame)
        {
            if (constructId == 0) return false;
            return GetConstructClangRate(constructId, currentFrame) > 0;
        }

        /// <summary>
        /// Returns true if the grid has recorded recent collision or voxel contacts.
        /// </summary>
        public bool HasRecentContacts(long gridId)
        {
            if (_consecutiveVoxelContactFrames.TryGetValue(gridId, out int vc) && vc > 0) return true;
            if (_consecutiveContactFrames.TryGetValue(gridId, out int gc) && gc > 0) return true;
            return HadRecentVoxelContact(gridId);
        }

        private bool AllowOrScale(MyCubeGrid grid, ref float separatingVelocity, bool isMissile)
        {
            if (grid == null) return true;
            PhysicsOptimizerConfig config = _plugin?.Config;
            DefenseStatistics stats = _plugin?.DefenseStats;
            if (config == null) return true;

            ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;

            if (config.DeformationMultiplier <= 0.0f)
            {
                return BlockDeformation(grid, currentFrame, stats);
            }

            if (config.DeformationMultiplier < 1.0f)
            {
                separatingVelocity *= config.DeformationMultiplier;
            }

            _lastDeformationFrames[grid.EntityId] = currentFrame;

            stats?.IncrementAllowed(isMissile: isMissile);
            if (isMissile)
            {
                RecordIncident("🎯", $"PMW torpedo impact confirmed on '{grid.DisplayName}'.");
            }
            return true;
        }

        private static bool ShouldLog(ConcurrentDictionary<long, ulong> dict, long key, ulong currentFrame, ulong intervalFrames = 60)
        {
            if (currentFrame == 0) return true;
            if (dict.TryGetValue(key, out ulong lastFrame))
            {
                if (currentFrame >= lastFrame && (currentFrame - lastFrame) < intervalFrames)
                {
                    return false;
                }
            }
            dict[key] = currentFrame;
            return true;
        }

        private static void TrimDictionary(ConcurrentDictionary<long, ulong> dict, ulong currentFrame, ulong maxAge, ConcurrentDictionary<long, int> secondaryDict = null)
        {
            foreach (KeyValuePair<long, ulong> kvp in dict)
            {
                if (currentFrame > kvp.Value && (currentFrame - kvp.Value) > maxAge)
                {
                    if (dict.TryRemove(kvp.Key, out _) && secondaryDict != null)
                    {
                        secondaryDict.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }

        /// <summary>
        /// Cold-path periodic cache sweep. Evicts expired missile engagements and frame trackers.
        /// </summary>
        public void SweepCaches(ulong? frame = null)
        {
            ulong currentFrame = frame ?? MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (currentFrame == 0) return;

            if (currentFrame % 600 == 0)
            {
                TrimOldFrames(currentFrame);
            }

            ProcessPendingVoxelArbDebugGps();
            ProcessPushQueue();
        }

        private void ProcessPendingVoxelArbDebugGps()
        {
            if (_pendingVoxelArbDebugGps.IsEmpty) return;

            if (_plugin?.Config?.EnableVoxelNormalArbitratorDebugDraw != true)
            {
                _pendingVoxelArbDebugGps.Clear();
                return;
            }

            foreach (var kvp in _pendingVoxelArbDebugGps)
            {
                if (_pendingVoxelArbDebugGps.TryRemove(kvp.Key, out VoxelArbDebugMarker marker))
                {
                    AddCappedDebugGps(
                        $"VA {marker.GridName} OLD",
                        "voxel-arb debug: pre-invert force dir",
                        marker.OldForcePos,
                        Color.Orange);
                    AddCappedDebugGps(
                        $"VA {marker.GridName} NEW",
                        "voxel-arb debug: post-invert force dir",
                        marker.NewForcePos,
                        Color.Cyan);
                }
            }
        }

        private void ProcessPushQueue()
        {
            PhysicsOptimizerConfig config = _plugin?.Config;
            while (_pushQueue.TryDequeue(out PushApartAction action))
            {
                try
                {
                    if (!MyEntities.TryGetEntityById(action.GridId, out MyEntity entity) || entity is not MyCubeGrid grid)
                    {
                        continue;
                    }

                    if (grid.MarkedForClose || grid.Closed) continue;

                    if (action.ConvertToStatic)
                    {
                        _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
                        _pushMechanicalGroupBuffer.Clear();
                        try
                        {
                            GridUtils.GetMechanicalGroupMembers(grid, _pushMechanicalGroupBuffer);
                            if (config?.LogPushApartStationConversion == true)
                            {
                                Log.Warn(LogSource, $"[PUSH-APART STATION] Executing station conversion for construct '{grid.DisplayName}' ({_pushMechanicalGroupBuffer.Count} members)...");
                            }
                            RecordIncident("⚓", $"Push-apart limit reached: converted '{grid.DisplayName}' to Static Station.");

                            foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                            {
                                if (member == null || member.MarkedForClose || member.Closed) continue;

                                if (member.Physics != null)
                                {
                                    member.Physics.LinearVelocity = Vector3.Zero;
                                    member.Physics.AngularVelocity = Vector3.Zero;
                                    if (member.Physics.RigidBody != null)
                                    {
                                        member.Physics.RigidBody.LinearVelocity = Vector3.Zero;
                                        member.Physics.RigidBody.AngularVelocity = Vector3.Zero;
                                        member.Physics.RigidBody.Deactivate();
                                    }
                                }

                                try
                                {
                                    if (MyMultiplayer.Static != null)
                                    {
                                        MyMultiplayer.RaiseEvent(member, (MyCubeGrid x) => x.ConvertToStatic);
                                    }
                                    else
                                    {
                                        member.ConvertToStatic();
                                    }
                                }
                                catch
                                {
                                    member.ConvertToStatic();
                                }

                                if (!member.IsStatic)
                                {
                                    member.Physics?.ConvertToStatic();
                                }

                                if (config?.LogPushApartStationConversion == true)
                                {
                                    if (member.IsStatic)
                                    {
                                        Log.Info(LogSource, $"[PUSH-APART STATION] Member '{member.DisplayName}' ({member.EntityId}) is now STATIC.");
                                    }
                                    else
                                    {
                                        Log.Warn(LogSource, $"[PUSH-APART STATION] Member '{member.DisplayName}' ({member.EntityId}) FAILED to convert to static!");
                                    }
                                }

                                _consecutiveContactFrames.TryRemove(member.EntityId, out _);
                                _lastContactFrameTracker.TryRemove(member.EntityId, out _);
                                _pushApartAttempts.TryRemove(member.EntityId, out _);
                                _lastEscapes.TryRemove(member.EntityId, out _);
                            }

                            ChatNotificationService.SendPushApartNotification(grid, 0, true, config, MySandboxGame.Static?.SimulationFrameCounter ?? 0);
                        }
                        finally
                        {
                            _pushMechanicalGroupBuffer.Clear();
                        }
                        continue;
                    }

                    if (config?.EnablePushApartDebugDraw == true)
                    {
                        DrawPushDebugGps(action, grid.DisplayName);
                    }

                    // Apply the same translation to the entire mechanical group (wheels are separate
                    // physics bodies - moving only the main grid leaves wheels buried and clanging)
                    _pushMechanicalGroupBuffer ??= new List<MyCubeGrid>();
                    _pushMechanicalGroupBuffer.Clear();
                    try
                    {
                        GridUtils.GetMechanicalGroupMembers(grid, _pushMechanicalGroupBuffer);

                        foreach (MyCubeGrid member in _pushMechanicalGroupBuffer)
                        {
                            if (member == null || member.MarkedForClose || member.Closed) continue;

                            MatrixD matrix = member.WorldMatrix;
                            matrix.Translation += action.SeparationDir * action.Distance;
                            member.PositionComp.SetWorldMatrix(ref matrix);

                            if (member.Physics != null)
                            {
                                // Wake the body first - velocity writes on a deactivated rigid body are lost
                                member.Physics.RigidBody?.Activate();
                                member.Physics.LinearVelocity = Vector3.Zero;
                                member.Physics.AngularVelocity = Vector3.Zero;
                            }

                            _consecutiveContactFrames[member.EntityId] = 0;
                        }

                        if (action.IsRescue)
                        {
                            if (action.IsAdminRescue)
                            {
                                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementAdminRescues();
                                RecordIncident("👑", $"Admin crosshairs-rescued '{grid.DisplayName}' +{action.Distance:F1}m out of voxels.");
                            }
                            else
                            {
                                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementPlayerRescues();
                                RecordIncident("🛟", $"Player self-rescue nudged '{grid.DisplayName}' +{action.Distance:F1}m out of voxels.");
                            }
                        }
                        else
                        {
                            RecordIncident("🚨", $"Push-Apart nudged '{grid.DisplayName}' +{action.Distance:F1}m out of voxels.");
                        }

                        PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementGridsSeparated();
                        PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementPushApartActionsExecuted();
                        ChatNotificationService.SendPushApartNotification(grid, action.Distance, false, config, MySandboxGame.Static?.SimulationFrameCounter ?? 0);
                        ChatNotificationService.SendPushApartNotification(grid, action.Distance, false, config, MySandboxGame.Static?.SimulationFrameCounter ?? 0, action.IsRescue);
                    }
                    finally
                    {
                        _pushMechanicalGroupBuffer.Clear();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, LogSource, "Error while applying queued push-apart action!");
                }
            }
        }

        // --- Capped Debug GPS Draws ---
        // Dispatched strictly on the game simulation thread to prevent collection concurrency issues.

        private const int MaxLiveDebugGpsMarkers = 10;
        private const int DebugGpsTtlSeconds = 10;

        private struct DebugGpsMarker
        {
            public string Name;
            public DateTime ExpiresUtc;
        }

        private static readonly object _debugGpsLock = new object();
        private static readonly List<DebugGpsMarker> _liveDebugGps = new List<DebugGpsMarker>();

        /// <summary>
        /// Capped debug GPS add visible to all players: at most MaxLiveDebugGpsMarkers live markers
        /// globally (10s TTL), and the previous same-name marker is deleted before re-adding so a
        /// repeatedly-inverting grid holds one marker instead of stacking duplicates.
        /// Must be called from the main game simulation thread.
        /// </summary>
        private static void AddCappedDebugGps(string name, string description, Vector3D pos, Color color)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                lock (_debugGpsLock)
                {
                    for (int i = _liveDebugGps.Count - 1; i >= 0; i--)
                    {
                        if (_liveDebugGps[i].ExpiresUtc <= now || _liveDebugGps[i].Name == name)
                            _liveDebugGps.RemoveAt(i);
                    }

                    if (_liveDebugGps.Count >= MaxLiveDebugGpsMarkers) return;
                    _liveDebugGps.Add(new DebugGpsMarker { Name = name, ExpiresUtc = now.AddSeconds(DebugGpsTtlSeconds) });
                }

                RemoveDebugGpsForAll(name);
                MyVisualScriptLogicProvider.AddGPSForAll(name, description, pos, color, DebugGpsTtlSeconds);
            }
            catch (Exception ex)
            {
                Log.Warn(LogSource, $"[Debug GPS] Failed adding GPS marker '{name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Bright GPS marker pair at the push origin/end so admins can watch pushes live in the HUD.
        /// One pair per grid via the shared cap: the previous pair is deleted before re-adding, so a
        /// repeatedly-pushed grid holds exactly two markers instead of flooding the GPS list.
        /// </summary>
        private static void DrawPushDebugGps(PushApartAction action, string gridName)
        {
            Color color = action.VoxelPush ? Color.Magenta : Color.Yellow;
            AddCappedDebugGps($"PD {gridName} START", $"push-apart debug: [{(action.VoxelPush ? "VOXEL" : "GRID")}] push origin", action.StartPos, color);
            AddCappedDebugGps($"PD {gridName} END (5m)", $"push-apart debug: pushed {action.Distance:F1}m along vector", action.StartPos + action.SeparationDir * 5.0, color);
        }

        private static void RemoveDebugGpsForAll(string name)
        {
            try
            {
                ICollection<MyPlayer> players = Sandbox.Game.World.MySession.Static?.Players?.GetOnlinePlayers();
                if (players == null) return;

                foreach (MyPlayer player in players)
                {
                    if (player?.Identity == null) continue;
                    IMyGps gps = Sandbox.Game.World.MySession.Static.Gpss?.GetGpsByName(player.Identity.IdentityId, name);
                    if (gps != null)
                    {
                        Sandbox.Game.World.MySession.Static.Gpss.SendDeleteGpsRequest(player.Identity.IdentityId, gps.Hash);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogSource, $"[Debug GPS] Failed removing previous GPS marker '{name}': {ex.Message}");
            }
        }

        private static void ClearAllDebugGps()
        {
            try
            {
                lock (_debugGpsLock)
                {
                    for (int i = 0; i < _liveDebugGps.Count; i++)
                    {
                        RemoveDebugGpsForAll(_liveDebugGps[i].Name);
                    }
                    _liveDebugGps.Clear();
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogSource, $"[Debug GPS] Error clearing all debug GPS markers: {ex.Message}");
            }
        }


        private void TrimOldFrames(ulong currentFrame)
        {
            try
            {
                foreach (KeyValuePair<long, MissileEngagement> kvp in _activeMissiles)
                {
                    if (currentFrame > kvp.Value.ExpireFrame + 300)
                    {
                        if (_activeMissiles.TryRemove(kvp.Key, out _) && MyEntities.TryGetEntityById(kvp.Key, out MyEntity ent) && ent is MyCubeGrid g)
                        {
                            g.OnClose -= OnTrackedGridClosed;
                            g.OnGridSplit -= OnTrackedGridSplit;
                        }
                    }
                }

                TrimDictionary(_lastContactFrameTracker, currentFrame, 600, _consecutiveContactFrames);
                TrimDictionary(_lastVoxelContactFrameTracker, currentFrame, 600, _consecutiveVoxelContactFrames);
                TrimDictionary(_lastDeformationFrames, currentFrame, 600);
                TrimDictionary(_lastVoxelContactFrames, currentFrame, 1800);
                TrimDictionary(_lastPushApartGiveUpLogFrames, currentFrame, 1200);
                TrimDictionary(_lastPushApartGateLogFrames, currentFrame, 1200);

                // Only refund attempt budget and evict contact caches when grid has had no voxel contact for double the contact threshold
                ulong resetFrames = (ulong)Math.Max(10, (_plugin?.Config?.PushApartThreshold ?? 25) * 2);
                foreach (var trackedId in _pushApartAttempts.Keys)
                {
                    if (!HadVoxelContactWithin(trackedId, resetFrames)) _pushApartAttempts.TryRemove(trackedId, out _);
                }
                foreach (var trackedId in _lastEscapes.Keys)
                {
                    if (!HadVoxelContactWithin(trackedId, resetFrames)) _lastEscapes.TryRemove(trackedId, out _);
                }
                foreach (var trackedId in _contactStartPositions.Keys)
                {
                    if (!HadRecentVoxelContact(trackedId)) _contactStartPositions.TryRemove(trackedId, out _);
                }
                foreach (var trackedId in _voxelContactStartPositions.Keys)
                {
                    if (!HadRecentVoxelContact(trackedId)) _voxelContactStartPositions.TryRemove(trackedId, out _);
                }
                foreach (var trackedId in _lastVoxelContactNormals.Keys)
                {
                    if (!HadVoxelContactWithin(trackedId, resetFrames)) _lastVoxelContactNormals.TryRemove(trackedId, out _);
                }
                foreach (var trackedId in _lastImpactPositions.Keys)
                {
                    if (!HadVoxelContactWithin(trackedId, resetFrames)) _lastImpactPositions.TryRemove(trackedId, out _);
                }
                foreach (var trackedId in _lastVoxelPenetrations.Keys)
                {
                    if (!HadVoxelContactWithin(trackedId, resetFrames)) _lastVoxelPenetrations.TryRemove(trackedId, out _);
                }
                foreach (var trackedId in _macroTerrainCache.Keys)
                {
                    if (!HadVoxelContactWithin(trackedId, resetFrames)) _macroTerrainCache.TryRemove(trackedId, out _);
                }
                TrimDictionary(_lastRammingLogFrames, currentFrame, 600);
                TrimDictionary(_lastVoxelLogFrames, currentFrame, 600);
                TrimDictionary(_lastStationLogFrames, currentFrame, 600);
                TrimDictionary(_lastSubgridLogFrames, currentFrame, 600);
                TrimDictionary(_lastExtremeSpeedLogFrames, currentFrame, 600);
                TrimDictionary(_lastVoxelArbitratorLogFrames, currentFrame, 600);

                // Decay or evict construct clang records
                foreach (var kvp in _constructClangRecords)
                {
                    ConstructClangRecord rec = kvp.Value;
                    if (rec == null) continue;
                    if (currentFrame > 0 && rec.LastClangFrame > 0)
                    {
                        if (currentFrame > rec.LastClangFrame + 600)
                        {
                            _constructClangRecords.TryRemove(kvp.Key, out _);
                        }
                        else if (currentFrame > rec.LastClangFrame + 60)
                        {
                            rec.CurrentSecondClangs = 0;
                            rec.PreviousSecondClangs = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, LogSource, "Error while trimming expired frame tracking collections.");
            }
        }

        // =========================================================================
        // DISSOLVED PATCHES & ENGINE HOOKS
        // =========================================================================

        public static void RegisterPatches(PatchContext ctx)
        {
            // 1. MyGridPhysics: PerformDeformation & ContactPointCallback
            try
            {
                MethodInfo targetMethod = typeof(MyGridPhysics).GetMethod("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (targetMethod != null)
                {
                    MethodInfo prefixMethod = typeof(GridDefender).GetMethod(nameof(Prefix_PerformDeformation), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(targetMethod).Prefixes.Add(prefixMethod);
                    PatchConflictAudit.RegisterTarget(targetMethod);
                    Log.Info(LogSource, "Registered MyGridPhysics.PerformDeformation hook.");
                }

                MethodInfo contactMethod = typeof(MyGridPhysics).GetMethod("RigidBody_ContactPointCallbackImpl", BindingFlags.Instance | BindingFlags.NonPublic);
                if (contactMethod != null)
                {
                    MethodInfo contactPrefix = typeof(GridDefender).GetMethod(nameof(Prefix_RigidBody_ContactPointCallbackImpl), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(contactMethod).Prefixes.Add(contactPrefix);
                    PatchConflictAudit.RegisterTarget(contactMethod);
                    Log.Info(LogSource, "Registered MyGridPhysics.RigidBody_ContactPointCallbackImpl hook.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyGridPhysics methods!");
            }

            // 2. MyExplosion: Voxel Cutouts
            try
            {
                Type explosionType = typeof(MyExplosions).Assembly.GetType("Sandbox.Game.MyExplosion");
                if (explosionType != null)
                {
                    MethodInfo applyVoxelMethod = explosionType.GetMethod("ApplyExplosionOnVoxel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (applyVoxelMethod != null)
                    {
                        MethodInfo prefixApply = typeof(GridDefender).GetMethod(nameof(PrefixApplyExplosionOnVoxel), BindingFlags.Static | BindingFlags.NonPublic);
                        ctx.GetPattern(applyVoxelMethod).Prefixes.Add(prefixApply);
                        PatchConflictAudit.RegisterTarget(applyVoxelMethod);
                    }

                    MethodInfo cutOutMethod = explosionType.GetMethod("CutOutVoxelMap", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cutOutMethod != null)
                    {
                        MethodInfo prefixCutOut = typeof(GridDefender).GetMethod(nameof(PrefixCutOutVoxelMap), BindingFlags.Static | BindingFlags.NonPublic);
                        ctx.GetPattern(cutOutMethod).Prefixes.Add(prefixCutOut);
                        PatchConflictAudit.RegisterTarget(cutOutMethod);
                    }
                    Log.Info(LogSource, "Registered MyExplosion voxel cutout hooks.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyExplosion voxel cutouts!");
            }

            // 3. Layered Armor Occlusion via MyDamageSystem
            try
            {
                MethodInfo initMethod = typeof(MyDamageSystem).GetMethod("LoadData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? typeof(MyDamageSystem).GetMethod("Init", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (initMethod != null)
                {
                    MethodInfo initPostfix = typeof(GridDefender).GetMethod(nameof(DamageSystemInitPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ctx.GetPattern(initMethod).Suffixes.Add(initPostfix);
                    PatchConflictAudit.RegisterTarget(initMethod);
                    Log.Info(LogSource, "Registered MyDamageSystem hook for Layered Armor Occlusion.");
                }
                else if (MyDamageSystem.Static != null)
                {
                    FieldInfo beforeField = typeof(MyDamageSystem).GetField("m_beforeDamageHandlers", BindingFlags.Instance | BindingFlags.NonPublic);
                    var handlers = beforeField?.GetValue(MyDamageSystem.Static) as List<Tuple<int, BeforeDamageApplied>>;
                    if (handlers == null || !handlers.Exists(t => t.Item2 == OnBeforeDamageApplied))
                    {
                        MyDamageSystem.Static.RegisterBeforeDamageHandler(100, OnBeforeDamageApplied);
                        _damageHandlerRegistered = true;
                        Log.Info(LogSource, "Registered Layered Armor Occlusion damage handler directly.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Failed to patch MyDamageSystem for Layered Armor Occlusion!");
            }
        }

        // --- MyGridPhysics Patches ---
        //
        // NOTE: Keen Software House engine quirk & internal preconditions for PerformDeformation:
        // PerformDeformation is NOT called on every collision. It is exclusively invoked by
        // MyGridPhysics.BreakAtPoint (Havok BreakPartsHandler callback) under the following strict conditions:
        // 1. Havok Break-Off Threshold: Contact force must exceed the rigid body / shape break-off limit.
        // 2. Safezones: MySessionComponentSafeZones.IsActionAllowedFullyInside must allow damage.
        // 3. Collision Momentum Threshold: (separatingVelocity * Math.Min(massA, massB)) > 21,000.
        //    - If momentum <= 21,000, BreakAtPoint returns false immediately and NEVER invokes PerformDeformation.
        //    - Example: A 10,000 kg grid must have separating velocity > 2.1 m/s to qualify. Low-speed wedged or
        //      vibrating grids (< 2.1 m/s) never reach this threshold.
        // 4. AABB Containment: Contact point must lie within the grid's WorldAABB inflated by 0.1m.
        // Therefore, systems requiring per-frame continuous contact detection (anti-clang oscillation arrest,
        // stuck-grid push-apart, resting detection) MUST NOT rely on PerformDeformation; they must be driven
        // from RigidBody_ContactPointCallbackImpl. High-energy kinetic impact features can use this 21,000 gate.

        public static bool Prefix_PerformDeformation(MyGridPhysics __instance, MyEntity otherEntity, ref float separatingVelocity)
        {
            PhysicsOptimizerPlugin plugin = PhysicsOptimizerPlugin.Instance;
            if (plugin?.GridDefender == null) return true;

            return plugin.GridDefender.ShouldAllowDeformation(__instance, otherEntity, ref separatingVelocity);
        }

        public static bool Prefix_RigidBody_ContactPointCallbackImpl(MyGridPhysics __instance, ref HkContactPointEvent value)
        {
            try
            {
                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementContactCallbacks();

                PhysicsOptimizerConfig config = PhysicsOptimizerPlugin.Instance?.Config;

                // Contact context feeds Layered Armor Occlusion and Push-Apart only; skip the per-contact
                // dictionary write when both are disabled.
                if (config != null && config.Enabled && config.EnableGridDefender &&
                    (config.EnableLayeredArmorOcclusion || config.EnablePushApart) &&
                    __instance.Entity is MyCubeGrid gridLocal)
                {
                    UpdateCollisionContext(gridLocal.EntityId, __instance.ClusterToWorld(value.ContactPoint.Position));
                }

                bool arbitratorEnabled = config != null && config.EnableVoxelNormalArbitrator;
                if (config == null)
                    return true;

                if (__instance.Entity is not MyCubeGrid grid || grid.MarkedForClose || grid.Closed)
                    return true;

                MyPhysicsBody rb = value.GetPhysicsBody(0);
                MyPhysicsBody otherRb = value.GetPhysicsBody(1);
                if (rb == null || otherRb == null) return true;

                bool isVoxel = false;
                IMyEntity otherEnt = otherRb.Entity;
                if (otherEnt is MyVoxelBase)
                {
                    isVoxel = true;
                }
                else
                {
                    IMyEntity selfEnt = rb.Entity;
                    if (selfEnt is MyVoxelBase)
                    {
                        isVoxel = true;
                    }
                }

                if (isVoxel)
                {
                    // Wheel subgrids: stamp both wheel and base grid so sleep guards can
                    // still rescue wheel-only wedges, while anti-clang/push-apart skip driving false positives.
                    ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                    if (currentFrame > 0)
                    {
                        _lastVoxelContactFrames[grid.EntityId] = currentFrame;
                        if (IsWheelSubgrid(grid, out long baseGridId) && baseGridId > 0L)
                        {
                            _lastVoxelContactFrames[baseGridId] = currentFrame;
                        }
                    }

                    // Havok contact normal points from Body B (index 1) to Body A (index 0).
                    // Separating force on Body A is along +Normal; separating force on Body B is along -Normal.
                    bool gridIsBodyA = rb.Entity == grid;
                    Vector3 gridForceDir = gridIsBodyA ? value.ContactPoint.Normal : -value.ContactPoint.Normal;
                    Vector3 rawForceDir = gridForceDir;

                    // Read Havok's true signed distance directly from NormalAndDistance.W (byte 28).
                    // NOTE: HkContactPoint.Distance property in HavokWrapper.dll has a layout bug with [FieldOffset(20)]
                    // which aliases Normal.Y instead of Distance. Reading NormalAndDistance.W gets the true Havok distance.
                    float havokDist = value.ContactPoint.NormalAndDistance.W;

                    bool isWheel = IsWheelSubgrid(grid, out _);

                    // Push-apart escape direction source: record the true force direction and impact position
                    // Suspension wheel subgrids are excluded so tire ground-reaction forces never overwrite chassis normals
                    if (!isWheel)
                    {
                        Vector3D contactWorldPos = __instance.ClusterToWorld(value.ContactPoint.Position);
                        _lastVoxelContactNormals[grid.EntityId] = gridForceDir;
                        _lastImpactPositions[grid.EntityId] = contactWorldPos;
                        _lastVoxelPenetrations[grid.EntityId] = havokDist;
                    }

                    // Voxel Normal Arbitrator: inspects all colliding grids (including wheels) in gravity to prevent "Voxel-Vice"
                    // terrain trapping. Reverses downward/inward collision forces on the grid and redirects Havok's native
                    // contact normal outward along the macroscopic voxel surface face normal.
                    // For non-wheel grids, friction is zeroed to prevent chassis snagging; wheel subgrids preserve full tire friction
                    // so driveability, steering, and traction are maintained without artificial velocity kicks.
                    if (arbitratorEnabled)
                    {
                        Vector3 gravity = __instance.Gravity;
                        if (gravity.LengthSquared() > 0.01f)
                        {
                            Vector3 upVector = -Vector3.Normalize((Vector3)gravity);
                            Vector3D contactPos = __instance.ClusterToWorld(value.ContactPoint.Position);
                            MyPlanet planet = (otherEnt as MyPlanet) ?? (rb.Entity as MyPlanet) ?? MyGamePruningStructure.GetClosestPlanet(contactPos);

                            Vector3 newGridForceDir;
                            Vector3D surfaceNormal;
                            double centerAltDiff = 0.0;
                            bool isSubmerged = false;

                            if (planet != null)
                            {
                                // Per-frame quadtree terrain sampling cache: computes macroscopic surface normal and
                                // submersion status at most once per frame per colliding grid, eliminating repeated
                                // heightmap lookups across multiple contact points.
                                if (!_macroTerrainCache.TryGetValue(grid.EntityId, out MacroTerrainCacheEntry terrainCache) || terrainCache.Frame != currentFrame)
                                {
                                    Vector3D gridCenter = grid.PositionComp.WorldVolume.Center;
                                    double sampleRadius = Math.Max(grid.PositionComp.WorldVolume.Radius, 3.0);
                                    surfaceNormal = SampleMacroTerrainNormal(planet, gridCenter, (Vector3D)upVector, sampleRadius, out Vector3D surfaceCenter);
                                    Vector3D planetCore = planet.PositionComp.WorldVolume.Center;
                                    double centerDist = (gridCenter - planetCore).Length();
                                    double surfaceDist = (surfaceCenter - planetCore).Length();
                                    centerAltDiff = centerDist - surfaceDist;
                                    isSubmerged = centerAltDiff < -0.3; // Grid center is submerged > 30cm below surface

                                    terrainCache = new MacroTerrainCacheEntry
                                    {
                                        Frame = currentFrame,
                                        SurfaceNormal = surfaceNormal,
                                        SurfaceCenter = surfaceCenter,
                                        CenterAltDiff = centerAltDiff,
                                        IsSubmerged = isSubmerged
                                    };
                                    _macroTerrainCache[grid.EntityId] = terrainCache;
                                }
                                else
                                {
                                    surfaceNormal = terrainCache.SurfaceNormal;
                                    centerAltDiff = terrainCache.CenterAltDiff;
                                    isSubmerged = terrainCache.IsSubmerged;
                                }

                                newGridForceDir = (Vector3)surfaceNormal;
                            }
                            else
                            {
                                surfaceNormal = (Vector3D)upVector;
                                newGridForceDir = -gridForceDir;
                            }

                            float upDot = Vector3.Dot(gridForceDir, upVector);
                            float surfaceDot = Vector3.Dot(gridForceDir, (Vector3)surfaceNormal);

                            // Trigger conditions:
                            // 1. Contact is in active contact / penetration (havokDist < 0.05m).
                            // 2. Separating force points inward into the terrain face (surfaceDot < -0.1f) OR downward into the planet (upDot < -0.2f).
                            // 3. Submerged trapping: If the grid is submerged in a steep hill/planet, any contact that does not actively push outward
                            //    towards open air (surfaceDot < 0.2f) is clamping/wedging the grid into the terrain and must be redirected.
                            bool shouldArbitrate = havokDist < 0.05f && (surfaceDot < -0.1f || upDot < -0.2f || (isSubmerged && surfaceDot < 0.2f));

                            if (shouldArbitrate)
                            {
                                // Contact positions arrive in Havok cluster space (origin-offset world); vanilla converts
                                // every consumer via ClusterToWorld - raw values are shifted by the cluster world anchor.
                                // Same two-tier rule as push-apart: in pristine voxel regions a gravity-downward normal can
                                // only be a Keen compression artifact pointing into the planet core, so invert it directly.
                                // Pristine voxel regions cannot have overhangs/cave ceilings on heightmap planets.
                                // Only carved/drilled regions require the confirmation raycast.
                                MyVoxelBase voxelEnt = otherEnt as MyVoxelBase ?? rb.Entity as MyVoxelBase;
                                bool hitAir = voxelEnt == null || !IsVoxelRegionModified(voxelEnt, contactPos);
                                if (!hitAir)
                                {
                                    hitAir = true; // Assume air above unless raycast hits solid voxel
                                    Vector3D rayStart = contactPos;
                                    Vector3D rayEnd = rayStart + (Vector3D)upVector * 1.5f;

                                    _voxelHitsCache ??= [];
                                    _voxelHitsCache.Clear();
                                    MyPhysics.CastRay(rayStart, rayEnd, _voxelHitsCache, MyPhysics.CollisionLayers.VoxelCollisionLayer);
                                    PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelArbitratorRaycasts();
                                    for (int i = 0; i < _voxelHitsCache.Count; i++)
                                    {
                                        IMyEntity hitEnt = _voxelHitsCache[i].HkHitInfo.GetHitEntity();
                                        if (hitEnt is MyVoxelBase)
                                        {
                                            hitAir = false;
                                            break;
                                        }
                                    }
                                    _voxelHitsCache.Clear();
                                }

                                if (hitAir)
                                {
                                    Vector3 oldGridForceDir = gridForceDir;

                                    // Mutate Havok contact normal directly without calling cp.Flip().
                                    // Calling Flip() inverts both Normal and signed Distance in native hkContactPoint;
                                    // when Distance < 0 (penetration), Flip() makes Distance > 0, which tricks Havok's solver
                                    // into treating the bodies as separated and blinds Push-Apart into thinking penetration is 0m.
                                    // To make separating force on grid equal +newGridForceDir:
                                    // Body A separating force is +Normal -> cp.Normal = newGridForceDir.
                                    // Body B separating force is -Normal -> cp.Normal = -newGridForceDir.
                                    var cp = value.ContactPoint;
                                    cp.Normal = gridIsBodyA ? newGridForceDir : -newGridForceDir;

                                    gridForceDir = newGridForceDir;
                                    _lastVoxelContactNormals[grid.EntityId] = newGridForceDir;

                                    // Calculate local contact penetration (havokDist < 0 means penetrating into voxel geometry)
                                    float pen = havokDist < 0f ? -havokDist : 0f;
                                    float localBias = 0f;

                                    if (isWheel)
                                    {
                                        // Wheel subgrids: deadzone for normal tire compression (< 5cm); rescue only severe wedges
                                        if (pen > 0.05f)
                                        {
                                            localBias = Math.Min(0.5f, (pen - 0.05f) * 10f);
                                        }
                                    }
                                    else
                                    {
                                        // Chassis hull: proportional Baumgarte contact bias to resolve resting penetration chatter
                                        if (pen > 0.005f)
                                        {
                                            localBias = Math.Min(0.8f, pen * 10f);
                                        }
                                    }

                                    // Reflect inward velocity in Havok's active solver buffer and apply local contact bias
                                    int bodyIndex = gridIsBodyA ? 0 : 1;
                                    value.AccessVelocities(bodyIndex);
                                    HkRigidBody rigidBody = __instance.RigidBody;
                                    if (rigidBody != null)
                                    {
                                        float velNormal = Vector3.Dot((Vector3)rigidBody.LinearVelocity, newGridForceDir);
                                        if (velNormal < 0f)
                                        {
                                            // velNormal < 0 means the body is penetrating inward against the outward surface normal.
                                            // Reflecting the normal component: -(1 + e) * velNormal cancels inward velocity and adds an outward bounce.
                                            float restitution = isSubmerged ? 0.5f : 0.2f;
                                            rigidBody.LinearVelocity -= newGridForceDir * ((1f + restitution) * velNormal);
                                            velNormal = Vector3.Dot((Vector3)rigidBody.LinearVelocity, newGridForceDir);
                                        }

                                        // Local contact bias: enforce small separation velocity proportional to contact penetration
                                        if (localBias > 0f && velNormal < localBias)
                                        {
                                            rigidBody.LinearVelocity += newGridForceDir * (localBias - velNormal);
                                        }
                                    }
                                    value.UpdateVelocities(bodyIndex);

                                    if (isWheel && isSubmerged && IsWheelSubgrid(grid, out long baseGridId) && baseGridId > 0L)
                                    {
                                        if (MyEntities.TryGetEntityById(baseGridId, out MyEntity baseEnt) && baseEnt is MyCubeGrid baseGrid && baseGrid.Physics?.RigidBody != null)
                                        {
                                            HkRigidBody baseRb = baseGrid.Physics.RigidBody;
                                            float baseVelNormal = Vector3.Dot((Vector3)baseRb.LinearVelocity, newGridForceDir);
                                            if (baseVelNormal < 0f)
                                            {
                                                float restitution = 0.5f;
                                                baseRb.LinearVelocity -= newGridForceDir * ((1f + restitution) * baseVelNormal);
                                            }
                                        }
                                    }

                                    // Only zero friction on non-wheel grids (chassis/armor) to prevent friction-snagging against voxels.
                                    // Wheel subgrids strictly retain tire friction so driving, steering, and traction are preserved.
                                    if (!isWheel)
                                    {
                                        var props = value.ContactProperties;
                                        props.Friction = 0f;
                                    }

                                    PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelNormalsInverted();

                                    if ((config.LogVoxelNormals || config.EnableDebugLogging) && ShouldLog(_lastVoxelArbitratorLogFrames, grid.EntityId, currentFrame, 30))
                                    {
                                        float slopeDot = Vector3.Dot(newGridForceDir, upVector);
                                        string subStr = isSubmerged ? " (Submerged)" : "";
                                        Log.Info(LogSource, string.Format(CultureInfo.InvariantCulture,
                                            "[Voxel Arbitrator] '{0}' [{1}] | Depth: {2:F2}m{3} | Pen: {4:F3}m | HavokDot: {5:F2} (SurfDot: {6:F2}) | SurfaceNormal: {7:F2}, {8:F2}, {9:F2} (SlopeDot: {10:F2})",
                                            grid.DisplayName, (isWheel ? "WHEEL" : "HULL"), centerAltDiff, subStr, pen, upDot, surfaceDot, newGridForceDir.X, newGridForceDir.Y, newGridForceDir.Z, slopeDot));
                                    }

                                    if (config.EnableVoxelNormalArbitratorDebugDraw && grid.PositionComp != null)
                                    {
                                        Vector3D startPos = contactPos;
                                        _pendingVoxelArbDebugGps[grid.EntityId] = new VoxelArbDebugMarker
                                        {
                                            GridName = grid.DisplayName,
                                            OldForcePos = startPos + (Vector3D)oldGridForceDir * 0.5,
                                            NewForcePos = startPos + (Vector3D)newGridForceDir * 5.0
                                        };
                                    }
                                }
                            }
                        }
                    }

                    if (currentFrame > 0 && config.EnablePushApart && !grid.IsStatic)
                    {
                        MyVoxelBase voxel = (otherRb.Entity as MyVoxelBase) ?? (rb.Entity as MyVoxelBase);
                        if (voxel != null)
                        {
                            // Pass raw un-inverted contact normal and real Havok distance so push-apart recognizes embedding accurately
                            PhysicsOptimizerPlugin.Instance?.GridDefender?.RecordVoxelContactFrame(grid, voxel, config, havokDist, rawForceDir);
                        }
                    }
                }
                else if (otherEnt is MyCubeGrid otherGrid && !otherGrid.IsStatic && !grid.IsStatic)
                {
                    ulong currentFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                    if (currentFrame > 0 && config.EnablePushApart)
                    {
                        PhysicsOptimizerPlugin.Instance?.GridDefender?.RecordGridContactFrame(grid, otherGrid, config);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogSource, "Unexpected error in Prefix_RigidBody_ContactPointCallbackImpl");
            }

            return true;
        }

        // --- MyExplosion Patches ---

        private static bool PrefixApplyExplosionOnVoxel()
        {
            PhysicsOptimizerConfig config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config != null && config.Enabled && config.SuppressAllVoxelExplosionDamage)
            {
                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelCutoutsPrevented();
                return false;
            }
            return true;
        }

        private static bool PrefixCutOutVoxelMap()
        {
            PhysicsOptimizerConfig config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config != null && config.Enabled && config.SuppressAllVoxelExplosionDamage)
            {
                PhysicsOptimizerPlugin.Instance?.DefenseStats?.IncrementVoxelCutoutsPrevented();
                return false;
            }
            return true;
        }

        // --- Layered Armor Occlusion Patches ---

        public static void DamageSystemInitPostfix(MyDamageSystem __instance)
        {
            if (__instance != null)
            {
                FieldInfo beforeField = typeof(MyDamageSystem).GetField("m_beforeDamageHandlers", BindingFlags.Instance | BindingFlags.NonPublic);
                var handlers = beforeField?.GetValue(__instance) as List<Tuple<int, BeforeDamageApplied>>;
                if (handlers != null && handlers.Exists(t => t.Item2 == OnBeforeDamageApplied))
                {
                    return;
                }

                __instance.RegisterBeforeDamageHandler(100, OnBeforeDamageApplied);
                _damageHandlerRegistered = true;
                Log.Info(LogSource, "Registered Layered Armor Occlusion damage handler via Postfix.");
            }
        }

        public static void OnBeforeDamageApplied(object target, ref MyDamageInformation info)
        {
            if (info.Amount <= 0f || info.Type != MyDamageType.Deformation) return;

            PhysicsOptimizerConfig config = PhysicsOptimizerPlugin.Instance?.Config;
            if (config == null || !config.EnableLayeredArmorOcclusion) return;

            if (target is MySlimBlock slimBlock && slimBlock.CubeGrid != null)
            {
                MyCubeGrid grid = slimBlock.CubeGrid;

                // Note: Occlusion relies on the most recent contact point recorded in _lastImpactPositions.
                // In simultaneous multi-point impacts from opposing directions, the single recorded position
                // may attribute occlusion to the latest contact point. This design is optimized for rover
                // collisions and frontal impacts without per-contact memory allocations.
                if (!_lastImpactPositions.TryGetValue(grid.EntityId, out Vector3D globalHitPos))
                {
                    return; // Can't determine direction without a collision point
                }

                Vector3D localHitPos = Vector3D.Transform(globalHitPos, grid.PositionComp.WorldMatrixNormalizedInv);
                Vector3D blockCenterLocal = slimBlock.Position * grid.GridSize;
                Vector3D D = localHitPos - blockCenterLocal;

                if (D.LengthSquared() < 0.001f) return;

                Vector3I step = Vector3I.Round(Vector3D.Normalize(D));
                Vector3I neighborCoord = slimBlock.Position + step;

                MySlimBlock occluder = grid.GetCubeBlock(neighborCoord);

                if (occluder == null || ReferenceEquals(occluder, slimBlock)) return;

                // Enforced mode: ONLY pure armor blocks occlude, and only while less than 50% deformed.
                // Functional blocks have no deformation skeleton (DeformationRatio always 0) and never qualify.
                bool isValidOccluder = !config.ArmorOnlyOcclusion ||
                    (occluder.FatBlock == null && occluder.DeformationRatio < 0.5f);

                // Physics-less fat blocks (interior lights, decorative blocks) are not structural and never occlude
                if (occluder.FatBlock != null && occluder.FatBlock.Physics == null)
                {
                    isValidOccluder = false;
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
