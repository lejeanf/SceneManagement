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
        
        private bool _zoneChangeNotificationPending;
        private bool _regionChangeNotificationPending;
        private FixedString128Bytes _pendingZoneNotification;
        private FixedString128Bytes _pendingRegionNotification;

        private NativeHashMap<FixedString128Bytes, FixedString128Bytes> _zoneToRegionMap;
        private NativeHashMap<FixedString128Bytes, NativeArray<FixedString128Bytes>> _precomputedCheckableZones;
        private NativeHashSet<FixedString128Bytes> _landingZones;
        private NativeArray<FixedString128Bytes> _allZones;
        private bool _precomputedDataInitialized;

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
            _precomputedCheckableZones = new NativeHashMap<FixedString128Bytes, NativeArray<FixedString128Bytes>>(100, Allocator.Persistent);
            _landingZones = new NativeHashSet<FixedString128Bytes>(50, Allocator.Persistent);

            _currentPlayerZone = new FixedString128Bytes();
            _currentPlayerRegion = new FixedString128Bytes();
            _lastNotifiedZone = new FixedString128Bytes();
            _lastNotifiedRegion = new FixedString128Bytes();
            _zoneChangeNotificationPending = false;
            _regionChangeNotificationPending = false;
            _pendingZoneNotification = new FixedString128Bytes();
            _pendingRegionNotification = new FixedString128Bytes();
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
            if (_landingZones.IsCreated) _landingZones.Dispose();
            if (_allZones.IsCreated) _allZones.Dispose();
            
            if (_precomputedCheckableZones.IsCreated)
            {
                var enumerator = _precomputedCheckableZones.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current.Value.IsCreated)
                        enumerator.Current.Value.Dispose();
                }
                enumerator.Dispose();
                _precomputedCheckableZones.Dispose();
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _activeVolumes.Clear();
            _toLoadList.Clear();
            _toUnloadList.Clear();

            var relevantPositions = _relevantQuery.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
            
            if (relevantPositions.Length == 0)
            {
                if (relevantPositions.IsCreated) relevantPositions.Dispose();
                return;
            }

            var playerPosition = relevantPositions[0].Position;

            if (!_precomputedDataInitialized)
            {
                LoadPrecomputedData(ref state);
                _precomputedDataInitialized = true;
            }

            CheckForZoneAndRegionChange(
                CheckVolumesForPlayerZone(
                    ref state, playerPosition, 
                    ref _currentPlayerZone, 
                    SetCheckableZones(_currentPlayerZone))
                );
            
            ProcessLevelLoadingStates(ref state);
            
            ProcessPendingNotifications();

            if (relevantPositions.IsCreated) relevantPositions.Dispose();
        }

        

        [BurstCompile]
        private void LoadPrecomputedData(ref SystemState state)
        {
            _zoneToRegionMap.Clear();
            _landingZones.Clear();
            
            var enumerator = _precomputedCheckableZones.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (enumerator.Current.Value.IsCreated)
                    enumerator.Current.Value.Dispose();
            }
            enumerator.Dispose();
            _precomputedCheckableZones.Clear();

            var precomputedEntity = _precomputedDataQuery.GetSingletonEntity();
            var precomputedBuffer = SystemAPI.GetBuffer<PrecomputedVolumeDataBuffer>(precomputedEntity);

            var tempZoneList = new NativeList<FixedString128Bytes>(100, Allocator.Temp);

            foreach (var entry in precomputedBuffer)
            {
                if (entry.isZoneRegionMapping && !entry.zoneId.IsEmpty && !entry.regionId.IsEmpty)
                {
                    _zoneToRegionMap.TryAdd(entry.zoneId, entry.regionId);
                    tempZoneList.Add(entry.zoneId);
                }
                else if (entry.isLandingZone && !entry.landingZoneId.IsEmpty)
                {
                    _landingZones.Add(entry.landingZoneId);
                }
            }

            _allZones = tempZoneList.ToArray(Allocator.Persistent);
            tempZoneList.Dispose();

            BuildPrecomputedCheckableZones(ref state, precomputedBuffer);
        }

        [BurstCompile]
        private void BuildPrecomputedCheckableZones(ref SystemState state, DynamicBuffer<PrecomputedVolumeDataBuffer> precomputedBuffer)
        {
            for (int i = 0; i < precomputedBuffer.Length; i++)
            {
                var entry = precomputedBuffer[i];
                if (entry.isHeader && !entry.primaryZoneId.IsEmpty)
                {
                    var tempList = new NativeList<FixedString128Bytes>(entry.count + _landingZones.Count, Allocator.Temp);

                    for (int j = 0; j < entry.count; j++)
                    {
                        var dataIndex = entry.startIndex + j;
                        if (dataIndex < precomputedBuffer.Length)
                        {
                            var dataEntry = precomputedBuffer[dataIndex];
                            if (dataEntry.isData && !dataEntry.checkableZoneId.IsEmpty)
                            {
                                tempList.Add(dataEntry.checkableZoneId);
                            }
                        }
                    }

                    var landingEnumerator = _landingZones.GetEnumerator();
                    while (landingEnumerator.MoveNext())
                    {
                        tempList.Add(landingEnumerator.Current);
                    }
                    landingEnumerator.Dispose();

                    var checkableArray = tempList.ToArray(Allocator.Persistent);
                    tempList.Dispose();
                    
                    _precomputedCheckableZones.TryAdd(entry.primaryZoneId, checkableArray);
                }
            }
        }

        [BurstCompile]
        private NativeHashSet<FixedString128Bytes> SetCheckableZones(FixedString128Bytes currentZone)
        {
            _checkableZoneIds.Clear();

            if (currentZone.IsEmpty)
            {
                for (int i = 0; i < _allZones.Length; i++)
                {
                    _checkableZoneIds.Add(_allZones[i]);
                }
                return _checkableZoneIds;
            }

            if (_precomputedCheckableZones.TryGetValue(currentZone, out var checkableArray))
            {
                for (int i = 0; i < checkableArray.Length; i++)
                {
                    _checkableZoneIds.Add(checkableArray[i]);
                }
            }

            return _checkableZoneIds;
        }

        [BurstCompile]
        private bool ShouldCheckVolume(NativeHashSet<FixedString128Bytes> checkableZones, FixedString128Bytes zoneId)
        {
            return !zoneId.IsEmpty && checkableZones.Contains(zoneId);
        }
        
        [BurstCompile]
        private FixedString128Bytes CheckVolumesForPlayerZone(ref SystemState state, float3 playerPosition, ref FixedString128Bytes currentZone, NativeHashSet<FixedString128Bytes> checkableZones)
        {
            var newPlayerZone = currentZone;

            foreach (var (volume, transform, entity) in
                     SystemAPI.Query<RefRO<Volume>, RefRO<LocalToWorld>>().WithEntityAccess())
            {
                if (!ShouldCheckVolume(checkableZones, volume.ValueRO.ZoneId))
                    continue;

                var range = volume.ValueRO.Scale * 0.5f;
                var pos = transform.ValueRO.Position;
                var distance = math.abs(playerPosition - pos);
                var insideAxis = distance < range;

                if (insideAxis.x && insideAxis.y && insideAxis.z)
                {
                    _activeVolumes.Add(entity);

                    if (!volume.ValueRO.ZoneId.IsEmpty)
                    {
                        newPlayerZone = volume.ValueRO.ZoneId;
                    }
                }
            }

            return newPlayerZone;
        }
        [BurstCompile]
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
                
                _zoneChangeNotificationPending = true;
                _pendingZoneNotification = _currentPlayerZone;
            }

            if (regionChanged)
            {
                _currentPlayerRegion = newPlayerRegion;
                
                _regionChangeNotificationPending = true;
                _pendingRegionNotification = newPlayerRegion;
            }
        }

        private void ProcessPendingNotifications()
        {
            if (_zoneChangeNotificationPending)
            {
                WorldManager.NotifyZoneChangeFromECS(_pendingRegionNotification);
                
                _lastNotifiedZone = _pendingRegionNotification;
                _zoneChangeNotificationPending = false;
            }

            if (_regionChangeNotificationPending)
            {
                WorldManager.NotifyRegionChangeFromECS(_pendingRegionNotification);
                
                _lastNotifiedRegion = _pendingRegionNotification;
                _regionChangeNotificationPending = false;
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
                for (int i = 0; i < volumes.Length; i++)
                {
                    if (_activeVolumes.Contains(volumes[i].volumeEntity))
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

            for (int i = 0; i < _toLoadList.Length; i++)
            {
                var toLoad = _toLoadList[i];
                var streamingData = toLoad.Item2;
                streamingData.runtimeEntity = SceneSystem.LoadSceneAsync(state.WorldUnmanaged, streamingData.sceneReference);
                state.EntityManager.SetComponentData(toLoad.Item1, streamingData);
            }

            for (int i = 0; i < _toUnloadList.Length; i++)
            {
                var toUnload = _toUnloadList[i];
                var streamingData = toUnload.Item2;
                SceneSystem.UnloadScene(state.WorldUnmanaged, streamingData.runtimeEntity, SceneSystem.UnloadParameters.DestroyMetaEntities);
                streamingData.runtimeEntity = Entity.Null;
                state.EntityManager.SetComponentData(toUnload.Item1, streamingData);
            }
        }
    }
}