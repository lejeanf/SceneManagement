using System.Linq;
using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement.editor
{
    /// <summary>
    /// Live view of the zone-tracking state: per-zone object lists, pending (coverage-gap)
    /// objects, and counts. Select the bridge in play mode to watch assignments happen.
    /// </summary>
    [CustomEditor(typeof(ObjectZoneTrackingBridge))]
    public class ObjectZoneTrackingBridgeEditor : UnityEditor.Editor
    {
        private const string SimulateKey = "OZTB_simulateProximity";
        private const string SimPosKey = "OZTB_simPlayerPos";

        private bool _showPending = true;
        private string _zoneFilter = "";

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live tracking", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play mode to see live zone assignments here.", MessageType.Info);
                DrawProximitySimulationControls();
                return;
            }

            var bridge = (ObjectZoneTrackingBridge)target;
            var zoneDict = WorldManager.GetZoneDictionary();

            EditorGUILayout.LabelField($"Registered objects: {bridge.TrackedObjectCount}");

            var pending = bridge.PendingObjects.Where(g => g != null).ToList();
            _showPending = EditorGUILayout.Foldout(_showPending, $"Pending / no volume match ({pending.Count})", true);
            if (_showPending)
            {
                if (pending.Count == 0) EditorGUILayout.LabelField("   none — full coverage");
                foreach (var go in pending)
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(go, typeof(GameObject), true);
            }

            EditorGUILayout.Space(4);
            _zoneFilter = EditorGUILayout.TextField("Filter zones", _zoneFilter);

            foreach (var zoneId in bridge.ZoneIdsWithObjects.OrderBy(z => z))
            {
                var label = zoneId;
                if (zoneDict != null && zoneDict.TryGetValue(zoneId, out var zone) && zone != null && !string.IsNullOrEmpty(zone.zoneName))
                    label = $"{zone.zoneName}  ({zoneId})";
                if (!string.IsNullOrEmpty(_zoneFilter) &&
                    label.IndexOf(_zoneFilter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                var objects = bridge.GetObjectsInZone(zoneId);
                var key = $"OZTB_zone_{zoneId}";
                var expanded = SessionState.GetBool(key, false);
                var next = EditorGUILayout.Foldout(expanded, $"{label} — {objects.Count} object(s)", true);
                if (next != expanded) SessionState.SetBool(key, next);
                if (!next) continue;

                foreach (var go in objects)
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(go, typeof(GameObject), true);
            }
        }

        #region Edit-mode proximity simulation

        private static bool SimulateProximity
        {
            get => SessionState.GetBool(SimulateKey, false);
            set => SessionState.SetBool(SimulateKey, value);
        }

        private static Vector3 SimPlayerPos
        {
            get => new Vector3(SessionState.GetFloat(SimPosKey + "x", 0f),
                               SessionState.GetFloat(SimPosKey + "y", 0f),
                               SessionState.GetFloat(SimPosKey + "z", 0f));
            set
            {
                SessionState.SetFloat(SimPosKey + "x", value.x);
                SessionState.SetFloat(SimPosKey + "y", value.y);
                SessionState.SetFloat(SimPosKey + "z", value.z);
            }
        }

        private void DrawProximitySimulationControls()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Proximity simulation (edit mode)", EditorStyles.boldLabel);

            var simulate = EditorGUILayout.ToggleLeft(
                new GUIContent("Simulate proximity in Scene view",
                    "Draws each proximity-gated object's threshold disc and a draggable 'simulated player' " +
                    "handle in the Scene view. Distance is horizontal-only, exactly like the runtime check — " +
                    "tune thresholds in the list above and watch discs and states update live."),
                SimulateProximity);
            if (simulate != SimulateProximity)
            {
                SimulateProximity = simulate;
                if (simulate) SimPlayerPos = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
                SceneView.RepaintAll();
            }

            if (simulate && GUILayout.Button("Move simulated player to Scene view pivot"))
            {
                if (SceneView.lastActiveSceneView != null) SimPlayerPos = SceneView.lastActiveSceneView.pivot;
                SceneView.RepaintAll();
            }
        }

        private void OnSceneGUI()
        {
            if (Application.isPlaying || !SimulateProximity) return;
            var bridge = (ObjectZoneTrackingBridge)target;

            var proximityByZone = new System.Collections.Generic.Dictionary<string, float>();
            foreach (var config in bridge.ProximityZones)
                if (config.zone != null && config.threshold > 0f) proximityByZone[$"{config.zone.id}"] = config.threshold;
            if (proximityByZone.Count == 0)
            {
                Handles.Label(bridge.transform.position, "No proximity zones configured on the bridge.");
                return;
            }

            // Draggable simulated player.
            var simPos = SimPlayerPos;
            EditorGUI.BeginChangeCheck();
            simPos = Handles.PositionHandle(simPos, Quaternion.identity);
            if (EditorGUI.EndChangeCheck()) SimPlayerPos = simPos;
            Handles.color = new Color(0.3f, 0.7f, 1f, 0.9f);
            Handles.SphereHandleCap(0, simPos, Quaternion.identity, 0.25f, EventType.Repaint);
            Handles.Label(simPos + Vector3.up * 0.4f, "Simulated player");

            var volumes = EditorZoneResolver.GatherVolumes();
            if (volumes.Count == 0)
            {
                Handles.Label(simPos + Vector3.up * 0.7f, "No volumes in open scenes — open the volume SubScene for edit.");
                return;
            }
            var chain = EditorZoneResolver.SceneChainParams();

            foreach (var tracked in EditorZoneResolver.GatherTrackedObjects())
            {
                var resolution = EditorZoneResolver.Resolve(tracked.position, volumes, chain);
                if (!resolution.HasZone) continue;
                if (!proximityByZone.TryGetValue($"{resolution.Zone.id}", out var threshold)) continue;

                // Horizontal distance, mirroring UpdateProximity.
                var delta = tracked.position - simPos;
                delta.y = 0f;
                var distance = delta.magnitude;
                var visible = distance <= threshold;

                Handles.color = visible ? new Color(0.2f, 0.9f, 0.3f, 0.9f) : new Color(0.95f, 0.4f, 0.25f, 0.7f);
                Handles.DrawWireDisc(tracked.position, Vector3.up, threshold, visible ? 3f : 1.5f);
                Handles.Label(tracked.position + Vector3.up * 0.3f,
                    $"{tracked.name}\n{distance:F1}m / {threshold:F1}m — {(visible ? "VISIBLE" : "hidden")}");
            }
        }

        #endregion
    }
}
