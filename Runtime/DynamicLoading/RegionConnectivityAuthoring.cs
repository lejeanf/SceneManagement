using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace jeanf.scenemanagement
{
    public class RegionConnectivityAuthoring : MonoBehaviour
    {
        [Header("Original Connectivity (for backward compatibility)")]
        public RegionConnectivity regionConnectivity;
        
        [Header("Pre-computed Data (for optimized performance)")]
        [Tooltip("Pre-computed volume data generated from RegionConnectivity")]
        public PrecomputedVolumeData precomputedVolumeData;

        class Baker : Baker<RegionConnectivityAuthoring>
        {
            public override void Bake(RegionConnectivityAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                
                // Build backward-compatible buffers if RegionConnectivity is assigned
                if (authoring.regionConnectivity != null)
                {
                    var regionBuffer = AddBuffer<RegionBuffer>(entity);
                    var zoneBuffer = AddBuffer<ZoneIdBuffer>(entity);
                    var landingBuffer = AddBuffer<LandingZoneBuffer>(entity);
                    BuildOriginalBuffers(authoring, regionBuffer, zoneBuffer, landingBuffer);
                }
                
                // Build optimized buffers if PrecomputedVolumeData is assigned
                if (authoring.precomputedVolumeData != null)
                {
                    var precomputedBuffer = AddBuffer<PrecomputedVolumeDataBuffer>(entity);
                    BuildOptimizedBuffers(authoring, precomputedBuffer);
                }
            }
            
            private void BuildOriginalBuffers(RegionConnectivityAuthoring authoring, 
                DynamicBuffer<RegionBuffer> regionBuffer,
                DynamicBuffer<ZoneIdBuffer> zoneBuffer,
                DynamicBuffer<LandingZoneBuffer> landingBuffer)
            {
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
            
            private void BuildOptimizedBuffers(RegionConnectivityAuthoring authoring,
                DynamicBuffer<PrecomputedVolumeDataBuffer> buffer)
            {
                var data = authoring.precomputedVolumeData;
                
                // Bake zone checkable sets
                foreach (var checkableSet in data.zoneCheckableSets)
                {
                    var startIndex = buffer.Length;
                    
                    // Add all checkable zones first
                    foreach (var checkableZone in checkableSet.checkableZoneIds)
                    {
                        buffer.Add(new PrecomputedVolumeDataBuffer
                        {
                            checkableZoneId = checkableZone,
                            isData = true
                        });
                    }
                    
                    // Add header entry
                    buffer.Add(new PrecomputedVolumeDataBuffer
                    {
                        primaryZoneId = checkableSet.primaryZoneId,
                        startIndex = startIndex,
                        count = checkableSet.checkableZoneIds.Count,
                        isHeader = true
                    });
                }
                
                // Add zone-region mappings
                foreach (var mapping in data.zoneRegionMappings)
                {
                    buffer.Add(new PrecomputedVolumeDataBuffer
                    {
                        zoneId = mapping.zoneId,
                        regionId = mapping.regionId,
                        isZoneRegionMapping = true
                    });
                }
                
                // Add landing zones
                foreach (var landingZone in data.landingZoneIds)
                {
                    buffer.Add(new PrecomputedVolumeDataBuffer
                    {
                        landingZoneId = landingZone,
                        isLandingZone = true
                    });
                }
            }
        }
    }
    
    // Keep existing buffer types for backward compatibility
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
    
    public struct PrecomputedVolumeDataBuffer : IBufferElementData
    {
        // Zone checkable data
        public FixedString128Bytes primaryZoneId;
        public FixedString128Bytes checkableZoneId;
        public int startIndex;
        public int count;
        
        // Zone-region mapping
        public FixedString128Bytes zoneId;
        public FixedString128Bytes regionId;
        
        // Landing zone
        public FixedString128Bytes landingZoneId;
        
        // Type flags
        public bool isHeader;
        public bool isData;
        public bool isZoneRegionMapping;
        public bool isLandingZone;
    }
}