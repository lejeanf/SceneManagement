#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace jeanf.scenemanagement
{
    [CustomEditor(typeof(WorldManager))]
    public class WorldManagerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            var worldManager = (WorldManager)target;
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Current Player State", EditorStyles.boldLabel);
            
            var currentZone = WorldManager.CurrentPlayerZone;
            var currentRegion = WorldManager.CurrentPlayerRegion;
            
            EditorGUI.BeginDisabledGroup(true);
            
            if (currentZone != null)
            {
                EditorGUILayout.TextField("Current Zone", $"{currentZone.zoneName}");
            }
            else
            {
                EditorGUILayout.TextField("Current Zone", "None");
            }
            
            if (currentRegion != null)
            {
                EditorGUILayout.TextField("Current Region", $"{currentRegion.levelName}");
            }
            else
            {
                EditorGUILayout.TextField("Current Region", "None");
            }
            
            EditorGUI.EndDisabledGroup();
            
            if (Application.isPlaying)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button("Debug Zone/Region Info"))
                {
                    Debug.Log($"=== WorldManager Debug Info ===");
                    Debug.Log($"Current Zone: {(currentZone != null ? $"'{currentZone.zoneName}' (ID: {currentZone.id})" : "None")}");
                    Debug.Log($"Current Region: {(currentRegion != null ? $"'{currentRegion.levelName}' (ID: {currentRegion.id})" : "None")}");
                    
                    var zoneCount = worldManager.GetType().GetField("_zoneDictionary", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        ?.GetValue(worldManager) as System.Collections.Generic.Dictionary<string, Zone>;
                    
                    var regionCount = worldManager.GetType().GetField("_regionDictionary", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        ?.GetValue(worldManager) as System.Collections.Generic.Dictionary<string, Region>;
                    
                    if (zoneCount != null && regionCount != null)
                    {
                        Debug.Log($"Total zones in dictionary: {zoneCount.Count}");
                        Debug.Log($"Total regions in dictionary: {regionCount.Count}");
                        
                        Debug.Log("Available zones:");
                        foreach (var zone in zoneCount.Values)
                        {
                            Debug.Log($"  - '{zone.zoneName}' (ID: {zone.id})");
                        }
                        
                        Debug.Log("Available regions:");
                        foreach (var region in regionCount.Values)
                        {
                            Debug.Log($"  - '{region.levelName}' (ID: {region.id})");
                        }
                    }
                }
            }
        }
    }
}
#endif