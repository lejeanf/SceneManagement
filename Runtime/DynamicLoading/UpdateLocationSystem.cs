// LocationUpdateNotificationSystem.cs

using Unity.Collections;
using Unity.Entities;

namespace jeanf.scenemanagement
{
    [UpdateAfter(typeof(VolumeSystem))]
    public partial class LocationUpdateNotificationSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            // Process zone change notifications
            Entities.WithoutBurst().WithStructuralChanges().ForEach((Entity entity, in ZoneChangeNotificationComponent notification) =>
            {
                WorldManager.NotifyZoneChangeFromECS(notification.ZoneId);
                EntityManager.DestroyEntity(entity);
                
            }).Run();
        
            // Process region change notifications
            Entities.WithoutBurst().WithStructuralChanges().ForEach((Entity entity, in RegionChangeNotificationComponent notification) =>
            {
                WorldManager.NotifyRegionChangeFromECS(notification.RegionId);
                EntityManager.DestroyEntity(entity);
                
            }).Run();
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