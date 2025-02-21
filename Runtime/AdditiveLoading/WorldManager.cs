using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using jeanf.EventSystem;
using jeanf.vrplayer;

namespace jeanf.scenemanagement
{
    [RequireComponent(typeof(SceneLoader))]
    [RequireComponent(typeof(ScenarioManager))]
    public class WorldManager : MonoBehaviour
    {
        public bool isDebug = false;
        private SceneLoader _sceneLoader;
        public List<Region> ListOfRegions;
        private Dictionary<string, Zone> _zoneDictionary = new Dictionary<string, Zone>();
        private Dictionary<string, Region> _regionDictionary = new Dictionary<string, Region>();
        private Dictionary<string, Region> _regionDictionaryPerZone = new Dictionary<string, Region>();
        private Dictionary<Collider, Region> _regionDictionaryPerCollider = new Dictionary<Collider, Region>();
        private Dictionary<string, List<SceneReference>> _dependenciesPerRegion = new Dictionary<string, List<SceneReference>>();

        private List<Region> _activeRegions = new List<Region>();
        private List<GameObject> _activeZones = new List<GameObject>();

        [SerializeField] private StringEventChannelSO regionChangeRequestChannel;
        [SerializeField] private SendTeleportTarget sendTeleportTarget;

        public static Zone CurrentPlayerZone { get; private set; }
        public static Region CurrentPlayerRegion { get; private set; }

        public delegate void SendId(string newRegionID);
        public static SendId RequestRegionChange;
        
        public static SendId PublishCurrentRegionId;
        public static SendId PublishCurrentZoneId;
        
        public delegate void Reset();
        public static Reset ResetWorld;
        
        public delegate void BroadcastAppList(List<AppType> list);
        public static BroadcastAppList _broadcastAppList;

        private bool hasGameBeenInitialized = false;
        

        private void Awake()
        {
            _sceneLoader = this.GetComponent<SceneLoader>();
            Init();
        }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();
        
        private void Subscribe()
        {
            regionChangeRequestChannel.OnEventRaised += OnRegionChange;
            RequestRegionChange += OnRegionChange;
            
            ZoneContainer.broadcastObject += SetCurrentZoneAndRegion;
            ResetWorld += Init;
            ScenarioManager.OnZoneOverridesChanged += OnZoneOverridesChanged;
        }

        private void Unsubscribe()
        {
            regionChangeRequestChannel.OnEventRaised -= OnRegionChange;
            RequestRegionChange -= OnRegionChange;
            
            ZoneContainer.broadcastObject -= SetCurrentZoneAndRegion;
            ResetWorld -= Init;
            ScenarioManager.OnZoneOverridesChanged -= OnZoneOverridesChanged;
        }


        private void Init()
        {
            if(isDebug) Debug.Log($"[WorldManager] World reset.");
            hasGameBeenInitialized = false;
            foreach (var region in ListOfRegions)
            {
                // build region Dictionary
                if(isDebug) Debug.Log($"[WorldManager] Adding region with id: {region.id.id} to the region dictionary.");
                _regionDictionary.TryAdd(region.id, region);
                
                // build scenario Dictionary
                foreach (var scenario in region.scenariosInThisRegion)
                {
                    if(isDebug) Debug.Log($"[WorldManager] Adding scenario with id: {scenario.id.id} to the scenario dictionary.");
                    ScenarioManager.ScenarioDictionary.TryAdd(scenario.id, scenario);
                }

                // build zoneData Dictionary
                foreach (var zone in region.zonesInThisRegion)
                {
                    if(isDebug) Debug.Log($"[WorldManager] Adding zone with id: {zone.id} to the zone dictionary.");
                    _zoneDictionary.TryAdd(zone.id, zone);
                    _regionDictionaryPerZone.TryAdd(zone.id, region);
                }   

                // build dependency Dictionary
                if(isDebug) Debug.Log($"[WorldManager] Adding list of dependencies for the region with id: {region.id.id} to the dependency dictionary.");
                _dependenciesPerRegion.TryAdd(region.id, region.dependenciesInThisRegion);
            }
        }
        
