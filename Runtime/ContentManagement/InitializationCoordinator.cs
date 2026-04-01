using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;

using jeanf.EventSystem;
using jeanf.scenemanagement;
using jeanf.universalplayer;

namespace jeanf.ContentManagement
{
    public class InitializationCoordinator : MonoBehaviour
    {
        [SerializeField] private GameInitConfig _config;
        [SerializeField] private StringEventChannelSO _loadingMessageChannel;

        private static InitializationCoordinator _instance;

        private ContentRegistry _contentRegistry;
        private readonly Dictionary<string, IInitializable> _registered = new();
        private readonly HashSet<string> _readySystems = new();
        private readonly Dictionary<string, LoadingEntry> _entries = new();

        public static event System.Action OnInitComplete;
        public static event System.Action OnProgressChanged;
        public static IReadOnlyCollection<LoadingEntry> Entries
            => _instance?._entries.Values;

        private void Awake()
        {
            _instance = this;
        }

        public static void Register(IInitializable system)
        {
            if (_instance == null) return;
            _instance._registered[system.SystemId] = system;
        }

        public static void ReportReady(string systemId)
        {
            if (_instance == null) return;
            _instance._readySystems.Add(systemId);
            _instance.SetEntry(systemId, LoadingState.Complete, 1f);
            _instance.CheckComplete();
        }

        public static void ReportProgress(string systemId, float progress)
        {
            if (_instance == null) return;
            _instance.SetEntry(systemId, LoadingState.Loading, progress);
        }

        public async UniTask Run(ContentRegistry contentRegistry)
        {
            _contentRegistry = contentRegistry;
            BuildEntries();
            SubscribeToContentProgress();
            await RunSequence();
        }

        private async UniTask RunSequence()
        {
            NoPeeking.SetIsLoadingState(true);

            SetEntry("content.cosmetics",    LoadingState.Loading, 0f);
            SetEntry("content.scenes",       LoadingState.Loading, 0f);
            BroadcastMessage("Loading content...");
            await _contentRegistry.Initialize();
            SetEntry("content.cosmetics",    LoadingState.Complete, 1f);
            SetEntry("content.scenes",       LoadingState.Complete, 1f);

            SetEntry("world.dependencies",   LoadingState.Loading, 0f);
            BroadcastMessage("Loading world...");
            await WorldManager.LoadWorldDependencies();
            SetEntry("world.dependencies",   LoadingState.Complete, 1f);

            SetEntry("world.region",         LoadingState.Loading, 0f);
            BroadcastMessage($"Loading {_config.startRegion.levelName}...");
            await WorldManager.LoadRegion(_config.startRegion);
            SetEntry("world.region",         LoadingState.Complete, 1f);

            SetEntry("world.player",         LoadingState.Loading, 0f);
            BroadcastMessage("Placing player...");
            WorldManager.SpawnPlayer(_config.startRegion.SpawnPosOnInit);
            WorldManager.SetInitialLocation(_config.startRegion, _config.startZone);
            SetEntry("world.player",         LoadingState.Complete, 1f);

            BroadcastMessage("Initializing systems...");
            await WaitForRequiredSystems();

            NoPeeking.SetIsLoadingState(false);
            OnInitComplete?.Invoke();
        }

        private void BuildEntries()
        {
            AddEntry("content.cosmetics",  "Cosmetics",                      "Content");
            AddEntry("content.scenes",     "Scene Metadata",                 "Content");
            AddEntry("world.dependencies", "World Dependencies",             "World");
            AddEntry("world.region",       _config.startRegion.levelName,    "World");
            AddEntry("world.player",       "Player",                         "World");

            foreach (var entry in _config.systemsConfig.requiredSystems)
            {
                var displayName = _registered.TryGetValue(entry.systemId, out var sys)
                    ? sys.DisplayName
                    : entry.systemId;
                AddEntry(entry.systemId, displayName, "Systems");
            }
        }

        private void SubscribeToContentProgress()
        {
            _contentRegistry.Cosmetics.OnProgressChanged += () =>
            {
                SetEntry("content.cosmetics", LoadingState.Loading,
                    _contentRegistry.Cosmetics.Progress,
                    _contentRegistry.Cosmetics.LoadedCount,
                    _contentRegistry.Cosmetics.TotalCount);
            };

            _contentRegistry.Scenes.OnProgressChanged += () =>
            {
                SetEntry("content.scenes", LoadingState.Loading,
                    _contentRegistry.Scenes.Progress,
                    _contentRegistry.Scenes.LoadedCount,
                    _contentRegistry.Scenes.TotalCount);
            };
        }

        private void AddEntry(string id, string displayName, string group)
        {
            _entries[id] = new LoadingEntry
            {
                Id = id, DisplayName = displayName, Group = group,
                State = LoadingState.Pending, Progress = 0f
            };
        }

        private void SetEntry(string id, LoadingState state, float progress,
            int loaded = 0, int total = 0)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            entry.State      = state;
            entry.Progress   = progress;
            entry.LoadedCount = loaded;
            entry.TotalCount  = total;
            OnProgressChanged?.Invoke();
        }

        private async UniTask WaitForRequiredSystems()
        {
            var required = _config.systemsConfig.requiredSystems.Select(e => e.systemId).ToList();
            if (required.All(id => _readySystems.Contains(id))) return;

            foreach (var entry in _config.systemsConfig.requiredSystems)
            {
                if (!_readySystems.Contains(entry.systemId))
                    BroadcastMessage(entry.loadingMessage);
            }

            await UniTask.WaitUntil(() => required.All(id => _readySystems.Contains(id)));
        }

        private void CheckComplete()
        {
            var required = _config.systemsConfig.requiredSystems.Select(e => e.systemId);
            if (!required.All(id => _readySystems.Contains(id))) return;
            NoPeeking.SetIsLoadingState(false);
            OnInitComplete?.Invoke();
        }

        private void BroadcastMessage(string message)
        {
            _loadingMessageChannel?.RaiseEvent(message);
        }
    }
}
