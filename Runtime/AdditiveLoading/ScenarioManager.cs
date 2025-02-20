using System.Collections.Generic;
using System.Linq;
using jeanf.EventSystem;
using UnityEngine;

namespace jeanf.scenemanagement
{
    public class ScenarioManager : MonoBehaviour
    {
        private SceneLoader _sceneLoader;
        private List<Scenario> _activeScenarios = new List<Scenario>();
        public static Dictionary<string, Scenario> ScenarioDictionary = new Dictionary<string, Scenario>();
        
        [Header("Listening on:")]
        [SerializeField] private StringEventChannelSO BeginScenarioRequest;
        [SerializeField] private StringEventChannelSO EndScenarioRequest;
        [SerializeField] private VoidEventChannelSO KillAllScenariosRequest;

        public static Dictionary<string, List<AppType>> activeOverridesPerZone = new Dictionary<string, List<AppType>>();
        private void Awake()
        {
            _sceneLoader = this.GetComponent<SceneLoader>();
        }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();
        
        private void Subscribe()
        {        
            BeginScenarioRequest.OnEventRaised += OnScenarioBeginRequest;
            EndScenarioRequest.OnEventRaised += OnScenarioEndRequest;
            KillAllScenariosRequest.OnEventRaised += OnKillAllRequest;
        }
        
        private void Unsubscribe()
        {
            BeginScenarioRequest.OnEventRaised -= OnScenarioBeginRequest;
            EndScenarioRequest.OnEventRaised -= OnScenarioEndRequest;
            KillAllScenariosRequest.OnEventRaised -= OnKillAllRequest;
        }

        private void OnScenarioBeginRequest(string scenarioID)
        {
            if(!ScenarioDictionary.TryGetValue(scenarioID, out var scenario)) return;

            if (_activeScenarios.Contains(scenario))
            {
                Debug.Log($"A request to load scenario with ID: {scenario.scenarioName} has been received but that scenario is already in the list of active scenarios. The request has been denied.");
                return;
            }

            foreach (var zoneOverride in ScenarioDictionary[scenarioID].ZoneOverrides)
            {
                activeOverridesPerZone.Add(zoneOverride.zone.id, zoneOverride.AppsForThisZone_Override);
            }

            foreach (var scene in CompileSceneList(scenario))
            {
                _sceneLoader.LoadSceneRequest(scene);
            }
            
            _activeScenarios.Add(scenario);
        }
        private void OnScenarioEndRequest(string scenarioID)
        {
            _activeScenarios.Remove(UnloadScenario(scenarioID));
        }

        private Scenario UnloadScenario(string scenarioID)
        {
            if(!ScenarioDictionary.TryGetValue(scenarioID, out var scenario)) return null;
            
            if (!_activeScenarios.Contains(scenario))
            {
                return null;
            }
            foreach (var zoneOverride in ScenarioDictionary[scenarioID].ZoneOverrides)
            {
                activeOverridesPerZone.Remove(zoneOverride.zone.id);
            }
            foreach (var scene in CompileSceneList(scenario))
            {
               _sceneLoader.UnLoadSceneRequest(scene);
            }
            return scenario;
        }

        private void OnKillAllRequest()
        {
            var scenariosToRemove = _activeScenarios;
            var obsoleteScenarios = scenariosToRemove.Select(scenario => UnloadScenario(scenario.id)).Where(scenarioToUnload => scenarioToUnload is not null).ToList();

            foreach (var obsoleteScenario in obsoleteScenarios)
            {
                _activeScenarios.Remove(obsoleteScenario);
            }
        }

        private static List<string> CompileSceneList(Scenario scenario)
        {
            var requiredScenes = new List<string> { scenario.scene.SceneName };
            requiredScenes.AddRange(scenario.dependenciesInThisScenario.Select(dependency => dependency.SceneName));
            return requiredScenes;
        }
    }
}