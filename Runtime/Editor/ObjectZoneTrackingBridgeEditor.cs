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
    }
}
