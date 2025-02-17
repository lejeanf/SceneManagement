using System.Collections.Generic;
using jeanf.propertyDrawer;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [ScriptableObjectDrawer]
    [CreateAssetMenu(fileName = "Zone", menuName = "LoadingSystem/Zone")]
    public class Zone : ScriptableObject
    {
        public Id id;
        public string zoneName;
        public int zoneNb;
        public ZoneType zoneType;

        // this can be overriden by scenario List when a scenario is launched
        // should be reset with that list after a scenario is completed
        public List<AppType> DefaultAppsInZone;  // default list of apps for this zone
    }
}