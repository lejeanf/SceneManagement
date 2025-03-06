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
        
        // Queue to throttle operations over multiple frames
        private NativeQueue<SceneOperation> _pendingOperations;
        
        // Default values if no config is present
        private const float DEFAULT_PRELOAD_HORIZONTAL_MULTIPLIER = 1.0f;
        private const float DEFAULT_PRELOAD_VERTICAL_MULTIPLIER = 1.0f;
        private const int DEFAULT_MAX_OPERATIONS_PER_FRAME = 3;
        private const bool IS_DEBUG = false;
        
        // Struct to track pending operations
        private struct SceneOperation
        {
            public enum OperationType { Load, Unload, Preload }
            
            public Entity sceneEntity;
            public OperationType type;
        }

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
            
            // Initialize persistent collections with appropriate initial capacities
            int initialCapacity = 16; // Adjusted based on expected scene count
            _activeScenes = new NativeHashSet<Entity>(initialCapacity, Allocator.Persistent);
            _preloadingScenes = new NativeHashSet<Entity>(initialCapacity, Allocator.Persistent);
            _volumeSets = new NativeList<Entity>(initialCapacity, Allocator.Persistent);
            _relevantTransforms = new NativeList<LocalToWorld>(2, Allocator.Persistent); // Usually just 1-2 players
            _containingVolumes = new NativeHashSet<Entity>(initialCapacity, Allocator.Persistent);
            _nearbyVolumes = new NativeHashSet<Entity>(initialCapacity * 2, Allocator.Persistent);
            _newActiveScenes = new NativeHashSet<Entity>(initialCapacity, Allocator.Persistent);
            _newPreloadingScenes = new NativeHashSet<Entity>(initialCapacity, Allocator.Persistent);
            _scenesToUnload = new NativeList<Entity>(initialCapacity, Allocator.Persistent);
            _scenesToLoad = new NativeList<Entity>(initialCapacity, Allocator.Persistent);
            _scenesToPreload = new NativeList<Entity>(initialCapacity, Allocator.Persistent);
            _pendingOperations = new NativeQueue<SceneOperation>(Allocator.Persistent);
                
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

            // Optimized with early returns for better branch prediction
            private bool IsPositionInsideVolume(float3 position, float3 volumePosition, float3 range)
            {
                var distance = math.abs(position - volumePosition);
                
                // Early return pattern improves branch prediction
                if (distance.x > range.x) return false;
                if (distance.y > range.y) return false;
                if (distance.z > range.z) return false;
                
                return true;
            }

            // Optimized with flattened conditionals
            private bool IsPositionNearVolume(float3 position, float3 volumePosition, float3 range)
            {
                var expandedRangeHorizontal = new float2(
                    range.x * PreloadHorizontalMultiplier,
                    range.z * PreloadHorizontalMultiplier);
                var expandedRangeVertical = range.y * PreloadVerticalMultiplier;
    
                var distance = math.abs(position - volumePosition);
                
                // Flattened conditionals for better CPU pipelining
                if (distance.x > expandedRangeHorizontal.x) return false;
                if (distance.z > expandedRangeHorizontal.y) return false;
                if (distance.y > expandedRangeVertical) return false;
                
                return true;
            }

            public void Execute()
            {
                // Clear collections at the start of job
                ContainingVolumes.Clear();
                NearbyVolumes.Clear();
                
                // Local variables to minimize property access
                var playerPos = PlayerPosition;
                
                foreach (var volumeSetEntity in VolumeSets)
                {
                    if (!VolumeBufferLookup.HasBuffer(volumeSetEntity)) continue;
                    var volumeBuffer = VolumeBufferLookup[volumeSetEntity];
                    
                    // Pre-fetch buffer length to avoid repeated property access
                    int bufferLength = volumeBuffer.Length;
                    for (int i = 0; i < bufferLength; i++)
                    {
                        var volume = volumeBuffer[i];
                        var volumeEntity = volume.volumeEntity;
                        
                        if (!LocalToWorldLookup.HasComponent(volumeEntity) || 
                            !VolumeLookup.HasComponent(volumeEntity)) continue;

                        var volumeTransform = LocalToWorldLookup[volumeEntity];
                        var volumeData = VolumeLookup[volumeEntity];
                        
                        var range = volumeData.Scale / 2f;
                        var volumePos = volumeTransform.Position;
                        
                        if (IsPositionInsideVolume(playerPos, volumePos, range))
                        {
                            ContainingVolumes.Add(volumeEntity);
                        }
                        else if (IsPositionNearVolume(playerPos, volumePos, range))
                        {
                            NearbyVolumes.Add(volumeEntity);
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
                
                // Pre-fetch count for branch prediction
                int volumeSetCount = VolumeSets.Length;
                
                for (int i = 0; i < volumeSetCount; i++)
                {
                    var volumeSetEntity = VolumeSets[i];
                    if (!VolumeBufferLookup.HasBuffer(volumeSetEntity)) continue;
                    
                    var volumeBuffer = VolumeBufferLookup[volumeSetEntity];
                    bool isActive = false;
                    bool shouldPreload = false;
                    
                    // Pre-fetch buffer length to avoid repeated property access
                    int bufferLength = volumeBuffer.Length;
                    
                    // Check for active volumes first (early exit if found)
                    for (int j = 0; j < bufferLength; j++)
                    {
                        if (ContainingVolumes.Contains(volumeBuffer[j].volumeEntity))
                        {
                            isActive = true;
                            break;
                        }
                    }
                    
                    if (isActive)
                    {
                        NewActiveScenes.Add(volumeSetEntity);
                        continue; // Skip preload check if already active
                    }
                    
                    // Only check for preloading if not active
                    for (int j = 0; j < bufferLength; j++)
                    {
                        if (NearbyVolumes.Contains(volumeBuffer[j].volumeEntity))
                        {
                            shouldPreload = true;
                            break;
                        }
                    }
                    
                    if (shouldPreload)
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
                
                // Use NativeHashSet APIs for faster iteration
                // Find scenes to unload from currently active scenes
                using (var iterator = CurrentActiveScenes.GetEnumerator())
                {
                    while (iterator.MoveNext())
                    {
                        Entity scene = iterator.Current;
                        // Only unload if it's not in either new set
                        if (!NewActiveScenes.Contains(scene) && !NewPreloadingScenes.Contains(scene))
                        {
                            ScenesToUnload.Add(scene);
                        }
                    }
                }
                
                // Find scenes to unload from currently preloading scenes
                using (var iterator = CurrentPreloadingScenes.GetEnumerator())
                {
                    while (iterator.MoveNext())
                    {
                        Entity scene = iterator.Current;
                        // Only unload if it's not in either new set
                        if (!NewActiveScenes.Contains(scene) && !NewPreloadingScenes.Contains(scene))
                        {
                            ScenesToUnload.Add(scene);
                        }
                    }
                }

                // Find scenes to load (from new active scenes that aren't already active)
                using (var iterator = NewActiveScenes.GetEnumerator())
                {
                    while (iterator.MoveNext())
                    {
                        Entity scene = iterator.Current;
                        if (!CurrentActiveScenes.Contains(scene))
                        {
                            // Check if it was preloading - if so, we don't need to add it to ScenesToLoad
                            if (!CurrentPreloadingScenes.Contains(scene))
                            {
                                ScenesToLoad.Add(scene);
                            }
                        }
                    }
                }

                // Find scenes to preload (from new preloading scenes that aren't loaded at all)
                using (var iterator = NewPreloadingScenes.GetEnumerator())
                {
                    while (iterator.MoveNext())
                    {
                        Entity scene = iterator.Current;
                        // Only preload if not already loaded or preloaded
                        if (!CurrentActiveScenes.Contains(scene) && !CurrentPreloadingScenes.Contains(scene))
                        {
                            ScenesToPreload.Add(scene);
                        }
                    }
                }
            }
        }

        // Helper method to batch clear collections and avoid repeated operations
        private void BatchClearCollections()
        {
            _relevantTransforms.Clear();
            _volumeSets.Clear();
        }
        
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
            
            // Batch clear collections for better performance
            BatchClearCollections();
            
            // Get relevant transforms (player positions) directly into persistent collection
            if (!_relevantQuery.IsEmpty)
            {
                // Use direct ToComponentDataArray to avoid entity iteration
                _relevantTransforms.AddRange(
                    _relevantQuery.ToComponentDataArray<LocalToWorld>(Allocator.Temp));
            }
            
            // Get volume sets using chunk iteration for better performance
            if (!_volumeSetQuery.IsEmpty)
            {
                using var entities = _volumeSetQuery.ToEntityArray(Allocator.Temp);
                _volumeSets.AddRange(entities);
            }
            
            // Make sure we have at least one relevant transform (player)
            if (_relevantTransforms.Length == 0)
                return;
            
            var playerPosition = _relevantTransforms[0].Position;

            // Use cached lookup tables for better performance
            var localToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true);
            var volumeLookup = SystemAPI.GetComponentLookup<Volume>(true);
            var volumeBufferLookup = SystemAPI.GetBufferLookup<VolumeBuffer>(true);
            
            // Schedule volume check job with dependency tracking
            var volumeCheckJob = new VolumeCheckJob
            {
                PlayerPosition = playerPosition,
                VolumeSets = _volumeSets.AsArray(),
                LocalToWorldLookup = localToWorldLookup,
                VolumeLookup = volumeLookup,
                VolumeBufferLookup = volumeBufferLookup,
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
                VolumeBufferLookup = volumeBufferLookup,
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

            // Create dependency chain with better error handling
            var volumeCheckHandle = volumeCheckJob.Schedule();
            var sceneFilterHandle = sceneFilterJob.Schedule(volumeCheckHandle);
            var sceneChangeHandle = sceneChangeJob.Schedule(sceneFilterHandle);
            
            // Complete the job chain to get the results
            sceneChangeHandle.Complete();

            // Queue operations instead of executing immediately
            QueueSceneOperations(ref state);
            
            // Process a limited number of pending operations per frame
            ProcessPendingOperations(ref state, maxOperationsPerFrame, isDebug);
            
            // Update tracking collections AFTER processing operations
            UpdateActiveSceneSets();
        }

        private void QueueSceneOperations(ref SystemState state)
        {
            // Use EntityManager.Exists only once per entity where possible
            NativeHashMap<Entity, bool> entityExistsCache = new NativeHashMap<Entity, bool>(
                _scenesToLoad.Length + _scenesToUnload.Length + _scenesToPreload.Length, 
                Allocator.Temp);
            
            // Queue high priority load operations first
            foreach (var scene in _scenesToLoad)
            {
                if (!entityExistsCache.TryGetValue(scene, out bool exists))
                {
                    exists = state.EntityManager.Exists(scene);
                    entityExistsCache[scene] = exists;
                }
                
                if (!exists) continue;
                
                var op = new SceneOperation
                {
                    sceneEntity = scene,
                    type = SceneOperation.OperationType.Load
                };
                _pendingOperations.Enqueue(op);
            }
            
            // Queue unload operations next
            foreach (var scene in _scenesToUnload)
            {
                if (!entityExistsCache.TryGetValue(scene, out bool exists))
                {
                    exists = state.EntityManager.Exists(scene);
                    entityExistsCache[scene] = exists;
                }
                
                if (!exists) continue;
                
                var op = new SceneOperation
                {
                    sceneEntity = scene,
                    type = SceneOperation.OperationType.Unload
                };
                _pendingOperations.Enqueue(op);
            }
            
            // Queue preload operations last (lowest priority)
            foreach (var scene in _scenesToPreload)
            {
                if (!entityExistsCache.TryGetValue(scene, out bool exists))
                {
                    exists = state.EntityManager.Exists(scene);
                    entityExistsCache[scene] = exists;
                }
                
                if (!exists) continue;
                
                var op = new SceneOperation
                {
                    sceneEntity = scene,
                    type = SceneOperation.OperationType.Preload
                };
                _pendingOperations.Enqueue(op);
            }
        }

        private void UpdateActiveSceneSets()
        {
            // Use more efficient set operations
            _activeScenes.Clear();
            _preloadingScenes.Clear();
            
            // Copy sets over - more efficient than iterating
            using (var iterator = _newActiveScenes.GetEnumerator())
            {
                while (iterator.MoveNext())
                {
                    _activeScenes.Add(iterator.Current);
                }
            }
            
            using (var iterator = _newPreloadingScenes.GetEnumerator())
            {
                while (iterator.MoveNext())
                {
                    _preloadingScenes.Add(iterator.Current);
                }
            }
        }
        
        // Optimized to batch operations and reduce per-operation overhead
        [BurstDiscard]
        private void ProcessPendingOperations(ref SystemState state, int maxOperationsPerFrame, bool isDebug)
        {
            // Process only a limited number of operations per frame
            int operationsProcessed = 0;
            int operationsToProcess = math.min(_pendingOperations.Count, maxOperationsPerFrame);
            
            if (operationsToProcess == 0) return;
            
            // Cache component data to avoid repeated lookups
            var tempOperations = new NativeArray<SceneOperation>(operationsToProcess, Allocator.Temp);
            var tempEntities = new NativeArray<Entity>(operationsToProcess, Allocator.Temp);
            var tempTypes = new NativeArray<SceneOperation.OperationType>(operationsToProcess, Allocator.Temp);
            var tempLevelInfos = new NativeArray<LevelInfo>(operationsToProcess, Allocator.Temp);
            
            // Extract operations to process
            for (int i = 0; i < operationsToProcess; i++)
            {
                if (_pendingOperations.TryDequeue(out var operation))
                {
                    tempOperations[i] = operation;
                    tempEntities[i] = operation.sceneEntity;
                    tempTypes[i] = operation.type;
                }
            }
            
            // Batch check existence and get LevelInfo
            for (int i = 0; i < tempOperations.Length; i++)
            {
                var entity = tempEntities[i];
                
                if (!state.EntityManager.Exists(entity)) continue;
                
                if (state.EntityManager.HasComponent<LevelInfo>(entity))
                {
                    tempLevelInfos[i] = state.EntityManager.GetComponentData<LevelInfo>(entity);
                }
            }
            
            // Process operations in a single loop
            for (int i = 0; i < tempOperations.Length; i++)
            {
                Entity sceneEntity = tempEntities[i];
                
                if (!state.EntityManager.Exists(sceneEntity) || 
                    !state.EntityManager.HasComponent<LevelInfo>(sceneEntity))
                    continue;
                    
                var levelInfo = tempLevelInfos[i];
                var operationType = tempTypes[i];
                
                switch (operationType)
                {
                    case SceneOperation.OperationType.Load:
                    case SceneOperation.OperationType.Preload:
                        // Skip if already loaded
                        if (levelInfo.runtimeEntity != Entity.Null)
                            continue;
                            
                        try
                        {
                            #if UNITY_EDITOR
                            if (isDebug && operationsProcessed == 0) 
                            {
                                // Log only the first operation to reduce logging overhead
                                UnityEngine.Debug.Log($"Processing operation: {operationType} for scene {sceneEntity.Index}");
                            }
                            #endif
                            
                            // Load the scene and store the runtime entity
                            var runtimeEntity = SceneSystem.LoadSceneAsync(
                                state.WorldUnmanaged,
                                levelInfo.sceneReference);
                                
                            // Set the runtime entity on the level info
                            levelInfo.runtimeEntity = runtimeEntity;
                            state.EntityManager.SetComponentData(sceneEntity, levelInfo);
                        }
                        catch (System.Exception e)
                        {
                            #if UNITY_EDITOR
                            if (isDebug) UnityEngine.Debug.LogError($"Exception loading scene {sceneEntity.Index}: {e.Message}");
                            #endif
                        }
                        break;
                        
                    case SceneOperation.OperationType.Unload:
                        // Skip if not loaded
                        if (levelInfo.runtimeEntity == Entity.Null)
                            continue;
                            
                        #if UNITY_EDITOR
                        if (isDebug && operationsProcessed == 0)
                        {
                            // Log only the first operation to reduce logging overhead
                            UnityEngine.Debug.Log($"Processing operation: Unload for scene {sceneEntity.Index}");
                        }
                        #endif
                        
                        SceneSystem.UnloadScene(
                            state.WorldUnmanaged,
                            levelInfo.runtimeEntity,
                            SceneSystem.UnloadParameters.DestroyMetaEntities);
                                
                        levelInfo.runtimeEntity = Entity.Null;
                        state.EntityManager.SetComponentData(sceneEntity, levelInfo);
                        break;
                }
                
                operationsProcessed++;
            }
            
            // Dispose temporary collections
            tempOperations.Dispose();
            tempEntities.Dispose();
            tempTypes.Dispose();
            tempLevelInfos.Dispose();
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
            if (_pendingOperations.IsCreated) _pendingOperations.Dispose();
        }
    }
}