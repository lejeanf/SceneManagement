using System.Collections.Generic;
using System.Collections;
using Cysharp.Threading.Tasks;
using UnityEngine;
using jeanf.EventSystem;
using jeanf.universalplayer;
using Unity.Collections;

namespace jeanf.scenemanagement
{
    [RequireComponent(typeof(SceneLoader))]
    [RequireComponent(typeof(ScenarioManager))]
    public class WorldManager : MonoBehaviour
    {
        public enum LoadingSource
        {
            WorldDependencies,
            PersistentSubScenes,
            InitialRegion
        }

        public bool isDebug = false;
        private SceneLoader _sceneLoader;
        [SerializeField] private List<SceneReference> worldDependencies = new List<SceneReference>();
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
        [SerializeField] private BoolFloatEventChannelSO FadeEventChannel;
        private static WorldManager Instance;
        private static bool _isRegionTransitioning = false;

        [Header("Loading Coordination")]
        [SerializeField] private bool trackInitialLoading = true;
        private readonly Dictionary<LoadingSource, bool> _loadingStates = new Dictionary<LoadingSource, bool>();
        private bool _allInitialLoadingComplete = false;

        private bool hasGameBeenInitialized = false;
        private string _lastNotifiedZone = "";
        private string _lastNotifiedRegion = "";

        public delegate void InitCompleteDelegate(bool status);
        public static InitCompleteDelegate InitComplete;
        private bool firstLoadCompleted = false;
        private bool _isQuitting = false;
        
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

        private void Awake()
        {
            Instance = this;
            _sceneLoader = this.GetComponent<SceneLoader>();

            NoPeeking.SetIsLoadingState(true);

            FadeMask.PrepareVolumeProfile(FadeMask.FadeType.Loading);
            FadeMask.SetVolumeWeight(1.0f);
    
            InitializeLoadingStates();
        }

        private void Start()
        {
            Init();
        }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();
        
        private void Subscribe()
        {
            LoadPersistentSubScenes.PersistentLoadingComplete += SetSubSceneLoadedState;
            SceneLoader.IsInitialLoadComplete += SetDependencyLoadedState;
            SceneLoader.LoadComplete += ctx => PublishAppList();
            regionChangeRequestChannel.OnEventRaised += OnRegionChange;
            RequestRegionChange += OnRegionChange;
            ResetWorld += Init;
            ScenarioManager.OnZoneOverridesChanged += OnZoneOverridesChanged;
        }
        private void OnApplicationQuit()
        {
            _isQuitting = true;
        }

        private void Unsubscribe()
        {
            LoadPersistentSubScenes.PersistentLoadingComplete -= SetSubSceneLoadedState;
            SceneLoader.IsInitialLoadComplete -= SetDependencyLoadedState;
            SceneLoader.LoadComplete -= ctx => PublishAppList();
            regionChangeRequestChannel.OnEventRaised -= OnRegionChange;
            RequestRegionChange -= OnRegionChange;
            ResetWorld -= Init;
            ScenarioManager.OnZoneOverridesChanged -= OnZoneOverridesChanged;

            if (_isQuitting || _sceneLoader == null) return;
            foreach (var dependency in worldDependencies)
            {
                _sceneLoader.UnLoadSceneRequest(dependency.SceneName);
            }
        }

        private void InitializeLoadingStates()
        {
            if (!trackInitialLoading) return;
    
            _loadingStates.Clear();
            foreach (LoadingSource source in System.Enum.GetValues(typeof(LoadingSource)))
            {
                _loadingStates[source] = false;
            }
            _allInitialLoadingComplete = false;
    
            if (isDebug) Debug.Log("[WorldManager] Loading states initialized");
        }

        public static void SetLoadingComplete(LoadingSource source, bool isComplete)
        {
            if (Instance != null)
            {
                Instance.UpdateLoadingState(source, isComplete);
            }
        }

        private void UpdateLoadingState(LoadingSource source, bool isComplete)
        {
            if (!trackInitialLoading) return;
    
            if (!_loadingStates.ContainsKey(source))
            {
                if (isDebug) Debug.LogWarning($"[WorldManager] Unknown loading source: {source}");
                return;
            }
    
            bool previousState = _loadingStates[source];
            _loadingStates[source] = isComplete;
    
            if (isDebug && previousState != isComplete)
            {
                Debug.Log($"[WorldManager] {source} loading state changed to: {isComplete}");
            }
    
            CheckAllInitialLoadingComplete();
        }

        private void CheckAllInitialLoadingComplete()
        {
            if (!trackInitialLoading || _allInitialLoadingComplete) return;
    
            bool allComplete = true;
            foreach (var kvp in _loadingStates)
            {
                if (!kvp.Value)
                {
                    allComplete = false;
                    if (isDebug) Debug.Log($"[WorldManager] Still waiting for: {kvp.Key}");
                    break;
                }
            }
    
            if (allComplete)
            {
                _allInitialLoadingComplete = true;
                if (isDebug) Debug.Log("[WorldManager] All initial loading sources complete!");
        
                WaitForFinalSceneOperations().Forget();
            }
        }

