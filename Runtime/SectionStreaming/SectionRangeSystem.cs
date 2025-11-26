using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;
using Unity.Profiling;
using UnityEngine;
using jeanf.scenemanagement;

namespace jeanf.SceneManagement
{
    [UpdateInGroup(typeof(SceneSystemGroup))]
    partial struct SectionRangeSystem : ISystem
    {
        private NativeHashSet<Entity> _interceptedSections;
        private NativeHashSet<Entity> _sectionsToLoad;
        private EntityQuery _sectionQuery;
        private EntityQuery _playerQuery;
        private float3 _lastPlayerPosition;
        private bool _hasLastPosition;
        private int _frameCounter;
        private const float MOVEMENT_THRESHOLD_SQ = 1f;
        private const int CLEANUP_INTERVAL = 300;

        private static readonly ProfilerMarker s_OnUpdateMarker = new ProfilerMarker("SectionRangeSystem.OnUpdate");
        private static readonly ProfilerMarker s_EarlyExitCheckMarker = new ProfilerMarker("SectionRange.EarlyExitCheck");
        private static readonly ProfilerMarker s_DistanceCalculationMarker = new ProfilerMarker("SectionRange.DistanceCalculation");
        private static readonly ProfilerMarker s_InterceptMarker = new ProfilerMarker("SectionRange.Intercept");
        private static readonly ProfilerMarker s_LoadUnloadMarker = new ProfilerMarker("SectionRange.LoadUnload");
        private static readonly ProfilerMarker s_ECBPlaybackMarker = new ProfilerMarker("SectionRange.ECBPlayback");
        private static readonly ProfilerMarker s_CleanupMarker = new ProfilerMarker("SectionRange.Cleanup");

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SectionRange>();
            _interceptedSections = new NativeHashSet<Entity>(16, Allocator.Persistent);
            _sectionsToLoad = new NativeHashSet<Entity>(16, Allocator.Persistent);

            _sectionQuery = SystemAPI.QueryBuilder()
                .WithAll<SectionRange, SectionRangeData, SceneSectionData>()
                .Build();

            _playerQuery = SystemAPI.QueryBuilder()
                .WithAll<LocalTransform, Player>()
                .Build();

            _hasLastPosition = false;
            _frameCounter = 0;
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_interceptedSections.IsCreated)
            {
                _interceptedSections.Dispose();
            }

