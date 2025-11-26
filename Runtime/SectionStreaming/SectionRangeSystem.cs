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
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SectionRange>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            NativeHashSet<Entity> sectionsToLoad = new NativeHashSet<Entity>(16, Allocator.Temp);

            var sectionQuery = SystemAPI.QueryBuilder()
                .WithAll<SectionRange, SectionRangeData, SceneSectionData>()
                .Build();

            var sectionEntities = sectionQuery.ToEntityArray(Allocator.Temp);
            var sectionRanges = sectionQuery.ToComponentDataArray<SectionRange>(Allocator.Temp);
            var sectionRangeData = sectionQuery.ToComponentDataArray<SectionRangeData>(Allocator.Temp);

            int playersProcessed = 0;
            foreach (var transform in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<Player>())
            {
                playersProcessed++;
                float3 entityPosition = transform.ValueRO.Position;

                for (int i = 0; i < sectionEntities.Length; i++)
                {
                    var sectionRange = sectionRanges[i];
                    var rangeData = sectionRangeData[i];

                    float3 distance = entityPosition - sectionRange.Center;
                    distance.y = 0;
                    float distanceLength = math.length(distance);

                    bool inRange = distanceLength >= rangeData.MinDistance && distanceLength < rangeData.MaxDistance;

                    if (inRange)
                    {
                        sectionsToLoad.Add(sectionEntities[i]);
                    }

                    DrawSectionRangeDebug(sectionRange.Center, rangeData.MinDistance, rangeData.MaxDistance,
                        sectionsToLoad.Contains(sectionEntities[i]), rangeData.Level);
                }
            }

            for (int i = 0; i < sectionEntities.Length; i++)
            {
                var sectionEntity = sectionEntities[i];
                var rangeData = sectionRangeData[i];
                var sectionState = SceneSystem.GetSectionStreamingState(state.WorldUnmanaged, sectionEntity);
                bool shouldBeLoaded = sectionsToLoad.Contains(sectionEntity);

                if (shouldBeLoaded)
                {
                    bool hasRequestSceneLoaded = state.EntityManager.HasComponent<RequestSceneLoaded>(sectionEntity);

                    if (sectionState == SceneSystem.SectionStreamingState.Unloaded)
                    {
                        if (!hasRequestSceneLoaded)
                        {
                            state.EntityManager.AddComponent<RequestSceneLoaded>(sectionEntity);
                        }
                    }
                    else if (!hasRequestSceneLoaded)
                    {
                        state.EntityManager.AddComponent<RequestSceneLoaded>(sectionEntity);
                    }
                }
                else
                {
                    if (playersProcessed > 0 && sectionsToLoad.Count > 0)
                    {
                        if (state.EntityManager.HasComponent<RequestSceneLoaded>(sectionEntity))
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
        }

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
    }
}
