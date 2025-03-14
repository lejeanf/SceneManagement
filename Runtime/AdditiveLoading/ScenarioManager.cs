using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jeanf.EventSystem;
using UnityEngine;
using UnityEngine.Serialization;

namespace jeanf.scenemanagement
{
    public class ScenarioManager : MonoBehaviour
    {
        private SceneLoader _sceneLoader;
        private List<Scenario> _activeScenarios = new List<Scenario>();
        public static Dictionary<string, Scenario> ScenarioDictionary = new Dictionary<string, Scenario>();
        
        [Header("Listening on:")]
        [SerializeField] private StringEventChannelSO BeginScenarioRequest;
        [FormerlySerializedAs("EndScenarioRequest")] [SerializeField] private StringEventChannelSO EndScenarioRequestSO;
        [SerializeField] private VoidEventChannelSO KillAllScenariosRequest;

        public static Dictionary<string, List<AppType>> activeOverridesPerZone = new Dictionary<string, List<AppType>>();
        
        public delegate void ScenarioStateChanged(string zoneId);
        public static ScenarioStateChanged OnZoneOverridesChanged;

        public delegate void EndScenarioRequestDelegate(string scenarioId);
        public static EndScenarioRequestDelegate EndScenarioRequestPrompt;
        public static EndScenarioRequestDelegate EndScenarioRequest;
        public static EndScenarioRequestDelegate StartScenarioRequest;
        public static EndScenarioRequestDelegate RestartScenarioRequest;

        [SerializeField] private bool automaticScenarioUnload = true;
        private void Awake()
        {
            _sceneLoader = this.GetComponent<SceneLoader>();
        }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();

        private void Subscribe()
        {
            BeginScenarioRequest.OnEventRaised += LoadScenario;
            EndScenarioRequestSO.OnEventRaised += OnScenarioEndRequest;
            KillAllScenariosRequest.OnEventRaised += UnloadAllScenarios;
            StartScenarioRequest += LoadScenario;
            RestartScenarioRequest += OnScenarioRestartRequest;
            EndScenarioRequest += OnScenarioEndRequest;
        }

        private void Unsubscribe()
        {
            BeginScenarioRequest.OnEventRaised -= LoadScenario;
            EndScenarioRequestSO.OnEventRaised -= OnScenarioEndRequest;
            KillAllScenariosRequest.OnEventRaised -= UnloadAllScenarios;
            StartScenarioRequest -= LoadScenario;
            RestartScenarioRequest -= OnScenarioRestartRequest;
            EndScenarioRequest -= OnScenarioEndRequest;
        }

        private void OnScenarioRestartRequest(string scenarioId)
        {
            _ = ScenarioRestartAsync(scenarioId);
        }
        
        private async Task ScenarioRestartAsync(string scenarioId)
        {
            Debug.Log("Restarting scenario : " + scenarioId); 
            UnloadScenario(scenarioId);
            Debug.Log("after unload wait 2s");
            await Task.Delay(2000);
            LoadScenario(scenarioId);
        }

        private void LoadScenario(string scenarioId)
        {
            if (!ScenarioDictionary.TryGetValue(scenarioId, out var scenario)) return;

            if (_activeScenarios.Contains(scenario))
            {
                Debug.Log(
                    $"A request to load scenario with ID: {scenario.scenarioName} has been received but that scenario is already in the list of active scenarios. The request has been denied.");
                return;
            }

            switch (_activeScenarios.Count)
            {
                // automatic unload previously loaded scenarios
                case > 0 when automaticScenarioUnload:
                    UnloadAllScenarios();
                    break;
                // if not automatic, send outside request to unload scenarios one by one.
                case > 0 when !automaticScenarioUnload:
                {
                    foreach (var s in _activeScenarios)
                    {
                        EndScenarioRequestPrompt.Invoke(s.id);
                    }

                    break;
                }
            }

            foreach (var zoneOverride in ScenarioDictionary[scenarioId].ZoneOverrides)
            {
                if (activeOverridesPerZone.ContainsKey(zoneOverride.zone.id)) continue;
                activeOverridesPerZone.Add(zoneOverride.zone.id, zoneOverride.AppsForThisZone_Override);
                OnZoneOverridesChanged?.Invoke(zoneOverride.zone.id);
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
            if (!ScenarioDictionary.TryGetValue(scenarioID, out var scenario)) return null;

            if (!_activeScenarios.Contains(scenario))
            {
                return null;
            }

            foreach (var zoneOverride in ScenarioDictionary[scenarioID].ZoneOverrides)
            {
                activeOverridesPerZone.Remove(zoneOverride.zone.id);
                OnZoneOverridesChanged?.Invoke(zoneOverride.zone.id);
            }

            foreach (var scene in CompileSceneList(scenario))
            {
                _sceneLoader.UnLoadSceneRequest(scene);
            }

            return scenario;
        }

        private void UnloadAllScenarios()
        {
            var scenariosToRemove = _activeScenarios;
            var obsoleteScenarios = scenariosToRemove.Select(scenario => UnloadScenario(scenario.id))
                .Where(scenarioToUnload => scenarioToUnload is not null).ToList();
            var affectedZones = new HashSet<string>();
            // Collect all affected zones before unloading
            foreach (var zoneOverride in scenariosToRemove.SelectMany(scenario => scenario.ZoneOverrides))
            {
                affectedZones.Add(zoneOverride.zone.id);
            }

            foreach (var obsoleteScenario in obsoleteScenarios)
            {
                _activeScenarios.Remove(obsoleteScenario);
            }

            // Notify for all affected zones after everything is unloaded
            foreach (var zoneId in affectedZones)
            {
                OnZoneOverridesChanged?.Invoke(zoneId);
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