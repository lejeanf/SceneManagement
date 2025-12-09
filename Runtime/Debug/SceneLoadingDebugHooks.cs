using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Hooks into existing scene loading systems to track events for debugging.
    /// This component is automatically created and doesn't modify core loading logic.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class SceneLoadingDebugHooks : MonoBehaviour
    {
        private static SceneLoadingDebugHooks _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[SceneLoadingDebugHooks]");
            _instance = go.AddComponent<SceneLoadingDebugHooks>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable()
        {
            // Subscribe to Unity's scene management callbacks
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            // Subscribe to custom loading events
            LoadingInformation.LoadingStatus += OnLoadingStatusChanged;
            LoadPersistentSubScenes.PersistentLoadingComplete += OnPersistentLoadingComplete;
            SceneLoader.IsLoading += OnSceneLoaderStatusChanged;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;

            LoadingInformation.LoadingStatus -= OnLoadingStatusChanged;
            LoadPersistentSubScenes.PersistentLoadingComplete -= OnPersistentLoadingComplete;
            SceneLoader.IsLoading -= OnSceneLoaderStatusChanged;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == gameObject.scene.name)
                return;

            var tracker = SceneLoadingTracker.Instance;
            if (tracker != null)
            {
                tracker.TrackSceneLoadComplete(scene.name);
            }
        }

        private void OnSceneUnloaded(Scene scene)
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker != null)
            {
                tracker.TrackSceneUnloadComplete(scene.name);
            }
        }

        private void OnLoadingStatusChanged(string status)
        {
            if (string.IsNullOrEmpty(status))
                return;

            var tracker = SceneLoadingTracker.Instance;
            if (tracker == null)
                return;

            // Parse loading status messages
            if (status.StartsWith("Loading: "))
            {
                var sceneName = status.Substring(9);
                tracker.TrackSceneLoadStart(sceneName, SceneLoadingTracker.SceneType.AdditiveScene);
            }
            else if (status.StartsWith("Loading subScene: "))
            {
                var sceneName = status.Substring(18).TrimEnd('.');
                tracker.TrackSubsceneLoadStart(sceneName);
            }
            else if (status.Contains("loaded successfully"))
            {
                var parts = status.Split(' ');
                if (parts.Length > 1)
                {
                    var sceneName = parts[1];
                    tracker.TrackSceneLoadComplete(sceneName);
                }
            }
        }

        private void OnPersistentLoadingComplete(bool status)
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker != null && status)
            {
                Debug.Log("[SceneLoadingDebugHooks] All persistent subscenes loaded");
            }
        }

        private void OnSceneLoaderStatusChanged(bool isLoading)
        {
            // Track when the scene loader changes state
            if (!isLoading)
            {
                Debug.Log($"[SceneLoadingDebugHooks] SceneLoader finished at {Time.realtimeSinceStartup:F2}s");
            }
        }
    }
}
