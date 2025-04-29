using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
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
        
        private EntityQuery _relevantQuery;
        private EntityQuery _volumeQuery;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<Relevant>();
            
            _activeVolumes = new NativeList<Entity>(100, Allocator.Persistent);
            _toLoadList = new NativeList<(Entity, LevelInfo)>(10, Allocator.Persistent);
            _toUnloadList = new NativeList<(Entity, LevelInfo)>(10, Allocator.Persistent);
            
            _relevantQuery = SystemAPI.QueryBuilder().WithAll<Relevant, LocalToWorld>().Build();
            _volumeQuery = SystemAPI.QueryBuilder().WithAll<Volume, LocalToWorld>().Build();
        }
        
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_activeVolumes.IsCreated) _activeVolumes.Dispose();
            if (_toLoadList.IsCreated) _toLoadList.Dispose();
            if (_toUnloadList.IsCreated) _toUnloadList.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _activeVolumes.Clear();
            _toLoadList.Clear();
            _toUnloadList.Clear();

            var relevantPositions = _relevantQuery.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
            var volumeEntities = _volumeQuery.ToEntityArray(Allocator.TempJob);
            var volumeTransforms = _volumeQuery.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
            var volumes = _volumeQuery.ToComponentDataArray<Volume>(Allocator.TempJob);
            var volumeActiveFlags = new NativeArray<int>(volumeEntities.Length, Allocator.TempJob);
            
            var detectVolumeJob = new DetectActiveVolumesJob
            {
                VolumeEntities = volumeEntities,
                VolumeTransforms = volumeTransforms,
                VolumeData = volumes,
                RelevantPositions = relevantPositions,
                VolumeActiveFlags = volumeActiveFlags
            };
            
            detectVolumeJob.Schedule(volumeEntities.Length, 16).Complete();
            
            for (int i = 0; i < volumeActiveFlags.Length; i++)
            {
                if (volumeActiveFlags[i] == 1)
                {
                    _activeVolumes.Add(volumeEntities[i]);
                }
            }
            
            ProcessLevelLoadingStates(ref state);
            
            relevantPositions.Dispose();
            volumeEntities.Dispose();
            volumeTransforms.Dispose();
            volumes.Dispose();
            volumeActiveFlags.Dispose();
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
    
    [BurstCompile]
    struct DetectActiveVolumesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Entity> VolumeEntities;
        [ReadOnly] public NativeArray<LocalToWorld> VolumeTransforms;
        [ReadOnly] public NativeArray<Volume> VolumeData;
        [ReadOnly] public NativeArray<LocalToWorld> RelevantPositions;
        
        [NativeDisableParallelForRestriction]
        public NativeArray<int> VolumeActiveFlags;
        
        public void Execute(int index)
        {
            var volumeTransform = VolumeTransforms[index];
            var volume = VolumeData[index];
            
            var range = volume.Scale / 2f;
            var pos = volumeTransform.Position;
            
            for (int i = 0; i < RelevantPositions.Length; i++)
            {
                var relevantPosition = RelevantPositions[i].Position;
                var distance = math.abs(relevantPosition - pos);
                var insideAxis = (distance < range);
                
                if (insideAxis.x && insideAxis.y && insideAxis.z)
                {
                    VolumeActiveFlags[index] = 1;
                    break;
                }
            }
        }
    }
}