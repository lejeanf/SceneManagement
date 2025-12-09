#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement.Editor
{
    public class SceneLoadingDebugWindow : EditorWindow
    {
        [MenuItem("Tools/Scene Management/Scene Loading Tracker")]
        public static void ShowWindow()
        {
            var window = GetWindow<SceneLoadingDebugWindow>("Scene Loading Tracker");
            window.minSize = new Vector2(800, 400);
        }

        private enum ViewMode
        {
            Status,
            Timeline
        }

        private ViewMode _currentView = ViewMode.Status;
        private Vector2 _scrollPosition;
        private string _searchFilter = "";
        private bool _autoScroll = true;
        private bool _showCompleted = true;
        private bool _showSubscenes = true;
        private bool _showAdditive = true;
        private bool _showSections = true;

        private Dictionary<string, bool> _expandedScenes = new Dictionary<string, bool>();
        private Dictionary<string, bool> _expandedErrors = new Dictionary<string, bool>();

        private GUIStyle _headerStyle;
        private GUIStyle _sceneNameStyle;
        private GUIStyle _timestampStyle;
        private GUIStyle _errorStyle;

        private float _timelineStartTime = 0f;
        private float _timelineEndTime = 0f;
        private float _timelineZoom = 1f;
        private Vector2 _timelineScroll;

        // Cache events when exiting Play mode to persist data
        [SerializeField]
        private List<SceneLoadingTracker.SceneLoadingEvent> _cachedEvents = new List<SceneLoadingTracker.SceneLoadingEvent>();
        private bool _isPlayMode = false;

        private const float ROW_HEIGHT = 20f;
        private const float INDENT_WIDTH = 20f;
        private const float TIMELINE_ROW_HEIGHT = 30f;
        private const float TIMELINE_PADDING = 10f;

        private void OnEnable()
        {
            EditorApplication.update += Repaint;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            _isPlayMode = EditorApplication.isPlaying;

            if (SceneLoadingTracker.Instance != null)
            {
                SceneLoadingTracker.Instance.EventAdded += OnEventAdded;
                SceneLoadingTracker.Instance.EventUpdated += OnEventUpdated;
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            if (SceneLoadingTracker.Instance != null)
            {
                SceneLoadingTracker.Instance.EventAdded -= OnEventAdded;
                SceneLoadingTracker.Instance.EventUpdated -= OnEventUpdated;
            }
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingPlayMode:
                    // Capture events before they're destroyed
                    if (SceneLoadingTracker.Instance != null)
                    {
                        _cachedEvents = new List<SceneLoadingTracker.SceneLoadingEvent>(
                            SceneLoadingTracker.Instance.GetAllEvents()
                        );
                    }
                    break;

                case PlayModeStateChange.EnteredPlayMode:
                    _isPlayMode = true;
                    // Clear cache when entering Play mode to show fresh data
                    _cachedEvents.Clear();
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    _isPlayMode = false;
                    break;
            }
        }

        private void OnEventAdded(SceneLoadingTracker.SceneLoadingEvent evt)
        {
            if (_autoScroll)
            {
                _scrollPosition.y = float.MaxValue;
            }
            Repaint();
        }

        private void OnEventUpdated(SceneLoadingTracker.SceneLoadingEvent evt)
        {
            Repaint();
        }

        private void InitStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12,
                    alignment = TextAnchor.MiddleLeft
                };
            }

            if (_sceneNameStyle == null)
            {
                _sceneNameStyle = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Bold
                };
            }

            if (_timestampStyle == null)
            {
                _timestampStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 9,
                    alignment = TextAnchor.MiddleRight
                };
            }

            if (_errorStyle == null)
            {
                _errorStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    fontSize = 10,
                    wordWrap = true,
                    normal = { textColor = Color.red }
                };
            }
        }

        private void OnGUI()
        {
            InitStyles();

            DrawToolbar();

            EditorGUILayout.Space(5);

            switch (_currentView)
            {
                case ViewMode.Status:
                    DrawStatusView();
                    break;
                case ViewMode.Timeline:
                    DrawTimelineView();
                    break;
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // View selection
            GUILayout.Label("View:", GUILayout.Width(40));
            _currentView = (ViewMode)GUILayout.Toolbar((int)_currentView, new[] { "Status", "Timeline" }, GUILayout.Width(150));

            GUILayout.Space(20);

            // Filter toggles
            GUILayout.Label("Show:", GUILayout.Width(40));
            _showSubscenes = GUILayout.Toggle(_showSubscenes, "Subscenes", EditorStyles.toolbarButton, GUILayout.Width(80));
            _showAdditive = GUILayout.Toggle(_showAdditive, "Additive", EditorStyles.toolbarButton, GUILayout.Width(70));
            _showSections = GUILayout.Toggle(_showSections, "Sections", EditorStyles.toolbarButton, GUILayout.Width(70));
            _showCompleted = GUILayout.Toggle(_showCompleted, "Completed", EditorStyles.toolbarButton, GUILayout.Width(80));

            GUILayout.Space(20);

            // Search
            GUILayout.Label("Search:", GUILayout.Width(50));
            _searchFilter = GUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(150));

            GUILayout.FlexibleSpace();

            // Auto-scroll toggle
            _autoScroll = GUILayout.Toggle(_autoScroll, "Auto-scroll", EditorStyles.toolbarButton, GUILayout.Width(80));

            // Clear button
            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                if (SceneLoadingTracker.Instance != null)
                {
                    SceneLoadingTracker.Instance.Clear();
                }
                _cachedEvents.Clear();
                _expandedScenes.Clear();
                _expandedErrors.Clear();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatusView()
        {
            // Get events from live tracker or cached data
            List<SceneLoadingTracker.SceneLoadingEvent> events;

            if (SceneLoadingTracker.Instance != null)
            {
                events = SceneLoadingTracker.Instance.GetAllEvents();
            }
            else if (_cachedEvents.Count > 0)
            {
                // Show cached events from previous Play mode session
                events = _cachedEvents;
                EditorGUILayout.HelpBox("Showing cached data from previous Play mode session. Enter Play mode to start fresh tracking.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Scene Loading Tracker is not active. Enter Play mode to start tracking.", MessageType.Info);
                return;
            }

            var filteredEvents = FilterEvents(events);

            if (filteredEvents.Count == 0)
            {
                EditorGUILayout.HelpBox("No scene loading events to display. Try adjusting filters or enter Play mode.", MessageType.Info);
                return;
            }

            // Draw header
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Scene Name", _headerStyle, GUILayout.Width(250));
            GUILayout.Label("Type", _headerStyle, GUILayout.Width(80));
            GUILayout.Label("State", _headerStyle, GUILayout.Width(80));
            GUILayout.Label("Start Time", _headerStyle, GUILayout.Width(100));
            GUILayout.Label("End Time", _headerStyle, GUILayout.Width(100));
            GUILayout.Label("Duration", _headerStyle, GUILayout.Width(80));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // Draw scene list
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            foreach (var evt in filteredEvents)
            {
                DrawSceneEvent(evt);
            }

            EditorGUILayout.EndScrollView();

            // Draw footer with stats
            DrawFooter(events);
        }

        private void DrawSceneEvent(SceneLoadingTracker.SceneLoadingEvent evt)
        {
            var bgColor = GetStateColor(evt.state);
            var originalColor = GUI.backgroundColor;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = bgColor;

            EditorGUILayout.BeginHorizontal(GUILayout.Height(ROW_HEIGHT));

            // Expandable arrow for scenes with errors or dependencies
            bool hasExpandable = !string.IsNullOrEmpty(evt.errorMessage) || evt.dependencies.Count > 0;
            bool isExpanded = _expandedScenes.ContainsKey(evt.sceneName) && _expandedScenes[evt.sceneName];

            if (hasExpandable)
            {
                if (GUILayout.Button(isExpanded ? "▼" : "▶", EditorStyles.label, GUILayout.Width(15)))
                {
                    _expandedScenes[evt.sceneName] = !isExpanded;
                }
            }
            else
            {
                GUILayout.Space(15);
            }

            // Scene name
            GUILayout.Label(evt.sceneName, _sceneNameStyle, GUILayout.Width(250));

            // Type
            GUILayout.Label(evt.sceneType.ToString(), GUILayout.Width(80));

            // State
            var stateContent = new GUIContent(evt.state.ToString(), GetStateIcon(evt.state));
            GUILayout.Label(stateContent, GUILayout.Width(80));

            // Start time
            GUILayout.Label(evt.startTime.HasValue ? FormatTime(evt.startTime.Value) : "-", _timestampStyle, GUILayout.Width(100));

            // End time
            GUILayout.Label(evt.endTime.HasValue ? FormatTime(evt.endTime.Value) : "-", _timestampStyle, GUILayout.Width(100));

            // Duration
            if (evt.startTime.HasValue)
            {
                if (evt.endTime.HasValue)
                {
                    var duration = evt.endTime.Value - evt.startTime.Value;
                    GUILayout.Label($"{duration:F2}s", _timestampStyle, GUILayout.Width(80));
                }
                else if (evt.state == SceneLoadingTracker.LoadingState.Loading && EditorApplication.isPlaying)
                {
                    // Only show live duration when in Play mode
                    var duration = Time.realtimeSinceStartup - evt.startTime.Value;
                    GUILayout.Label($"{duration:F2}s", _timestampStyle, GUILayout.Width(80));
                }
                else
                {
                    GUILayout.Label("-", _timestampStyle, GUILayout.Width(80));
                }
            }
            else
            {
                GUILayout.Label("-", _timestampStyle, GUILayout.Width(80));
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();

            // Draw expanded content
            if (isExpanded)
            {
                EditorGUI.indentLevel++;

                if (!string.IsNullOrEmpty(evt.parentScene))
                {
                    EditorGUILayout.LabelField("Parent:", evt.parentScene);
                }

                if (evt.dependencies.Count > 0)
                {
                    EditorGUILayout.LabelField("Dependencies:", string.Join(", ", evt.dependencies));
                }

                if (!string.IsNullOrEmpty(evt.errorMessage))
                {
                    EditorGUILayout.BeginVertical(_errorStyle);
                    EditorGUILayout.LabelField("Error:", evt.errorMessage, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.EndVertical();
                }

                EditorGUI.indentLevel--;
            }

            GUI.backgroundColor = originalColor;
            EditorGUILayout.EndVertical();
        }

        private void DrawTimelineView()
        {
            // Get events from live tracker or cached data
            List<SceneLoadingTracker.SceneLoadingEvent> events;

            if (SceneLoadingTracker.Instance != null)
            {
                events = SceneLoadingTracker.Instance.GetAllEvents();
            }
            else if (_cachedEvents.Count > 0)
            {
                // Show cached events from previous Play mode session
                events = _cachedEvents;
                EditorGUILayout.HelpBox("Showing cached data from previous Play mode session. Enter Play mode to start fresh tracking.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Scene Loading Tracker is not active. Enter Play mode to start tracking.", MessageType.Info);
                return;
            }

            var filteredEvents = FilterEvents(events);

            if (filteredEvents.Count == 0)
            {
                EditorGUILayout.HelpBox("No scene loading events to display. Try adjusting filters or enter Play mode.", MessageType.Info);
                return;
            }

            // Calculate timeline bounds
            CalculateTimelineBounds(filteredEvents);

            // Timeline controls
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label($"Timeline: {FormatTime(_timelineStartTime)} - {FormatTime(_timelineEndTime)}", GUILayout.Width(300));
            GUILayout.Label("Zoom:", GUILayout.Width(45));
            _timelineZoom = GUILayout.HorizontalSlider(_timelineZoom, 0.1f, 5f, GUILayout.Width(150));
            GUILayout.Label($"{_timelineZoom:F1}x", GUILayout.Width(40));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // Timeline view
            _timelineScroll = EditorGUILayout.BeginScrollView(_timelineScroll);

            var timelineRect = GUILayoutUtility.GetRect(position.width - 20, filteredEvents.Count * TIMELINE_ROW_HEIGHT + 100);
            DrawTimelineGrid(timelineRect);

            float yPos = TIMELINE_PADDING;
            foreach (var evt in filteredEvents.OrderBy(e => e.timestamp))
            {
                DrawTimelineBar(evt, timelineRect, yPos);
                yPos += TIMELINE_ROW_HEIGHT;
            }

            EditorGUILayout.EndScrollView();

            DrawFooter(events);
        }

        private void DrawTimelineGrid(Rect rect)
        {
            if (_timelineEndTime <= _timelineStartTime)
                return;

            var duration = _timelineEndTime - _timelineStartTime;
            var gridWidth = (rect.width - 200) * _timelineZoom;
            var timeStep = CalculateTimeStep(duration);

            Handles.color = new Color(0.5f, 0.5f, 0.5f, 0.3f);

            for (float t = 0; t <= duration; t += timeStep)
            {
                var x = 200 + (t / duration) * gridWidth;
                if (x < rect.xMax)
                {
                    Handles.DrawLine(new Vector3(x, rect.y), new Vector3(x, rect.yMax));
                    GUI.Label(new Rect(x - 30, rect.y, 60, 20), FormatTime(_timelineStartTime + t), _timestampStyle);
                }
            }
        }

        private float CalculateTimeStep(float duration)
        {
            if (duration < 10) return 1f;
            if (duration < 60) return 5f;
            if (duration < 300) return 30f;
            return 60f;
        }

        private void DrawTimelineBar(SceneLoadingTracker.SceneLoadingEvent evt, Rect timelineRect, float yPos)
        {
            if (!evt.startTime.HasValue)
                return;

            var duration = _timelineEndTime - _timelineStartTime;
            if (duration <= 0)
                return;

            var gridWidth = (timelineRect.width - 200) * _timelineZoom;

            // Calculate bar position and width
            var startOffset = evt.startTime.Value - _timelineStartTime;
            var xStart = 200 + (startOffset / duration) * gridWidth;

            float barWidth;
            if (evt.endTime.HasValue)
            {
                var endOffset = evt.endTime.Value - _timelineStartTime;
                var xEnd = 200 + (endOffset / duration) * gridWidth;
                barWidth = xEnd - xStart;
            }
            else if (EditorApplication.isPlaying)
            {
                // Only show live duration in Play mode
                var currentOffset = Time.realtimeSinceStartup - _timelineStartTime;
                var xEnd = 200 + (currentOffset / duration) * gridWidth;
                barWidth = xEnd - xStart;
            }
            else
            {
                // For cached data, show minimal bar width
                barWidth = 2f;
            }

            barWidth = Mathf.Max(barWidth, 2f);

            // Draw scene label
            var labelRect = new Rect(10, yPos, 180, TIMELINE_ROW_HEIGHT - 5);
            GUI.Label(labelRect, evt.sceneName, _sceneNameStyle);

            // Draw bar
            var barRect = new Rect(xStart, yPos + 5, barWidth, TIMELINE_ROW_HEIGHT - 10);
            var barColor = GetStateColor(evt.state);

            EditorGUI.DrawRect(barRect, barColor);
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width, 1), Color.black);
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.yMax - 1, barRect.width, 1), Color.black);

            if (barRect.width > 20)
            {
                var iconContent = new GUIContent(GetStateIcon(evt.state));
                GUI.Label(new Rect(barRect.x + 2, barRect.y, 16, 16), iconContent);
            }
        }

        private void CalculateTimelineBounds(List<SceneLoadingTracker.SceneLoadingEvent> events)
        {
            if (events.Count == 0)
                return;

            _timelineStartTime = events.Min(e => e.startTime ?? e.timestamp);

            float maxEndTime;
            if (EditorApplication.isPlaying)
            {
                maxEndTime = events.Max(e => e.endTime ?? Time.realtimeSinceStartup);
            }
            else
            {
                maxEndTime = events.Max(e => e.endTime ?? e.startTime ?? e.timestamp);
            }

            _timelineEndTime = Mathf.Max(maxEndTime, _timelineStartTime + 1f);
        }

        private List<SceneLoadingTracker.SceneLoadingEvent> FilterEvents(List<SceneLoadingTracker.SceneLoadingEvent> events)
        {
            return events.Where(evt =>
            {
                if (!_showSubscenes && evt.sceneType == SceneLoadingTracker.SceneType.Subscene)
                    return false;
                if (!_showAdditive && evt.sceneType == SceneLoadingTracker.SceneType.AdditiveScene)
                    return false;
                if (!_showSections && evt.sceneType == SceneLoadingTracker.SceneType.Section)
                    return false;

                if (!_showCompleted && (evt.state == SceneLoadingTracker.LoadingState.Loaded ||
                                        evt.state == SceneLoadingTracker.LoadingState.Unloaded))
                    return false;

                if (!string.IsNullOrEmpty(_searchFilter) &&
                    !evt.sceneName.ToLower().Contains(_searchFilter.ToLower()))
                    return false;

                return true;
            }).ToList();
        }

        private void DrawFooter(List<SceneLoadingTracker.SceneLoadingEvent> allEvents)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var totalCount = allEvents.Count;
            var loadingCount = allEvents.Count(e => e.state == SceneLoadingTracker.LoadingState.Loading);
            var loadedCount = allEvents.Count(e => e.state == SceneLoadingTracker.LoadingState.Loaded);
            var failedCount = allEvents.Count(e => e.state == SceneLoadingTracker.LoadingState.Failed);

            GUILayout.Label($"Total: {totalCount}", GUILayout.Width(80));
            GUILayout.Label($"Loading: {loadingCount}", GUILayout.Width(80));
            GUILayout.Label($"Loaded: {loadedCount}", GUILayout.Width(80));
            GUILayout.Label($"Failed: {failedCount}", GUILayout.Width(80));

            GUILayout.FlexibleSpace();

            if (Application.isPlaying)
            {
                GUILayout.Label("▶ PLAYING", EditorStyles.boldLabel);
            }

            EditorGUILayout.EndHorizontal();
        }

        private Color GetStateColor(SceneLoadingTracker.LoadingState state)
        {
            switch (state)
            {
                case SceneLoadingTracker.LoadingState.Pending:
                    return new Color(0.7f, 0.7f, 0.7f, 0.3f);
                case SceneLoadingTracker.LoadingState.Loading:
                    return new Color(1f, 0.92f, 0.016f, 0.3f); // Yellow
                case SceneLoadingTracker.LoadingState.Loaded:
                    return new Color(0.3f, 0.8f, 0.3f, 0.3f); // Green
                case SceneLoadingTracker.LoadingState.Failed:
                    return new Color(0.8f, 0.2f, 0.2f, 0.5f); // Red
                case SceneLoadingTracker.LoadingState.Unloading:
                    return new Color(0.8f, 0.5f, 0.2f, 0.3f); // Orange
                case SceneLoadingTracker.LoadingState.Unloaded:
                    return new Color(0.5f, 0.5f, 0.5f, 0.2f); // Dark gray
                default:
                    return Color.white;
            }
        }

        private Texture2D GetStateIcon(SceneLoadingTracker.LoadingState state)
        {
            switch (state)
            {
                case SceneLoadingTracker.LoadingState.Pending:
                    return EditorGUIUtility.IconContent("d_WaitSpin00").image as Texture2D;
                case SceneLoadingTracker.LoadingState.Loading:
                    return EditorGUIUtility.IconContent("d_Refresh").image as Texture2D;
                case SceneLoadingTracker.LoadingState.Loaded:
                    return EditorGUIUtility.IconContent("d_TestPassed").image as Texture2D;
                case SceneLoadingTracker.LoadingState.Failed:
                    return EditorGUIUtility.IconContent("d_TestFailed").image as Texture2D;
                case SceneLoadingTracker.LoadingState.Unloading:
                    return EditorGUIUtility.IconContent("d_TreeEditor.Trash").image as Texture2D;
                case SceneLoadingTracker.LoadingState.Unloaded:
                    return EditorGUIUtility.IconContent("d_SceneAsset Icon").image as Texture2D;
                default:
                    return null;
            }
        }

        private string FormatTime(float time)
        {
            var timeSpan = TimeSpan.FromSeconds(time);
            if (time < 60)
                return $"{time:F2}s";
            return $"{timeSpan.Minutes}:{timeSpan.Seconds:D2}";
        }
    }
}
#endif