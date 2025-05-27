using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;
using UnityEngine;
using System.Collections.Generic;

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
        
        private FixedString128Bytes _currentPlayerZone;
        private FixedString128Bytes _currentPlayerRegion;
        private FixedString128Bytes _lastNotifiedZone;
        private FixedString128Bytes _lastNotifiedRegion;
        
        private NativeHashMap<FixedString128Bytes, FixedString128Bytes> _zoneToRegionMap;
        private NativeHashMap<FixedString128Bytes, NativeList<FixedString128Bytes>> _zoneNeighbors;
        private NativeHashSet<FixedString128Bytes> _landingZones;
        private bool _mappingInitialized;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<Relevant>();
            state.RequireForUpdate<RegionBuffer>();
            
            _activeVolumes = new NativeList<Entity>(100, Allocator.Persistent);
            _toLoadList = new NativeList<(Entity, LevelInfo)>(10, Allocator.Persistent);
            _toUnloadList = new NativeList<(Entity, LevelInfo)>(10, Allocator.Persistent);
            _checkableZoneIds = new NativeHashSet<FixedString128Bytes>(50, Allocator.Persistent);
            _zoneToRegionMap = new NativeHashMap<FixedString128Bytes, FixedString128Bytes>(100, Allocator.Persistent);
            _zoneNeighbors = new NativeHashMap<FixedString128Bytes, NativeList<FixedString128Bytes>>(100, Allocator.Persistent);
            _landingZones = new NativeHashSet<FixedString128Bytes>(50, Allocator.Persistent);
            
            _currentPlayerZone = new FixedString128Bytes();
            _currentPlayerRegion = new FixedString128Bytes();
            _lastNotifiedZone = new FixedString128Bytes();
            _lastNotifiedRegion = new FixedString128Bytes();
            _mappingInitialized = false;
            
            _relevantQuery = SystemAPI.QueryBuilder().WithAll<Relevant, LocalToWorld>().Build();
            _volumeQuery = SystemAPI.QueryBuilder().WithAll<Volume, LocalToWorld>().Build();
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
            if (_zoneNeighbors.IsCreated)
            {
                foreach (var kvp in _zoneNeighbors)
                {
                    if (kvp.Value.IsCreated) kvp.Value.Dispose();
                }
                _zoneNeighbors.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            _activeVolumes.Clear();
            _toLoadList.Clear();
            _toUnloadList.Clear();

            var relevantPositions = _relevantQuery.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
            
            if (relevantPositions.Length == 0)
            {
                relevantPositions.Dispose();
                return;
            }
            
            var playerPosition = relevantPositions[0].Position;
            relevantPositions.Dispose();
            
            if (!_mappingInitialized)
            {
                BuildConnectivityMappings(ref state);
                _mappingInitialized = true;
            }
            
            UpdateCheckableZones();
            
            var newPlayerZone = CheckVolumesForPlayerZone(ref state, playerPosition);
            CheckForZoneAndRegionChange(newPlayerZone);
            ProcessLevelLoadingStates(ref state);
        }
        
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
        
        private void BuildConnectivityMappings(ref SystemState state)
        {
            _zoneToRegionMap.Clear();
            _landingZones.Clear();
            
            foreach (var kvp in _zoneNeighbors)
            {
                if (kvp.Value.IsCreated) kvp.Value.Dispose();
            }
            _zoneNeighbors.Clear();
            
            foreach (var (regionBuffer, zoneBuffer, landingBuffer) in 
                     SystemAPI.Query<DynamicBuffer<RegionBuffer>, DynamicBuffer<ZoneIdBuffer>, DynamicBuffer<LandingZoneBuffer>>())
            {
                foreach (var landingData in landingBuffer)
                {
                    if (!landingData.landingZoneId.IsEmpty)
                    {
                        _landingZones.Add(landingData.landingZoneId);
                    }
                }
                
                foreach (var regionData in regionBuffer)
                {
                    var regionZones = new NativeList<FixedString128Bytes>(regionData.zoneCount, Allocator.Temp);
                    
                    for (int i = 0; i < regionData.zoneCount; i++)
                    {
                        var zoneIndex = regionData.zoneStartIndex + i;
                        if (zoneIndex < zoneBuffer.Length)
                        {
                            var zoneId = zoneBuffer[zoneIndex].zoneId;
                            _zoneToRegionMap.TryAdd(zoneId, regionData.regionId);
                            regionZones.Add(zoneId);
                        }
                    }
                    
                    for (int i = 0; i < regionZones.Length; i++)
                    {
                        var zoneA = regionZones[i];
                        if (!_zoneNeighbors.ContainsKey(zoneA))
                        {
                            _zoneNeighbors[zoneA] = new NativeList<FixedString128Bytes>(10, Allocator.Persistent);
                        }
                        
                        for (int j = 0; j < regionZones.Length; j++)
                        {
                            if (i == j) continue;
                            var zoneB = regionZones[j];
                            _zoneNeighbors[zoneA].Add(zoneB);
                        }
                    }
                    
                    regionZones.Dispose();
                }
            }
        }
        
        private void UpdateCheckableZones()
        {
            _checkableZoneIds.Clear();
            
            if (_currentPlayerZone.IsEmpty)
            {
                return;
            }
            
            _checkableZoneIds.Add(_currentPlayerZone);
            
            if (_zoneNeighbors.TryGetValue(_currentPlayerZone, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    _checkableZoneIds.Add(neighbor);
                }
            }
            
            foreach (var landingZone in _landingZones)
            {
                _checkableZoneIds.Add(landingZone);
            }
        }
        
        private bool ShouldCheckVolume(FixedString128Bytes zoneId)
        {
            if (zoneId.IsEmpty) return false;
            
            if (_currentPlayerZone.IsEmpty)
            {
                return true;
            }
            
            return _checkableZoneIds.Contains(zoneId);
        }
        
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

            if (zoneChanged && !newPlayerZone.IsEmpty && !_lastNotifiedZone.Equals(newPlayerZone))
            {
                _lastNotifiedZone = newPlayerZone;
                var zoneString = newPlayerZone.ToString();
                WorldManager.NotifyZoneChangeFromECS(zoneString);
            }
            
            if (regionChanged && !newPlayerRegion.IsEmpty && !_lastNotifiedRegion.Equals(newPlayerRegion))
            {
                _lastNotifiedRegion = newPlayerRegion;
                var regionString = newPlayerRegion.ToString();
                WorldManager.NotifyRegionChangeFromECS(regionString);
            }
        }
        
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