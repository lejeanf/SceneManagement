#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace jeanf.scenemanagement
{
    public class VolumeDataGenerator : EditorWindow
    {
        private RegionConnectivity sourceRegionConnectivity;
        private PrecomputedVolumeData targetPrecomputedData;
        private string savePath = "Assets/ScriptableObjects/";
        private string fileName = "PrecomputedVolumeData";
        
        private Vector2 scrollPosition;
        private bool showPreview = false;
        private Dictionary<string, List<string>> previewData = new Dictionary<string, List<string>>();

        [MenuItem("Tools/Scene Management/Volume Data Generator")]
        public static void ShowWindow()
        {
            GetWindow<VolumeDataGenerator>("Volume Data Generator");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Volume Data Generator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Generate pre-computed volume connectivity data from RegionConnectivity for maximum runtime performance.", MessageType.Info);
            EditorGUILayout.Space();
            
            DrawSourceSelection();
            EditorGUILayout.Space();
            
            DrawTargetSelection();
            EditorGUILayout.Space();
            
            DrawGenerationControls();
            EditorGUILayout.Space();
            
            if (showPreview && previewData.Count > 0)
            {
                DrawPreview();
            }
        }
        
        private void DrawSourceSelection()
        {
            EditorGUILayout.LabelField("Source Data", EditorStyles.boldLabel);
            
            sourceRegionConnectivity = (RegionConnectivity)EditorGUILayout.ObjectField(
                "Region Connectivity", 
                sourceRegionConnectivity, 
                typeof(RegionConnectivity), 
                false);
                
            if (sourceRegionConnectivity == null)
            {
                EditorGUILayout.HelpBox("Select a RegionConnectivity asset to generate from.", MessageType.Warning);
                return;
            }
            
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("Source Analysis:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"• Active Regions: {sourceRegionConnectivity.activeRegions.Count}");
            EditorGUILayout.LabelField($"• Landing Zones: {sourceRegionConnectivity.landingZones.Count}");
            EditorGUILayout.LabelField($"• Zone Connections: {sourceRegionConnectivity.zoneConnections.Count}");
            
            int totalZones = 0;
            foreach (var region in sourceRegionConnectivity.activeRegions)
            {
                if (region?.zonesInThisRegion != null)
                    totalZones += region.zonesInThisRegion.Count;
            }
            EditorGUILayout.LabelField($"• Total Zones: {totalZones}");
            EditorGUILayout.EndVertical();
        }
        
        private void DrawTargetSelection()
        {
            EditorGUILayout.LabelField("Target Asset", EditorStyles.boldLabel);
            
            targetPrecomputedData = (PrecomputedVolumeData)EditorGUILayout.ObjectField(
                "Precomputed Volume Data", 
                targetPrecomputedData, 
                typeof(PrecomputedVolumeData), 
                false);
                
            EditorGUILayout.BeginHorizontal();
            savePath = EditorGUILayout.TextField("Save Path", savePath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string selectedPath = EditorUtility.SaveFolderPanel("Select Save Folder", savePath, "");
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    savePath = FileUtil.GetProjectRelativePath(selectedPath) + "/";
                }
            }
            EditorGUILayout.EndHorizontal();
            
            fileName = EditorGUILayout.TextField("File Name", fileName);
        }
        
        private void DrawGenerationControls()
        {
            EditorGUILayout.LabelField("Generation Options", EditorStyles.boldLabel);
            
            showPreview = EditorGUILayout.Toggle("Show Preview", showPreview);
            
            EditorGUILayout.BeginHorizontal();
            
            GUI.enabled = sourceRegionConnectivity != null;
            if (GUILayout.Button("Preview Data"))
            {
                GeneratePreviewData();
            }
            
            if (GUILayout.Button("Generate New Asset"))
            {
                GenerateNewAsset();
            }
            
            GUI.enabled = sourceRegionConnectivity != null && targetPrecomputedData != null;
            if (GUILayout.Button("Update Existing"))
            {
                UpdateExistingAsset();
            }
            
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawPreview()
        {
            EditorGUILayout.LabelField("Preview Generated Data", EditorStyles.boldLabel);
            
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(300));
            
            foreach (var kvp in previewData)
            {
                EditorGUILayout.LabelField($"Zone: {kvp.Key}", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField($"Checkable zones ({kvp.Value.Count}):");
                foreach (var checkable in kvp.Value)
                {
                    EditorGUILayout.LabelField($"• {checkable}");
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space();
            }
            
            EditorGUILayout.EndScrollView();
        }
        
        private void GeneratePreviewData()
        {
            if (sourceRegionConnectivity == null) return;
            
            previewData.Clear();
            var generator = new VolumeDataProcessor(sourceRegionConnectivity);
            
            foreach (var region in sourceRegionConnectivity.activeRegions)
            {
                if (region?.zonesInThisRegion == null) continue;
                
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone == null) continue;
                    
                    var checkableZones = generator.GetCheckableZonesForZone(zone.id.ToString());
                    previewData[zone.id.ToString()] = new List<string>(checkableZones);
                }
            }
            
            showPreview = true;
            Repaint();
        }
        
        private void GenerateNewAsset()
        {
            if (sourceRegionConnectivity == null) return;
            
            var asset = CreateInstance<PrecomputedVolumeData>();
            var generator = new VolumeDataProcessor(sourceRegionConnectivity);
            generator.PopulatePrecomputedData(asset);
            
            string fullPath = $"{savePath}{fileName}.asset";
            AssetDatabase.CreateAsset(asset, fullPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            targetPrecomputedData = asset;
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
            
            Debug.Log($"Created PrecomputedVolumeData asset at: {fullPath}");
        }
        
        private void UpdateExistingAsset()
        {
            if (sourceRegionConnectivity == null || targetPrecomputedData == null) return;
            
            var generator = new VolumeDataProcessor(sourceRegionConnectivity);
            generator.PopulatePrecomputedData(targetPrecomputedData);
            
            EditorUtility.SetDirty(targetPrecomputedData);
            AssetDatabase.SaveAssets();
            
            Debug.Log($"Updated PrecomputedVolumeData asset: {AssetDatabase.GetAssetPath(targetPrecomputedData)}");
        }
    }
    
    public class VolumeDataProcessor
    {
        private RegionConnectivity sourceData;
        private Dictionary<string, string> zoneToRegionMap = new Dictionary<string, string>();
        private Dictionary<string, HashSet<string>> zoneNeighborsMap = new Dictionary<string, HashSet<string>>();
        private HashSet<string> landingZoneIds = new HashSet<string>();
        
        public VolumeDataProcessor(RegionConnectivity source)
        {
            sourceData = source;
            BuildMappings();
        }
        
        private void BuildMappings()
        {
            BuildZoneToRegionMapping();
            BuildZoneNeighborsMapping();
            BuildLandingZoneMapping();
        }
        
        private void BuildZoneToRegionMapping()
        {
            foreach (var region in sourceData.activeRegions)
            {
                if (region?.zonesInThisRegion == null) continue;
                
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone != null)
                    {
                        zoneToRegionMap[zone.id.ToString()] = region.id.ToString();
                    }
                }
            }
        }
        
        private void BuildZoneNeighborsMapping()
        {
            // Build neighbors from region membership
            foreach (var region in sourceData.activeRegions)
            {
                if (region?.zonesInThisRegion == null) continue;
                
                foreach (var zoneA in region.zonesInThisRegion)
                {
                    if (zoneA == null) continue;
                    
                    var zoneAId = zoneA.id.ToString();
                    if (!zoneNeighborsMap.ContainsKey(zoneAId))
                    {
                        zoneNeighborsMap[zoneAId] = new HashSet<string>();
                    }
                    
                    foreach (var zoneB in region.zonesInThisRegion)
                    {
                        if (zoneB == null || zoneA.id.ToString() == zoneB.id.ToString()) continue;
                        zoneNeighborsMap[zoneAId].Add(zoneB.id.ToString());
                    }
                }
            }
            
            // Add explicit zone connections
            foreach (var connection in sourceData.zoneConnections)
            {
                if (connection.zoneA == null || connection.zoneB == null) continue;
                
                var zoneAId = connection.zoneA.id.ToString();
                var zoneBId = connection.zoneB.id.ToString();
                
                if (!zoneNeighborsMap.ContainsKey(zoneAId))
                    zoneNeighborsMap[zoneAId] = new HashSet<string>();
                if (!zoneNeighborsMap.ContainsKey(zoneBId))
                    zoneNeighborsMap[zoneBId] = new HashSet<string>();
                
                zoneNeighborsMap[zoneAId].Add(zoneBId);
                
                if (connection.isBidirectional)
                {
                    zoneNeighborsMap[zoneBId].Add(zoneAId);
                }
            }
        }
        
        private void BuildLandingZoneMapping()
        {
            foreach (var landing in sourceData.landingZones)
            {
                if (landing?.landingZone != null)
                {
                    landingZoneIds.Add(landing.landingZone.id.ToString());
                }
            }
        }
        
        public HashSet<string> GetCheckableZonesForZone(string zoneId)
        {
            var result = new HashSet<string> { zoneId };
            
            if (zoneNeighborsMap.TryGetValue(zoneId, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    result.Add(neighbor);
                }
            }
            
            foreach (var landingZoneId in landingZoneIds)
            {
                result.Add(landingZoneId);
            }
            
            return result;
        }
        
        public void PopulatePrecomputedData(PrecomputedVolumeData target)
        {
            target.Clear();
            
            // Generate zone checkable sets
            foreach (var kvp in zoneToRegionMap)
            {
                var zoneId = kvp.Key;
                var checkableZones = GetCheckableZonesForZone(zoneId);
                
                var checkableSet = new ZoneCheckableSet(zoneId);
                checkableSet.checkableZoneIds.AddRange(checkableZones);
                
                target.zoneCheckableSets.Add(checkableSet);
            }
            
            // Add landing zones
            target.landingZoneIds.AddRange(landingZoneIds);
            
            // Add zone-region mappings
            foreach (var kvp in zoneToRegionMap)
            {
                target.zoneRegionMappings.Add(new ZoneRegionMapping(kvp.Key, kvp.Value));
            }
            
            // Add generation metadata
            target.sourceRegionConnectivityAsset = AssetDatabase.GetAssetPath(sourceData);
            target.generatedDateTime = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            target.totalZones = zoneToRegionMap.Count;
            target.totalRegions = sourceData.activeRegions.Count;
        }
    }
}
#endif