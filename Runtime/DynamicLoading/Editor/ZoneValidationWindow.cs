#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Edit-mode audit of play-mode zone detection: every <see cref="IZoneTrackedObject"/> in the
    /// open scenes is resolved against the open volumes with the bridge's exact failsafe chain
    /// (<see cref="EditorZoneResolver"/>) and classified binary:
    ///  - BROKEN — resolves to no zone at all (pending at runtime), or to a zone that is not among
    ///    the ones the governing <see cref="Scenario"/> requires (listOfZonesNeededForThisScenario);
    ///  - OK — resolves to a scenario-consistent zone. How it resolved (edge/lifted/fallback) is
    ///    kept as informational detail, not a status.
    /// Rows offer Select (ping in hierarchy) and, when the pivot is not exactly inside its correct
    /// volume, Fix — the minimal per-axis snap of the object into that volume (undoable).
    /// The governing scenario is auto-detected from the open scenes (Scenario.scene or its
    /// dependencies) and can be overridden manually.
    /// </summary>
    public class ZoneValidationWindow : EditorWindow
    {
        private enum Status { Broken, Ok }

        private struct Row
        {
            public Transform Target;
            public Status Status;
            public string ZoneLabel;
            public string Detail;
            /// <summary>Volume to snap the object into with Fix; null when no fix applies.</summary>
            public VolumeAuthoring FixVolume;
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
            EditorGUILayout.LabelField($"Volumes: {_volumeCount}   Objects: {_rows.Count}   Scenario: {scenarioLabel}");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Broken: {broken}   OK: {_rows.Count - broken}", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(broken == 0))
                    if (GUILayout.Button($"Select broken ({broken})", GUILayout.Width(140)))
                        Selection.objects = _rows.Where(r => r.Status == Status.Broken && r.Target != null)
                            .Select(r => (Object)r.Target.gameObject).ToArray();
            }
            EditorGUILayout.Space(2);

            var rescanNeeded = false;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in _rows)
            {
                if (row.Status == Status.Ok && !_showOk) continue;
                if (row.Target == null) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    var icon = row.Status == Status.Broken ? "console.erroricon.sml" : "TestPassed";
                    GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(20), GUILayout.Height(18));
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(row.Target.gameObject, typeof(GameObject), true, GUILayout.MinWidth(140));
                    if (GUILayout.Button("Select", GUILayout.Width(50)))
                    {
                        Selection.activeGameObject = row.Target.gameObject;
                        EditorGUIUtility.PingObject(row.Target.gameObject);
                        SceneView.lastActiveSceneView?.Frame(new Bounds(row.Target.position, Vector3.one * 2f), false);
                    }
                    using (new EditorGUI.DisabledScope(row.FixVolume == null))
                        if (GUILayout.Button(new GUIContent("Fix",
                                row.FixVolume != null
                                    ? $"Snap into '{row.FixVolume.zone.zoneName}' with the minimal per-axis move (undoable)."
                                    : "No target volume to snap into."), GUILayout.Width(36)))
                        {
                            SnapIntoVolume(row.Target, row.FixVolume);
                            rescanNeeded = true;
                        }
                    EditorGUILayout.LabelField(row.ZoneLabel, GUILayout.Width(170));
                    EditorGUILayout.LabelField(row.Detail);
                }
            }
            EditorGUILayout.EndScrollView();
            if (rescanNeeded)
                EditorApplication.delayCall += () => { Scan(); Repaint(); };

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
                    row.Detail = "outside every volume (all failsafes miss): pending forever at runtime.";
                    row.FixVolume = NearestVolume(target.position, volumes, expectedZoneIds);
                }
                else
                {
                    row.ZoneLabel = $"{res.Zone.zoneName} ({res.Zone.id})";
                    var kindNote = res.Kind switch
                    {
                        ObjectZoneTrackingBridge.MatchKind.Soft => $"pivot on the volume edge (±{chain.Tolerance}m). ",
                        ObjectZoneTrackingBridge.MatchKind.Lifted => $"pivot {res.Distance:F2}m above the volume (ceiling-lift). ",
                        ObjectZoneTrackingBridge.MatchKind.Fallback => $"pivot outside, nearest volume at {res.Distance:F2}m. ",
                        _ => "",
                    };

                    if (expectedZoneIds.Count > 0 && !expectedZoneIds.Contains($"{res.Zone.id}"))
                    {
                        row.Status = Status.Broken;
                        row.Detail = $"zone not required by the scenario — wrong room. {kindNote}";
                        row.FixVolume = NearestVolume(target.position, volumes, expectedZoneIds);
                    }
                    else
                    {
                        row.Status = Status.Ok;
                        if (proximityByZone.TryGetValue($"{res.Zone.id}", out var threshold))
                            kindNote += $"proximity-gated, threshold {threshold}m.";
                        row.Detail = kindNote;
                        // Not exactly inside its own volume: works at runtime, but offer the snap.
                        if (res.Kind != ObjectZoneTrackingBridge.MatchKind.Exact) row.FixVolume = res.Volume;
                    }
                }
                _rows.Add(row);
            }

            _rows.Sort((a, b) => a.Status.CompareTo(b.Status));
        }

        /// <summary>Nearest volume the object SHOULD be in: nearest among the scenario's expected
        /// zones when any are known, nearest overall otherwise.</summary>
        private static VolumeAuthoring NearestVolume(Vector3 position, List<VolumeAuthoring> volumes, HashSet<string> expectedZoneIds)
        {
            VolumeAuthoring best = null;
            var bestDistSq = float.MaxValue;
            foreach (var volume in volumes)
            {
                if (expectedZoneIds.Count > 0 && !expectedZoneIds.Contains($"{volume.zone.id}")) continue;
                var t = volume.transform;
                var distSq = VolumeMath.DistanceSq(float4x4.TRS(t.position, t.rotation, new float3(1f)), t.localScale, position);
                if (distSq >= bestDistSq) continue;
                bestDistSq = distSq;
                best = volume;
            }
            return best;
        }

        /// <summary>
        /// Moves the object into the volume with the minimal per-axis correction: the pivot is
        /// expressed in the volume's rotation-only local frame and clamped (with a small inset)
        /// into the box — axes already inside don't move, so the common case is a straight move
        /// along one axis toward the volume. Undoable.
        /// </summary>
        private static void SnapIntoVolume(Transform target, VolumeAuthoring volume)
        {
            var t = volume.transform;
            var frame = float4x4.TRS(t.position, t.rotation, new float3(1f));
            var local = VolumeMath.ToLocal(frame, target.position);
            var extents = (float3)t.localScale * 0.5f;
            var inset = math.min(new float3(0.1f), extents * 0.25f);
            var limit = math.max(extents - inset, new float3(0f));
            var clamped = math.clamp(local, -limit, limit);
            if (math.all(clamped == local)) return; // already inside

            Undo.RecordObject(target, "Snap into zone volume");
            target.position = math.transform(frame, clamped);
            EditorUtility.SetDirty(target);
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
