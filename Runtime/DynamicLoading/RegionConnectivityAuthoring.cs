using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace jeanf.scenemanagement
{
    public class RegionConnectivityAuthoring : MonoBehaviour
    {
        public RegionConnectivity regionConnectivity;

        class Baker : Baker<RegionConnectivityAuthoring>
        {
            public override void Bake(RegionConnectivityAuthoring authoring)
            {
                if (authoring.regionConnectivity == null) return;

                var entity = GetEntity(TransformUsageFlags.None);
                
                var regionBuffer = AddBuffer<RegionBuffer>(entity);
                var zoneBuffer = AddBuffer<ZoneIdBuffer>(entity);
                var landingBuffer = AddBuffer<LandingZoneBuffer>(entity);
                
                int zoneStartIndex = 0;
                
                foreach (var region in authoring.regionConnectivity.activeRegions)
                {
                    if (region == null) continue;
                    
                    int zoneCount = 0;
                    
                    foreach (var zone in region.zonesInThisRegion)
                    {
                        if (zone != null)
                        {
                            zoneBuffer.Add(new ZoneIdBuffer 
                            { 
                                zoneId = zone.id.ToString() 
                            });
                            zoneCount++;
                        }
                    }
                    
                    regionBuffer.Add(new RegionBuffer
                    {
                        regionId = region.id.ToString(),
                        zoneStartIndex = zoneStartIndex,
                        zoneCount = zoneCount
                    });
                    
                    zoneStartIndex += zoneCount;
                }
                
                foreach (var landingData in authoring.regionConnectivity.landingZones)
                {
                    landingBuffer.Add(new LandingZoneBuffer
                    {
                        regionId = landingData.region != null ? landingData.region.id.ToString() : "",
                        landingZoneId = landingData.landingZone != null ? 
                            landingData.landingZone.id.ToString() : ""
                    });
                }
            }
        }
    }
    
    public struct RegionBuffer : IBufferElementData
    {
        public FixedString128Bytes regionId;
        public int zoneStartIndex;
        public int zoneCount;
    }
    
    public struct ZoneIdBuffer : IBufferElementData
    {
        public FixedString128Bytes zoneId;
    }
    
    public struct LandingZoneBuffer : IBufferElementData
    {
        public FixedString128Bytes regionId;
        public FixedString128Bytes landingZoneId;
    }
}