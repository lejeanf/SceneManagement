using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement.Editor
{
    [CustomEditor(typeof(ZoneExitAnchor))]
    [CanEditMultipleObjects]
    public class ZoneExitAnchorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Place this anchor OUTSIDE the zones it evacuates, in a scene that stays loaded while " +
                "the scenario runs. The scene gizmo names the zone it actually resolves to and turns " +
                "red when it sits in a zone it is supposed to empty.\n" +
                "Zones with neither an anchor nor a door exit are treated as not lockable: the scenario " +
                "unloads without moving anybody.", MessageType.Info);

            if (!GUILayout.Button("Detect zone this anchor stands in")) return;

            foreach (var target in targets)
            {
                if (target is not ZoneExitAnchor anchor) continue;

                var zone = anchor.DetectDestinationZone();
                if (zone == null)
                {
                    Debug.LogWarning($"[ZoneExitAnchor] '{anchor.name}' resolved to no zone. Open the SubScene " +
                                     "holding the VolumeAuthoring objects for edit, then try again.", anchor);
                    continue;
                }

                Undo.RecordObject(anchor, "Detect zone");
                anchor.DestinationZone = zone;
                EditorUtility.SetDirty(anchor);
                Debug.Log($"[ZoneExitAnchor] '{anchor.name}' stands in zone '{zone.zoneName}'.", anchor);
            }
        }
    }
}
