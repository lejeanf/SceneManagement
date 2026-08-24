#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Edit-mode audit of play-mode zone detection, grouped per scenario. Every
    /// <see cref="IZoneTrackedObject"/> in the open scenes is resolved once against the open
    /// volumes with the bridge's exact failsafe chain (<see cref="EditorZoneResolver"/>), then
    /// classified under every <see cref="Scenario"/> whose scene or dependencies include the scene
    /// the object lives in (the same object can be OK for one scenario and broken for another):
    ///  - BROKEN — resolves to no zone at all (pending at runtime), or to a zone the scenario's
    ///    listOfZonesNeededForThisScenario does not include;
    ///  - OK — resolves to a scenario-consistent zone; how it resolved (edge/lifted/fallback) is
    ///    informational detail, not a status.
    /// Objects living in scenes no scenario owns are grouped separately and only checked for
    /// volume coverage. 'Load scenario scenes + scan' opens every scenario's scene and
    /// dependencies (only the override's when one is set) additively and audits all of them at
    /// once. Row actions: Select (ping + frame), Fix (minimal per-axis snap into the correct
    /// volume, undoable), Add zone (append the detected zone to that scenario's
    /// listOfZonesNeededForThisScenario when the object is correctly placed and the scenario list
    /// is what's incomplete).
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
            public Zone DetectedZone;
            /// <summary>Volume to snap the object into with Fix; null when no fix applies.</summary>
            public VolumeAuthoring FixVolume;
            /// <summary>Scenario 'Add zone' would modify; null outside scenario groups.</summary>
            public Scenario TargetScenario;
        }

        private class Group
        {
            public Scenario Scenario;
            public string Name;
            public string Note;
            public readonly List<Row> Rows = new List<Row>();
            public int Broken;
        }

        private readonly List<Group> _groups = new List<Group>();
        private Scenario _scenarioOverride;
        private bool _showOk = true;
        private int _volumeCount;
        private int _objectCount;
        private bool _scanned;
        private Vector2 _scroll;

        [MenuItem("Tools/Jeanf/SceneManagement/Zone Detection Validation")]
        private static void Open() => GetWindow<ZoneValidationWindow>("Zone Validation");

        private void OnHierarchyChange() => _scanned = false;

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan open scenes", GUILayout.Height(24))) Scan();
                if (_scenarioOverride != null &&
                    GUILayout.Button(new GUIContent($"Load '{_scenarioOverride.scenarioName}' + scan",
                        "Opens this scenario's scene and dependencies additively, then scans."),
                        GUILayout.Height(24), GUILayout.Width(200)))
                    LoadScenarioScenesAndScan(allScenarios: false);
                if (GUILayout.Button(new GUIContent("Load ALL scenarios + scan",
                        "Opens every scenario's scene and dependencies additively, clears the override, and audits " +
                        "the whole project at once — one group per scenario."), GUILayout.Height(24), GUILayout.Width(180)))
                    LoadScenarioScenesAndScan(allScenarios: true);
                _showOk = GUILayout.Toggle(_showOk, "show OK", GUILayout.Width(80));
            }
            _scenarioOverride = (Scenario)EditorGUILayout.ObjectField(
                new GUIContent("Scenario override", "Leave empty to audit every scenario; set to restrict the audit to one."),
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

            var broken = _groups.Sum(g => g.Broken);
            var total = _groups.Sum(g => g.Rows.Count);
            EditorGUILayout.LabelField($"Volumes: {_volumeCount}   Objects: {_objectCount}   Scenarios: {_groups.Count(g => g.Scenario != null)}");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Broken: {broken}   OK: {total - broken}", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(broken == 0))
                    if (GUILayout.Button($"Select broken ({broken})", GUILayout.Width(140)))
                        Selection.objects = _groups.SelectMany(g => g.Rows)
                            .Where(r => r.Status == Status.Broken && r.Target != null)
                            .Select(r => (Object)r.Target.gameObject).Distinct().ToArray();
            }
            EditorGUILayout.Space(2);

            var rescanNeeded = false;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var group in _groups)
            {
                var key = $"ZoneVal_group_{group.Name}";
                var expanded = SessionState.GetBool(key, group.Broken > 0);
                var header = $"{group.Name} — Broken: {group.Broken}   OK: {group.Rows.Count - group.Broken}";
                bool next;
                using (new EditorGUILayout.HorizontalScope())
                {
                    next = EditorGUILayout.Foldout(expanded, header, true,
                        group.Broken > 0 ? EditorStyles.foldoutHeader : EditorStyles.foldout);
                    if (next != expanded) SessionState.SetBool(key, next);

                    var dirtyScenes = group.Rows.Where(r => r.Target != null)
                        .Select(r => r.Target.gameObject.scene).Distinct().Where(s => s.isDirty).ToList();
                    var scenarioDirty = group.Scenario != null && EditorUtility.IsDirty(group.Scenario);
                    using (new EditorGUI.DisabledScope(dirtyScenes.Count == 0 && !scenarioDirty))
                        if (GUILayout.Button(new GUIContent(
                                dirtyScenes.Count > 1 ? $"Save {dirtyScenes.Count} scenes" : "Save",
                                "Saves this group's modified scene(s), and the scenario asset when 'Add zone' changed it."),
                                GUILayout.Width(100)))
                        {
                            foreach (var scene in dirtyScenes) EditorSceneManager.SaveScene(scene);
                            if (scenarioDirty) AssetDatabase.SaveAssetIfDirty(group.Scenario);
                        }
                }
                if (!next) continue;

                using (new EditorGUI.IndentLevelScope())
                {
                    if (!string.IsNullOrEmpty(group.Note))
                        EditorGUILayout.HelpBox(group.Note, MessageType.Info);
                    foreach (var row in group.Rows)
                    {
                        if (row.Status == Status.Ok && !_showOk) continue;
                        if (row.Target == null) continue;
                        if (DrawRow(row)) rescanNeeded = true;
                    }
                }
            }
            EditorGUILayout.EndScrollView();
            if (rescanNeeded)
                EditorApplication.delayCall += () => { Scan(); Repaint(); };

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox("Proximity: select the ObjectZoneTrackingBridge (Core prefab) and enable " +
                "'Simulate proximity' in its inspector to tune thresholds in the Scene view without entering play mode.", MessageType.None);
        }

        /// <summary>Draws one result row; returns true when an action changed the scene/assets.</summary>
        private bool DrawRow(in Row row)
        {
            var changed = false;
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
                        changed = true;
                    }
                if (row.Status == Status.Broken && row.DetectedZone != null && row.TargetScenario != null)
                {
                    if (GUILayout.Button(new GUIContent("Add zone",
                            $"The object is correctly placed — add '{row.DetectedZone.zoneName}' to " +
                            $"'{row.TargetScenario.name}'.listOfZonesNeededForThisScenario instead of moving it (undoable)."),
                            GUILayout.Width(70)))
                    {
                        AddZoneToScenario(row.TargetScenario, row.DetectedZone);
                        changed = true;
                    }
                }
                EditorGUILayout.LabelField(row.Target.gameObject.scene.name, GUILayout.Width(120));
                EditorGUILayout.LabelField(row.ZoneLabel, GUILayout.Width(170));
                EditorGUILayout.LabelField(row.Detail);
            }
            return changed;
        }

        private void Scan()
        {
            _groups.Clear();
            _scanned = true;

            var volumes = EditorZoneResolver.GatherVolumes();
            _volumeCount = volumes.Count;
            if (_volumeCount == 0) return;

            var chain = EditorZoneResolver.SceneChainParams();
            var proximityByZone = CollectProximityThresholds();
            var scenarios = CollectScenarios();

            // Resolve every object once; classification varies per scenario.
            var resolved = EditorZoneResolver.GatherTrackedObjects()
                .Select(t => (Target: t, Res: EditorZoneResolver.Resolve(t.position, volumes, chain)))
                .ToList();
            _objectCount = resolved.Count;

            var openPaths = new HashSet<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++) openPaths.Add(SceneManager.GetSceneAt(i).path);

            var claimed = new HashSet<Transform>();
            foreach (var scenario in scenarios)
            {
                // Match by asset path AND by scene name — SceneReference assets can go stale while
                // the name still identifies the open scene unambiguously.
                var paths = new HashSet<string>(ScenarioScenePaths(scenario));
                var names = new HashSet<string>(paths.Select(System.IO.Path.GetFileNameWithoutExtension));
                if (!string.IsNullOrEmpty(scenario.scene?.Name)) names.Add(scenario.scene.Name);
                var members = resolved.Where(r => paths.Contains(r.Target.gameObject.scene.path)
                                               || names.Contains(r.Target.gameObject.scene.name)).ToList();

                // Skip scenarios with no presence at all; keep the override and any scenario whose
                // scenes are open, so an empty result is visible instead of silently vanishing.
                if (members.Count == 0 && scenario != _scenarioOverride && !paths.Overlaps(openPaths)) continue;

                var group = new Group { Scenario = scenario, Name = scenario.scenarioName ?? scenario.name };
                if (members.Count == 0)
                {
                    var perScene = resolved.GroupBy(r => r.Target.gameObject.scene.name)
                        .Select(g => $"{g.Key}:{g.Count()}");
                    group.Note = $"No tracked objects matched this scenario's scenes [{string.Join(", ", names)}]. " +
                                 $"Tracked objects live in: {string.Join(", ", perScene)}.";
                    Debug.Log($"[ZoneValidation] '{group.Name}': 0 objects matched. Scenario scene paths: " +
                              $"[{string.Join(" | ", paths)}]. Open scenes: [{string.Join(" | ", openPaths)}]. " +
                              $"Objects per scene: {string.Join(", ", perScene)}");
                }

                var expectedZoneIds = new HashSet<string>();
                if (scenario.listOfZonesNeededForThisScenario != null)
                    foreach (var zone in scenario.listOfZonesNeededForThisScenario)
                        if (zone != null) expectedZoneIds.Add($"{zone.id}");

                foreach (var (target, res) in members)
                {
                    claimed.Add(target);
                    group.Rows.Add(Classify(target, res, expectedZoneIds, scenario, volumes, chain, proximityByZone));
                }
                Finish(group);
                _groups.Add(group);
            }

            // Objects in scenes no audited scenario owns (persistent scene, tools…): coverage check only.
            var orphans = resolved.Where(r => !claimed.Contains(r.Target)).ToList();
            if (orphans.Count > 0)
            {
                var group = new Group { Scenario = null, Name = "No scenario (persistent/unowned scenes)" };
                var none = new HashSet<string>();
                foreach (var (target, res) in orphans)
                    group.Rows.Add(Classify(target, res, none, null, volumes, chain, proximityByZone));
                Finish(group);
                _groups.Add(group);
            }

            _groups.Sort((a, b) => (b.Broken - a.Broken) != 0 ? b.Broken.CompareTo(a.Broken)
                : string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
        }

        private static void Finish(Group group)
        {
            group.Rows.Sort((a, b) => a.Status.CompareTo(b.Status));
            group.Broken = group.Rows.Count(r => r.Status == Status.Broken);
        }

        private Row Classify(Transform target, in EditorZoneResolver.Resolution res, HashSet<string> expectedZoneIds,
            Scenario scenario, List<VolumeAuthoring> volumes, in EditorZoneResolver.ChainParams chain,
            Dictionary<string, float> proximityByZone)
        {
            var row = new Row { Target = target, DetectedZone = res.Zone, TargetScenario = scenario };

            if (!res.HasZone)
            {
                row.Status = Status.Broken;
                row.ZoneLabel = "— no volume —";
                row.Detail = "outside every volume (all failsafes miss): pending forever at runtime.";
                row.FixVolume = NearestVolume(target.position, volumes, expectedZoneIds);
                return row;
            }

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
                row.Detail = $"zone not required by the scenario — wrong room, or add it to the scenario. {kindNote}";
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
            return row;
        }

        /// <summary>The override scenario alone, or every Scenario asset in the project.</summary>
        private List<Scenario> CollectScenarios()
        {
            var scenarios = new List<Scenario>();
            if (_scenarioOverride != null) { scenarios.Add(_scenarioOverride); return scenarios; }
            foreach (var guid in AssetDatabase.FindAssets("t:Scenario"))
            {
                var scenario = AssetDatabase.LoadAssetAtPath<Scenario>(AssetDatabase.GUIDToAssetPath(guid));
                if (scenario != null) scenarios.Add(scenario);
            }
            return scenarios;
        }

        private static IEnumerable<string> ScenarioScenePaths(Scenario scenario)
        {
            var main = ScenePathOf(scenario.scene);
            if (main != null) yield return main;
            if (scenario.dependenciesInThisScenario == null) yield break;
            foreach (var dependency in scenario.dependenciesInThisScenario)
            {
                var path = ScenePathOf(dependency);
                if (path != null) yield return path;
            }
        }

        /// <summary>Asset path of the referenced scene; falls back to the addressable address when
        /// it is itself an asset path (the common case in this project).</summary>
        private static string ScenePathOf(SceneReference reference)
        {
            if (reference == null) return null;
            var asset = reference.EditorSceneAsset;
            if (asset != null)
            {
                var path = AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(path)) return path;
            }
            var address = reference.Address;
            return !string.IsNullOrEmpty(address) && address.EndsWith(".unity") ? address : null;
        }

        /// <summary>Opens the audited scenarios' scenes additively, then scans all of them at once.</summary>
        private void LoadScenarioScenesAndScan(bool allScenarios)
        {
            if (allScenarios) _scenarioOverride = null; // group per scenario, audit everything

            var toOpen = CollectScenarios().SelectMany(ScenarioScenePaths).Distinct()
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => { var s = SceneManager.GetSceneByPath(path); return !s.IsValid() || !s.isLoaded; })
                .ToList();

            if (toOpen.Count > 10 && !EditorUtility.DisplayDialog("Zone Validation",
                    $"This will open {toOpen.Count} scenario scenes additively. Continue?", "Open + scan", "Cancel"))
                return;

            foreach (var path in toOpen) EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            Debug.Log($"[ZoneValidation] Opened {toOpen.Count} scenario scene(s) additively.");
            Scan();
        }

        private static void AddZoneToScenario(Scenario scenario, Zone zone)
        {
            Undo.RecordObject(scenario, "Add zone to scenario");
            scenario.listOfZonesNeededForThisScenario ??= new List<Zone>();
            if (!scenario.listOfZonesNeededForThisScenario.Contains(zone))
                scenario.listOfZonesNeededForThisScenario.Add(zone);
            EditorUtility.SetDirty(scenario);
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
