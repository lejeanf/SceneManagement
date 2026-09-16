using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using jeanf.EventSystem;
using jeanf.universalplayer;
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

        public delegate void ManageScenarioDelegate(string scenarioId);
        public static ManageScenarioDelegate EndScenarioRequestPrompt;
        public static ManageScenarioDelegate EndScenarioRequest;
        public static ManageScenarioDelegate StartScenarioRequest;
        public static ManageScenarioDelegate RestartScenarioRequest;
        public delegate void OnUnloadScenario();
        public static OnUnloadScenario onUnloadScenario;
        public delegate void UpdateScenariosDelegate(List<string> activeScenarios);
        public static UpdateScenariosDelegate UpdateScenariosList;

        /// <summary>
        /// Evacuation feedback for project UI: the zone the player must leave and how many seconds
        /// are left before they get teleported out. Seconds is negative once the evacuation is
        /// over, whether they walked out or were moved.
        /// </summary>
        public delegate void EvacuationDelegate(string zoneId, float secondsLeft);
        public static EvacuationDelegate PlayerEvacuationCountdown;

        /// <summary>
        /// Where the player has to go, published while an evacuation runs and cleared with null
        /// when it ends. A navigation tooltip bridge listens to this to draw the way out; the
        /// transform is a marker this manager owns, so read it, do not keep it past the clear.
        /// </summary>
        public delegate void EvacuationDestinationDelegate(Transform destination);
        public static EvacuationDestinationDelegate PlayerEvacuationDestination;

        [SerializeField] private bool automaticScenarioUnload = true;
        [SerializeField] private bool isDebug = false;

        [Header("Reset on unload")]
        [Tooltip("What gets put back in order when a scenario unload is requested: objects the scenario teleported, and the player when they stand in a zone that is about to be locked.")]
        [SerializeField] private ScenarioResetOptions resetOptions = new ScenarioResetOptions();

        [Tooltip("SendTeleportTarget used to move the player out of a zone about to be locked. It needs Is Teleport Player ticked - usually the very same one WorldManager uses for region changes.")]
        [SerializeField] private SendTeleportTarget playerEvacuationTarget;

        [Tooltip("OPTIONAL: transform standing in for the player when picking the nearest exit. Defaults to the main camera, which is what the ECS zone detection follows.")]
        [SerializeField] private Transform playerPositionProbe;

        [propertyDrawer.ReadOnly] [SerializeField] private List<string> journaledTeleports = new List<string>();

        private ScenarioTeleportJournal _journal;
        private bool _isTearingDown;
        private float _nextDebugRefresh;
        private Transform _evacuationMarker;

        private void Awake()
        {
            _sceneLoader = this.GetComponent<SceneLoader>();
            _journal = new ScenarioTeleportJournal(
                () => _activeScenarios.Count > 0 ? _activeScenarios[0].id.ToString() : null,
                () => resetOptions);
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
            _journal?.Enable();
        }

        private void Unsubscribe()
        {
            BeginScenarioRequest.OnEventRaised -= LoadScenario;
            EndScenarioRequestSO.OnEventRaised -= OnScenarioEndRequest;
            KillAllScenariosRequest.OnEventRaised -= UnloadAllScenarios;
            StartScenarioRequest -= LoadScenario;
            RestartScenarioRequest -= OnScenarioRestartRequest;
            EndScenarioRequest -= OnScenarioEndRequest;
            _journal?.Disable();
        }

        private void OnScenarioRestartRequest(string scenarioId)
        {
            _ = ScenarioRestartAsync(scenarioId);
        }

        private async Task ScenarioRestartAsync(string scenarioId)
        {
            if (!ScenarioDictionary.TryGetValue(scenarioId, out var scenario)) return;
            if (scenario.enableFadeOnLoad) NoPeeking.SetIsLoadingState(true);

            // Reset first (props home, player out of the zone this scenario locks), then unload.
            await EndScenarioAsync(scenarioId);
            if (!this) return;

            await Task.Delay(500);
            if (!this) return;

            LoadScenario(scenarioId);
            await WaitForSceneLoadingComplete();
            if (scenario.enableFadeOnLoad) NoPeeking.SetIsLoadingState(false); // Handle fade here
        }
        private async Task WaitForSceneLoadingComplete()
        {
            await UniTask.Yield();

            while (_sceneLoader.IsCurrentlyLoading() || _sceneLoader.GetPendingOperationCount() > 0)
            {
                Debug.Log($"[ScenarioManager] Waiting for scene operations - IsLoading: {_sceneLoader.IsCurrentlyLoading()}, Pending: {_sceneLoader.GetPendingOperationCount()}");
                await UniTask.Delay(100);
            }

            await UniTask.Delay(200);
        }

        private void LoadScenario(string scenarioId) => LoadScenarioAsync(scenarioId).Forget();

        private async UniTaskVoid LoadScenarioAsync(string scenarioId)
        {
            if (!ScenarioDictionary.TryGetValue(scenarioId, out var scenario)) return;

            if (_activeScenarios.Contains(scenario))
            {
                Debug.Log($"A request to load scenario with ID: {scenario.scenarioName} has been received but that scenario is already in the list of active scenarios. The request has been denied.");
                return;
            }

            // Check if we need to handle scenario transition (fade out)
            bool isTransition = _activeScenarios.Count > 0;

            if (isTransition)
            {
                // FADE OUT: Trigger fade before unloading
                if (scenario.enableFadeOnLoad) NoPeeking.SetIsLoadingState(true);
                Debug.Log($"Starting scenario transition from {_activeScenarios[0].scenarioName} to {scenario.scenarioName}");
            }

            switch (_activeScenarios.Count)
            {
                // automatic unload previously loaded scenarios
                case > 0 when automaticScenarioUnload:
                    // The reset (props put back, player out of the zone about to be locked) has to
                    // finish BEFORE the next scenario starts loading, or the player is evacuated
                    // into a world that is already being rebuilt around them.
                    await UnloadAllScenariosAsync();
                    if (!this) return;
                    break;
                // if not automatic, send outside request to unload scenarios one by one.
                case > 0 when !automaticScenarioUnload:
                {
                    foreach (var s in _activeScenarios)
                    {
                        EndScenarioRequestPrompt?.Invoke(s.id);
                    }
                    break;
                }
            }

            // Apply zone overrides
            foreach (var zoneOverride in ScenarioDictionary[scenarioId].ZoneOverrides)
            {
                if (activeOverridesPerZone.ContainsKey(zoneOverride.zone.id)) continue;
                if (zoneOverride.AppsForThisZone_Override.Count > 0)
                    activeOverridesPerZone.Add(zoneOverride.zone.id, zoneOverride.AppsForThisZone_Override);
                OnZoneOverridesChanged?.Invoke(zoneOverride.zone.id);
            }

            // Load new scenario scenes
            foreach (var scene in CompileSceneList(scenario))
            {
                _sceneLoader.LoadSceneRequest(scene);
            }

            _activeScenarios.Add(scenario);
            PublishScenarioList();

            // If this was a transition, handle fade in after loading completes
            if (isTransition)
            {
                HandleScenarioTransitionComplete(scenario).Forget();
            }
        }
        private async UniTaskVoid HandleScenarioTransitionComplete(Scenario scenario)
        {
            // Wait for scene loading to complete
            await WaitForSceneLoadingComplete();

            // FADE IN: Clear loading state to trigger fade in
            if (!WorldManager.IsRegionTransitioning)
            {
                if (scenario.enableFadeOnLoad) NoPeeking.SetIsLoadingState(false);
                Debug.Log("Scenario transition complete - fading in");
            }
        }


        private void OnScenarioEndRequest(string scenarioID) => EndScenarioAsync(scenarioID).Forget();

        private async UniTask EndScenarioAsync(string scenarioID)
        {
            // Only a scenario that is actually running has something to reset; an unknown or
            // already-finished id still runs the tail below, exactly as it always did.
            if (ScenarioDictionary.TryGetValue(scenarioID, out var scenario) && _activeScenarios.Contains(scenario))
            {
                await TeardownAsync(new List<Scenario> { scenario });
                if (!this) return;
            }

            _activeScenarios.Remove(UnloadScenario(scenarioID));
            PublishScenarioList();
            onUnloadScenario?.Invoke();

            if (!WorldManager.IsRegionTransitioning)
            {
                NoPeeking.SetIsLoadingState(false);
            }
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

        private void UnloadAllScenarios() => UnloadAllScenariosAsync().Forget();

        private async UniTask UnloadAllScenariosAsync()
        {
            if (_activeScenarios.Count == 0) return;

            var scenariosToRemove = new List<Scenario>(_activeScenarios);

            await TeardownAsync(scenariosToRemove);
            if (!this) return;

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

            PublishScenarioList();
        }

        private void PublishScenarioList()
        {
            var scenarioList = _activeScenarios.Select(scenario => scenario.id.ToString()).ToList();
            UpdateScenariosList?.Invoke(scenarioList);
        }

        private static List<string> CompileSceneList(Scenario scenario)
        {
            var requiredScenes = new List<string> { scenario.scene.Address };
            requiredScenes.AddRange(scenario.dependenciesInThisScenario.Select(dependency => dependency.Address));
            return requiredScenes;
        }

        #region Reset on unload

        /// <summary>
        /// Everything that has to happen BEFORE the scenario scenes go away: props the scenario
        /// teleported go home, and the player leaves the zones this unload locks - on their own if
        /// they are quick enough, by teleport otherwise.
        /// </summary>
        private async UniTask TeardownAsync(IReadOnlyList<Scenario> scenarios)
        {
            // A second end request landing mid-teardown waits for the first one instead of
            // starting a second evacuation.
            if (_isTearingDown) await UniTask.WaitWhile(() => _isTearingDown);
            if (!this) return;

            _isTearingDown = true;
            try
            {
                // 0 means "no limit": the scenes wait for the player rather than unloading under them.
                var deadline = resetOptions.maxTeardownTimeout > 0f
                    ? Time.realtimeSinceStartup + resetOptions.maxTeardownTimeout
                    : float.MaxValue;

                if (resetOptions.restoreTeleportedObjects)
                {
                    var scenarioIds = new HashSet<string>();
                    foreach (var scenario in scenarios)
                        if (scenario != null) scenarioIds.Add(scenario.id);

                    var restored = _journal.Restore(scenarioIds, resetOptions.resetVelocityOnRestore, isDebug);
                    if (restored > 0)
                        Debug.Log($"[ScenarioManager] {restored} teleported object(s) put back to their pre-scenario position.");
                }

                await EvacuatePlayerAsync(scenarios, deadline);
            }
            catch (Exception e)
            {
                // A failed reset must never strand a scenario in the loaded state.
                Debug.LogException(e, this);
            }
            finally
            {
                _isTearingDown = false;
            }
        }

        private async UniTask EvacuatePlayerAsync(IReadOnlyList<Scenario> scenarios, float deadline)
        {
            if (resetOptions.evacuationMode == ScenarioResetOptions.EvacuationMode.Disabled) return;

            var lockedZoneIds = CollectEvacuationZoneIds(scenarios);
            if (lockedZoneIds.Count == 0) return;
            if (!IsPlayerInLockedZone(lockedZoneIds, out var playerZoneId)) return;

            // A zone nobody declared an exit for is a zone that does not lock (typically one with
            // no door): there is nowhere to evacuate towards, and nothing to protect the player from.
            if (!ZoneEvacuationRegistry.TryResolveExit(playerZoneId, PlayerPosition, lockedZoneIds, out _))
            {
                if (isDebug)
                    Debug.Log($"[ScenarioManager] the player is in zone '{playerZoneId}' but no exit is registered for " +
                              "it (no door side declares it, no ZoneExitAnchor serves it) - treated as a zone that does " +
                              "not lock, unloading without moving anybody.");
                return;
            }

            if (resetOptions.evacuationMode == ScenarioResetOptions.EvacuationMode.WaitThenForce &&
                resetOptions.gracePeriod > 0f)
            {
                var graceEnd = Time.realtimeSinceStartup + resetOptions.gracePeriod;
                var pollMs = Mathf.Max(50, Mathf.RoundToInt(resetOptions.pollInterval * 1000f));
                var warningShown = false;

                Debug.Log($"[ScenarioManager] unload held: the player is in '{playerZoneId}', which this scenario locks. " +
                          $"Waiting up to {resetOptions.gracePeriod:F0}s for them to walk out.");

                Announce(resetOptions.evacuationMessage);
                PublishEvacuationDestination(lockedZoneIds, playerZoneId);

                // Nothing cuts this short while the player is inside: the notice stays up until
                // they are out, and the wait ends on the grace period, which the forced teleport
                // then resolves. maxTeardownTimeout is an opt-in ceiling, off by default.
                while (Time.realtimeSinceStartup < graceEnd)
                {
                    if (!IsPlayerInLockedZone(lockedZoneIds, out playerZoneId))
                    {
                        Debug.Log("[ScenarioManager] the player left the zone on their own - unloading.");
                        ClearEvacuationNotice();
                        return;
                    }

                    var secondsLeft = graceEnd - Time.realtimeSinceStartup;

                    if (!warningShown && secondsLeft <= resetOptions.warningDuration)
                    {
                        warningShown = true;
                        Announce(resetOptions.evacuationWarningMessage);
                    }

                    resetOptions.evacuationCountdownChannel?.RaiseEvent(secondsLeft);
                    PlayerEvacuationCountdown?.Invoke(playerZoneId, secondsLeft);

                    if (resetOptions.maxTeardownTimeout > 0f && Time.realtimeSinceStartup >= deadline)
                    {
                        Debug.LogError($"[ScenarioManager] teardown timeout ({resetOptions.maxTeardownTimeout:F0}s) " +
                                       "reached while the player was still inside - teleporting them out now.", this);
                        break;
                    }

                    await UniTask.Delay(pollMs, DelayType.UnscaledDeltaTime);
                    if (!this) return;
                }

                if (!IsPlayerInLockedZone(lockedZoneIds, out playerZoneId))
                {
                    ClearEvacuationNotice();
                    return;
                }
            }
            else
            {
                PublishEvacuationDestination(lockedZoneIds, playerZoneId);
            }

            await ForceEvacuateAsync(lockedZoneIds, playerZoneId);
        }

        private async UniTask ForceEvacuateAsync(HashSet<string> lockedZoneIds, string playerZoneId)
        {
            if (!ZoneEvacuationRegistry.TryResolveExit(playerZoneId, PlayerPosition, lockedZoneIds, out var exit))
            {
                Debug.LogWarning($"[ScenarioManager] no exit left for zone '{playerZoneId}' at teleport time (every " +
                                 "candidate lands in another zone this unload locks). Unloading with the player still " +
                                 "inside - add a ZoneExitAnchor outside the scenario's zones.", this);
                ClearEvacuationNotice();
                return;
            }

            if (playerEvacuationTarget == null)
            {
                Debug.LogError("[ScenarioManager] the player has to be moved out of the zone being locked, but no " +
                               "Player Evacuation Target is assigned on this ScenarioManager. Assign the player's " +
                               "SendTeleportTarget (the one WorldManager uses for region changes).", this);
                ClearEvacuationNotice();
                return;
            }

            if (!playerEvacuationTarget.isTeleportPlayer)
                Debug.LogWarning($"[ScenarioManager] '{playerEvacuationTarget.name}' has Is Teleport Player OFF: the " +
                                 "evacuation event will move an object instead of the player.", playerEvacuationTarget);

            playerEvacuationTarget.transform.SetPositionAndRotation(exit.Position, exit.Rotation);
            playerEvacuationTarget.Teleport(resetOptions.fadeOnForcedTeleport);

            Debug.Log($"[ScenarioManager] player force-teleported out of '{playerZoneId}' to {exit.Position} " +
                      $"(via '{exit.SourceName}').");

            if (resetOptions.teleportSettleDelay > 0f)
            {
                await UniTask.Delay(Mathf.RoundToInt(resetOptions.teleportSettleDelay * 1000f), DelayType.UnscaledDeltaTime);
                if (!this) return;
            }

            if (IsPlayerInLockedZone(lockedZoneIds, out var stillInside))
                Debug.LogWarning($"[ScenarioManager] the player still reads as being in '{stillInside}' after the " +
                                 "evacuation teleport - check that the exit point sits outside that zone's volume.", this);

            ClearEvacuationNotice();
        }

        /// <summary>
        /// Zones this unload is expected to lock: the scenario's own zone list (zones with no way
        /// out are dropped later, when the exit is resolved), or its explicit override when set.
        /// </summary>
        private HashSet<string> CollectEvacuationZoneIds(IReadOnlyList<Scenario> scenarios)
        {
            var ids = new HashSet<string>();

            foreach (var scenario in scenarios)
            {
                if (scenario == null || !scenario.evacuatePlayerOnUnload) continue;

                var zones = scenario.zonesToEvacuateOverride != null && scenario.zonesToEvacuateOverride.Count > 0
                    ? scenario.zonesToEvacuateOverride
                    : scenario.listOfZonesNeededForThisScenario;
                if (zones == null) continue;

                foreach (var zone in zones)
                    if (zone != null) ids.Add(zone.id);
            }

            return ids;
        }

        private static bool IsPlayerInLockedZone(HashSet<string> lockedZoneIds, out string zoneId)
        {
            var zone = WorldManager.CurrentPlayerZone;
            zoneId = zone != null ? zone.id.ToString() : null;
            return !string.IsNullOrEmpty(zoneId) && lockedZoneIds.Contains(zoneId);
        }

        /// <summary>Same position the ECS zone detection follows (the main camera), unless overridden.</summary>
        private Vector3 PlayerPosition
        {
            get
            {
                if (playerPositionProbe != null) return playerPositionProbe.position;
                var mainCamera = Camera.main;
                return mainCamera != null ? mainCamera.transform.position : transform.position;
            }
        }

        private void Announce(string message)
        {
            if (resetOptions.evacuationMessageChannel != null)
                resetOptions.evacuationMessageChannel.RaiseEvent(message);
        }

        /// <summary>
        /// Parks the evacuation marker on the exit and publishes it, so a navigation tooltip can
        /// draw the way out. The marker belongs to this manager and is reused across evacuations.
        /// </summary>
        private void PublishEvacuationDestination(HashSet<string> lockedZoneIds, string playerZoneId)
        {
            if (!resetOptions.showNavigationPathToExit) return;
            if (!ZoneEvacuationRegistry.TryResolveExit(playerZoneId, PlayerPosition, lockedZoneIds, out var exit)) return;

            if (_evacuationMarker == null)
            {
                _evacuationMarker = new GameObject("ScenarioEvacuationDestination").transform;
                _evacuationMarker.SetParent(transform, true);
            }

            _evacuationMarker.gameObject.SetActive(true);
            _evacuationMarker.SetPositionAndRotation(exit.Position, exit.Rotation);
            PlayerEvacuationDestination?.Invoke(_evacuationMarker);
        }

        private void ClearEvacuationNotice()
        {
            Announce(string.Empty);
            resetOptions.evacuationCountdownChannel?.RaiseEvent(-1f);
            PlayerEvacuationCountdown?.Invoke(string.Empty, -1f);

            PlayerEvacuationDestination?.Invoke(null);
            if (_evacuationMarker != null) _evacuationMarker.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (!isDebug || _journal == null) return;
            if (Time.unscaledTime < _nextDebugRefresh) return;
            _nextDebugRefresh = Time.unscaledTime + 0.5f;

            journaledTeleports.Clear();
            var entries = _journal.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                journaledTeleports.Add(entry.Target != null
                    ? $"{entry.Target.name} -> back to {entry.Position}"
                    : "<destroyed>");
            }
        }

        #endregion
    }
}
