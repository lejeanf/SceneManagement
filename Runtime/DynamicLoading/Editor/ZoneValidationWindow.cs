#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Edit-mode audit of play-mode zone detection: every <see cref="IZoneTrackedObject"/> in the
    /// open scenes is resolved against the open volumes with the bridge's exact failsafe chain
    /// (<see cref="EditorZoneResolver"/>) and classified:
    ///  - BROKEN — matches no volume (pending at runtime), or its detected zone is not among the
    ///    zones the governing <see cref="Scenario"/> requires (listOfZonesNeededForThisScenario);
    ///  - SUSPECT — assigned only via the soft/lifted/fallback failsafe, or the pivot sits within
    ///    tolerance of more than one volume (ambiguous — a resized room can silently flip it);
    ///  - OK — exact, unambiguous, and consistent with the scenario.
    /// The governing scenario is auto-detected from the open scenes (Scenario.scene or its
    /// dependencies) and can be overridden manually.
    /// </summary>
    public class ZoneValidationWindow : EditorWindow
    {
        private enum Status { Broken, Suspect, Ok }

        private struct Row
        {
            public Transform Target;
            public Status Status;
            public string ZoneLabel;
            public string Detail;
            public float ProximityThreshold; // 0 = not proximity-gated
        }

        private readonly List<Row> _rows = new List<Row>();
        private readonly List<string> _scenarioNames = new List<string>();
        private Scenario _scenarioOverride;
        private bool _showOk = true;
        private int _volumeCount;
        private bool _scanned;
        private Vector2 _scroll;

        [MenuItem("Tools/SceneManagement/Zone Detection Validation")]
        private static void Open() => GetWindow<ZoneValidationWindow>("Zone Validation");

        private void OnHierarchyChange() => _scanned = false;

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan open scenes", GUILayout.Height(24))) Scan();
                _showOk = GUILayout.Toggle(_showOk, "show OK", GUILayout.Width(80));
            }
            _scenarioOverride = (Scenario)EditorGUILayout.ObjectField(
                new GUIContent("Scenario override", "Leave empty to auto-detect the scenario governing the open scenes."),
                _scenarioOverride, typeof(Scenario), false);

            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Play mode: inspect the ObjectZoneTrackingBridge component for live assignments instead.", MessageType.Info);
                return;
            }
            if (!_scanned) { EditorGUILayout.HelpBox("Scan to audit the open scenes.", MessageType.Info); return; }

            if (_volumeCount == 0)
            {
                EditorGUILayout.HelpBox("No VolumeAuthoring found in the open scenes. Zone volumes usually live in a " +
                    "SubScene — open it for edit (check the SubScene's 'Edit' toggle) and re-scan.", MessageType.Warning);
                return;
            }

            var scenarioLabel = _scenarioNames.Count > 0 ? string.Join(", ", _scenarioNames) : "none detected";
            var broken = _rows.Count(r => r.Status == Status.Broken);
            var suspect = _rows.Count(r => r.Status == Status.Suspect);
            EditorGUILayout.LabelField($"Volumes: {_volumeCount}   Objects: {_rows.Count}   Scenario: {scenarioLabel}");
            EditorGUILayout.LabelField($"Broken: {broken}   Suspect: {suspect}   OK: {_rows.Count - broken - suspect}", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in _rows)
            {
                if (row.Status == Status.Ok && !_showOk) continue;
                if (row.Target == null) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    var icon = row.Status switch
                    {
                        Status.Broken => "console.erroricon.sml",
                        Status.Suspect => "console.warnicon.sml",
                        _ => "TestPassed",
                    };
                    GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(20), GUILayout.Height(18));
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(row.Target.gameObject, typeof(GameObject), true, GUILayout.MinWidth(140));
                    EditorGUILayout.LabelField(row.ZoneLabel, GUILayout.Width(170));
                    EditorGUILayout.LabelField(row.Detail);
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox("Proximity: select the ObjectZoneTrackingBridge (Core prefab) and enable " +
                "'Simulate proximity' in its inspector to tune thresholds in the Scene view without entering play mode.", MessageType.None);
        }

        private void Scan()
        {
            _rows.Clear();
            _scenarioNames.Clear();
            _scanned = true;

            var volumes = EditorZoneResolver.GatherVolumes();
            _volumeCount = volumes.Count;
            if (_volumeCount == 0) return;

            var chain = EditorZoneResolver.SceneChainParams();
            var expectedZoneIds = CollectExpectedZoneIds();
            var proximityByZone = CollectProximityThresholds();

            foreach (var target in EditorZoneResolver.GatherTrackedObjects())
            {
                var res = EditorZoneResolver.Resolve(target.position, volumes, chain);
                var row = new Row { Target = target };

                if (!res.HasZone)
                {
                    row.Status = Status.Broken;
                    row.ZoneLabel = "— no volume —";
                    row.Detail = "matches no volume even after all failsafes: pending forever at runtime.";
                }
                else
                {
                    row.ZoneLabel = $"{res.Zone.zoneName} ({res.Zone.id})";
                    if (proximityByZone.TryGetValue($"{res.Zone.id}", out var threshold)) row.ProximityThreshold = threshold;

                    if (expectedZoneIds.Count > 0 && !expectedZoneIds.Contains($"{res.Zone.id}"))
                    {
                        row.Status = Status.Broken;
                        row.Detail = $"detected zone is NOT required by the scenario ({res.Kind}) — object is in the wrong room for this scenario.";
                    }
                    else if (res.Kind != ObjectZoneTrackingBridge.MatchKind.Exact)
                    {
                        row.Status = Status.Suspect;
                        row.Detail = res.Kind switch
                        {
                            ObjectZoneTrackingBridge.MatchKind.Soft => $"only inside with ±{chain.Tolerance}m tolerance — enlarge the volume or move the object.",
                            ObjectZoneTrackingBridge.MatchKind.Lifted => $"pivot {res.Distance:F2}m above the volume top — lower the object (ceiling-lift relic).",
                            _ => $"fallback: nearest volume at {res.Distance:F2}m — coverage gap, fix the volume.",
                        };
                    }
                    else if (res.AmbiguousMatches > 1)
                    {
                        row.Status = Status.Suspect;
                        row.Detail = $"within {chain.Tolerance}m of {res.AmbiguousMatches} volumes — a small room resize can flip the zone.";
                    }
                    else
                    {
                        row.Status = Status.Ok;
                        row.Detail = row.ProximityThreshold > 0f ? $"proximity-gated, threshold {row.ProximityThreshold}m." : "";
                    }
                }
                _rows.Add(row);
            }

            _rows.Sort((a, b) => a.Status.CompareTo(b.Status));
        }

        /// <summary>Zone ids required by the governing scenario(s): the manual override, or every
        /// Scenario asset whose scene or dependencies are among the open scenes.</summary>
        private HashSet<string> CollectExpectedZoneIds()
        {
            var expected = new HashSet<string>();
            var scenarios = new List<Scenario>();

            if (_scenarioOverride != null) scenarios.Add(_scenarioOverride);
            else
            {
                var openPaths = new HashSet<string>();
                for (var i = 0; i < SceneManager.sceneCount; i++) openPaths.Add(SceneManager.GetSceneAt(i).path);

                foreach (var guid in AssetDatabase.FindAssets("t:Scenario"))
                {
                    var scenario = AssetDatabase.LoadAssetAtPath<Scenario>(AssetDatabase.GUIDToAssetPath(guid));
                    if (scenario == null) continue;
                    if (ReferencesAnOpenScene(scenario, openPaths)) scenarios.Add(scenario);
                }
            }

            foreach (var scenario in scenarios)
            {
                _scenarioNames.Add(scenario.scenarioName ?? scenario.name);
                if (scenario.listOfZonesNeededForThisScenario == null) continue;
                foreach (var zone in scenario.listOfZonesNeededForThisScenario)
                    if (zone != null) expected.Add($"{zone.id}");
            }
            return expected;
        }

        private static bool ReferencesAnOpenScene(Scenario scenario, HashSet<string> openPaths)
        {
            if (SceneRefMatches(scenario.scene, openPaths)) return true;
            if (scenario.dependenciesInThisScenario == null) return false;
            foreach (var dependency in scenario.dependenciesInThisScenario)
                if (SceneRefMatches(dependency, openPaths)) return true;
            return false;
        }

        private static bool SceneRefMatches(SceneReference reference, HashSet<string> openPaths)
        {
            var asset = reference?.EditorSceneAsset;
            return asset != null && openPaths.Contains(AssetDatabase.GetAssetPath(asset));
        }

        private Dictionary<string, float> CollectProximityThresholds()
        {
            var result = new Dictionary<string, float>();
            var bridge = FindFirstObjectByType<ObjectZoneTrackingBridge>(FindObjectsInactive.Include);
            if (bridge == null) return result;
            foreach (var config in bridge.ProximityZones)
                if (config.zone != null && config.threshold > 0f) result[$"{config.zone.id}"] = config.threshold;
            return result;
        }
    }
}
#endif
