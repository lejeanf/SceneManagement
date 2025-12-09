using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Example script showing how to manually track custom scene loading events.
    /// This is useful for tracking loading operations that aren't automatically captured.
    /// </summary>
    public class SceneLoadingTrackerExample : MonoBehaviour
    {
        // Example: Track a custom subscene loading operation
        public void LoadCustomSubscene(string subsceneName)
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker == null)
            {
                Debug.LogWarning("SceneLoadingTracker not available");
                return;
            }

            // Mark the scene as starting to load
            tracker.TrackSubsceneLoadStart(subsceneName);

            // Your loading logic here
            // ...

            // Mark the scene as loaded when complete
            // tracker.TrackSceneLoadComplete(subsceneName);
        }

        // Example: Track a custom additive scene with dependencies
        public void LoadCustomSceneWithDependencies(string mainScene, string[] dependencies)
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker == null)
            {
                Debug.LogWarning("SceneLoadingTracker not available");
                return;
            }

            // Track each dependency
            foreach (var dependency in dependencies)
            {
                tracker.TrackSceneLoadStart(dependency, SceneLoadingTracker.SceneType.AdditiveScene, mainScene);
            }

            // Track the main scene
            tracker.TrackSceneLoadStart(mainScene, SceneLoadingTracker.SceneType.AdditiveScene);
        }

        // Example: Track a failed loading operation
        public void HandleLoadFailure(string sceneName, string errorMessage)
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker == null)
            {
                Debug.LogWarning("SceneLoadingTracker not available");
                return;
            }

            tracker.TrackSceneLoadFailed(sceneName, errorMessage);
        }

        // Example: Clear tracking data at the start of a new level
        public void OnNewLevelStart()
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker == null)
            {
                Debug.LogWarning("SceneLoadingTracker not available");
                return;
            }

            Debug.Log("Clearing scene loading tracker for new level");
            tracker.Clear();
        }

        // Example: Get loading statistics
        public void PrintLoadingStats()
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker == null)
            {
                Debug.LogWarning("SceneLoadingTracker not available");
                return;
            }

            Debug.Log($"Total events tracked: {tracker.GetEventCount()}");
            Debug.Log($"Active scenes: {tracker.GetActiveSceneCount()}");

            var events = tracker.GetAllEvents();
            var loadedCount = 0;
            var failedCount = 0;

            foreach (var evt in events)
            {
                if (evt.state == SceneLoadingTracker.LoadingState.Loaded)
                    loadedCount++;
                else if (evt.state == SceneLoadingTracker.LoadingState.Failed)
                    failedCount++;
            }

            Debug.Log($"Loaded scenes: {loadedCount}");
            Debug.Log($"Failed scenes: {failedCount}");
        }

        // Example: Get all currently active scenes
        public void ListActiveScenes()
        {
            var tracker = SceneLoadingTracker.Instance;
            if (tracker == null)
            {
                Debug.LogWarning("SceneLoadingTracker not available");
                return;
            }

            var activeScenes = tracker.GetActiveScenes();
            Debug.Log($"Currently active scenes ({activeScenes.Count}):");

            foreach (var scene in activeScenes)
            {
                Debug.Log($"  - {scene.sceneName} ({scene.sceneType}): {scene.state}");
            }
        }
    }
}