        // Add new method to handle zone override changes
        private void OnZoneOverridesChanged(string zoneId)
        {
            // Only update if we're in the affected zone
            if (CurrentPlayerZone != null && CurrentPlayerZone.id == zoneId)
            {
                PublishAppList(CurrentPlayerZone);
            }
        }

        private void SetCurrentZoneAndRegion(GameObject gameObject, Zone zone)
        {
            if (!gameObject.CompareTag("Player")) return;
            CurrentPlayerZone = zone;
            PublishCurrentZoneId?.Invoke(zone.id);
            PublishAppList(zone);
            var newRegion = _regionDictionaryPerZone[zone.id];
            if(newRegion != CurrentPlayerRegion) OnRegionChange(newRegion);
        }

        private void OnRegionChange(string newRegionID)
        {
            if (!_regionDictionary.TryGetValue(newRegionID, out var region)) return;
            OnRegionChange(region);
        }
        
        // ReSharper disable Unity.PerformanceAnalysis
        private void OnRegionChange(Region region)
        {
            CurrentPlayerRegion = region;
            PublishCurrentRegionId?.Invoke(CurrentPlayerRegion.id);

            var currentActiveRegion = _activeRegions;
            var regionsToRemove = currentActiveRegion.Select(RequestUnLoadForObsoleteRegion).ToList();

            foreach (var r in regionsToRemove)
            {
                _activeRegions.Remove(r);
            }
            
            RequestLoadForRegionDependencies(region);

            // set teleporting position
            var spawnPos = SetTeleportTarget(region, hasGameBeenInitialized);
            sendTeleportTarget.transform.position = spawnPos.position;
            sendTeleportTarget.transform.rotation = Quaternion.Euler(spawnPos.rotation);
            // teleport!
            sendTeleportTarget.Teleport();
            
            Debug.Log($"[WorldManager] Current region: {region.levelName}.");
            _activeRegions.Add(region);
        }

        private SpawnPos SetTeleportTarget(Region region, bool hasRegionBeenInitialized)
        {
            var spawnPos = new SpawnPos(region.SpawnPosOnRegionChangeRequest.position, region.SpawnPosOnRegionChangeRequest.rotation);
            if (hasRegionBeenInitialized) return spawnPos;
            
            spawnPos.position = region.SpawnPosOnInit.position;
            spawnPos.rotation = region.SpawnPosOnInit.rotation;
            hasGameBeenInitialized = true;

            return spawnPos;
        }

        private void PublishAppList(Zone zone)
        {
            var listToBroadcast = zone.DefaultAppsInZone;
            if(isDebug) Debug.Log($"[WorldManager] Default list for zone [{zone.name}] : [{string.Join(", ", listToBroadcast)}]");
            // check if for this zone there is no override 
            if (ScenarioManager.activeOverridesPerZone.TryGetValue(zone.id, out var value))
            {
                // if yes, send override list
                listToBroadcast = value;
                if(isDebug) Debug.Log($"[WorldManager] List override found for zone [{zone.name}] : [{string.Join(", ", listToBroadcast)}]");
            }
            
            // broadcast list
            _broadcastAppList?.Invoke(listToBroadcast);
        }

        private void RequestLoadForRegionDependencies(Region region)
        {
            if ( region.dependenciesInThisRegion.Count <= 0 ) return;
            foreach (var scene in CompileSceneList(region))
            {
                _sceneLoader.LoadSceneRequest(scene);
            }
        }
        private Region RequestUnLoadForObsoleteRegion(Region region)
        {
            foreach (var scene in CompileSceneList(region))
            {
                _sceneLoader.UnLoadSceneRequest(scene);
            }
            return region;
        }
        
        private static List<string> CompileSceneList(Region region)
        {
            return region.dependenciesInThisRegion.Select(dependency => dependency.SceneName).ToList();
        }
    }
}