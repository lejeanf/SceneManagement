#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [CustomEditor(typeof(WorldManager))]
    public class SpawnPosEditor : Editor
    {
        private void OnSceneGUI()
        {
            WorldManager worldManager = (WorldManager) target;
            if (worldManager.ListOfRegions == null) return;

            foreach (var region in worldManager.ListOfRegions)
            {
                if (region.isUsingOnInitSpawnPos)
                {
                    DrawSpawnPosHandle(ref region.SpawnPosOnInit, $"{region.levelName} - Initial Spawn");
                }
                DrawSpawnPosHandle(ref region.SpawnPosOnRegionChangeRequest, $"{region.levelName} - Manual Spawn");
            }
        }

        private void DrawSpawnPosHandle(ref SpawnPos spawnPos, string label)
        {
            EditorGUI.BeginChangeCheck();

            float handleSize = HandleUtility.GetHandleSize(spawnPos.position) * .5f;
        
            // Draw position handle
            Vector3 newPosition = Handles.PositionHandle(spawnPos.position, Quaternion.identity);
        
            // Draw rotation handle (only yaw rotation around Y-axis)
            Quaternion newRotation = Handles.Disc(Quaternion.Euler(spawnPos.rotation), spawnPos.position, Vector3.up, handleSize, false, 1f);
        
            // Draw label
            Handles.Label(spawnPos.position, label, new GUIStyle { fontStyle = FontStyle.Bold, normal = new GUIStyleState { textColor = Color.white } });
        
            // Draw front marker with scaled size
            Vector3 forward = Quaternion.Euler(spawnPos.rotation) * Vector3.forward * handleSize;
            Handles.color = Color.cyan;
            Handles.ArrowHandleCap(0, spawnPos.position, Quaternion.LookRotation(forward), handleSize, EventType.Repaint);
        
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(Selection.activeGameObject, "Move Spawn Position");
                spawnPos.position = newPosition;
                spawnPos.rotation = new Vector3(0, newRotation.eulerAngles.y, 0);
                EditorUtility.SetDirty(Selection.activeGameObject);
            }
        }
    }
}
#endif