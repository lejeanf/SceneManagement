using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;
using Streaming.SceneManagement.Common;

namespace jeanf.SceneManagment
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct VolumeSystem : ISystem
    {
        private EntityQuery _relevantQuery;
        private EntityQuery _volumeSetQuery;
        private NativeHashSet<Entity> _activeScenes;
        private NativeHashSet<Entity> _preloadingScenes;
        
        private const float PRELOAD_HORIZONTAL_MULTIPLIER = 2.0f;
        private const float PRELOAD_VERTICAL_MULTIPLIER = 1.0f;
        private const int MAX_OPERATIONS_PER_FRAME = 2;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _relevantQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<Relevant>(),
                ComponentType.ReadOnly<LocalToWorld>());
                
            _volumeSetQuery = state.GetEntityQuery(
                ComponentType.ReadWrite<LevelInfo>(),
                ComponentType.ReadOnly<VolumeBuffer>());
                
            _activeScenes = new NativeHashSet<Entity>(10, Allocator.Persistent);
            _preloadingScenes = new NativeHashSet<Entity>(10, Allocator.Persistent);
                
            state.RequireForUpdate<Relevant>();
        }

        [BurstCompile]
        private struct VolumeCheckJob : IJob
        {
            [ReadOnly] public float3 PlayerPosition;
            [ReadOnly] public NativeArray<Entity> VolumeSets;
            [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorldLookup;
            [ReadOnly] public ComponentLookup<Volume> VolumeLookup;
            [ReadOnly] public BufferLookup<VolumeBuffer> VolumeBufferLookup;
            
            public NativeHashSet<Entity> ContainingVolumes;
            public NativeHashSet<Entity> NearbyVolumes;

            private bool IsPositionInsideVolume(float3 position, float3 volumePosition, float3 range)
            {
                var distance = math.abs(position - volumePosition);
                return distance.x < range.x && 
                       distance.y < range.y && 
                       distance.z < range.z;
            }

            private bool IsPositionNearVolume(float3 position, float3 volumePosition, float3 range)
            {
                var distance = math.abs(position - volumePosition);
                bool isNearHorizontally = distance.x < range.x * PRELOAD_HORIZONTAL_MULTIPLIER && 
                                        distance.z < range.z * PRELOAD_HORIZONTAL_MULTIPLIER;
                bool isNearVertically = distance.y < range.y * PRELOAD_VERTICAL_MULTIPLIER;
                return isNearHorizontally && isNearVertically;
            }

            public void Execute()
            {
                foreach (var volumeSetEntity in VolumeSets)
                {
                    if (!VolumeBufferLookup.HasBuffer(volumeSetEntity)) continue;
                    var volumeBuffer = VolumeBufferLookup[volumeSetEntity];
                    
                    foreach (var volume in volumeBuffer)
                    {
                        if (!LocalToWorldLookup.HasComponent(volume.volumeEntity) || 
                            !VolumeLookup.HasComponent(volume.volumeEntity)) continue;

                        var volumeTransform = LocalToWorldLookup[volume.volumeEntity];
                        var volumeData = VolumeLookup[volume.volumeEntity];
                        
                        var range = volumeData.Scale / 2f;
                        
                        if (IsPositionInsideVolume(PlayerPosition, volumeTransform.Position, range))
                        {
                            ContainingVolumes.Add(volume.volumeEntity);
                        }
                        else if (IsPositionNearVolume(PlayerPosition, volumeTransform.Position, range))
                        {
                            NearbyVolumes.Add(volume.volumeEntity);
                        }
                    }
                }
            }
        }

        [BurstCompile]
        private struct SceneFilterJob : IJob
        {
            [ReadOnly] public NativeArray<Entity> VolumeSets;
            [ReadOnly] public NativeHashSet<Entity> ContainingVolumes;
            [ReadOnly] public NativeHashSet<Entity> NearbyVolumes;
            [ReadOnly] public BufferLookup<VolumeBuffer> VolumeBufferLookup;
            
            public NativeHashSet<Entity> NewActiveScenes;
            public NativeHashSet<Entity> NewPreloadingScenes;

            public void Execute()
            {
                foreach (var volumeSetEntity in VolumeSets)
                {
                    if (!VolumeBufferLookup.HasBuffer(volumeSetEntity)) continue;
                    var volumeBuffer = VolumeBufferLookup[volumeSetEntity];
                    
                    var shouldBeActive = false;
                    var shouldPreload = false;
                    
                    foreach (var volume in volumeBuffer)
                    {
                        if (ContainingVolumes.Contains(volume.volumeEntity))
                        {
                            shouldBeActive = true;
                            break;
                        }
                        if (NearbyVolumes.Contains(volume.volumeEntity))
                        {
                            shouldPreload = true;
                        }
                    }

                    if (shouldBeActive)
                    {
                        NewActiveScenes.Add(volumeSetEntity);
                    }
                    else if (shouldPreload)
                    {
                        NewPreloadingScenes.Add(volumeSetEntity);
                    }
                }
            }
        }

        [BurstCompile]
        private struct SceneChangeJob : IJob
        {
            [ReadOnly] public NativeHashSet<Entity> NewActiveScenes;
            [ReadOnly] public NativeHashSet<Entity> NewPreloadingScenes;
            [ReadOnly] public NativeHashSet<Entity> CurrentActiveScenes;
            [ReadOnly] public NativeHashSet<Entity> CurrentPreloadingScenes;
            
            public NativeList<Entity> ScenesToUnload;
            public NativeList<Entity> ScenesToLoad;
            public NativeList<Entity> ScenesToPreload;

            public void Execute()
            {
                // Find scenes to unload
                foreach (var scene in CurrentActiveScenes)
                {
                    if (!NewActiveScenes.Contains(scene) && !NewPreloadingScenes.Contains(scene))
                    {
                        ScenesToUnload.Add(scene);
                    }
                }
                
                foreach (var scene in CurrentPreloadingScenes)
                {
                    if (!NewActiveScenes.Contains(scene) && !NewPreloadingScenes.Contains(scene))
                    {
                        ScenesToUnload.Add(scene);
                    }
                }

                // Find scenes to load
                foreach (var scene in NewActiveScenes)
                {
                    if (!CurrentActiveScenes.Contains(scene))
                    {
                        ScenesToLoad.Add(scene);
                    }
                }

                // Find scenes to preload
                foreach (var scene in NewPreloadingScenes)
                {
                    if (!CurrentActiveScenes.Contains(scene) && !CurrentPreloadingScenes.Contains(scene))
                    {
                        ScenesToPreload.Add(scene);
                    }
                }
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var relevantTransforms = _relevantQuery.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
            var playerPosition = relevantTransforms[0].Position;
            var volumeSets = _volumeSetQuery.ToEntityArray(Allocator.TempJob);

            // Create temporary collections
            var containingVolumes = new NativeHashSet<Entity>(10, Allocator.TempJob);
            var nearbyVolumes = new NativeHashSet<Entity>(10, Allocator.TempJob);
            var newActiveScenes = new NativeHashSet<Entity>(10, Allocator.TempJob);
            var newPreloadingScenes = new NativeHashSet<Entity>(10, Allocator.TempJob);
            var scenesToUnload = new NativeList<Entity>(Allocator.TempJob);
            var scenesToLoad = new NativeList<Entity>(Allocator.TempJob);
            var scenesToPreload = new NativeList<Entity>(Allocator.TempJob);

            // Schedule volume check job
            var volumeCheckJob = new VolumeCheckJob
            {
                PlayerPosition = playerPosition,
                VolumeSets = volumeSets,
                LocalToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true),
                VolumeLookup = SystemAPI.GetComponentLookup<Volume>(true),
                VolumeBufferLookup = SystemAPI.GetBufferLookup<VolumeBuffer>(true),
                ContainingVolumes = containingVolumes,
                NearbyVolumes = nearbyVolumes
            };

            // Schedule scene filter job
            var sceneFilterJob = new SceneFilterJob
            {
                VolumeSets = volumeSets,
                ContainingVolumes = containingVolumes,
                NearbyVolumes = nearbyVolumes,
                VolumeBufferLookup = SystemAPI.GetBufferLookup<VolumeBuffer>(true),
                NewActiveScenes = newActiveScenes,
                NewPreloadingScenes = newPreloadingScenes
            };

            // Schedule scene change job
            var sceneChangeJob = new SceneChangeJob
            {
                NewActiveScenes = newActiveScenes,
                NewPreloadingScenes = newPreloadingScenes,
                CurrentActiveScenes = _activeScenes,
                CurrentPreloadingScenes = _preloadingScenes,
                ScenesToUnload = scenesToUnload,
                ScenesToLoad = scenesToLoad,
                ScenesToPreload = scenesToPreload
            };

            // Create dependency chain
            var volumeCheckHandle = volumeCheckJob.Schedule();
            var sceneFilterHandle = sceneFilterJob.Schedule(volumeCheckHandle);
            var sceneChangeHandle = sceneChangeJob.Schedule(sceneFilterHandle);
            sceneChangeHandle.Complete();

            // Process scene operations (this part cannot be burst compiled or parallelized due to SceneSystem calls)
            ProcessSceneOperations(ref state, scenesToUnload, scenesToLoad, scenesToPreload);

            // Cleanup
            relevantTransforms.Dispose();
            volumeSets.Dispose();
            containingVolumes.Dispose();
            nearbyVolumes.Dispose();
            newActiveScenes.Dispose();
            newPreloadingScenes.Dispose();
            scenesToUnload.Dispose();
            scenesToLoad.Dispose();
            scenesToPreload.Dispose();
        }

        private void ProcessSceneOperations(
            ref SystemState state,
            NativeList<Entity> scenesToUnload,
            NativeList<Entity> scenesToLoad,
            NativeList<Entity> scenesToPreload)
        {
            var operationsThisFrame = 0;

            // Process unloads first
            foreach (var scene in scenesToUnload)
            {
                if (operationsThisFrame >= MAX_OPERATIONS_PER_FRAME) break;
                
                var levelInfo = state.EntityManager.GetComponentData<LevelInfo>(scene);
                if (levelInfo.runtimeEntity == Entity.Null) continue;
                
                SceneSystem.UnloadScene(
                    state.WorldUnmanaged,
                    levelInfo.runtimeEntity,
                    SceneSystem.UnloadParameters.DestroyMetaEntities);
                    
                levelInfo.runtimeEntity = Entity.Null;
                state.EntityManager.SetComponentData(scene, levelInfo);
                _activeScenes.Remove(scene);
                _preloadingScenes.Remove(scene);
                operationsThisFrame++;
            }

            // Process loads
            foreach (var scene in scenesToLoad)
            {
                if (operationsThisFrame >= MAX_OPERATIONS_PER_FRAME) break;
                
                var levelInfo = state.EntityManager.GetComponentData<LevelInfo>(scene);
                if (levelInfo.runtimeEntity != Entity.Null || !levelInfo.sceneReference.IsReferenceValid) continue;
                
                levelInfo.runtimeEntity = SceneSystem.LoadSceneAsync(
                    state.WorldUnmanaged,
                    levelInfo.sceneReference);
                    
                state.EntityManager.SetComponentData(scene, levelInfo);
                _activeScenes.Add(scene);
                _preloadingScenes.Remove(scene);
                operationsThisFrame++;
            }

            // Process preloads
            foreach (var scene in scenesToPreload)
            {
                if (operationsThisFrame >= MAX_OPERATIONS_PER_FRAME) break;
                
                var levelInfo = state.EntityManager.GetComponentData<LevelInfo>(scene);
                if (levelInfo.runtimeEntity != Entity.Null || !levelInfo.sceneReference.IsReferenceValid) continue;
                
                levelInfo.runtimeEntity = SceneSystem.LoadSceneAsync(
                    state.WorldUnmanaged,
                    levelInfo.sceneReference);
                    
                state.EntityManager.SetComponentData(scene, levelInfo);
                _preloadingScenes.Add(scene);
                operationsThisFrame++;
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_activeScenes.IsCreated)
            {
                _activeScenes.Dispose();
            }
            if (_preloadingScenes.IsCreated)
            {
                _preloadingScenes.Dispose();
            }
        }
    }
}