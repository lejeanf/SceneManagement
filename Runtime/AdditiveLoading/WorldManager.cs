using System.Collections.Generic;
using System.Collections;
using System.Linq;
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

        [Header("Initial Region")]
        [Tooltip("Optional: Region to load automatically at startup. If not set, relies on ECS volume detection.")]
        [SerializeField] private Region initialRegion;
        [Tooltip("Optional: Zone to set as current at startup. Should be a zone within the initial region.")]
        [SerializeField] private Zone initialZone;
        
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
        public static bool IsRegionTransitioning => _isRegionTransitioning;
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

        private SpawnPos? _pendingTeleport = null;
        private bool _isWaitingForScenesToLoad = false;
        
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

        public delegate void SendId(string newRegionID);
        public static SendId RequestRegionChange;
        public static SendId PublishCurrentRegionId;
        public static SendId PublishCurrentZoneId;

        public delegate void Reset();
        public static Reset ResetWorld;

        public delegate void BroadcastAppList(List<AppType> list);
        public static BroadcastAppList _broadcastAppList;

        public delegate void BroadcastRegion(Region region);
        public static BroadcastRegion PublishCurrentRegion;

        public static bool IsInitialized { get; private set; }

        private void Awake()
        {
            Instance = this;
            _sceneLoader = this.GetComponent<SceneLoader>();

            NoPeeking.SetIsLoadingState(true);

            FadeMask.SetStateLoading();
    
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
            SceneLoader.LoadComplete += OnSceneLoadComplete;
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
            SceneLoader.LoadComplete -= OnSceneLoadComplete;
            regionChangeRequestChannel.OnEventRaised -= OnRegionChange;
            RequestRegionChange -= OnRegionChange;
            ResetWorld -= Init;
            ScenarioManager.OnZoneOverridesChanged -= OnZoneOverridesChanged;

            if (_isQuitting || _sceneLoader == null) return;
            foreach (var dependency in worldDependencies)
            {
                _sceneLoader.UnLoadSceneRequest(dependency.Address);
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

            // Load initial region if specified
            if (initialRegion != null && _currentPlayerRegion == null)
            {
                if (isDebug) Debug.Log($"[WorldManager] Loading initial region: {initialRegion.levelName} ({initialRegion.id})");
                if (isDebug) Debug.Log($"[WorldManager] SpawnPosOnInit: {initialRegion.SpawnPosOnInit.position}");
                if (isDebug) Debug.Log($"[WorldManager] SpawnPosOnRegionChangeRequest: {initialRegion.SpawnPosOnRegionChangeRequest.position}");

                // Set initial zone if specified
                if (initialZone != null)
                {
                    _currentPlayerZone = initialZone;
                    _lastNotifiedZone = initialZone.id;
                    PublishCurrentZoneId?.Invoke(initialZone.id);
                    PublishAppList(initialZone);
                    if (isDebug) Debug.Log($"[WorldManager] Set initial zone: {initialZone.zoneName} ({initialZone.id})");
                }

                // OnRegionChange is async void — don't set firstLoadCompleted or fade here,
                // CompleteRegionTransition handles fade-out after teleport is done
                OnRegionChange(initialRegion);
                return;
            }

            SetLoadingComplete(LoadingSource.InitialRegion, true);
            FadeEventChannel?.RaiseEvent(false, 1.0f);
            firstLoadCompleted = true;
        }

        private void LoadWorldDependenciesInternal()
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

                if (string.IsNullOrEmpty(dependency.Address))
                {
                    Debug.LogError($"Dependency has null or empty Address: {dependency}");
                    continue;
                }

                try
                {
                    _sceneLoader.LoadSceneRequest(dependency.Address);
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Exception calling LoadSceneRequest for {dependency.Address}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        public static async UniTask LoadWorldDependencies()
        {
            if (Instance == null) return;
            Instance.LoadWorldDependenciesInternal();
            await UniTask.WaitUntil(AreWorldDependenciesLoaded);
        }

        public static async UniTask LoadRegion(Region region)
        {
            if (Instance == null) return;
            Instance.RequestLoadForRegionDependencies(region);
            await UniTask.WaitUntil(() =>
                Instance._sceneLoader != null &&
                !Instance._sceneLoader.IsCurrentlyLoading() &&
                Instance._sceneLoader.GetPendingOperationCount() == 0);
        }

        public static void SpawnPlayer(SpawnPos spawnPos)
        {
            Instance?.PerformTeleport(spawnPos);
        }

        private void SetSubSceneLoadedState(bool state)
        {
            SetLoadingComplete(LoadingSource.PersistentSubScenes, state);
            CheckIfInitialLoadIsComplete();
        }

        private void SetDependencyLoadedState(bool state)
        {
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
            IsInitialized = false;

            InitializeLoadingStates();

            ClearAllMappings();
            BuildDataMappings();
            LoadWorldDependenciesInternal();
        }

        public static void SetInitialLocation(Region region, Zone zone)
        {
            if (Instance == null) return;
            Instance._currentPlayerRegion = region;
            Instance._currentPlayerZone   = zone ?? region.zonesInThisRegion.FirstOrDefault();

            PublishCurrentRegionId?.Invoke(region.id);
            PublishCurrentRegion?.Invoke(region);
            PublishCurrentZoneId?.Invoke(Instance._currentPlayerZone?.id ?? default);
            IsInitialized = true;
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
                sceneNames.Add(region.dependenciesInThisRegion[i].Address);
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
            Instance.OnZoneChangedFromECS(zoneId);
        }
        
        public static void NotifyRegionChangeFromECS(FixedString128Bytes regionId)
        {
            if (Instance == null || _isRegionTransitioning || regionId.IsEmpty) return;
            Instance.OnRegionChangedFromECS(regionId);
        }

        private void OnZoneChangedFromECS(FixedString128Bytes id)
        {
            var zoneId = id.ToString();
            if (zoneId == _lastNotifiedZone) return;
            if (string.IsNullOrEmpty(zoneId)) return;
            if (!_zoneDictionary.TryGetValue(zoneId, out var zone)) return;

            if (zone != null && !zone.IsAccessible()) return;

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

            _lastNotifiedRegion  = regionId;
            _currentPlayerRegion = region;

            PublishCurrentRegionId?.Invoke(region.id);
            PublishCurrentRegion?.Invoke(region);
            RequestLoadForRegionDependencies(region);
        }
        
        private void OnZoneOverridesChanged(string zoneId)
        {
            if (CurrentPlayerZone != null && CurrentPlayerZone.id == zoneId)
            {
                PublishAppList(CurrentPlayerZone);
            }
        }

        private void OnSceneLoadComplete(bool isComplete)
        {
            if (isDebug) Debug.Log($"[WorldManager] OnSceneLoadComplete called. isComplete: {isComplete}, _isWaitingForScenesToLoad: {_isWaitingForScenesToLoad}, HasPendingTeleport: {_pendingTeleport.HasValue}");

            PublishAppList();

            // Check if we have a pending teleport and scenes are done loading
            if (_isWaitingForScenesToLoad && _pendingTeleport.HasValue)
            {
                if (isDebug) Debug.Log($"[WorldManager] Checking scene loader state - IsLoading: {_sceneLoader?.IsCurrentlyLoading()}, Pending: {_sceneLoader?.GetPendingOperationCount()}");

                // Verify all scenes are actually loaded
                if (_sceneLoader != null && !_sceneLoader.IsCurrentlyLoading() && _sceneLoader.GetPendingOperationCount() == 0)
                {
                    if (isDebug) Debug.Log($"[WorldManager] All scenes loaded! Executing pending teleport to: {_pendingTeleport.Value.position}");

                    PerformTeleport(_pendingTeleport.Value);

                    _pendingTeleport = null;
                    _isWaitingForScenesToLoad = false;
                }
                else
                {
                    if (isDebug) Debug.Log($"[WorldManager] Cannot teleport yet - scenes still loading. IsLoading: {_sceneLoader?.IsCurrentlyLoading()}, Pending: {_sceneLoader?.GetPendingOperationCount()}");
                }
            }
            else
            {
                if (isDebug && (_isWaitingForScenesToLoad || _pendingTeleport.HasValue))
                {
                    Debug.Log($"[WorldManager] Not ready to teleport - _isWaitingForScenesToLoad: {_isWaitingForScenesToLoad}, HasPendingTeleport: {_pendingTeleport.HasValue}");
                }
            }
        }

        private void PerformTeleport(SpawnPos spawnPos)
        {
            if (sendTeleportTarget == null)
            {
                Debug.LogWarning("[WorldManager] Cannot teleport - sendTeleportTarget is null");
                return;
            }

            if (isDebug) Debug.Log($"[WorldManager] Executing teleport to position: {spawnPos.position}");

            sendTeleportTarget.transform.position = spawnPos.position;
            sendTeleportTarget.transform.rotation = Quaternion.Euler(spawnPos.rotation);
            sendTeleportTarget.Teleport(false);

            if (isDebug) Debug.Log($"[WorldManager] Teleport completed. Transform position: {sendTeleportTarget.transform.position}");
        }

        private void OnRegionChange(string newRegionID)
        {
            if (!_regionDictionary.TryGetValue(newRegionID, out var region)) return;
            OnRegionChange(region);
        }
        
        private async void OnRegionChange(Region region)
        {
            if (_currentPlayerRegion == region && hasGameBeenInitialized)
            {
                if (isDebug) Debug.Log($"[WorldManager] The player is already in the requested region: {region.id} --- ignoring the request.");
                return;
            }

            // Fade to black FIRST
            FadeMask.SetStateLoading();
            if (isDebug) Debug.Log("[WorldManager] Fading to black before region change...");
            
            // Wait for fade to complete
            await UniTask.Delay(System.TimeSpan.FromSeconds(0.2f));

            // Now start the region change while screen is black
            FadeEventChannel?.RaiseEvent(true, 0.1f);

            _isRegionTransitioning = true;

            _currentPlayerRegion = region;
            _lastNotifiedRegion = region.id;

            PublishCurrentRegionId?.Invoke(_currentPlayerRegion.id);
            PublishCurrentRegion?.Invoke(region);

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

            var hasLoadRequests = RequestLoadForRegionDependencies(region);
            var hasActiveOperations = _sceneLoader.IsCurrentlyLoading() || _sceneLoader.GetPendingOperationCount() > 0;

            var spawnPos = SetTeleportTarget(region, hasGameBeenInitialized);

            if (hasLoadRequests || hasActiveOperations)
            {
                _pendingTeleport = spawnPos;
                _isWaitingForScenesToLoad = true;
                if (isDebug) Debug.Log($"[WorldManager] OnRegionChange: Pending teleport to {spawnPos.position} set. Waiting for scenes to load...");
            }
            else
            {
                PerformTeleport(spawnPos);
                if (isDebug) Debug.Log($"[WorldManager] OnRegionChange: No pending operations, teleporting immediately to {spawnPos.position}");
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
        }
        private async UniTaskVoid CompleteRegionTransition()
        {
            while (_isWaitingForScenesToLoad || _pendingTeleport.HasValue)
            {
                await UniTask.Delay(50);
                if (isDebug) Debug.Log($"[WorldManager] Waiting for teleport to complete before ending region transition...");
            }

            await UniTask.NextFrame();
            await UniTask.NextFrame();

            _isRegionTransitioning = false;

            if (isDebug) Debug.Log($"[WorldManager] Region transition complete. Fading back to clear...");
    
            FadeMask.SetStateClear();

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
            if (isDebug) Debug.Log($"[WorldManager] SetTeleportTarget called for region: {region.levelName} (ID: {region.id})");

            var spawnPos = new SpawnPos(region.SpawnPosOnRegionChangeRequest.position, region.SpawnPosOnRegionChangeRequest.rotation);
            if (hasRegionBeenInitialized)
            {
                if (isDebug) Debug.Log($"[WorldManager] SetTeleportTarget: Using SpawnPosOnRegionChangeRequest: {spawnPos.position}");
                return spawnPos;
            }

            spawnPos.position = region.SpawnPosOnInit.position;
            spawnPos.rotation = region.SpawnPosOnInit.rotation;
            hasGameBeenInitialized = true;

            if (isDebug) Debug.Log($"[WorldManager] SetTeleportTarget: Using SpawnPosOnInit (first load): {spawnPos.position}");
            if (isDebug) Debug.Log($"[WorldManager] SetTeleportTarget: Region.SpawnPosOnInit raw value: {region.SpawnPosOnInit.position}");
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

        private bool RequestLoadForRegionDependencies(Region region)
        {
            if (!_compiledSceneLists.TryGetValue(region.id, out var sceneNames) || sceneNames.Count <= 0)
            {
                if (isDebug) Debug.Log($"[WorldManager] No region dependencies to load for region: {region.id}");
                return false;
            }

            if (isDebug) Debug.Log($"[WorldManager] Requesting load for {sceneNames.Count} region dependencies");

            for (int i = 0; i < sceneNames.Count; i++)
            {
                if (isDebug) Debug.Log($"[WorldManager] Loading region scene: {sceneNames[i]}");
                _sceneLoader.LoadSceneRequest(sceneNames[i]);
            }

            if (isDebug) Debug.Log($"[WorldManager] All region scene load requests submitted. SceneLoader state - IsLoading: {_sceneLoader.IsCurrentlyLoading()}, Pending: {_sceneLoader.GetPendingOperationCount()}");
            return true;
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