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
        
        [Header("Zone Connectivity")]
        [Tooltip("Define which zones are neighbors within the same region")]
        public List<ZoneNeighborData> zoneConnections = new List<ZoneNeighborData>();
        
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
        
        public List<Zone> GetNeighborsForZone(Zone zone)
        {
            var result = new List<Zone>();
            foreach (var connection in zoneConnections)
            {
                if (connection.zoneA == zone && connection.zoneB != null)
                    result.Add(connection.zoneB);
                else if (connection.zoneB == zone && connection.zoneA != null)
                    result.Add(connection.zoneA);
            }
            return result;
        }
        
        public bool IsRegionActive(Region region)
        {
            return activeRegions.Contains(region);
        }
        
        public bool IsLandingZone(Zone zone)
        {
            foreach (var landing in landingZones)
            {
                if (landing.landingZone == zone)
                    return true;
            }
            return false;
        }
    }
    
    [System.Serializable]
    public class LandingZoneData
    {
        public Region region;
        public Zone landingZone;
    }
    
    [System.Serializable]
    public class ZoneNeighborData
    {
        public Zone zoneA;
        public Zone zoneB;
        [Tooltip("Bidirectional connection between these zones")]
        public bool isBidirectional = true;
    }
}