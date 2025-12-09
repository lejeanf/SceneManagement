using System.Reflection;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Enhanced debug wrapper for SceneLoader that tracks detailed loading metrics.
    /// Hooks into SceneLoader delegates to capture all load/unload requests.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class SceneLoaderDebugWrapper : MonoBehaviour
    {
        private static SceneLoaderDebugWrapper _instance;
        private SceneLoader _sceneLoader;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (_instance != null)
                return;

            var sceneLoaderObj = FindFirstObjectByType<SceneLoader>();
            if (sceneLoaderObj != null)
            {
                _instance = sceneLoaderObj.gameObject.AddComponent<SceneLoaderDebugWrapper>();
            }
        }

        private void Awake()
        {
            _sceneLoader = GetComponent<SceneLoader>();
            if (_sceneLoader == null)
            {
                Debug.LogWarning("[SceneLoaderDebugWrapper] No SceneLoader found on this GameObject");
                Destroy(this);
                return;
            }
        }

        private void OnEnable()
        {
            if (_sceneLoader == null)
                return;

            // Hook into the SceneLoader delegates
            _sceneLoader.LoadSceneRequest += OnLoadSceneRequest;
            _sceneLoader.UnLoadSceneRequest += OnUnloadSceneRequest;
            _sceneLoader.UnloadAllScenesRequest += OnUnloadAllScenesRequest;

            // Hook into static delegates
            SceneLoader.IsLoading += OnIsLoadingChanged;
            SceneLoader.LoadComplete += OnLoadComplete;
        }

        private void OnDisable()
        {
            if (_sceneLoader == null)
                return;

            _sceneLoader.LoadSceneRequest -= OnLoadSceneRequest;
            _sceneLoader.UnLoadSceneRequest -= OnUnloadSceneRequest;
            _sceneLoader.UnloadAllScenesRequest -= OnUnloadAllScenesRequest;

            SceneLoader.IsLoading -= OnIsLoadingChanged;
            SceneLoader.LoadComplete -= OnLoadComplete;
        }

        private void OnLoadSceneRequest(string sceneName)
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker != null)
            {
                tracker.TrackSceneLoadStart(sceneName, SceneLoadingTracker.SceneType.AdditiveScene);

                if (_sceneLoader != null && _sceneLoader.isDebug)
                {
                    Debug.Log($"[SceneLoaderDebugWrapper] Load requested: {sceneName} at {Time.realtimeSinceStartup:F2}s");
                }
            }
        }

        private void OnUnloadSceneRequest(string sceneName)
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker != null)
            {
                tracker.TrackSceneUnloadStart(sceneName);

                if (_sceneLoader != null && _sceneLoader.isDebug)
                {
                    Debug.Log($"[SceneLoaderDebugWrapper] Unload requested: {sceneName} at {Time.realtimeSinceStartup:F2}s");
                }
            }
        }

        private void OnUnloadAllScenesRequest()
        {
            if (_sceneLoader != null && _sceneLoader.isDebug)
            {
                Debug.Log($"[SceneLoaderDebugWrapper] Unload all scenes requested at {Time.realtimeSinceStartup:F2}s");
            }
        }

        private void OnIsLoadingChanged(bool isLoading)
        {
            if (_sceneLoader != null && _sceneLoader.isDebug)
            {
                Debug.Log($"[SceneLoaderDebugWrapper] Loading state changed to: {isLoading} at {Time.realtimeSinceStartup:F2}s");
            }
        }

        private void OnLoadComplete(bool isComplete)
        {
            if (_sceneLoader != null && _sceneLoader.isDebug)
            {
                Debug.Log($"[SceneLoaderDebugWrapper] Load complete: {isComplete} at {Time.realtimeSinceStartup:F2}s");
            }
        }

        private void Update()
        {
            // Periodically check SceneLoader state for debugging
            if (_sceneLoader == null)
                return;

            // We can use reflection to check internal state if needed
            // For now, we rely on the public API
        }

        public int GetPendingOperationCount()
        {
            return _sceneLoader?.GetPendingOperationCount() ?? 0;
        }

        public int GetLoadedSceneCount()
        {
            return _sceneLoader?.GetLoadedSceneCount() ?? 0;
        }

        public bool IsCurrentlyLoading()
        {
            return _sceneLoader?.IsCurrentlyLoading() ?? false;
        }
    }
}
