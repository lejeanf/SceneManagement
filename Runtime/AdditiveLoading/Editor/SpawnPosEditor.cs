#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [CustomEditor(typeof(WorldManager))]
    public class SpawnPosEditor : Editor
    {
        private static readonly Color InitColor = new Color(0.35f, 1f, 0.45f);
        private static readonly Color ManualColor = new Color(0.4f, 0.7f, 1f);

        private bool _showSpawns = true;
        private bool _showInit = true;
        private bool _showManual = true;
        private bool _editMode = false;
        private int _editIndex = 0;
        private float _maxLabelDistance = 60f;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var worldManager = (WorldManager)target;
            if (worldManager.ListOfRegions == null || worldManager.ListOfRegions.Count == 0) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Spawn Visualization", EditorStyles.boldLabel);

            _showSpawns = EditorGUILayout.Toggle("Show Spawns", _showSpawns);

            using (new EditorGUI.DisabledScope(!_showSpawns))
            {
                EditorGUILayout.BeginHorizontal();
                _showInit = EditorGUILayout.ToggleLeft("Initial", _showInit, GUILayout.Width(90));
                _showManual = EditorGUILayout.ToggleLeft("Manual", _showManual, GUILayout.Width(90));
                EditorGUILayout.EndHorizontal();

                _maxLabelDistance = EditorGUILayout.Slider("Label Distance", _maxLabelDistance, 5f, 500f);

                _editMode = EditorGUILayout.Toggle("Edit Mode", _editMode);

                using (new EditorGUI.DisabledScope(!_editMode))
                {
                    var regions = worldManager.ListOfRegions;
                    var names = new string[regions.Count];
                    for (int i = 0; i < names.Length; i++)
                    {
                        var r = regions[i];
                        names[i] = r != null ? r.levelName : $"<null {i}>";
                    }
                    _editIndex = Mathf.Clamp(_editIndex, 0, names.Length - 1);
                    _editIndex = EditorGUILayout.Popup("Edit Region", _editIndex, names);
                }
            }

            if (GUI.changed) SceneView.RepaintAll();
        }

        private void OnSceneGUI()
        {
            if (!_showSpawns) return;

            var worldManager = (WorldManager)target;
            if (worldManager.ListOfRegions == null) return;

            var regions = worldManager.ListOfRegions;
            for (int i = 0; i < regions.Count; i++)
            {
                var region = regions[i];
                if (region == null) continue;

                var editable = _editMode && i == _editIndex;

                if (_showInit && region.isUsingOnInitSpawnPos)
                    DrawSpawnPosHandle(region, ref region.SpawnPosOnInit, $"{region.levelName} • Init", InitColor, editable);

                if (_showManual)
                    DrawSpawnPosHandle(region, ref region.SpawnPosOnRegionChangeRequest, $"{region.levelName} • Manual", ManualColor, editable);
            }
        }

        private void DrawSpawnPosHandle(Region region, ref SpawnPos spawnPos, string label, Color color, bool editable)
        {
            float handleSize = HandleUtility.GetHandleSize(spawnPos.position) * .5f;

            if (editable)
            {
                EditorGUI.BeginChangeCheck();

                // Draw position handle
                Vector3 newPosition = Handles.PositionHandle(spawnPos.position, Quaternion.identity);

                // Draw rotation handle (only yaw rotation around Y-axis)
                Quaternion newRotation = Handles.Disc(Quaternion.Euler(spawnPos.rotation), spawnPos.position, Vector3.up, handleSize, false, 1f);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(region, "Move Spawn Position");
                    spawnPos.position = newPosition;
                    spawnPos.rotation = new Vector3(0, newRotation.eulerAngles.y, 0);
                    EditorUtility.SetDirty(region);
                }
            }
            else
            {
                Handles.color = color;
                Handles.SphereHandleCap(0, spawnPos.position, Quaternion.identity, handleSize * 0.35f, EventType.Repaint);
            }

            // Draw front marker with scaled size
            Vector3 forward = Quaternion.Euler(spawnPos.rotation) * Vector3.forward * handleSize;
            if (forward == Vector3.zero) forward = Vector3.forward;
            Handles.color = color;
            Handles.ArrowHandleCap(0, spawnPos.position, Quaternion.LookRotation(forward), handleSize, EventType.Repaint);

            // Draw label
            if (IsWithinLabelDistance(spawnPos.position))
            {
                Handles.Label(spawnPos.position + Vector3.up * handleSize * 1.2f, label,
                    new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = color } });
            }
        }

        private bool IsWithinLabelDistance(Vector3 worldPos)
        {
            var sceneView = SceneView.currentDrawingSceneView;
            if (sceneView == null || sceneView.camera == null) return true;
            return Vector3.Distance(sceneView.camera.transform.position, worldPos) <= _maxLabelDistance;
        }
    }
}
#endif
