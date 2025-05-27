using System.Collections.Generic;
using UnityEngine;
using jeanf.EventSystem;
using jeanf.propertyDrawer;
using jeanf.universalplayer;

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
        private Dictionary<string, List<SceneReference>> _dependenciesPerRegion = new Dictionary<string, List<SceneReference>>();
        private Dictionary<string, List<string>> _compiledSceneLists = new Dictionary<string, List<string>>();
        private HashSet<string> _landingZoneIds = new HashSet<string>();

        private List<Region> _activeRegions = new List<Region>();
        private bool _mappingInitialized = false;
        
        private List<string> _tempSceneNames = new List<string>();
        private List<Region> _tempRegionsToRemove = new List<Region>();

        [SerializeField] private StringEventChannelSO regionChangeRequestChannel;
        [SerializeField] private SendTeleportTarget sendTeleportTarget;

        [ReadOnly] [SerializeField] private Zone _currentPlayerZone;
        [ReadOnly] [SerializeField] private Region _currentPlayerRegion;
        
        private static WorldManager Instance;
        private static bool _isRegionTransitioning = false;
        
        public static Zone CurrentPlayerZone 
        { 
            get => Instance?._currentPlayerZone;
            private set 
            {
                if (Instance != null)
                {
                    Instance._currentPlayerZone = value;
                }
            }
        }
        
        public static Region CurrentPlayerRegion
        { 
            get => Instance?._currentPlayerRegion;
            private set 
            {
                if (Instance != null)
                {
                    Instance._currentPlayerRegion = value;
                }
            }
        }

        public static bool IsRegionTransitioning => _isRegionTransitioning;

        public delegate void SendId(string newRegionID);
        public static SendId RequestRegionChange;
        public static SendId PublishCurrentRegionId;
        public static SendId PublishCurrentZoneId;
        
        public delegate void Reset();
        public static Reset ResetWorld;
        
        public delegate void BroadcastAppList(List<AppType> list);
        public static BroadcastAppList _broadcastAppList;

        private bool hasGameBeenInitialized = false;
        private string _lastNotifiedZone = "";
        private string _lastNotifiedRegion = "";

        private void Awake()
        {
            Instance = this;
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
            ResetWorld += Init;
            ScenarioManager.OnZoneOverridesChanged += OnZoneOverridesChanged;
        }

        private void Unsubscribe()
        {
            regionChangeRequestChannel.OnEventRaised -= OnRegionChange;
            RequestRegionChange -= OnRegionChange;
            ResetWorld -= Init;
            ScenarioManager.OnZoneOverridesChanged -= OnZoneOverridesChanged;
        }

        private void Init()
        {
            hasGameBeenInitialized = false;
            _currentPlayerZone = null;
            _currentPlayerRegion = null;
            _lastNotifiedZone = "";
            _lastNotifiedRegion = "";
            _isRegionTransitioning = false;
            
            ClearAllMappings();
            BuildDataMappings();
        }
        
        private void ClearAllMappings()
        {
            _zoneDictionary.Clear();
            _regionDictionary.Clear();
            _regionDictionaryPerZone.Clear();
            _dependenciesPerRegion.Clear();
            _compiledSceneLists.Clear();
            _landingZoneIds.Clear();
            _activeRegions.Clear();
            _tempSceneNames.Clear();
            _tempRegionsToRemove.Clear();
            _mappingInitialized = false;
        }
        
        private void BuildDataMappings()
        {
            if (_mappingInitialized) return;
            
            var regionCount = ListOfRegions?.Count ?? 0;
            if (regionCount == 0) return;
            
            foreach (var region in ListOfRegions)
            {
                if (region == null) continue;
                if (!_regionDictionary.TryAdd(region.id, region)) continue;
                
                _dependenciesPerRegion.TryAdd(region.id, region.dependenciesInThisRegion);
                PrecompileSceneList(region);
                
                if (region.scenariosInThisRegion != null)
                {
                    foreach (var scenario in region.scenariosInThisRegion)
                    {
                        if (scenario != null)
                        {
                            ScenarioManager.ScenarioDictionary.TryAdd(scenario.id, scenario);
                        }
                    }
                }

                if (region.zonesInThisRegion != null)
                {
                    foreach (var zone in region.zonesInThisRegion)
                    {
                        if (zone != null)
                        {
                            _zoneDictionary.TryAdd(zone.id, zone);
                            _regionDictionaryPerZone.TryAdd(zone.id, region);
                        }
                    }
                }
            }
            
            BuildLandingZoneCache();
            _mappingInitialized = true;
        }
        
        private void PrecompileSceneList(Region region)
        {
            var sceneNames = new List<string>(region.dependenciesInThisRegion.Count);
            foreach (var dependency in region.dependenciesInThisRegion)
            {
                sceneNames.Add(dependency.SceneName);
            }
            _compiledSceneLists[region.id] = sceneNames;
        }
        
        private void BuildLandingZoneCache()
        {
            var connectivity = FindObjectOfType<RegionConnectivityAuthoring>();
            if (connectivity?.regionConnectivity?.landingZones == null) return;
            
            foreach (var landing in connectivity.regionConnectivity.landingZones)
            {
                if (landing?.landingZone != null)
                {
                    _landingZoneIds.Add(landing.landingZone.id);
                }
            }
        }
        
        public static void NotifyZoneChangeFromECS(string zoneId)
        {
            if (Instance != null && !_isRegionTransitioning)
            {
                Instance.OnZoneChangedFromECS(zoneId);
            }
        }
        
        public static void NotifyRegionChangeFromECS(string regionId)
        {
            if (Instance != null && !_isRegionTransitioning)
            {
                Instance.OnRegionChangedFromECS(regionId);
            }
        }

        private void OnZoneChangedFromECS(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId) || _lastNotifiedZone == zoneId) return;
            
            if (!_zoneDictionary.TryGetValue(zoneId, out var zone)) return;

            _lastNotifiedZone = zoneId;
            _currentPlayerZone = zone;
            
            PublishCurrentZoneId?.Invoke(zone.id);
            PublishAppList(zone);
        }
        
        private void OnRegionChangedFromECS(string regionId)
        {
            if (string.IsNullOrEmpty(regionId) || _lastNotifiedRegion == regionId) return;
            
            if (!_regionDictionary.TryGetValue(regionId, out var region)) return;
            
            _lastNotifiedRegion = regionId;
            _currentPlayerRegion = region;
            
            OnRegionChange(region);
        }
        
        private void OnZoneOverridesChanged(string zoneId)
        {
            if (CurrentPlayerZone != null && CurrentPlayerZone.id == zoneId)
            {
                PublishAppList(CurrentPlayerZone);
            }
        }

        private void OnRegionChange(string newRegionID)
        {
            if (!_regionDictionary.TryGetValue(newRegionID, out var region)) return;
            OnRegionChange(region);
        }
        
        private void OnRegionChange(Region region)
        {
            _isRegionTransitioning = true;
            
            _currentPlayerRegion = region;
            _lastNotifiedRegion = region.id;
            
            PublishCurrentRegionId?.Invoke(_currentPlayerRegion.id);

            _tempRegionsToRemove.Clear();
            foreach (var activeRegion in _activeRegions)
            {
                var removedRegion = RequestUnLoadForObsoleteRegion(activeRegion);
                _tempRegionsToRemove.Add(removedRegion);
            }

            foreach (var r in _tempRegionsToRemove)
            {
                _activeRegions.Remove(r);
            }
            
            RequestLoadForRegionDependencies(region);

            var spawnPos = SetTeleportTarget(region, hasGameBeenInitialized);
            
            if (sendTeleportTarget != null)
            {
                sendTeleportTarget.transform.position = spawnPos.position;
                sendTeleportTarget.transform.rotation = Quaternion.Euler(spawnPos.rotation);
                sendTeleportTarget.Teleport();
            }
            
            _activeRegions.Add(region);
            
            StartCoroutine(CompleteRegionTransition());
        }

        private System.Collections.IEnumerator CompleteRegionTransition()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            
            _isRegionTransitioning = false;
            
            if (_currentPlayerRegion?.zonesInThisRegion != null && _currentPlayerRegion.zonesInThisRegion.Count > 0)
            {
                var firstZone = _currentPlayerRegion.zonesInThisRegion[0];
                if (firstZone != null)
                {
                    _currentPlayerZone = firstZone;
                    _lastNotifiedZone = firstZone.id;
                    PublishCurrentZoneId?.Invoke(firstZone.id);
                    PublishAppList(firstZone);
                }
            }
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
            if (ScenarioManager.activeOverridesPerZone.TryGetValue(zone.id, out var value))
            {
                listToBroadcast = value;
            }
            
            _broadcastAppList?.Invoke(listToBroadcast);
        }

        private void RequestLoadForRegionDependencies(Region region)
        {
            if (!_compiledSceneLists.TryGetValue(region.id, out var sceneNames) || sceneNames.Count <= 0) return;
            
            foreach (var sceneName in sceneNames)
            {
                _sceneLoader.LoadSceneRequest(sceneName);
            }
        }
        
        private Region RequestUnLoadForObsoleteRegion(Region region)
        {
            if (_compiledSceneLists.TryGetValue(region.id, out var sceneNames))
            {
                foreach (var sceneName in sceneNames)
                {
                    _sceneLoader.UnLoadSceneRequest(sceneName);
                }
            }
            return region;
        }

        public bool IsLandingZone(string zoneId)
        {
            return _landingZoneIds.Contains(zoneId);
        }

        public HashSet<string> GetLandingZoneIds()
        {
            return _landingZoneIds;
        }

        public static Dictionary<string, Zone> GetZoneDictionary()
        {
            return Instance?._zoneDictionary;
        }

        public static Dictionary<string, Region> GetRegionDictionary()
        {
            return Instance?._regionDictionary;
        }

        public static Dictionary<string, Region> GetRegionDictionaryPerZone()
        {
            return Instance?._regionDictionaryPerZone;
        }
    }
}