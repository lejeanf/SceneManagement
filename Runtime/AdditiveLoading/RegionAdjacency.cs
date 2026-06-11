using System.Collections.Generic;
using UnityEngine;
using jeanf.propertyDrawer;

namespace jeanf.scenemanagement
{
    public enum RegionConnectionType
    {
        Doorway,
        Elevator
    }

    [ScriptableObjectDrawer]
    [CreateAssetMenu(fileName = "RegionAdjacency", menuName = "LoadingSystem/RegionAdjacency")]
    public class RegionAdjacency : ScriptableObject
    {
        [Tooltip("Each link declares two adjacent regions once. Adjacency is symmetric, so both regions infer each other from the same entry.")]
        public List<AdjacencyLink> links = new List<AdjacencyLink>();

        public void GetCoLoadedRegions(Region region, List<Region> results)
        {
            results.Clear();
            if (region == null) return;

            for (int i = 0; i < links.Count; i++)
            {
                var link = links[i];
                if (link == null || link.connectionType != RegionConnectionType.Doorway) continue;

                if (link.regionA == region && link.regionB != null && !results.Contains(link.regionB))
                    results.Add(link.regionB);
                else if (link.regionB == region && link.regionA != null && !results.Contains(link.regionA))
                    results.Add(link.regionA);
            }
        }

        public bool TryGetLink(Region a, Region b, out AdjacencyLink found)
        {
            for (int i = 0; i < links.Count; i++)
            {
                var link = links[i];
                if (link == null) continue;
                if ((link.regionA == a && link.regionB == b) || (link.regionA == b && link.regionB == a))
                {
                    found = link;
                    return true;
                }
            }
            found = null;
            return false;
        }
    }

    [System.Serializable]
    public class AdjacencyLink
    {
        public Region regionA;
        public Region regionB;
        [Tooltip("Doorway: both regions stay loaded together (seamless walk-through). Elevator: a hard transition — only one side is loaded at a time.")]
        public RegionConnectionType connectionType = RegionConnectionType.Doorway;
        [Tooltip("Zones that physically touch across the border. Used by Doorway links so detection flips when the player walks over. Optional for Elevator links.")]
        public List<ZonePair> touchingZones = new List<ZonePair>();
    }

    [System.Serializable]
    public class ZonePair
    {
        public Zone zoneOnA;
        public Zone zoneOnB;
    }
}
