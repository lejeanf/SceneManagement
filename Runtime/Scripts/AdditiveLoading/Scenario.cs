using System.Collections.Generic;
using jeanf.propertyDrawer;
using UnityEngine;

namespace jeanf.SceneManagment
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
    }
}