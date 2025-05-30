using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;

namespace jeanf.scenemanagement
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    partial struct VolumeSystem : ISystem
    {
        private NativeList<Entity> _activeVolumes;
        private NativeList<(Entity, LevelInfo)> _toLoadList;
        private NativeList<(Entity, LevelInfo)> _toUnloadList;
        private NativeHashSet<FixedString128Bytes> _checkableZoneIds;

        private EntityQuery _relevantQuery;
        private EntityQuery _volumeQuery;
        private EntityQuery _precomputedDataQuery;

        private FixedString128Bytes _currentPlayerZone;
        private FixedString128Bytes _currentPlayerRegion;
        private FixedString128Bytes _lastNotifiedZone;
        private FixedString128Bytes _lastNotifiedRegion;

        private NativeHashMap<FixedString128Bytes, FixedString128Bytes> _zoneToRegionMap;
        private NativeHashMap<FixedString128Bytes, int> _zoneToCheckableIndex;
        private NativeHashSet<FixedString128Bytes> _landingZones;
        private bool _precomputedDataInitialized;
        
        // GC ALLOCATION FIX: Cache string conversions
        private FixedString128Bytes _lastZoneStringConverted;
        private FixedString128Bytes _lastRegionStringConverted;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<Relevant>();
            state.RequireForUpdate<PrecomputedVolumeDataBuffer>();

            _activeVolumes = new NativeList<Entity>(100, Allocator.Persistent);
            _toLoadList = new NativeList<(Entity, LevelInfo)>(10, Allocator.Persistent);
            _toUnloadList = new NativeList<(Entity, LevelInfo)>(10, Allocator.Persistent);
            _checkableZoneIds = new NativeHashSet<FixedString128Bytes>(50, Allocator.Persistent);
            _zoneToRegionMap = new NativeHashMap<FixedString128Bytes, FixedString128Bytes>(100, Allocator.Persistent);
            _zoneToCheckableIndex = new NativeHashMap<FixedString128Bytes, int>(100, Allocator.Persistent);
            _landingZones = new NativeHashSet<FixedString128Bytes>(50, Allocator.Persistent);

            _currentPlayerZone = new FixedString128Bytes();
            _currentPlayerRegion = new FixedString128Bytes();
            _lastNotifiedZone = new FixedString128Bytes();
            _lastNotifiedRegion = new FixedString128Bytes();
            _lastZoneStringConverted = new FixedString128Bytes();
            _lastRegionStringConverted = new FixedString128Bytes();
            _precomputedDataInitialized = false;

            _relevantQuery = SystemAPI.QueryBuilder().WithAll<Relevant, LocalToWorld>().Build();
            _volumeQuery = SystemAPI.QueryBuilder().WithAll<Volume, LocalToWorld>().Build();
            _precomputedDataQuery = SystemAPI.QueryBuilder().WithAll<PrecomputedVolumeDataBuffer>().Build();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_activeVolumes.IsCreated) _activeVolumes.Dispose();
            if (_toLoadList.IsCreated) _toLoadList.Dispose();
            if (_toUnloadList.IsCreated) _toUnloadList.Dispose();
            if (_checkableZoneIds.IsCreated) _checkableZoneIds.Dispose();
            if (_zoneToRegionMap.IsCreated) _zoneToRegionMap.Dispose();
            if (_zoneToCheckableIndex.IsCreated) _zoneToCheckableIndex.Dispose();
            if (_landingZones.IsCreated) _landingZones.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _activeVolumes.Clear();
            _toLoadList.Clear();
            _toUnloadList.Clear();

            // GC ALLOCATION FIX: Use try-finally for guaranteed disposal
            var relevantPositions = _relevantQuery.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
            try
            {
                if (relevantPositions.Length == 0)
                {
                    return;
                }

                var playerPosition = relevantPositions[0].Position;

                if (!_precomputedDataInitialized)
                {
                    LoadPrecomputedData(ref state);
                    _precomputedDataInitialized = true;
                }

                UpdateCheckableZonesFromPrecomputed(ref state);

                var newPlayerZone = CheckVolumesForPlayerZone(ref state, playerPosition);
                CheckForZoneAndRegionChange(newPlayerZone);
                ProcessLevelLoadingStates(ref state);
            }
            finally
            {
                if (relevantPositions.IsCreated)
                {
                    relevantPositions.Dispose();
                }
            }
        }

        [BurstCompile]
        private FixedString128Bytes CheckVolumesForPlayerZone(ref SystemState state, float3 playerPosition)
        {
            var newPlayerZone = new FixedString128Bytes();

            foreach (var (volume, transform, entity) in
                     SystemAPI.Query<RefRO<Volume>, RefRO<LocalToWorld>>().WithEntityAccess())
            {
                if (!ShouldCheckVolume(volume.ValueRO.ZoneId))
                    continue;

                var range = volume.ValueRO.Scale / 2f;
                var pos = transform.ValueRO.Position;
                var distance = math.abs(playerPosition - pos);
                var insideAxis = (distance < range);

                if (insideAxis.x && insideAxis.y && insideAxis.z)
                {
                    _activeVolumes.Add(entity);

                    if (!volume.ValueRO.ZoneId.IsEmpty && newPlayerZone.IsEmpty)
                    {
                        newPlayerZone = volume.ValueRO.ZoneId;
                    }
                }
            }

            return newPlayerZone;
        }

        [BurstCompile]
        private void LoadPrecomputedData(ref SystemState state)
        {
            _zoneToRegionMap.Clear();
            _zoneToCheckableIndex.Clear();
            _landingZones.Clear();

            var precomputedEntity = _precomputedDataQuery.GetSingletonEntity();
            var precomputedBuffer = SystemAPI.GetBuffer<PrecomputedVolumeDataBuffer>(precomputedEntity);

            // Load zone-region mappings and landing zones
            foreach (var entry in precomputedBuffer)
            {
                if (entry.isZoneRegionMapping && !entry.zoneId.IsEmpty && !entry.regionId.IsEmpty)
                {
                    _zoneToRegionMap.TryAdd(entry.zoneId, entry.regionId);
                }
                else if (entry.isLandingZone && !entry.landingZoneId.IsEmpty)
                {
                    _landingZones.Add(entry.landingZoneId);
                }
            }

            // Build checkable zone index
            for (int i = 0; i < precomputedBuffer.Length; i++)
            {
                var entry = precomputedBuffer[i];
                if (entry.isHeader && !entry.primaryZoneId.IsEmpty)
                {
                    _zoneToCheckableIndex.TryAdd(entry.primaryZoneId, i);
                }
            }
        }

        [BurstCompile]
        private void UpdateCheckableZonesFromPrecomputed(ref SystemState state)
        {
            _checkableZoneIds.Clear();

            if (_currentPlayerZone.IsEmpty)
            {
                // Bootstrap state - add all zones
                // GC ALLOCATION FIX: Use manual enumeration instead of foreach
                var enumerator = _zoneToRegionMap.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    _checkableZoneIds.Add(enumerator.Current.Key);
                }
                enumerator.Dispose();
                return;
            }

            // Find precomputed checkable zones for current zone
            if (_zoneToCheckableIndex.TryGetValue(_currentPlayerZone, out var headerIndex))
            {
                var precomputedEntity = _precomputedDataQuery.GetSingletonEntity();
                var precomputedBuffer = SystemAPI.GetBuffer<PrecomputedVolumeDataBuffer>(precomputedEntity);

                if (headerIndex < precomputedBuffer.Length)
                {
                    var header = precomputedBuffer[headerIndex];

                    for (int i = 0; i < header.count; i++)
                    {
                        var dataIndex = header.startIndex + i;
                        if (dataIndex < precomputedBuffer.Length)
                        {
                            var dataEntry = precomputedBuffer[dataIndex];
                            if (dataEntry.isData && !dataEntry.checkableZoneId.IsEmpty)
                            {
                                _checkableZoneIds.Add(dataEntry.checkableZoneId);
                            }
                        }
                    }
                }
            }

            // Always add landing zones
            // GC ALLOCATION FIX: Use manual enumeration instead of foreach
            var landingEnumerator = _landingZones.GetEnumerator();
            while (landingEnumerator.MoveNext())
            {
                _checkableZoneIds.Add(landingEnumerator.Current);
            }
            landingEnumerator.Dispose();
        }

        [BurstCompile]
        private bool ShouldCheckVolume(FixedString128Bytes zoneId)
        {
            if (zoneId.IsEmpty) return false;

            if (_currentPlayerZone.IsEmpty)
            {
                return true;
            }

            return _checkableZoneIds.Contains(zoneId);
        }

        // GC ALLOCATION FIX: Remove [BurstCompile] and optimize string conversions
        private void CheckForZoneAndRegionChange(FixedString128Bytes newPlayerZone)
        {
            bool zoneChanged = !_currentPlayerZone.Equals(newPlayerZone);
            bool regionChanged = false;
            FixedString128Bytes newPlayerRegion = new FixedString128Bytes();

            if (!newPlayerZone.IsEmpty && _zoneToRegionMap.TryGetValue(newPlayerZone, out newPlayerRegion))
            {
                regionChanged = !_currentPlayerRegion.Equals(newPlayerRegion);
            }

            if (zoneChanged)
            {
                _currentPlayerZone = newPlayerZone;
            }

            if (regionChanged)
            {
                _currentPlayerRegion = newPlayerRegion;
            }

            // GC ALLOCATION FIX: Only convert to string when absolutely necessary
            if (zoneChanged && !newPlayerZone.IsEmpty && !_lastNotifiedZone.Equals(newPlayerZone))
            {
                _lastNotifiedZone = newPlayerZone;
                
                // Only convert if different from last conversion
                if (!_lastZoneStringConverted.Equals(newPlayerZone))
                {
                    _lastZoneStringConverted = newPlayerZone;
                    var zoneString = newPlayerZone.ToString();
                    WorldManager.NotifyZoneChangeFromECS(zoneString);
                }
            }

            if (regionChanged && !newPlayerRegion.IsEmpty && !_lastNotifiedRegion.Equals(newPlayerRegion))
            {
                _lastNotifiedRegion = newPlayerRegion;
                
                // Only convert if different from last conversion
                if (!_lastRegionStringConverted.Equals(newPlayerRegion))
                {
                    _lastRegionStringConverted = newPlayerRegion;
                    var regionString = newPlayerRegion.ToString();
                    WorldManager.NotifyRegionChangeFromECS(regionString);
                }
            }
        }

        [BurstCompile]
        private void ProcessLevelLoadingStates(ref SystemState state)
        {
            foreach (var (volumes, levelInfo, entity) in
                     SystemAPI.Query<DynamicBuffer<VolumeBuffer>, RefRW<LevelInfo>>()
                         .WithEntityAccess())
            {
                bool shouldLoad = false;
                foreach (var volume in volumes)
                {
                    if (_activeVolumes.Contains(volume.volumeEntity))
                    {
                        shouldLoad = true;
                        break;
                    }
                }

                if (shouldLoad && levelInfo.ValueRW.runtimeEntity == Entity.Null)
                {
                    _toLoadList.Add((entity, levelInfo.ValueRW));
                }
                else if (!shouldLoad && levelInfo.ValueRW.runtimeEntity != Entity.Null)
                {
                    _toUnloadList.Add((entity, levelInfo.ValueRW));
                }
            }

            foreach (var toLoad in _toLoadList)
            {
                var (entity, streamingData) = toLoad;
                streamingData.runtimeEntity =
                    SceneSystem.LoadSceneAsync(state.WorldUnmanaged, streamingData.sceneReference);
                state.EntityManager.SetComponentData(entity, streamingData);
            }

            foreach (var toUnload in _toUnloadList)
            {
                var (entity, streamingData) = toUnload;
                SceneSystem.UnloadScene(state.WorldUnmanaged, streamingData.runtimeEntity,
                    SceneSystem.UnloadParameters.DestroyMetaEntities);
                streamingData.runtimeEntity = Entity.Null;
                state.EntityManager.SetComponentData(entity, streamingData);
            }
        }
    }
}