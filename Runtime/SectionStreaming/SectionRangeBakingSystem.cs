using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Serialization;

namespace jeanf.SceneManagement
{
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    partial struct SectionRangeBakingSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var cleanupQuery = SystemAPI.QueryBuilder()
                .WithAll<SectionRange, SectionMetadataSetup>()
                .Build();
            state.EntityManager.RemoveComponent<SectionRange>(cleanupQuery);

            var cleanupSectionBuffer = SystemAPI.QueryBuilder()
                .WithAll<SectionLevelData, SectionMetadataSetup>()
                .Build();
            state.EntityManager.RemoveComponent<SectionLevelData>(cleanupSectionBuffer);

            var rangeQuery = SystemAPI.QueryBuilder()
                .WithAll<SectionRange, SceneSection>()
                .Build();

            var rangeEntities = rangeQuery.ToEntityArray(Allocator.Temp);
            var sectionQuery = SystemAPI.QueryBuilder().WithAll<SectionMetadataSetup>().Build();

            foreach (var rangeEntity in rangeEntities)
            {
                var sectionRange = state.EntityManager.GetComponentData<SectionRange>(rangeEntity);
                var sectionBuffer = state.EntityManager.GetBuffer<SectionLevelData>(rangeEntity);
                var sceneSection = state.EntityManager.GetSharedComponent<SceneSection>(rangeEntity);

                var sectionLevels = new NativeArray<SectionLevelData>(sectionBuffer.Length, Allocator.Temp);
                for (int i = 0; i < sectionBuffer.Length; i++)
                {
                    sectionLevels[i] = sectionBuffer[i];
                }

                var sectionsToProcess = new NativeList<(Entity, SectionRangeData, int)>(Allocator.Temp);

                for (int i = 0; i < sectionLevels.Length; i++)
                {
                    var sectionLevel = sectionLevels[i];

                    var sectionEntity = SerializeUtility.GetSceneSectionEntity(
                        sectionLevel.SectionIndex,
                        state.EntityManager,
                        ref sectionQuery,
                        true);

                    if (sectionEntity == Entity.Null)
                    {
                        continue;
                    }

                    float minDist = i == 0 ? 0f : sectionLevels[i - 1].MaxDistance;
                    float maxDist = sectionLevel.MaxDistance;

                    var rangeData = new SectionRangeData
                    {
                        MinDistance = minDist,
                        MaxDistance = maxDist,
                        MinDistanceSq = minDist * minDist,
                        MaxDistanceSq = maxDist * maxDist,
                        Level = i
                    };

                    sectionsToProcess.Add((sectionEntity, rangeData, sectionLevel.SectionIndex));
                }

                foreach (var (sectionEntity, rangeData, sectionIndex) in sectionsToProcess)
                {
                    if (!state.EntityManager.HasComponent<SectionRange>(sectionEntity))
                    {
                        state.EntityManager.AddComponentData(sectionEntity, sectionRange);
                    }

                    state.EntityManager.AddComponentData(sectionEntity, rangeData);
                }

                sectionsToProcess.Dispose();
                sectionLevels.Dispose();
            }
        }
    }

    public struct SectionRangeData : IComponentData
    {
        public float MinDistance;
        public float MaxDistance;
        public float MinDistanceSq;
        public float MaxDistanceSq;
        public int Level;
    }
}
