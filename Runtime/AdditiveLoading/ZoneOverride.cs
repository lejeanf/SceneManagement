using System.Collections.Generic;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [CreateAssetMenu(fileName = "ZoneOverride", menuName = "LoadingSystem/ZoneOverride")]
    public class ZoneOverride : ScriptableObject
    {
        [Tooltip("The list app in this zone will be overriden by the following list")]
        public Zone zone;
        public List<AppType> AppsForThisZone_Override; 
    }
}