        private async UniTaskVoid WaitForFinalSceneOperations()
        {
            if (_sceneLoader == null)
            {
                Debug.LogError("[WorldManager] SceneLoader is null!");
                return;
            }
    
            await UniTask.Delay(100);
    
            while (_sceneLoader.IsCurrentlyLoading() || _sceneLoader.GetPendingOperationCount() > 0)
            {
                if (isDebug) 
                    Debug.Log($"[WorldManager] Waiting for SceneLoader - IsLoading: {_sceneLoader.IsCurrentlyLoading()}, Pending: {_sceneLoader.GetPendingOperationCount()}");
                await UniTask.Delay(100);
            }
    
            await UniTask.Delay(200);
    
            if (isDebug) Debug.Log("[WorldManager] All scene operations complete - triggering fade out");
    
            if (!_isRegionTransitioning)
            {
                NoPeeking.SetIsLoadingState(false);
            }
        }

        private void CheckIfInitialLoadIsComplete()
        {
            bool subscenesLoaded = _loadingStates.TryGetValue(LoadingSource.PersistentSubScenes, out bool subValue) && subValue;
            bool dependenciesLoaded = _loadingStates.TryGetValue(LoadingSource.WorldDependencies, out bool depValue) && depValue;
            
            if (!subscenesLoaded || !dependenciesLoaded) return;

            //FadeMask.TogglePPE?.Invoke(true);
            InitComplete?.Invoke(true);

            if (isDebug) Debug.Log("[WorldManager] Initial load dependencies complete");

            SetLoadingComplete(LoadingSource.InitialRegion, true);
            FadeEventChannel?.RaiseEvent(false, 1.0f);
            FadeMask.TogglePPE.Invoke(true);
            firstLoadCompleted = true;
        }

        private void LoadWorldDependencies()
        {
            if (_sceneLoader == null) return;
            if (worldDependencies == null) return;
    
            if (worldDependencies.Count == 0)
            {
                SetDependencyLoadedState(true);
                return;
            }
    
            foreach (var dependency in worldDependencies)
            {
                if (dependency == null)
                {
                    Debug.LogError("Found null dependency in worldDependencies list");
                    continue;
                }
        
                if (string.IsNullOrEmpty(dependency.SceneName))
                {
                    Debug.LogError($"Dependency has null or empty SceneName: {dependency}");
                    continue;
                }
        
                Debug.Log($"[WorldManager] Loading world dependency: {dependency.SceneName}");
        
                try
                {
                    _sceneLoader.LoadSceneRequest(dependency.SceneName);
                    Debug.Log($"Successfully called LoadSceneRequest for: {dependency.SceneName}");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Exception calling LoadSceneRequest for {dependency.SceneName}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private void SetSubSceneLoadedState(bool state)
        {
            if (isDebug) Debug.Log($"[WorldManager] SetSubSceneLoadedState: {state}");
    
            SetLoadingComplete(LoadingSource.PersistentSubScenes, state);
    
            CheckIfInitialLoadIsComplete();
        }

        private void SetDependencyLoadedState(bool state)
        {
            if (isDebug) Debug.Log($"[WorldManager] SetDependencyLoadedState: {state}");
    
            SetLoadingComplete(LoadingSource.WorldDependencies, state);
    
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
            
            InitializeLoadingStates();

            ClearAllMappings();
            BuildDataMappings();
            LoadWorldDependencies();
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
            FadeMask.TogglePPE.Invoke(false);
            FadeEventChannel?.RaiseEvent(true, 0.1f);

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
            if (firstLoadCompleted)
            {
                StartCoroutine(FadeOnRegionChange());
            }
            CompleteRegionTransition().Forget();
        }

        IEnumerator FadeOnRegionChange()
        {
            yield return new WaitForSeconds(1.0f);
            FadeEventChannel?.RaiseEvent(false, 1.0f);
            FadeMask.TogglePPE.Invoke(true);
        }
        private async UniTaskVoid CompleteRegionTransition()
        {
            await UniTask.NextFrame();
            await UniTask.NextFrame();
            
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

        //Used post scene loading
        private void PublishAppList()
        {
            if (CurrentPlayerZone == null) return;
            var listToBroadcast = CurrentPlayerZone.DefaultAppsInZone;
            if (ScenarioManager.activeOverridesPerZone.TryGetValue(CurrentPlayerZone.id, out var value))
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

        public static bool IsLoadingComplete(LoadingSource source)
        {
            if (Instance == null) return false;
            return Instance._loadingStates.TryGetValue(source, out bool value) && value;
        }

        public static bool ArePersistentSubScenesLoaded()
        {
            return IsLoadingComplete(LoadingSource.PersistentSubScenes);
        }

        public static bool AreWorldDependenciesLoaded()
        {
            return IsLoadingComplete(LoadingSource.WorldDependencies);
        }
    }
}