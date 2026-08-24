#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        private Dictionary<string, string> _zoneDisplayNames = new Dictionary<string, string>();

        [MenuItem("Tools/Jeanf/SceneManagement/Volume Data Generator")]
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
            EditorGUILayout.LabelField($"• Adjacency Links: {CountAdjacencyLinks(sourceRegionConnectivity)}");
            
            int totalZones = 0;
            foreach (var region in sourceRegionConnectivity.activeRegions)
            {
                if (region?.zonesInThisRegion != null)
                    totalZones += region.zonesInThisRegion.Count;
            }
            EditorGUILayout.LabelField($"• Total Zones: {totalZones}");
            EditorGUILayout.EndVertical();

            var sharedZones = FindZonesInMultipleRegions(sourceRegionConnectivity);
            if (sharedZones.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "These zones belong to more than one region, so region detection is ambiguous (last region wins):\n  • " +
                    string.Join("\n  • ", sharedZones),
                    MessageType.Warning);
            }

            DrawRegionConnectivityAnalysis(sourceRegionConnectivity);
            DrawDependencyAddressableCheck(sourceRegionConnectivity);
        }

        private void DrawDependencyAddressableCheck(RegionConnectivity connectivity)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;

            var notAddressable = new List<(Region region, SceneReference sref)>();
            var unassigned = new List<string>();

            foreach (var region in connectivity.activeRegions)
            {
                if (region?.dependenciesInThisRegion == null) continue;

                for (int i = 0; i < region.dependenciesInThisRegion.Count; i++)
                {
                    var sref = region.dependenciesInThisRegion[i];
                    if (sref == null) continue;

                    var asset = sref.EditorSceneAsset;
                    if (asset == null)
                    {
                        unassigned.Add($"{region.levelName} dependency #{i}");
                        continue;
                    }

                    if (settings == null) continue;
                    var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
                    if (settings.FindAssetEntry(guid) == null)
                        notAddressable.Add((region, sref));
                }
            }

            if (unassigned.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "Dependencies with no scene assigned (will never load):\n  • " + string.Join("\n  • ", unassigned),
                    MessageType.Error);
            }

            if (notAddressable.Count > 0)
            {
                var lines = notAddressable.Select(d => $"{d.region.levelName}: {d.sref.EditorSceneAsset.name}");
                EditorGUILayout.HelpBox(
                    "Dependency scenes not marked Addressable (they are silently skipped at load):\n  • " + string.Join("\n  • ", lines),
                    MessageType.Warning);

                using (new EditorGUI.DisabledScope(settings == null))
                {
                    if (GUILayout.Button($"Make {notAddressable.Count} scene(s) Addressable"))
                        MakeDependenciesAddressable(notAddressable, settings);
                }

                if (settings == null)
                    EditorGUILayout.HelpBox("No Addressable settings found. Open Window > Asset Management > Addressables > Groups first.", MessageType.Info);
            }
        }

        private void MakeDependenciesAddressable(List<(Region region, SceneReference sref)> items, AddressableAssetSettings settings)
        {
            var group = settings.DefaultGroup;
            if (group == null)
            {
                Debug.LogError("No default Addressable group set.");
                return;
            }

            foreach (var item in items)
            {
                var path = AssetDatabase.GetAssetPath(item.sref.EditorSceneAsset);
                var guid = AssetDatabase.AssetPathToGUID(path);
                var entry = settings.CreateOrMoveEntry(guid, group);
                if (entry == null) continue;

                entry.SetAddress(Path.GetFileNameWithoutExtension(path));
                item.sref.UpdateAddress();
                EditorUtility.SetDirty(item.region);
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log($"[VolumeDataGenerator] Marked {items.Count} dependency scene(s) Addressable in group '{group.name}'.");
        }

        private void DrawRegionConnectivityAnalysis(RegionConnectivity connectivity)
        {
            var zoneToRegion = BuildZoneToRegion(connectivity);

            var landingTargets = new List<string>();
            var landingRegions = new HashSet<Region>();
            foreach (var landing in connectivity.landingZones)
            {
                if (landing?.landingZone == null) continue;
                zoneToRegion.TryGetValue(landing.landingZone.id.ToString(), out var target);
                var targetName = target != null ? target.levelName
                    : (landing.region != null ? landing.region.levelName : "<unmapped>");
                if (target != null) landingRegions.Add(target);
                landingTargets.Add($"any → {targetName}  (landing '{landing.landingZone.zoneName}')");
            }

            var adjacencyAssets = CollectAdjacencyAssets(connectivity);
            var linkLines = new List<string>();
            var warnings = new List<string>();

            foreach (var adjacency in adjacencyAssets)
            {
                foreach (var link in adjacency.links)
                {
                    if (link?.regionA == null || link.regionB == null) continue;

                    var touchCount = 0;
                    if (link.touchingZones != null)
                    {
                        foreach (var pair in link.touchingZones)
                            if (pair?.zoneOnA != null && pair.zoneOnB != null) touchCount++;
                    }

                    var typeLabel = link.connectionType == RegionConnectionType.Elevator ? "elevator, transition" : "doorway, co-loaded";
                    linkLines.Add($"{link.regionA.levelName} ↔ {link.regionB.levelName}  [{typeLabel}, {touchCount} touching zones]");

                    if (link.connectionType == RegionConnectionType.Doorway
                        && touchCount == 0
                        && !landingRegions.Contains(link.regionA)
                        && !landingRegions.Contains(link.regionB))
                    {
                        warnings.Add($"{link.regionA.levelName} ↔ {link.regionB.levelName}: doorway link with no touching zones — both load, but the seamless crossing won't be detected. Add touching-zone pairs.");
                    }
                }
            }

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("Region Connections (detection paths):", EditorStyles.boldLabel);
            if (landingTargets.Count == 0 && linkLines.Count == 0)
            {
                EditorGUILayout.LabelField("• none — regions are only detectable at startup");
            }
            foreach (var s in landingTargets) EditorGUILayout.LabelField($"• {s}");
            foreach (var s in linkLines) EditorGUILayout.LabelField($"• {s}");
            EditorGUILayout.EndVertical();

            if (warnings.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "Adjacency links without a detection path:\n  • " + string.Join("\n  • ", warnings),
                    MessageType.Warning);
            }
        }

        private static List<RegionAdjacency> CollectAdjacencyAssets(RegionConnectivity connectivity)
        {
            var result = new List<RegionAdjacency>();
            foreach (var region in connectivity.activeRegions)
            {
                if (region?.regionAdjacency == null) continue;
                if (!result.Contains(region.regionAdjacency)) result.Add(region.regionAdjacency);
            }
            return result;
        }

        private static int CountAdjacencyLinks(RegionConnectivity connectivity)
        {
            var count = 0;
            foreach (var adjacency in CollectAdjacencyAssets(connectivity))
                count += adjacency.links.Count;
            return count;
        }

        private static Dictionary<string, Region> BuildZoneToRegion(RegionConnectivity connectivity)
        {
            var map = new Dictionary<string, Region>();
            foreach (var region in connectivity.activeRegions)
            {
                if (region?.zonesInThisRegion == null) continue;
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone == null) continue;
                    map[zone.id.ToString()] = region;
                }
            }
            return map;
        }

        private static List<string> FindZonesInMultipleRegions(RegionConnectivity connectivity)
        {
            var owners = new Dictionary<string, HashSet<string>>();
            foreach (var region in connectivity.activeRegions)
            {
                if (region?.zonesInThisRegion == null) continue;
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone == null) continue;
                    var key = $"{zone.zoneName} ({zone.id})";
                    if (!owners.TryGetValue(key, out var set))
                    {
                        set = new HashSet<string>();
                        owners[key] = set;
                    }
                    set.Add(region.levelName);
                }
            }

            var result = new List<string>();
            foreach (var kvp in owners)
            {
                if (kvp.Value.Count > 1)
                    result.Add($"{kvp.Key} → {string.Join(", ", kvp.Value)}");
            }
            return result;
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
                var zoneLabel = _zoneDisplayNames.TryGetValue(kvp.Key, out var name) ? name : kvp.Key;
                EditorGUILayout.LabelField($"Zone: {zoneLabel}", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField($"Checkable zones ({kvp.Value.Count}):");
                foreach (var checkable in kvp.Value)
                {
                    var checkableLabel = _zoneDisplayNames.TryGetValue(checkable, out var cName) ? cName : checkable;
                    EditorGUILayout.LabelField($"\u2022 {checkableLabel}");
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
            _zoneDisplayNames.Clear();
            var generator = new VolumeDataProcessor(sourceRegionConnectivity);

            foreach (var region in sourceRegionConnectivity.activeRegions)
            {
                if (region?.zonesInThisRegion == null) continue;

                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone == null) continue;

                    var zoneKey = zone.id.ToString();
                    _zoneDisplayNames[zoneKey] = $"{zone.zoneName}  ({region.levelName})";

                    var checkableZones = generator.GetCheckableZonesForZone(zoneKey);
                    previewData[zoneKey] = new List<string>(checkableZones);
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
            var dir = savePath.TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(dir))
            {
                var parent = System.IO.Path.GetDirectoryName(dir);
                var folderName = System.IO.Path.GetFileName(dir);
                AssetDatabase.CreateFolder(parent, folderName);
            }
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

            // Add cross-region neighbors from RegionAdjacency touching zones
            foreach (var adjacency in CollectAdjacencyAssets())
            {
                foreach (var link in adjacency.links)
                {
                    if (link == null || link.connectionType != RegionConnectionType.Doorway) continue;
                    if (link.touchingZones == null) continue;

                    foreach (var pair in link.touchingZones)
                    {
                        if (pair?.zoneOnA == null || pair.zoneOnB == null) continue;

                        var zoneAId = pair.zoneOnA.id.ToString();
                        var zoneBId = pair.zoneOnB.id.ToString();

                        if (!zoneNeighborsMap.ContainsKey(zoneAId))
                            zoneNeighborsMap[zoneAId] = new HashSet<string>();
                        if (!zoneNeighborsMap.ContainsKey(zoneBId))
                            zoneNeighborsMap[zoneBId] = new HashSet<string>();

                        zoneNeighborsMap[zoneAId].Add(zoneBId);
                        zoneNeighborsMap[zoneBId].Add(zoneAId);
                    }
                }
            }
        }

        private List<RegionAdjacency> CollectAdjacencyAssets()
        {
            var result = new List<RegionAdjacency>();
            foreach (var region in sourceData.activeRegions)
            {
                if (region?.regionAdjacency == null) continue;
                if (!result.Contains(region.regionAdjacency)) result.Add(region.regionAdjacency);
            }
            return result;
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