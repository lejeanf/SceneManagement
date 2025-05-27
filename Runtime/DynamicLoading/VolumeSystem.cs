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
        private NativeHashSet<FixedString128Bytes> _relevantZoneIds;
        
        private EntityQuery _relevantQuery;
        private EntityQuery _volumeQuery;
        
        private FixedString128Bytes _currentPlayerZone;
        private FixedString128Bytes _currentRegionId;
        
        private int _frameCounter;
        private const int DEBUG_FREQUENCY = 60;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<Relevant>();
            state.RequireForUpdate<RegionBuffer>();
            
            _activeVolumes = new NativeList<Entity>(100, Allocator.Persistent);
            _toLoadList = new NativeList<(Entity, LevelInfo)>(10, Allocator.Persistent);
            _toUnloadList = new NativeList<(Entity, LevelInfo)>(10, Allocator.Persistent);
            _relevantZoneIds = new NativeHashSet<FixedString128Bytes>(50, Allocator.Persistent);
            _currentPlayerZone = new FixedString128Bytes();
            
            _relevantQuery = SystemAPI.QueryBuilder().WithAll<Relevant, LocalToWorld>().Build();
            _volumeQuery = SystemAPI.QueryBuilder().WithAll<Volume, LocalToWorld>().Build();
        }
        
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_activeVolumes.IsCreated) _activeVolumes.Dispose();
            if (_toLoadList.IsCreated) _toLoadList.Dispose();
            if (_toUnloadList.IsCreated) _toUnloadList.Dispose();
            if (_relevantZoneIds.IsCreated) _relevantZoneIds.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            _frameCounter++;
            bool shouldDebug = _frameCounter % DEBUG_FREQUENCY == 0;
            
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
            
            UpdateRelevantZones(ref state);
            
            var volumeEntities = _volumeQuery.ToEntityArray(Allocator.TempJob);
            var volumeTransforms = _volumeQuery.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
            var volumes = _volumeQuery.ToComponentDataArray<Volume>(Allocator.TempJob);
            
            FixedString128Bytes newPlayerZone = new FixedString128Bytes();
            
            for (int i = 0; i < volumeEntities.Length; i++)
            {
                var volume = volumes[i];
                
                if (!_relevantZoneIds.Contains(volume.ZoneId) && !volume.ZoneId.IsEmpty)
                    continue;
                
                var volumeTransform = volumeTransforms[i];
                var range = volume.Scale / 2f;
                var pos = volumeTransform.Position;
                var distance = math.abs(playerPosition - pos);
                var insideAxis = (distance < range);
                
                if (insideAxis.x && insideAxis.y && insideAxis.z)
                {
                    _activeVolumes.Add(volumeEntities[i]);
                    
                    if (!volume.ZoneId.IsEmpty && newPlayerZone.IsEmpty)
                    {
                        newPlayerZone = volume.ZoneId;
                    }
                }
            }
            
            CheckForZoneChange(newPlayerZone, shouldDebug);
            ProcessLevelLoadingStates(ref state);
            
            relevantPositions.Dispose();
            volumeEntities.Dispose();
            volumeTransforms.Dispose();
            volumes.Dispose();
        }
        
        private void UpdateRelevantZones(ref SystemState state)
        {
            _relevantZoneIds.Clear();
            
            foreach (var (regionBuffer, zoneBuffer, landingBuffer) in 
                     SystemAPI.Query<DynamicBuffer<RegionBuffer>, DynamicBuffer<ZoneIdBuffer>, DynamicBuffer<LandingZoneBuffer>>())
            {
                foreach (var regionData in regionBuffer)
                {
                    if (regionData.regionId.Equals(_currentRegionId))
                    {
                        for (int i = 0; i < regionData.zoneCount; i++)
                        {
                            var zoneIndex = regionData.zoneStartIndex + i;
                            if (zoneIndex < zoneBuffer.Length)
                            {
                                _relevantZoneIds.Add(zoneBuffer[zoneIndex].zoneId);
                            }
                        }
                        break;
                    }
                }
                
                foreach (var landingData in landingBuffer)
                {
                    if (!landingData.landingZoneId.IsEmpty)
                    {
                        _relevantZoneIds.Add(landingData.landingZoneId);
                    }
                }
            }
        }
        
        private void CheckForZoneChange(FixedString128Bytes newPlayerZone, bool shouldDebug)
        {
            bool hasChanged = !_currentPlayerZone.Equals(newPlayerZone);

            if (!hasChanged) return;
            _currentPlayerZone = newPlayerZone;

            if (newPlayerZone.IsEmpty) return;
                
            var zoneString = newPlayerZone.ToString();
                    
            try 
            {
                WorldManager.NotifyZoneChangeFromECS(zoneString);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[VolumeSystem] Failed to notify WorldManager: {e.Message}");
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
        
        public void SetCurrentRegion(Region region)
        {
            _currentRegionId = region != null ? region.id.ToString() : "";
        }
    }
}