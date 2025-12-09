using Unity.Entities;
using Unity.Scenes;
using Unity.Collections;
using UnityEngine;
using jeanf.SceneManagement;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// ECS system that monitors subscene loading state for debugging purposes.
    /// Runs after VolumeSystem to capture scene load/unload requests.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(VolumeSystem))]
    public partial struct SceneLoadingDebugSystem : ISystem
    {
        private NativeHashMap<Entity, FixedString128Bytes> _trackedScenes;
        private NativeHashMap<Entity, SceneLoadingState> _sceneStates;
        private EntityQuery _levelInfoQuery;
        private EntityQuery _sectionQuery;

        private enum SceneLoadingState
        {
            Unloaded,
            Loading,
            Loaded
        }

        public void OnCreate(ref SystemState state)
        {
            _trackedScenes = new NativeHashMap<Entity, FixedString128Bytes>(100, Allocator.Persistent);
            _sceneStates = new NativeHashMap<Entity, SceneLoadingState>(100, Allocator.Persistent);

            _levelInfoQuery = SystemAPI.QueryBuilder()
                .WithAll<LevelInfo>()
                .Build();

            _sectionQuery = SystemAPI.QueryBuilder()
                .WithAll<SectionRange, SceneSectionData>()
                .Build();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_trackedScenes.IsCreated)
                _trackedScenes.Dispose();
            if (_sceneStates.IsCreated)
                _sceneStates.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker == null)
                return;

            // Track scenes with LevelInfo (dynamic volume-based loading)
            var levelEntities = _levelInfoQuery.ToEntityArray(Allocator.Temp);
            var levelInfos = _levelInfoQuery.ToComponentDataArray<LevelInfo>(Allocator.Temp);

            for (int i = 0; i < levelEntities.Length; i++)
            {
                var entity = levelEntities[i];
                var levelInfo = levelInfos[i];
                var sceneName = levelInfo.sceneReference.ToString();
                var isLoaded = levelInfo.runtimeEntity != Entity.Null;

                // Check if we're already tracking this scene
                if (!_trackedScenes.ContainsKey(entity))
                {
                    _trackedScenes.Add(entity, new FixedString128Bytes(sceneName));
                    _sceneStates.Add(entity, SceneLoadingState.Unloaded);
                }

                var currentState = _sceneStates[entity];
                var newState = isLoaded ? SceneLoadingState.Loaded : SceneLoadingState.Unloaded;

                // State changed
                if (currentState != newState)
                {
                    _sceneStates[entity] = newState;

                    // Track state changes (tracker is MonoBehaviour singleton, already on main thread)
                    if (newState == SceneLoadingState.Loaded)
                    {
                        // Scene just loaded
                        tracker.TrackSubsceneLoadStart(sceneName);
                        tracker.TrackSceneLoadComplete(sceneName);
                    }
                    else if (currentState == SceneLoadingState.Loaded && newState == SceneLoadingState.Unloaded)
                    {
                        // Scene unloading
                        tracker.TrackSceneUnloadStart(sceneName);
                        tracker.TrackSceneUnloadComplete(sceneName);
                    }
                }
            }

            levelEntities.Dispose();
            levelInfos.Dispose();

            // Track section loading (for SectionRangeSystem)
            var sectionEntities = _sectionQuery.ToEntityArray(Allocator.Temp);
            var sectionRanges = _sectionQuery.ToComponentDataArray<SectionRange>(Allocator.Temp);

            for (int i = 0; i < sectionEntities.Length; i++)
            {
                var entity = sectionEntities[i];
                var sectionRange = sectionRanges[i];

                // Create a readable name using entity index and position
                // Note: SceneSectionData can't be accessed due to Unity.Mathematics.Extensions dependency
                var sceneName = $"Section_{entity.Index}_Pos({sectionRange.Center.x:F0},{sectionRange.Center.z:F0})";
                var hasRequestSceneLoaded = state.EntityManager.HasComponent<RequestSceneLoaded>(entity);

                if (!_trackedScenes.ContainsKey(entity))
                {
                    _trackedScenes.Add(entity, new FixedString128Bytes(sceneName));
                    _sceneStates.Add(entity, SceneLoadingState.Unloaded);
                }

                var currentState = _sceneStates[entity];
                var newState = hasRequestSceneLoaded ? SceneLoadingState.Loading : SceneLoadingState.Unloaded;

                if (currentState != newState)
                {
                    _sceneStates[entity] = newState;

                    if (newState == SceneLoadingState.Loading)
                    {
                        tracker.TrackSectionLoadStart(sceneName);
                    }
                    else if (currentState == SceneLoadingState.Loading && newState == SceneLoadingState.Unloaded)
                    {
                        tracker.TrackSceneUnloadStart(sceneName);
                    }
                }
            }

            sectionEntities.Dispose();
            sectionRanges.Dispose();
        }
    }
}
