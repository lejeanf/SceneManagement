using System;
using System.Collections.Generic;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Runtime tracker that collects scene loading events for debugging purposes.
    /// This is a singleton that persists across domain reloads.
    /// </summary>
    public class SceneLoadingTracker : MonoBehaviour
    {
        private static SceneLoadingTracker _instance;
        public static SceneLoadingTracker Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[SceneLoadingTracker]");
                    _instance = go.AddComponent<SceneLoadingTracker>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public enum SceneType
        {
            Subscene,
            AdditiveScene,
            Section
        }

        public enum LoadingState
        {
            Pending,
            Loading,
            Loaded,
            Failed,
            Unloading,
            Unloaded
        }

        [Serializable]
        public class SceneLoadingEvent
        {
            public string sceneName;
            public SceneType sceneType;
            public LoadingState state;
            public float timestamp;
            public float? startTime;
            public float? endTime;
            public string errorMessage;
            public string parentScene;
            public List<string> dependencies = new List<string>();
            public int eventId;

            public SceneLoadingEvent(string name, SceneType type, LoadingState initialState, string parent = null)
            {
                sceneName = name;
                sceneType = type;
                state = initialState;
                timestamp = Time.realtimeSinceStartup;
                parentScene = parent;
                eventId = GetHashCode();
            }
        }

        private List<SceneLoadingEvent> _events = new List<SceneLoadingEvent>();
        private Dictionary<string, SceneLoadingEvent> _activeScenes = new Dictionary<string, SceneLoadingEvent>();
        private const int MAX_EVENTS = 1000;

        public delegate void OnEventAdded(SceneLoadingEvent evt);
        public event OnEventAdded EventAdded;

        public delegate void OnEventUpdated(SceneLoadingEvent evt);
        public event OnEventUpdated EventUpdated;

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

        public void TrackSceneLoadStart(string sceneName, SceneType type, string parent = null)
        {
            var evt = new SceneLoadingEvent(sceneName, type, LoadingState.Loading, parent)
            {
                startTime = Time.realtimeSinceStartup
            };

            _activeScenes[sceneName] = evt;
            AddEvent(evt);
        }

        public void TrackSceneLoadComplete(string sceneName)
        {
            if (_activeScenes.TryGetValue(sceneName, out var evt))
            {
                evt.state = LoadingState.Loaded;
                evt.endTime = Time.realtimeSinceStartup;
                EventUpdated?.Invoke(evt);
            }
        }

        public void TrackSceneLoadFailed(string sceneName, string errorMessage)
        {
            if (_activeScenes.TryGetValue(sceneName, out var evt))
            {
                evt.state = LoadingState.Failed;
                evt.endTime = Time.realtimeSinceStartup;
                evt.errorMessage = errorMessage;
                EventUpdated?.Invoke(evt);
            }
        }

        public void TrackSceneUnloadStart(string sceneName)
        {
            if (_activeScenes.TryGetValue(sceneName, out var evt))
            {
                evt.state = LoadingState.Unloading;
                EventUpdated?.Invoke(evt);
            }
            else
            {
                var unloadEvent = new SceneLoadingEvent(sceneName, SceneType.AdditiveScene, LoadingState.Unloading);
                _activeScenes[sceneName] = unloadEvent;
                AddEvent(unloadEvent);
            }
        }

        public void TrackSceneUnloadComplete(string sceneName)
        {
            if (_activeScenes.TryGetValue(sceneName, out var evt))
            {
                evt.state = LoadingState.Unloaded;
                evt.endTime = Time.realtimeSinceStartup;
                EventUpdated?.Invoke(evt);
                _activeScenes.Remove(sceneName);
            }
        }

        public void TrackSubsceneLoadStart(string subsceneName, string parent = null)
        {
            TrackSceneLoadStart(subsceneName, SceneType.Subscene, parent);
        }

        public void TrackSectionLoadStart(string sectionName, string parent = null)
        {
            TrackSceneLoadStart(sectionName, SceneType.Section, parent);
        }

        private void AddEvent(SceneLoadingEvent evt)
        {
            _events.Add(evt);

            // Maintain max size
            if (_events.Count > MAX_EVENTS)
            {
                _events.RemoveAt(0);
            }

            EventAdded?.Invoke(evt);
        }

        public List<SceneLoadingEvent> GetAllEvents()
        {
            return new List<SceneLoadingEvent>(_events);
        }

        public List<SceneLoadingEvent> GetActiveScenes()
        {
            return new List<SceneLoadingEvent>(_activeScenes.Values);
        }

        public void Clear()
        {
            _events.Clear();
            _activeScenes.Clear();
        }

        public int GetEventCount()
        {
            return _events.Count;
        }

        public int GetActiveSceneCount()
        {
            return _activeScenes.Count;
        }
    }
}
