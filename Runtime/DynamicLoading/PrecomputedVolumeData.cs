using System.Collections.Generic;
using UnityEngine;
using jeanf.propertyDrawer;

namespace jeanf.scenemanagement
{
    [ScriptableObjectDrawer]
    [CreateAssetMenu(fileName = "PrecomputedVolumeData", menuName = "LoadingSystem/PrecomputedVolumeData")]
    public class PrecomputedVolumeData : ScriptableObject
    {
        [Header("Pre-computed Connectivity Data")]
        [Tooltip("Generated from RegionConnectivity - do not edit manually")]
        public List<ZoneCheckableSet> zoneCheckableSets = new List<ZoneCheckableSet>();
        
        [Header("Landing Zones")]
        [Tooltip("Zones that are always checkable regardless of current zone")]
        public List<string> landingZoneIds = new List<string>();
        
        [Header("Zone to Region Mapping")]
        [Tooltip("Maps each zone ID to its region ID")]
        public List<ZoneRegionMapping> zoneRegionMappings = new List<ZoneRegionMapping>();
        
        [Header("Generation Info")]
        [Tooltip("Information about when this data was generated")]
        public string sourceRegionConnectivityAsset;
        public string generatedDateTime;
        public int totalZones;
        public int totalRegions;
        
        // Runtime lookup methods
        public HashSet<string> GetCheckableZoneIds(string currentZone)
        {
            var result = new HashSet<string>();
            
            if (string.IsNullOrEmpty(currentZone))
            {
                // Bootstrap state - return all zones
                foreach (var mapping in zoneRegionMappings)
                {
                    result.Add(mapping.zoneId);
                }
                return result;
            }
            
            // Find the checkable set for this zone
            foreach (var checkableSet in zoneCheckableSets)
            {
                if (checkableSet.primaryZoneId == currentZone)
                {
                    foreach (var checkableZone in checkableSet.checkableZoneIds)
                    {
                        result.Add(checkableZone);
                    }
                    break;
                }
            }
            
            // Always add landing zones
            foreach (var landingZone in landingZoneIds)
            {
                result.Add(landingZone);
            }
            
            return result;
        }
        
        public string GetRegionForZone(string zoneId)
        {
            foreach (var mapping in zoneRegionMappings)
            {
                if (mapping.zoneId == zoneId)
                {
                    return mapping.regionId;
                }
            }
            return "";
        }
        
        public bool IsLandingZone(string zoneId)
        {
            return landingZoneIds.Contains(zoneId);
        }
        
        public void Clear()
        {
            zoneCheckableSets.Clear();
            landingZoneIds.Clear();
            zoneRegionMappings.Clear();
            sourceRegionConnectivityAsset = "";
            generatedDateTime = "";
            totalZones = 0;
            totalRegions = 0;
        }
    }
    
    [System.Serializable]
    public class ZoneCheckableSet
    {
        [Tooltip("The zone that this checkable set applies to")]
        public string primaryZoneId;
        
        [Tooltip("All zones that should be checked when in the primary zone")]
        public List<string> checkableZoneIds = new List<string>();
        
        public ZoneCheckableSet(string primaryZone)
        {
            primaryZoneId = primaryZone;
        }
    }
    
    [System.Serializable]
    public class ZoneRegionMapping
    {
        public string zoneId;
        public string regionId;
        
        public ZoneRegionMapping(string zone, string region)
        {
            zoneId = zone;
            regionId = region;
        }
    }
}