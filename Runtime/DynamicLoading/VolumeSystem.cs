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
        private const int DEFAULT_MAX_OPERATIONS_PER_FRAME = 10;
        private const bool IS_DEBUG = false;

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
                return distance.x <= range.x && 
                       distance.y <= range.y && 
                       distance.z <= range.z;
            }

            private bool IsPositionNearVolume(float3 position, float3 volumePosition, float3 range)
            {
                var expandedRangeHorizontal = new float2(
                    range.x * PreloadHorizontalMultiplier,
                    range.z * PreloadHorizontalMultiplier);
                var expandedRangeVertical = range.y * PreloadVerticalMultiplier;
    
                var distance = math.abs(position - volumePosition);
                bool isNearHorizontally = distance.x <= expandedRangeHorizontal.x && 
                                          distance.z <= expandedRangeHorizontal.y;
                bool isNearVertically = distance.y <= expandedRangeVertical;
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
                
                // Find scenes to unload from currently active scenes
                foreach (var scene in CurrentActiveScenes)
                {
                    // Only unload if it's not in either new set
                    if (!NewActiveScenes.Contains(scene) && !NewPreloadingScenes.Contains(scene))
                    {
                        ScenesToUnload.Add(scene);
                    }
                }
                
                // Find scenes to unload from currently preloading scenes
                foreach (var scene in CurrentPreloadingScenes)
                {
                    // Only unload if it's not in either new set
                    if (!NewActiveScenes.Contains(scene) && !NewPreloadingScenes.Contains(scene))
                    {
                        ScenesToUnload.Add(scene);
                    }
                }

                // Find scenes to load (from new active scenes that aren't already active)
                foreach (var scene in NewActiveScenes)
                {
                    if (!CurrentActiveScenes.Contains(scene))
                    {
                        // Check if it was preloading - if so, we don't need to add it to ScenesToLoad
                        // since it's already loaded, just not active
                        if (!CurrentPreloadingScenes.Contains(scene))
                        {
                            ScenesToLoad.Add(scene);
                        }
                    }
                }

                // Find scenes to preload (from new preloading scenes that aren't loaded at all)
                foreach (var scene in NewPreloadingScenes)
                {
                    // Only preload if not already loaded or preloaded
                    if (!CurrentActiveScenes.Contains(scene) && !CurrentPreloadingScenes.Contains(scene))
                    {
                        ScenesToPreload.Add(scene);
                    }
                }
            }
        }

        [BurstDiscard]  // Add this to disable Burst compilation for OnUpdate
        public void OnUpdate(ref SystemState state)
        {
            // Get config values with fallback to defaults
            float preloadHorizontalMultiplier = DEFAULT_PRELOAD_HORIZONTAL_MULTIPLIER;
            float preloadVerticalMultiplier = DEFAULT_PRELOAD_VERTICAL_MULTIPLIER;
            int maxOperationsPerFrame = DEFAULT_MAX_OPERATIONS_PER_FRAME;
            bool isDebug = IS_DEBUG;

            if (_configQuery.HasSingleton<VolumeSystemConfig>())
            {
                var config = SystemAPI.GetSingleton<VolumeSystemConfig>();
                preloadHorizontalMultiplier = config.PreloadHorizontalMultiplier;
                preloadVerticalMultiplier = config.PreloadVerticalMultiplier;
                maxOperationsPerFrame = config.MaxOperationsPerFrame;
                isDebug = config.IsDebug;
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

            #if UNITY_EDITOR
            if (isDebug)
            {
                UnityEngine.Debug.Log($"After jobs - Scenes to load: {_scenesToLoad.Length}");
                if (_scenesToLoad.Length > 0)
                {
                    for (int i = 0; i < _scenesToLoad.Length; i++)
                    {
                        UnityEngine.Debug.Log($"  Scene to load: {_scenesToLoad[i].Index}");
                    }
                }
            }
            #endif

            // Important: Don't clear _scenesToLoad, _scenesToUnload, or _scenesToPreload here
            // Process scene operations without clearing the lists first
            ProcessSceneOperations(ref state, _scenesToUnload, _scenesToLoad, _scenesToPreload, maxOperationsPerFrame, isDebug);

            // Update tracking collections AFTER processing operations
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
        
        private void ProcessSceneOperations(ref SystemState state, NativeList<Entity> scenesToUnload, NativeList<Entity> scenesToLoad, NativeList<Entity> scenesToPreload, int maxOperationsPerFrame, bool isDebug)
        {
            #if UNITY_EDITOR
            if (isDebug) UnityEngine.Debug.Log($"Process operations - Unload: {scenesToUnload.Length}, Load: {scenesToLoad.Length}, Preload: {scenesToPreload.Length}");
            #endif
            
            // Force load all scenes immediately, regardless of operation limits
            foreach (var scene in scenesToLoad)
            {
                if (!state.EntityManager.Exists(scene) || !state.EntityManager.HasComponent<LevelInfo>(scene))
                    continue;
                
                var levelInfo = state.EntityManager.GetComponentData<LevelInfo>(scene);
                
                // Skip if already loaded
                if (levelInfo.runtimeEntity != Entity.Null)
                    continue;
                    
                try
                {
                    #if UNITY_EDITOR
                    if (isDebug) UnityEngine.Debug.Log($"Attempting to load scene: {scene.Index}");
                    #endif
                    
                    // Load the scene and store the runtime entity
                    var runtimeEntity = SceneSystem.LoadSceneAsync(
                        state.WorldUnmanaged,
                        levelInfo.sceneReference);
                        
                    #if UNITY_EDITOR
                    if (isDebug)UnityEngine.Debug.Log($"Load result for scene {scene.Index}: {runtimeEntity.Index}");
                    #endif
                    
                    // Set the runtime entity on the level info
                    levelInfo.runtimeEntity = runtimeEntity;
                    state.EntityManager.SetComponentData(scene, levelInfo);
                }
                catch (System.Exception e)
                {
                    #if UNITY_EDITOR
                    if(isDebug) UnityEngine.Debug.LogError($"Exception loading scene {scene.Index}: {e.Message}");
                    #endif
                }
            }
            
            // Handle unloads after all loads are processed
            foreach (var scene in scenesToUnload)
            {
                if (!state.EntityManager.Exists(scene) || !state.EntityManager.HasComponent<LevelInfo>(scene)) 
                    continue;
                
                var levelInfo = state.EntityManager.GetComponentData<LevelInfo>(scene);
                if (levelInfo.runtimeEntity == Entity.Null) continue;
                
                #if UNITY_EDITOR
                if (isDebug) UnityEngine.Debug.Log($"Unloading scene: {scene.Index}");
                #endif
                
                SceneSystem.UnloadScene(
                    state.WorldUnmanaged,
                    levelInfo.runtimeEntity,
                    SceneSystem.UnloadParameters.DestroyMetaEntities);
                        
                levelInfo.runtimeEntity = Entity.Null;
                state.EntityManager.SetComponentData(scene, levelInfo);
            }
            
            // Handle preloads after loads and unloads
            foreach (var scene in scenesToPreload)
            {
                if (!state.EntityManager.Exists(scene) || !state.EntityManager.HasComponent<LevelInfo>(scene))
                    continue;
                
                var levelInfo = state.EntityManager.GetComponentData<LevelInfo>(scene);
                
                // Skip if already loaded
                if (levelInfo.runtimeEntity != Entity.Null)
                    continue;
                    
                try
                {
                    #if UNITY_EDITOR
                    if (isDebug) UnityEngine.Debug.Log($"Attempting to preload scene: {scene.Index}");
                    #endif
                    
                    // Preload the scene
                    var runtimeEntity = SceneSystem.LoadSceneAsync(
                        state.WorldUnmanaged,
                        levelInfo.sceneReference);
                        
                    #if UNITY_EDITOR
                    if (isDebug) UnityEngine.Debug.Log($"Preload result for scene {scene.Index}: {runtimeEntity.Index}");
                    #endif
                    
                    // Store the runtime entity
                    levelInfo.runtimeEntity = runtimeEntity;
                    state.EntityManager.SetComponentData(scene, levelInfo);
                }
                catch (System.Exception e)
                {
                    #if UNITY_EDITOR
                    if (isDebug) UnityEngine.Debug.LogError($"Exception preloading scene {scene.Index}: {e.Message}");
                    #endif
                }
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