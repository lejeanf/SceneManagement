using System.Collections.Generic;
using UnityEngine;
using jeanf.propertyDrawer;

namespace jeanf.scenemanagement
{
    [ScriptableObjectDrawer]
    [CreateAssetMenu(fileName = "RegionConnectivity", menuName = "LoadingSystem/RegionConnectivity")]
    public class RegionConnectivity : ScriptableObject
    {
        [Header("Active Regions")]
        [Tooltip("Regions that are currently active in the scene")]
        public List<Region> activeRegions = new List<Region>();
        
        [Header("Landing Zones")]
        [Tooltip("Landing zones for manual teleportation between regions")]
        public List<LandingZoneData> landingZones = new List<LandingZoneData>();
        
        public List<Zone> GetZonesForRegion(Region region)
        {
            return region != null ? region.zonesInThisRegion : new List<Zone>();
        }
        
        public List<Zone> GetLandingZonesForAllRegions()
        {
            var result = new List<Zone>();
            foreach (var landing in landingZones)
            {
                if (landing.landingZone != null)
                    result.Add(landing.landingZone);
            }
            return result;
        }
        
        public bool IsRegionActive(Region region)
        {
            return activeRegions.Contains(region);
        }
    }
    
    [System.Serializable]
    public class LandingZoneData
    {
        public Region region;
        public Zone landingZone;
    }
}