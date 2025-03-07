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
    [BurstCompile]
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
        
        private EntityTypeHandle _entityTypeHandle;

        private struct SceneOperation
        {
            public enum OperationType { Load, Unload, Preload }

            public Entity sceneEntity;
            public OperationType type;
        }

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _entityTypeHandle = state.GetEntityTypeHandle();
            
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

            int initialCapacity = 64; // Adjusted based on expected scene count
            _activeScenes = new NativeHashSet<Entity>(initialCapacity, Allocator.Persistent);
            _preloadingScenes = new NativeHashSet<Entity>(initialCapacity, Allocator.Persistent);
            _volumeSets = new NativeList<Entity>(initialCapacity, Allocator.Persistent);
            _relevantTransforms = new NativeList<LocalToWorld>(2, Allocator.Persistent); // Usually just 1 player
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

            [BurstCompile]
            private bool IsPositionInsideVolume(float3 position, float3 volumePosition, float3 range)
            {
                var distance = math.abs(position - volumePosition);

                if (distance.x > range.x) return false;
                if (distance.y > range.y) return false;
                if (distance.z > range.z) return false;

                return true;
            }

            [BurstCompile]
            private bool IsPositionNearVolume(float3 position, float3 volumePosition, float3 range)
            {
                var expandedRangeHorizontal = new float2(
                    range.x * PreloadHorizontalMultiplier,
                    range.z * PreloadHorizontalMultiplier);
                var expandedRangeVertical = range.y * PreloadVerticalMultiplier;

                var distance = math.abs(position - volumePosition);

                if (distance.x > expandedRangeHorizontal.x) return false;
                if (distance.z > expandedRangeHorizontal.y) return false;
                if (distance.y > expandedRangeVertical) return false;

                return true;
            }

            [BurstCompile]
            public void Execute()
            {
                ContainingVolumes.Clear();
                NearbyVolumes.Clear();

                var playerPos = PlayerPosition;

                foreach (var volumeSetEntity in VolumeSets)
                {
                    if (!VolumeBufferLookup.HasBuffer(volumeSetEntity)) continue;
                    var volumeBuffer = VolumeBufferLookup[volumeSetEntity];

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

            [BurstCompile]
            public void Execute()
            {
                NewActiveScenes.Clear();
                NewPreloadingScenes.Clear();

                int volumeSetCount = VolumeSets.Length;

                for (int i = 0; i < volumeSetCount; i++)
                {
                    var volumeSetEntity = VolumeSets[i];
                    if (!VolumeBufferLookup.HasBuffer(volumeSetEntity)) continue;

                    var volumeBuffer = VolumeBufferLookup[volumeSetEntity];
                    bool isActive = false;
                    bool shouldPreload = false;

                    int bufferLength = volumeBuffer.Length;

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

            [BurstCompile]
            public void Execute()
            {
                ScenesToUnload.Clear();
                ScenesToLoad.Clear();
                ScenesToPreload.Clear();

                using (var iterator = CurrentActiveScenes.GetEnumerator())
                {
                    while (iterator.MoveNext())
                    {
                        Entity scene = iterator.Current;
                        if (!NewActiveScenes.Contains(scene) && !NewPreloadingScenes.Contains(scene))
                        {
                            ScenesToUnload.Add(scene);
                        }
                    }
                }

                using (var iterator = CurrentPreloadingScenes.GetEnumerator())
                {
                    while (iterator.MoveNext())
                    {
                        Entity scene = iterator.Current;
                        if (!NewActiveScenes.Contains(scene) && !NewPreloadingScenes.Contains(scene))
                        {
                            ScenesToUnload.Add(scene);
                        }
                    }
                }

                using (var iterator = NewActiveScenes.GetEnumerator())
                {
                    while (iterator.MoveNext())
                    {
                        Entity scene = iterator.Current;
                        if (!CurrentActiveScenes.Contains(scene))
                        {
                            if (!CurrentPreloadingScenes.Contains(scene))
                            {
                                ScenesToLoad.Add(scene);
                            }
                        }
                    }
                }

                using (var iterator = NewPreloadingScenes.GetEnumerator())
                {
                    while (iterator.MoveNext())
                    {
                        Entity scene = iterator.Current;
                        if (!CurrentActiveScenes.Contains(scene) && !CurrentPreloadingScenes.Contains(scene))
                        {
                            ScenesToPreload.Add(scene);
                        }
                    }
                }
            }
        }

        [BurstCompile]
        private void BatchClearCollections()
        {
            _relevantTransforms.Clear();
            _volumeSets.Clear();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _entityTypeHandle.Update(ref state);
            var entityTypeHandle = _entityTypeHandle;

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

            BatchClearCollections();

            if (!_relevantQuery.IsEmpty)
            {
                _relevantTransforms.AddRange(
                    _relevantQuery.ToComponentDataArray<LocalToWorld>(Allocator.Temp));
            }

            if (!_volumeSetQuery.IsEmpty)
            {
                var chunks = _volumeSetQuery.ToArchetypeChunkArray(Allocator.Temp);
                for (int i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    var entities = chunk.GetNativeArray(entityTypeHandle);
                    _volumeSets.AddRange(entities);
                }
            }

            if (_relevantTransforms.Length == 0)
                return;

            var playerPosition = _relevantTransforms[0].Position;

            var localToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true);
            var volumeLookup = SystemAPI.GetComponentLookup<Volume>(true);
            var volumeBufferLookup = SystemAPI.GetBufferLookup<VolumeBuffer>(true);

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

            var sceneFilterJob = new SceneFilterJob
            {
                VolumeSets = _volumeSets.AsArray(),
                ContainingVolumes = _containingVolumes,
                NearbyVolumes = _nearbyVolumes,
                VolumeBufferLookup = volumeBufferLookup,
                NewActiveScenes = _newActiveScenes,
                NewPreloadingScenes = _newPreloadingScenes
            };

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

            var volumeCheckHandle = volumeCheckJob.Schedule();
            var sceneFilterHandle = sceneFilterJob.Schedule(volumeCheckHandle);
            var sceneChangeHandle = sceneChangeJob.Schedule(sceneFilterHandle);

            sceneChangeHandle.Complete();

            QueueSceneOperations(ref state);
            ProcessPendingOperations(ref state, maxOperationsPerFrame, isDebug);
            UpdateActiveSceneSets();
        }

        [BurstCompile]
        private void QueueSceneOperations(ref SystemState state)
        {
            NativeHashMap<Entity, bool> entityExistsCache = new NativeHashMap<Entity, bool>(
                _scenesToLoad.Length + _scenesToUnload.Length + _scenesToPreload.Length,
                Allocator.Temp);

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

        [BurstCompile]
        private void UpdateActiveSceneSets()
        {
            _activeScenes.Clear();
            _preloadingScenes.Clear();

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

        [BurstDiscard]
        private void ProcessPendingOperations(ref SystemState state, int maxOperationsPerFrame, bool isDebug)
        {
            int operationsProcessed = 0;
            int operationsToProcess = math.min(_pendingOperations.Count, maxOperationsPerFrame);

            if (operationsToProcess == 0) return;

            var tempOperations = new NativeArray<SceneOperation>(operationsToProcess, Allocator.Temp);
            var tempEntities = new NativeArray<Entity>(operationsToProcess, Allocator.Temp);
            var tempTypes = new NativeArray<SceneOperation.OperationType>(operationsToProcess, Allocator.Temp);
            var tempLevelInfos = new NativeArray<LevelInfo>(operationsToProcess, Allocator.Temp);

            for (int i = 0; i < operationsToProcess; i++)
            {
                if (_pendingOperations.TryDequeue(out var operation))
                {
                    tempOperations[i] = operation;
                    tempEntities[i] = operation.sceneEntity;
                    tempTypes[i] = operation.type;
                }
            }

            for (int i = 0; i < tempOperations.Length; i++)
            {
                var entity = tempEntities[i];

                if (!state.EntityManager.Exists(entity)) continue;

                if (state.EntityManager.HasComponent<LevelInfo>(entity))
                {
                    tempLevelInfos[i] = state.EntityManager.GetComponentData<LevelInfo>(entity);
                }
            }

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
                        if (levelInfo.runtimeEntity != Entity.Null)
                            continue;

                        try
                        {
                            var runtimeEntity = SceneSystem.LoadSceneAsync(
                                state.WorldUnmanaged,
                                levelInfo.sceneReference);

                            levelInfo.runtimeEntity = runtimeEntity;
                            state.EntityManager.SetComponentData(sceneEntity, levelInfo);
                        }
                        catch (System.Exception e)
                        {
                        }
                        break;

                    case SceneOperation.OperationType.Unload:
                        // Skip if not loaded
                        if (levelInfo.runtimeEntity == Entity.Null)
                            continue;

                        #if UNITY_EDITOR
                        if (isDebug && operationsProcessed == 0)
                        {
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

            tempOperations.Dispose();
            tempEntities.Dispose();
            tempTypes.Dispose();
            tempLevelInfos.Dispose();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
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