            if (_sectionsToLoad.IsCreated)
            {
                _sectionsToLoad.Dispose();
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            s_OnUpdateMarker.Begin();
            _frameCounter++;

            s_EarlyExitCheckMarker.Begin();
            bool hasPlayer = !_playerQuery.IsEmpty;
            float3 playerPosition = default;

            if (hasPlayer)
            {
                var playerTransforms = _playerQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                if (playerTransforms.Length > 0)
                {
                    playerPosition = playerTransforms[0].Position;
                }
                else
                {
                    hasPlayer = false;
                }
                playerTransforms.Dispose();
            }

            bool playerMoved = !_hasLastPosition ||
                (hasPlayer && math.lengthsq(playerPosition - _lastPlayerPosition) > MOVEMENT_THRESHOLD_SQ);

            int sectionCount = _sectionQuery.CalculateEntityCount();
            bool hasNewSections = sectionCount != _interceptedSections.Count;

            if (!hasPlayer && !hasNewSections)
            {
                s_EarlyExitCheckMarker.End();
                s_OnUpdateMarker.End();
                return;
            }

            if (hasPlayer && !playerMoved && !hasNewSections)
            {
                s_EarlyExitCheckMarker.End();
                s_OnUpdateMarker.End();
                return;
            }
            s_EarlyExitCheckMarker.End();

            if (hasPlayer && playerMoved)
            {
                _lastPlayerPosition = playerPosition;
                _hasLastPosition = true;
            }

            var sectionEntities = _sectionQuery.ToEntityArray(Allocator.Temp);
            var sectionRanges = _sectionQuery.ToComponentDataArray<SectionRange>(Allocator.Temp);
            var sectionRangeData = _sectionQuery.ToComponentDataArray<SectionRangeData>(Allocator.Temp);

            _sectionsToLoad.Clear();

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < sectionEntities.Length; i++)
            {
                var sectionEntity = sectionEntities[i];
                var sectionRange = sectionRanges[i];
                var rangeData = sectionRangeData[i];

                bool shouldBeLoaded = false;

                s_DistanceCalculationMarker.Begin();
                if (hasPlayer)
                {
                    float3 distance = playerPosition - sectionRange.Center;
                    distance.y = 0;
                    float distanceSq = math.lengthsq(distance);

                    shouldBeLoaded = distanceSq >= rangeData.MinDistanceSq && distanceSq < rangeData.MaxDistanceSq;

                    if (shouldBeLoaded)
                    {
                        _sectionsToLoad.Add(sectionEntity);
                    }
                }
                s_DistanceCalculationMarker.End();

                var sectionState = SceneSystem.GetSectionStreamingState(state.WorldUnmanaged, sectionEntity);
                bool hasRequestSceneLoaded = state.EntityManager.HasComponent<RequestSceneLoaded>(sectionEntity);

                bool isNewSection = !_interceptedSections.Contains(sectionEntity);

                if (isNewSection)
                {
                    s_InterceptMarker.Begin();
                    if (!shouldBeLoaded)
                    {
                        if (hasRequestSceneLoaded)
                        {
                            ecb.RemoveComponent<RequestSceneLoaded>(sectionEntity);
                        }

                        if (sectionState == SceneSystem.SectionStreamingState.Loading ||
                            sectionState == SceneSystem.SectionStreamingState.Loaded)
                        {
                            SceneSystem.UnloadScene(state.WorldUnmanaged, sectionEntity, SceneSystem.UnloadParameters.Default);
                        }
                    }

                    _interceptedSections.Add(sectionEntity);
                    s_InterceptMarker.End();
                }
                else if (hasPlayer && playerMoved)
                {
                    s_LoadUnloadMarker.Begin();
                    if (shouldBeLoaded)
                    {
                        if (!hasRequestSceneLoaded)
                        {
                            ecb.AddComponent<RequestSceneLoaded>(sectionEntity);
                        }
                    }
                    else
                    {
                        if (_sectionsToLoad.Count > 0)
                        {
                            if (hasRequestSceneLoaded)
                            {
                                ecb.RemoveComponent<RequestSceneLoaded>(sectionEntity);
                            }

                            if (sectionState == SceneSystem.SectionStreamingState.Loaded)
                            {
                                SceneSystem.UnloadScene(state.WorldUnmanaged, sectionEntity, SceneSystem.UnloadParameters.Default);
                            }
                        }
                    }
                    s_LoadUnloadMarker.End();
                }

#if UNITY_EDITOR
                if (hasPlayer)
                {
                    DrawSectionRangeDebug(sectionRange.Center, rangeData.MinDistance, rangeData.MaxDistance,
                        shouldBeLoaded, rangeData.Level);
                }
#endif
            }

            s_ECBPlaybackMarker.Begin();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            s_ECBPlaybackMarker.End();

            sectionEntities.Dispose();
            sectionRanges.Dispose();
            sectionRangeData.Dispose();

            if (_frameCounter % CLEANUP_INTERVAL == 0)
            {
                s_CleanupMarker.Begin();
                CleanupInterceptedSections(ref state);
                s_CleanupMarker.End();
            }

            s_OnUpdateMarker.End();
        }

        [BurstCompile]
        private void CleanupInterceptedSections(ref SystemState state)
        {
            var toRemove = new NativeList<Entity>(Allocator.Temp);

            var enumerator = _interceptedSections.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (!state.EntityManager.Exists(enumerator.Current))
                {
                    toRemove.Add(enumerator.Current);
                }
            }
            enumerator.Dispose();

            for (int i = 0; i < toRemove.Length; i++)
            {
                _interceptedSections.Remove(toRemove[i]);
            }

            toRemove.Dispose();
        }

#if UNITY_EDITOR
        private static void DrawSectionRangeDebug(float3 center, float minDistance, float maxDistance, bool isActive, int level)
        {
            Color baseColor = isActive ? new Color(0f, 0.8f, 0f) : new Color(0.8f, 0f, 0f);
            float brightness = 1f - (level * 0.15f);
            Color color = baseColor * brightness;

            float3 offset = new float3(0f, 0.2f + level * 0.1f, 0f);

            if (minDistance > 0.01f)
            {
                DrawCircleXZ(center + offset, minDistance, color * 0.5f);
            }

            DrawCircleXZ(center + offset, maxDistance, color);
        }

        private static void DrawCircleXZ(float3 position, float radius, Color color, int segments = 32)
        {
            float angleStep = (math.PI * 2f) / segments;

            for (int i = 0; i < segments; i++)
            {
                float angle1 = i * angleStep;
                float angle2 = (i + 1) * angleStep;

                float3 point1 = new float3(
                    math.sin(angle1) * radius,
                    0f,
                    math.cos(angle1) * radius
                ) + position;

                float3 point2 = new float3(
                    math.sin(angle2) * radius,
                    0f,
                    math.cos(angle2) * radius
                ) + position;

                Debug.DrawLine(point1, point2, color);
            }
        }
#endif
    }
}
