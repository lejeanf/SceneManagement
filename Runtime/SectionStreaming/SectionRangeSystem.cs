using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;
using UnityEngine;
using jeanf.scenemanagement;

namespace jeanf.SceneManagement
{
    [UpdateInGroup(typeof(SceneSystemGroup))]
    partial struct SectionRangeSystem : ISystem
    {
        private NativeHashSet<Entity> _interceptedSections;
        private EntityQuery _sectionQuery;
        private EntityQuery _playerQuery;
        private float3 _lastPlayerPosition;
        private bool _hasLastPosition;
        private const float MOVEMENT_THRESHOLD = 1f;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SectionRange>();
            _interceptedSections = new NativeHashSet<Entity>(16, Allocator.Persistent);

            _sectionQuery = SystemAPI.QueryBuilder()
                .WithAll<SectionRange, SectionRangeData, SceneSectionData>()
                .Build();

            _playerQuery = SystemAPI.QueryBuilder()
                .WithAll<LocalTransform, Player>()
                .Build();

            _hasLastPosition = false;
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_interceptedSections.IsCreated)
            {
                _interceptedSections.Dispose();
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sectionEntities = _sectionQuery.ToEntityArray(Allocator.Temp);

            if (sectionEntities.Length == 0)
            {
                sectionEntities.Dispose();
                return;
            }

            var sectionRanges = _sectionQuery.ToComponentDataArray<SectionRange>(Allocator.Temp);
            var sectionRangeData = _sectionQuery.ToComponentDataArray<SectionRangeData>(Allocator.Temp);

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

            bool playerMoved = !_hasLastPosition || (hasPlayer && math.distance(playerPosition, _lastPlayerPosition) > MOVEMENT_THRESHOLD);

            if (hasPlayer && playerMoved)
            {
                _lastPlayerPosition = playerPosition;
                _hasLastPosition = true;
            }

            NativeHashSet<Entity> sectionsToLoad = new NativeHashSet<Entity>(sectionEntities.Length, Allocator.Temp);

            if (hasPlayer)
            {
                for (int i = 0; i < sectionEntities.Length; i++)
                {
                    var sectionRange = sectionRanges[i];
                    var rangeData = sectionRangeData[i];

                    float3 distance = playerPosition - sectionRange.Center;
                    distance.y = 0;
                    float distanceLength = math.length(distance);

                    bool inRange = distanceLength >= rangeData.MinDistance && distanceLength < rangeData.MaxDistance;

                    if (inRange)
                    {
                        sectionsToLoad.Add(sectionEntities[i]);
                    }
                }
            }

            for (int i = 0; i < sectionEntities.Length; i++)
            {
                var sectionEntity = sectionEntities[i];

                if (!state.EntityManager.Exists(sectionEntity))
                {
                    continue;
                }

                var rangeData = sectionRangeData[i];
                bool shouldBeLoaded = sectionsToLoad.Contains(sectionEntity);

                var sectionState = SceneSystem.GetSectionStreamingState(state.WorldUnmanaged, sectionEntity);
                bool hasRequestSceneLoaded = state.EntityManager.HasComponent<RequestSceneLoaded>(sectionEntity);

                bool isNewSection = !_interceptedSections.Contains(sectionEntity);

                if (isNewSection)
                {
                    if (!shouldBeLoaded)
                    {
                        if (hasRequestSceneLoaded)
                        {
                            state.EntityManager.RemoveComponent<RequestSceneLoaded>(sectionEntity);
                        }

                        if (sectionState == SceneSystem.SectionStreamingState.Loading ||
                            sectionState == SceneSystem.SectionStreamingState.Loaded)
                        {
                            SceneSystem.UnloadScene(state.WorldUnmanaged, sectionEntity, SceneSystem.UnloadParameters.Default);
                        }
                    }

                    _interceptedSections.Add(sectionEntity);
                }
                else if (hasPlayer && playerMoved)
                {
                    if (shouldBeLoaded)
                    {
                        if (sectionState == SceneSystem.SectionStreamingState.Unloaded && !hasRequestSceneLoaded)
                        {
                            state.EntityManager.AddComponent<RequestSceneLoaded>(sectionEntity);
                        }
                        else if (!hasRequestSceneLoaded)
                        {
                            state.EntityManager.AddComponent<RequestSceneLoaded>(sectionEntity);
                        }
                    }
                    else
                    {
                        if (sectionsToLoad.Count > 0)
                        {
                            if (hasRequestSceneLoaded)
                            {
                                state.EntityManager.RemoveComponent<RequestSceneLoaded>(sectionEntity);
                            }

                            if (sectionState == SceneSystem.SectionStreamingState.Loaded)
                            {
                                SceneSystem.UnloadScene(state.WorldUnmanaged, sectionEntity, SceneSystem.UnloadParameters.Default);
                            }
                        }
                    }
                }

#if UNITY_EDITOR
                if (hasPlayer)
                {
                    var sectionRange = sectionRanges[i];
                    DrawSectionRangeDebug(sectionRange.Center, rangeData.MinDistance, rangeData.MaxDistance,
                        shouldBeLoaded, rangeData.Level);
                }
#endif
            }

            sectionEntities.Dispose();
            sectionRanges.Dispose();
            sectionRangeData.Dispose();
            sectionsToLoad.Dispose();
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
