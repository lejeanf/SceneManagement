using System.Collections.Generic;
using UnityEngine;
using jeanf.EventSystem;
using jeanf.universalplayer;
using Unity.Burst;
using Unity.Collections;

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
        
        private readonly List<string> _tempSceneNames = new List<string>();
        private readonly List<Region> _tempRegionsToRemove = new List<Region>();

        [SerializeField] private StringEventChannelSO regionChangeRequestChannel;
        [SerializeField] private SendTeleportTarget sendTeleportTarget;

        [propertyDrawer.ReadOnly] [SerializeField] private Zone _currentPlayerZone;
        [propertyDrawer.ReadOnly] [SerializeField] private Region _currentPlayerRegion;
        
        private static WorldManager Instance;
        private static bool _isRegionTransitioning = false;

        public delegate void InitCompleteDelegate(bool status);
        public static InitCompleteDelegate InitComplete;
        
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
            FadeMask.FadeValue(false);
            Instance = this;
            _sceneLoader = this.GetComponent<SceneLoader>();
            Init();
        }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();
        
        private void Subscribe()
        {
            LoadPersistentSubScenes.PersistentLoadingComplete += SetSubSceneLoadedState;
            SceneLoader.IsInitialLoadComplete += SetDependencyLoadedState;
            regionChangeRequestChannel.OnEventRaised += OnRegionChange;
            RequestRegionChange += OnRegionChange;
            ResetWorld += Init;
            ScenarioManager.OnZoneOverridesChanged += OnZoneOverridesChanged;
        }

        private void Unsubscribe()
        {
            LoadPersistentSubScenes.PersistentLoadingComplete -= SetSubSceneLoadedState;
            SceneLoader.IsInitialLoadComplete -= SetDependencyLoadedState;
            regionChangeRequestChannel.OnEventRaised -= OnRegionChange;
            RequestRegionChange -= OnRegionChange;
            ResetWorld -= Init;
            ScenarioManager.OnZoneOverridesChanged -= OnZoneOverridesChanged;
        }

        private bool isSubscenesLoaded = false;
        private bool isDepedencyLoaded = false;
        private void CheckIfInitialLoadIsComplete()
        {
            if (isSubscenesLoaded && isDepedencyLoaded)
            {
                NoPeeking.SetIsLoadingState(true);
                InitComplete?.Invoke(true);
            }
        }

        private void SetSubSceneLoadedState(bool state)
        {
            isSubscenesLoaded = state;
            CheckIfInitialLoadIsComplete();
        }
        private void SetDependencyLoadedState(bool state)
        {
            isDepedencyLoaded = state;
            CheckIfInitialLoadIsComplete();
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
            
            for (int i = 0; i < ListOfRegions.Count; i++)
            {
                var region = ListOfRegions[i];
                if (region == null) continue;
                if (!_regionDictionary.TryAdd(region.id, region)) continue;
                
                _dependenciesPerRegion.TryAdd(region.id, region.dependenciesInThisRegion);
                PrecompileSceneList(region);
                
                if (region.scenariosInThisRegion != null)
                {
                    for (int j = 0; j < region.scenariosInThisRegion.Count; j++)
                    {
                        var scenario = region.scenariosInThisRegion[j];
                        if (scenario != null)
                        {
                            ScenarioManager.ScenarioDictionary.TryAdd(scenario.id, scenario);
                        }
                    }
                }

                if (region.zonesInThisRegion != null)
                {
                    for (int j = 0; j < region.zonesInThisRegion.Count; j++)
                    {
                        var zone = region.zonesInThisRegion[j];
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
            
            for (int i = 0; i < region.dependenciesInThisRegion.Count; i++)
            {
                sceneNames.Add(region.dependenciesInThisRegion[i].SceneName);
            }
            _compiledSceneLists[region.id] = sceneNames;
        }
        
        private void BuildLandingZoneCache()
        {
            var connectivity = FindFirstObjectByType<RegionConnectivityAuthoring>();
            if (connectivity?.regionConnectivity?.landingZones == null) return;
            
            for (int i = 0; i < connectivity.regionConnectivity.landingZones.Count; i++)
            {
                var landing = connectivity.regionConnectivity.landingZones[i];
                if (landing?.landingZone != null)
                {
                    _landingZoneIds.Add(landing.landingZone.id);
                }
            }
        }
        
        public static void NotifyZoneChangeFromECS(FixedString128Bytes zoneId)
        {
            if (Instance == null || _isRegionTransitioning || zoneId.IsEmpty) return;
            var zoneIdString = zoneId.ToString();
            Instance.OnZoneChangedFromECS(zoneIdString);
        }
        
        public static void NotifyRegionChangeFromECS(FixedString128Bytes regionId)
        {
            if (Instance == null || _isRegionTransitioning || regionId.IsEmpty) return;
            var regionIdString = regionId.ToString();
            Instance.OnRegionChangedFromECS(regionIdString);
        }

        private void OnZoneChangedFromECS(FixedString128Bytes id)
        {
            var zoneId = id.ToString();
            if (zoneId == _lastNotifiedZone) return;
            if (string.IsNullOrEmpty(zoneId)) return;
            if (!_zoneDictionary.TryGetValue(zoneId, out var zone)) return;

            _lastNotifiedZone = zoneId;
            _currentPlayerZone = zone;
            
            PublishCurrentZoneId?.Invoke(zone.id);
            PublishAppList(zone);
        }
        
        private void OnRegionChangedFromECS(FixedString128Bytes id)
        {
            var regionId = id.ToString();
            if (regionId == _lastNotifiedRegion) return;
            if (string.IsNullOrEmpty(regionId)) return;
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
            if (_currentPlayerRegion == region && hasGameBeenInitialized)
            {
                Debug.Log($"[WorldManager] The player is already in the requested region: {region.id} --- ignoring the request.");
                return;
            }

            _isRegionTransitioning = true;
            
            _currentPlayerRegion = region;
            _lastNotifiedRegion = region.id;
            
            PublishCurrentRegionId?.Invoke(_currentPlayerRegion.id);

            _tempRegionsToRemove.Clear();
            
            for (int i = 0; i < _activeRegions.Count; i++)
            {
                var removedRegion = RequestUnLoadForObsoleteRegion(_activeRegions[i]);
                _tempRegionsToRemove.Add(removedRegion);
            }

            for (int i = 0; i < _tempRegionsToRemove.Count; i++)
            {
                _activeRegions.Remove(_tempRegionsToRemove[i]);
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
            if (!zone) return;

            var listToBroadcast = zone.DefaultAppsInZone;
            if (ScenarioManager.activeOverridesPerZone.TryGetValue(zone.id, out var value))
            {
                listToBroadcast = value;
            }

            if (listToBroadcast.Count == 0)
            {
                Debug.Log($"[WorldManager] listToBroadcast (apps) is empty.");
                return;
            }

            _broadcastAppList?.Invoke(listToBroadcast);
        }

        private void RequestLoadForRegionDependencies(Region region)
        {
            if (!_compiledSceneLists.TryGetValue(region.id, out var sceneNames) || sceneNames.Count <= 0) return;
            
            for (int i = 0; i < sceneNames.Count; i++)
            {
                _sceneLoader.LoadSceneRequest(sceneNames[i]);
            }
        }
        
        private Region RequestUnLoadForObsoleteRegion(Region region)
        {
            if (_compiledSceneLists.TryGetValue(region.id, out var sceneNames))
            {
                for (int i = 0; i < sceneNames.Count; i++)
                {
                    _sceneLoader.UnLoadSceneRequest(sceneNames[i]);
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