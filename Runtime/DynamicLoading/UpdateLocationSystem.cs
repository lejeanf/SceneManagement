using Unity.Collections;
using Unity.Entities;

namespace jeanf.scenemanagement
{
    [UpdateAfter(typeof(VolumeSystem))]
    public partial class LocationUpdateNotificationSystem : SystemBase
    {
        private EndSimulationEntityCommandBufferSystem _ecbSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _ecbSystem = World.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
        }

        protected override void OnUpdate()
        {
            var ecb = _ecbSystem.CreateCommandBuffer();

            foreach (var (notification, entity) in SystemAPI.Query<RefRO<ZoneChangeNotificationComponent>>().WithEntityAccess())
            {
                WorldManager.NotifyZoneChangeFromECS(notification.ValueRO.ZoneId);

                ecb.DestroyEntity(entity);
            }

            foreach (var (notification, entity) in SystemAPI.Query<RefRO<RegionChangeNotificationComponent>>().WithEntityAccess())
            {
                WorldManager.NotifyRegionChangeFromECS(notification.ValueRO.RegionId);
                ecb.DestroyEntity(entity);
            }
        }
    }

    public struct ZoneChangeNotificationComponent : IComponentData
    {
        public FixedString128Bytes ZoneId;
    }

    public struct RegionChangeNotificationComponent : IComponentData
    {
        public FixedString128Bytes RegionId;
    }
}