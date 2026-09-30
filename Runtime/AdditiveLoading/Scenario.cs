using System.Collections.Generic;
using jeanf.propertyDrawer;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [ScriptableObjectDrawer]
    [CreateAssetMenu(fileName = "Scenario", menuName = "LoadingSystem/Scenario")]
    public class Scenario : ScriptableObject
    {
        public Id id;
        public string scenarioName;
        public SceneReference scene;
        
        // will override the default app list for the listed zones and for time the scenario is running
        public List<ZoneOverride> ZoneOverrides;

        public List<SceneReference> dependenciesInThisScenario;
        
        public List<Zone> listOfZonesNeededForThisScenario;

        public bool enableFadeOnLoad = true;

        [Header("Reset on unload")]
        [Tooltip("Take the player out of the zones this scenario locks before its scenes are unloaded. Zones with no registered exit (no door, no ZoneExitAnchor) are skipped: they do not lock, so nobody gets trapped in them.")]
        public bool evacuatePlayerOnUnload = true;

        [Tooltip("OPTIONAL: zones to evacuate. Leave empty and the zones above (List Of Zones Needed For This Scenario) are used, which is the normal case - fill it only to narrow or widen that set.")]
        public List<Zone> zonesToEvacuateOverride = new List<Zone>();
    }
}