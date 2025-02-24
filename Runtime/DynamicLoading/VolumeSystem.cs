using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;

namespace jeanf.scenemanagement
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct VolumeSystem : ISystem
    {
        private EntityQuery _relevantQuery;
        private EntityQuery _volumeSetQuery;
        private EntityQuery _configQuery;
        private NativeHashSet<Entity> _activeScenes;
        private NativeHashSet<Entity> _preloadingScenes;
        
        // Persistent collections to avoid per-frame allocations
        private NativeList<Entity> _volumeSets;
        private NativeList<LocalToWorld> _relevantTransforms;
        private NativeHashSet<Entity> _containingVolumes;
        private NativeHashSet<Entity> _nearbyVolumes;
        private NativeHashSet<Entity> _newActiveScenes;
        private NativeHashSet<Entity> _newPreloadingScenes;
        private NativeList<Entity> _scenesToUnload;
        private NativeList<Entity> _scenesToLoad;
        private NativeList<Entity> _scenesToPreload;
        
        // Default values if no config is present
        private const float DEFAULT_PRELOAD_HORIZONTAL_MULTIPLIER = 1.0f;
        private const float DEFAULT_PRELOAD_VERTICAL_MULTIPLIER = 1.0f;
        private const int DEFAULT_MAX_OPERATIONS_PER_FRAME = 2;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _relevantQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Relevant, LocalToWorld>()
                .WithOptions(EntityQueryOptions.FilterWriteGroup)
                .Build(ref state);

            _volumeSetQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<LevelInfo>()
                .WithAll<VolumeBuffer>()
                .Build(ref state);
            
            _configQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<VolumeSystemConfig>()
                .Build(ref state);
            
            // Initialize persistent collections
            _activeScenes = new NativeHashSet<Entity>(10, Allocator.Persistent);
            _preloadingScenes = new NativeHashSet<Entity>(10, Allocator.Persistent);
            _volumeSets = new NativeList<Entity>(10, Allocator.Persistent);
            _relevantTransforms = new NativeList<LocalToWorld>(1, Allocator.Persistent);
            _containingVolumes = new NativeHashSet<Entity>(10, Allocator.Persistent);
            _nearbyVolumes = new NativeHashSet<Entity>(10, Allocator.Persistent);
            _newActiveScenes = new NativeHashSet<Entity>(10, Allocator.Persistent);
            _newPreloadingScenes = new NativeHashSet<Entity>(10, Allocator.Persistent);
            _scenesToUnload = new NativeList<Entity>(10, Allocator.Persistent);
            _scenesToLoad = new NativeList<Entity>(10, Allocator.Persistent);
            _scenesToPreload = new NativeList<Entity>(10, Allocator.Persistent);
                
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
            [ReadOnly] public float PreloadHorizontalMultiplier;
            [ReadOnly] public float PreloadVerticalMultiplier;
            
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
                bool isNearHorizontally = distance.x < range.x * PreloadHorizontalMultiplier && 
                                        distance.z < range.z * PreloadHorizontalMultiplier;
                bool isNearVertically = distance.y < range.y * PreloadVerticalMultiplier;
                return isNearHorizontally && isNearVertically;
            }

            public void Execute()
            {
                // Clear collections at the start of job
                ContainingVolumes.Clear();
                NearbyVolumes.Clear();
                
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
                // Clear collections at the start of job
                NewActiveScenes.Clear();
                NewPreloadingScenes.Clear();
                
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
                // Clear collections at the start of job
                ScenesToUnload.Clear();
                ScenesToLoad.Clear();
                ScenesToPreload.Clear();
                
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
            // Get config values with fallback to defaults
            float preloadHorizontalMultiplier = DEFAULT_PRELOAD_HORIZONTAL_MULTIPLIER;
            float preloadVerticalMultiplier = DEFAULT_PRELOAD_VERTICAL_MULTIPLIER;
            int maxOperationsPerFrame = DEFAULT_MAX_OPERATIONS_PER_FRAME;

            if (_configQuery.HasSingleton<VolumeSystemConfig>())
            {
                var config = SystemAPI.GetSingleton<VolumeSystemConfig>();
                preloadHorizontalMultiplier = config.PreloadHorizontalMultiplier;
                preloadVerticalMultiplier = config.PreloadVerticalMultiplier;
                maxOperationsPerFrame = config.MaxOperationsPerFrame;
            }
            
            // Reuse persistent collections instead of creating new ones
            _relevantTransforms.Clear();
            _volumeSets.Clear();
            
            // Get relevant transforms data using the correct API
            var relevantTransformsArray = _relevantQuery.ToComponentDataArray<LocalToWorld>(Allocator.Temp);
            for (int i = 0; i < relevantTransformsArray.Length; i++)
            {
                _relevantTransforms.Add(relevantTransformsArray[i]);
            }
            relevantTransformsArray.Dispose();
            
            // Get volume sets entities using the correct API
            var volumeSetsArray = _volumeSetQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < volumeSetsArray.Length; i++)
            {
                _volumeSets.Add(volumeSetsArray[i]);
            }
            volumeSetsArray.Dispose();
            
            // Make sure we have at least one relevant transform (player)
            if (_relevantTransforms.Length == 0)
                return;
            
            var playerPosition = _relevantTransforms[0].Position;

            // Schedule volume check job
            var volumeCheckJob = new VolumeCheckJob
            {
                PlayerPosition = playerPosition,
                VolumeSets = _volumeSets.AsArray(),
                LocalToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true),
                VolumeLookup = SystemAPI.GetComponentLookup<Volume>(true),
                VolumeBufferLookup = SystemAPI.GetBufferLookup<VolumeBuffer>(true),
                ContainingVolumes = _containingVolumes,
                NearbyVolumes = _nearbyVolumes,
                PreloadHorizontalMultiplier = preloadHorizontalMultiplier,
                PreloadVerticalMultiplier = preloadVerticalMultiplier
            };

            // Schedule scene filter job
            var sceneFilterJob = new SceneFilterJob
            {
                VolumeSets = _volumeSets.AsArray(),
                ContainingVolumes = _containingVolumes,
                NearbyVolumes = _nearbyVolumes,
                VolumeBufferLookup = SystemAPI.GetBufferLookup<VolumeBuffer>(true),
                NewActiveScenes = _newActiveScenes,
                NewPreloadingScenes = _newPreloadingScenes
            };

            // Schedule scene change job
            var sceneChangeJob = new SceneChangeJob
            {
                NewActiveScenes = _newActiveScenes,
                NewPreloadingScenes = _newPreloadingScenes,
                CurrentActiveScenes = _activeScenes,
                CurrentPreloadingScenes = _preloadingScenes,
                ScenesToUnload = _scenesToUnload,
                ScenesToLoad = _scenesToLoad,
                ScenesToPreload = _scenesToPreload
            };

            // Create dependency chain and complete it
            var volumeCheckHandle = volumeCheckJob.Schedule();
            var sceneFilterHandle = sceneFilterJob.Schedule(volumeCheckHandle);
            var sceneChangeHandle = sceneChangeJob.Schedule(sceneFilterHandle);
            sceneChangeHandle.Complete();

            // Process scene operations
            ProcessSceneOperations(ref state, _scenesToUnload, _scenesToLoad, _scenesToPreload, maxOperationsPerFrame);

            // Update tracking collections
            UpdateActiveSceneSets();
        }

        private void UpdateActiveSceneSets()
        {
            // Update active scenes
            _activeScenes.Clear();
            foreach (var scene in _newActiveScenes)
            {
                _activeScenes.Add(scene);
            }
            
            // Update preloading scenes
            _preloadingScenes.Clear();
            foreach (var scene in _newPreloadingScenes)
            {
                _preloadingScenes.Add(scene);
            }
        }

        private void ProcessSceneOperations(ref SystemState state, NativeList<Entity> scenesToUnload, NativeList<Entity> scenesToLoad, NativeList<Entity> scenesToPreload, int maxOperationsPerFrame)
        {
            var operationsThisFrame = 0;

            // Process unloads first - prioritize this to free up resources
            for (int i = 0; i < scenesToUnload.Length; i++)
            {
                if (operationsThisFrame >= maxOperationsPerFrame) break;
                
                var scene = scenesToUnload[i];
                
                // Check if entity exists before trying to access it
                if (!state.EntityManager.Exists(scene)) 
                    continue;

                // Check if entity has the required component
                if (!state.EntityManager.HasComponent<LevelInfo>(scene)) 
                    continue;
                
                var levelInfo = state.EntityManager.GetComponentData<LevelInfo>(scene);
                if (levelInfo.runtimeEntity == Entity.Null) continue;
                
                SceneSystem.UnloadScene(
                    state.WorldUnmanaged,
                    levelInfo.runtimeEntity,
                    SceneSystem.UnloadParameters.DestroyMetaEntities);
                    
                levelInfo.runtimeEntity = Entity.Null;
                state.EntityManager.SetComponentData(scene, levelInfo);
                operationsThisFrame++;
            }

            // Process loads
            for (int i = 0; i < scenesToLoad.Length; i++)
            {
                if (operationsThisFrame >= maxOperationsPerFrame) break;
                
                var scene = scenesToLoad[i];
                
                if (!state.EntityManager.Exists(scene) || !state.EntityManager.HasComponent<LevelInfo>(scene))
                    continue;
                
                var levelInfo = state.EntityManager.GetComponentData<LevelInfo>(scene);
                if (levelInfo.runtimeEntity != Entity.Null || !levelInfo.sceneReference.IsReferenceValid) continue;
                
                levelInfo.runtimeEntity = SceneSystem.LoadSceneAsync(
                    state.WorldUnmanaged,
                    levelInfo.sceneReference);
                    
                state.EntityManager.SetComponentData(scene, levelInfo);
                operationsThisFrame++;
            }

            // Process preloads
            for (int i = 0; i < scenesToPreload.Length; i++)
            {
                if (operationsThisFrame >= maxOperationsPerFrame) break;
                
                var scene = scenesToPreload[i];
                
                if (!state.EntityManager.Exists(scene) || !state.EntityManager.HasComponent<LevelInfo>(scene))
                    continue;
                
                var levelInfo = state.EntityManager.GetComponentData<LevelInfo>(scene);
                if (levelInfo.runtimeEntity != Entity.Null || !levelInfo.sceneReference.IsReferenceValid) continue;
                
                levelInfo.runtimeEntity = SceneSystem.LoadSceneAsync(
                    state.WorldUnmanaged,
                    levelInfo.sceneReference);
                    
                state.EntityManager.SetComponentData(scene, levelInfo);
                operationsThisFrame++;
            }
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            // Dispose all persistent collections
            if (_activeScenes.IsCreated) _activeScenes.Dispose();
            if (_preloadingScenes.IsCreated) _preloadingScenes.Dispose();
            if (_volumeSets.IsCreated) _volumeSets.Dispose();
            if (_relevantTransforms.IsCreated) _relevantTransforms.Dispose();
            if (_containingVolumes.IsCreated) _containingVolumes.Dispose();
            if (_nearbyVolumes.IsCreated) _nearbyVolumes.Dispose();
            if (_newActiveScenes.IsCreated) _newActiveScenes.Dispose();
            if (_newPreloadingScenes.IsCreated) _newPreloadingScenes.Dispose();
            if (_scenesToUnload.IsCreated) _scenesToUnload.Dispose();
            if (_scenesToLoad.IsCreated) _scenesToLoad.Dispose();
            if (_scenesToPreload.IsCreated) _scenesToPreload.Dispose();
        }
    }
}