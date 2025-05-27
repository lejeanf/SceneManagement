#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace jeanf.scenemanagement
{
    [CustomEditor(typeof(RegionConnectivityAuthoring))]
    public class RegionConnectivityAuthoringEditor : Editor
    {
        private bool _showVisualization = true;
        private Dictionary<Region, Color> _regionColors = new Dictionary<Region, Color>();
        private static readonly Color[] _predefinedColors = {
            Color.red, Color.blue, Color.green, Color.yellow, Color.cyan, 
            Color.magenta, Color.white, new Color(1f, 0.5f, 0f)
        };

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            var authoring = (RegionConnectivityAuthoring)target;
            if (authoring.regionConnectivity == null)
            {
                EditorGUILayout.HelpBox("Please assign a RegionConnectivity asset to see visualization.", MessageType.Warning);
                return;
            }
            
            EditorGUILayout.Space();
            _showVisualization = EditorGUILayout.Toggle("Show Connectivity Visualization", _showVisualization);
            
            if (GUILayout.Button("Refresh Visualization"))
            {
                SceneView.RepaintAll();
            }
            
            EditorGUILayout.Space();
            DrawConnectivityInfo(authoring.regionConnectivity);
        }
        
        private void OnSceneGUI()
        {
            if (!_showVisualization) return;
            
            var authoring = (RegionConnectivityAuthoring)target;
            if (authoring.regionConnectivity == null) return;
            
            DrawVolumeConnections(authoring.regionConnectivity);
        }
        
        private void DrawConnectivityInfo(RegionConnectivity connectivity)
        {
            EditorGUILayout.LabelField("Connectivity Overview", EditorStyles.boldLabel);
            
            foreach (var region in connectivity.activeRegions)
            {
                if (region == null) continue;
                
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.LabelField($"Region: {region.levelName}", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Zones: {region.zonesInThisRegion.Count}");
                
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone != null)
                    {
                        EditorGUILayout.LabelField($"  - {zone.zoneName} ({zone.id})");
                    }
                }
                EditorGUILayout.EndVertical();
            }
            
            if (connectivity.landingZones.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Landing Zones", EditorStyles.boldLabel);
                foreach (var landing in connectivity.landingZones)
                {
                    if (landing.landingZone != null && landing.region != null)
                    {
                        EditorGUILayout.LabelField($"{landing.region.levelName} -> {landing.landingZone.zoneName}");
                    }
                }
            }
        }
        
        private void DrawVolumeConnections(RegionConnectivity connectivity)
        {
            var volumeAuthorings = FindObjectsOfType<VolumeAuthoring>();
            var regionVolumeMap = new Dictionary<Region, List<VolumeAuthoring>>();
            
            // Group volumes by region and assign colors
            foreach (var region in connectivity.activeRegions)
            {
                if (region == null) continue;
                
                if (!_regionColors.ContainsKey(region))
                {
                    var colorIndex = _regionColors.Count % _predefinedColors.Length;
                    _regionColors[region] = _predefinedColors[colorIndex];
                }
                
                regionVolumeMap[region] = new List<VolumeAuthoring>();
                
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone == null) continue;
                    
                    foreach (var volumeAuth in volumeAuthorings)
                    {
                        if (volumeAuth.zone != null && volumeAuth.zone.id.Equals(zone.id))
                        {
                            regionVolumeMap[region].Add(volumeAuth);
                            break; // Only add one volume per zone
                        }
                    }
                }
            }
            
            // Priority 3: Draw regular volumes and their connections first
            foreach (var kvp in regionVolumeMap)
            {
                var region = kvp.Key;
                var volumes = kvp.Value;
                var color = _regionColors[region];
                
                // Draw volumes (actual size, no labels)
                for (int i = 0; i < volumes.Count; i++)
                {
                    var currentVolume = volumes[i];
                    if (currentVolume == null) continue;
                    
                    var currentPos = currentVolume.transform.position;
                    var currentScale = currentVolume.transform.localScale;
                    
                    Handles.color = color;
                    Handles.DrawWireCube(currentPos, currentScale);
                }
                
                // Draw connections between volumes in the same region (red dotted lines)
                for (int i = 0; i < volumes.Count; i++)
                {
                    var currentVolume = volumes[i];
                    if (currentVolume == null) continue;
                    
                    var currentPos = currentVolume.transform.position;
                    
                    for (int j = i + 1; j < volumes.Count; j++)
                    {
                        var otherVolume = volumes[j];
                        if (otherVolume == null) continue;
                        
                        var otherPos = otherVolume.transform.position;
                        Handles.color = Color.red;
                        Handles.DrawDottedLine(currentPos, otherPos, 5f);
                    }
                }
            }
            
            // Draw landing zone connections (yellow solid lines)
            foreach (var landing in connectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;
                
                var landingVolume = System.Array.Find(volumeAuthorings, 
                    v => v.zone != null && v.zone.id.Equals(landing.landingZone.id));
                
                if (landingVolume == null) continue;
                
                var landingPos = landingVolume.transform.position;
                
                // Connect landing zone to volumes in OTHER regions only
                foreach (var kvp in regionVolumeMap)
                {
                    var region = kvp.Key;
                    var volumes = kvp.Value;
                    
                    if (region.id.Equals(landing.region.id)) continue; // Skip same region
                    
                    foreach (var regionVolume in volumes)
                    {
                        if (regionVolume != null)
                        {
                            Handles.color = Color.yellow;
                            Handles.DrawLine(landingPos, regionVolume.transform.position);
                        }
                    }
                }
            }
            
            // Priority 2: Draw landing zones on top (blue boxes, actual size)
            foreach (var landing in connectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;
                
                var landingVolume = System.Array.Find(volumeAuthorings, 
                    v => v.zone != null && v.zone.id.Equals(landing.landingZone.id));
                
                if (landingVolume == null) continue;
                
                var landingPos = landingVolume.transform.position;
                var landingScale = landingVolume.transform.localScale;
                
                // Draw landing zone in blue, actual size
                Handles.color = Color.blue;
                Handles.DrawWireCube(landingPos, landingScale);
            }
        }
        
        private void DrawLandingZoneConnections(RegionConnectivity connectivity, VolumeAuthoring[] volumeAuthorings, Dictionary<Region, List<VolumeAuthoring>> regionVolumeMap)
        {
            foreach (var landing in connectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;
                
                var landingVolume = System.Array.Find(volumeAuthorings, 
                    v => v.zone != null && v.zone.id.Equals(landing.landingZone.id));
                
                if (landingVolume == null) continue;
                
                var landingPos = landingVolume.transform.position;
                var landingScale = landingVolume.transform.localScale;
                
                Handles.color = Color.white;
                Handles.DrawWireCube(landingPos, landingScale * 1.2f);
                Handles.Label(landingPos + Vector3.up * (landingScale.y * 0.8f), 
                    $"LANDING\n{landing.landingZone.zoneName}", 
                    new GUIStyle(GUI.skin.label) { 
                        normal = { textColor = Color.white },
                        fontStyle = FontStyle.Bold
                    });
                
                foreach (var kvp in regionVolumeMap)
                {
                    var region = kvp.Key;
                    var volumes = kvp.Value;
                    
                    if (region.id.Equals(landing.region.id)) continue;
                    
                    foreach (var regionVolume in volumes)
                    {
                        if (regionVolume != null)
                        {
                            Handles.color = Color.yellow;
                            Handles.DrawLine(landingPos, regionVolume.transform.position);
                        }
                    }
                }
            }
        }
    }

    [CustomEditor(typeof(RegionConnectivity))]
    public class RegionConnectivityEditor : Editor
    {
        private bool _showVisualization = true;
        private Dictionary<Region, Color> _regionColors = new Dictionary<Region, Color>();
        private static readonly Color[] _predefinedColors = {
            Color.red, Color.blue, Color.green, Color.yellow, Color.cyan, 
            Color.magenta, Color.white, new Color(1f, 0.5f, 0f)
        };

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            EditorGUILayout.Space();
            _showVisualization = EditorGUILayout.Toggle("Show Connectivity Visualization", _showVisualization);
            
            if (GUILayout.Button("Refresh Visualization"))
            {
                SceneView.RepaintAll();
            }
            
            EditorGUILayout.Space();
            DrawConnectivityInfo();
        }
        
        private void DrawConnectivityInfo()
        {
            var connectivity = (RegionConnectivity)target;
            
            EditorGUILayout.LabelField("Connectivity Overview", EditorStyles.boldLabel);
            
            foreach (var region in connectivity.activeRegions)
            {
                if (region == null) continue;
                
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.LabelField($"Region: {region.levelName}", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Zones: {region.zonesInThisRegion.Count}");
                
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone != null)
                    {
                        EditorGUILayout.LabelField($"  - {zone.zoneName} ({zone.id})");
                    }
                }
                EditorGUILayout.EndVertical();
            }
            
            if (connectivity.landingZones.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Landing Zones", EditorStyles.boldLabel);
                foreach (var landing in connectivity.landingZones)
                {
                    if (landing.landingZone != null && landing.region != null)
                    {
                        EditorGUILayout.LabelField($"{landing.region.levelName} -> {landing.landingZone.zoneName}");
                    }
                }
            }
        }
        
        private void OnSceneGUI()
        {
            if (!_showVisualization) return;
            
            var connectivity = (RegionConnectivity)target;
            DrawVolumeConnections(connectivity);
        }
        
        private void DrawVolumeConnections(RegionConnectivity connectivity)
        {
            var volumeAuthorings = FindObjectsOfType<VolumeAuthoring>();
            var regionVolumeMap = new Dictionary<Region, List<VolumeAuthoring>>();
            
            foreach (var region in connectivity.activeRegions)
            {
                if (region == null) continue;
                
                if (!_regionColors.ContainsKey(region))
                {
                    var colorIndex = _regionColors.Count % _predefinedColors.Length;
                    _regionColors[region] = _predefinedColors[colorIndex];
                }
                
                regionVolumeMap[region] = new List<VolumeAuthoring>();
                
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone == null) continue;
                    
                    foreach (var volumeAuth in volumeAuthorings)
                    {
                        if (volumeAuth.zone != null && volumeAuth.zone.id.Equals(zone.id))
                        {
                            regionVolumeMap[region].Add(volumeAuth);
                        }
                    }
                }
            }
            
            foreach (var kvp in regionVolumeMap)
            {
                var region = kvp.Key;
                var volumes = kvp.Value;
                var color = _regionColors[region];
                
                Handles.color = color;
                
                for (int i = 0; i < volumes.Count; i++)
                {
                    var currentVolume = volumes[i];
                    if (currentVolume == null) continue;
                    
                    var currentPos = currentVolume.transform.position;
                    var currentScale = currentVolume.transform.localScale;
                    
                    Handles.DrawWireCube(currentPos, currentScale);
                    Handles.Label(currentPos + Vector3.up * (currentScale.y * 0.6f), 
                        $"{region.levelName}\n{currentVolume.zone.zoneName}", 
                        new GUIStyle(GUI.skin.label) { normal = { textColor = color } });
                    
                    for (int j = i + 1; j < volumes.Count; j++)
                    {
                        var otherVolume = volumes[j];
                        if (otherVolume == null) continue;
                        
                        var otherPos = otherVolume.transform.position;
                        Handles.DrawDottedLine(currentPos, otherPos, 5f);
                    }
                }
            }
            
            DrawLandingZoneConnections(connectivity, volumeAuthorings, regionVolumeMap);
        }
        
        private void DrawLandingZoneConnections(RegionConnectivity connectivity, VolumeAuthoring[] volumeAuthorings, Dictionary<Region, List<VolumeAuthoring>> regionVolumeMap)
        {
            foreach (var landing in connectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;
                
                var landingVolume = System.Array.Find(volumeAuthorings, 
                    v => v.zone != null && v.zone.id.Equals(landing.landingZone.id));
                
                if (landingVolume == null) continue;
                
                var landingPos = landingVolume.transform.position;
                var landingScale = landingVolume.transform.localScale;
                
                Handles.color = Color.white;
                Handles.DrawWireCube(landingPos, landingScale * 1.2f);
                Handles.Label(landingPos + Vector3.up * (landingScale.y * 0.8f), 
                    $"LANDING\n{landing.landingZone.zoneName}", 
                    new GUIStyle(GUI.skin.label) { 
                        normal = { textColor = Color.white },
                        fontStyle = FontStyle.Bold
                    });
                
                foreach (var kvp in regionVolumeMap)
                {
                    var region = kvp.Key;
                    var volumes = kvp.Value;
                    
                    if (region.id.Equals(landing.region.id)) continue;
                    
                    foreach (var regionVolume in volumes)
                    {
                        if (regionVolume != null)
                        {
                            Handles.color = Color.yellow;
                            Handles.DrawLine(landingPos, regionVolume.transform.position);
                        }
                    }
                }
            }
        }
    }
    
    [CustomEditor(typeof(VolumeAuthoring))]
    public class VolumeAuthoringEditor : Editor
    {
        private bool _showConnectivity = true;
        private RegionConnectivity _foundConnectivity;
        private Dictionary<Region, Color> _regionColors = new Dictionary<Region, Color>();
        private static readonly Color[] _predefinedColors = {
            Color.red, Color.blue, Color.green, Color.yellow, Color.cyan, 
            Color.magenta, Color.white, new Color(1f, 0.5f, 0f)
        };

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            var volumeAuth = (VolumeAuthoring)target;
            if (volumeAuth.zone == null)
            {
                EditorGUILayout.HelpBox("Assign a Zone to see connectivity.", MessageType.Info);
                return;
            }
            
            FindRegionConnectivity();
            
            EditorGUILayout.Space();
            _showConnectivity = EditorGUILayout.Toggle("Show Volume Connectivity", _showConnectivity);
            
            if (GUILayout.Button("Refresh Connectivity"))
            {
                _foundConnectivity = null;
                FindRegionConnectivity();
                SceneView.RepaintAll();
            }
            
            if (_foundConnectivity != null)
            {
                DrawVolumeConnectivityInfo(volumeAuth);
            }
            else
            {
                EditorGUILayout.HelpBox("No RegionConnectivity found in scene. Add a GameObject with RegionConnectivityAuthoring.", MessageType.Warning);
            }
        }
        
        private void FindRegionConnectivity()
        {
            if (_foundConnectivity != null) return;
            
            var connectivityAuthoring = FindObjectOfType<RegionConnectivityAuthoring>();
            if (connectivityAuthoring != null && connectivityAuthoring.regionConnectivity != null)
            {
                _foundConnectivity = connectivityAuthoring.regionConnectivity;
            }
        }
        
        private void DrawVolumeConnectivityInfo(VolumeAuthoring volumeAuth)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Volume Connectivity", EditorStyles.boldLabel);
            
            var currentZone = volumeAuth.zone;
            Region currentRegion = null;
            
            // Debug: Show what regions we have in the connectivity data
            EditorGUILayout.LabelField("DEBUG - Active Regions in Connectivity:", EditorStyles.boldLabel);
            foreach (var region in _foundConnectivity.activeRegions)
            {
                if (region == null)
                {
                    EditorGUILayout.LabelField("  - NULL REGION", EditorStyles.miniLabel);
                    continue;
                }
                
                EditorGUILayout.LabelField($"  - {region.levelName} ({region.zonesInThisRegion.Count} zones)", EditorStyles.miniLabel);
                
                // Check if current zone is in this region
                if (region.zonesInThisRegion.Contains(currentZone))
                {
                    currentRegion = region;
                    EditorGUILayout.LabelField($"    → CURRENT REGION (contains {currentZone.zoneName})", EditorStyles.miniLabel);
                }
                
                // Show zones in this region
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone != null)
                    {
                        string marker = zone == currentZone ? " ← SELECTED" : "";
                        EditorGUILayout.LabelField($"      • {zone.zoneName}{marker}", EditorStyles.miniLabel);
                    }
                }
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("DEBUG - Landing Zones:", EditorStyles.boldLabel);
            foreach (var landing in _foundConnectivity.landingZones)
            {
                if (landing.region != null && landing.landingZone != null)
                {
                    string marker = landing.landingZone == currentZone ? " ← SELECTED IS LANDING ZONE" : "";
                    EditorGUILayout.LabelField($"  - {landing.region.levelName} → {landing.landingZone.zoneName}{marker}", EditorStyles.miniLabel);
                }
            }
            
            EditorGUILayout.Space();
            
            if (currentRegion != null)
            {
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.LabelField($"Region: {currentRegion.levelName}", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Zone: {currentZone.zoneName}");
                
                var connectedZones = currentRegion.zonesInThisRegion.Where(z => z != currentZone && z != null).ToList();
                if (connectedZones.Count > 0)
                {
                    EditorGUILayout.LabelField($"Connected to {connectedZones.Count} zones in region:");
                    foreach (var zone in connectedZones)
                    {
                        EditorGUILayout.LabelField($"  - {zone.zoneName}");
                    }
                }
                
                var landingConnections = _foundConnectivity.landingZones.Where(l => l.landingZone == currentZone).ToList();
                if (landingConnections.Count > 0)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Landing Zone Connections:", EditorStyles.boldLabel);
                    foreach (var landing in landingConnections)
                    {
                        if (landing.region != null)
                        {
                            EditorGUILayout.LabelField($"  Landing for: {landing.region.levelName}");
                            
                            var targetRegions = _foundConnectivity.activeRegions.Where(r => r != landing.region && r != null).ToList();
                            foreach (var targetRegion in targetRegions)
                            {
                                EditorGUILayout.LabelField($"    → {targetRegion.levelName} ({targetRegion.zonesInThisRegion.Count} zones)");
                            }
                        }
                    }
                }
                
                EditorGUILayout.EndVertical();
            }
            else
            {
                EditorGUILayout.HelpBox($"This volume's zone ({currentZone.zoneName}) is not found in any active region in the RegionConnectivity asset!", MessageType.Error);
                EditorGUILayout.LabelField("Check that:", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("1. The Region ScriptableObject contains this zone", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("2. The Region is added to the RegionConnectivity 'activeRegions' list", EditorStyles.miniLabel);
            }
        }
        
        private void OnSceneGUI()
        {
            if (!_showConnectivity) return;
            
            FindRegionConnectivity();
            if (_foundConnectivity == null) return;
            
            var volumeAuth = (VolumeAuthoring)target;
            if (volumeAuth.zone == null) return;
            
            DrawVolumeConnectivity(volumeAuth);
        }
        
        private void DrawVolumeConnectivity(VolumeAuthoring selectedVolume)
        {
            var allVolumeAuthorings = FindObjectsOfType<VolumeAuthoring>();
            var currentZone = selectedVolume.zone;
            Region currentRegion = null;
            
            // Find which region this volume belongs to
            foreach (var region in _foundConnectivity.activeRegions)
            {
                if (region != null && region.zonesInThisRegion.Contains(currentZone))
                {
                    currentRegion = region;
                    break;
                }
            }
            
            if (currentRegion == null) return;
            
            if (!_regionColors.ContainsKey(currentRegion))
            {
                var colorIndex = _regionColors.Count % _predefinedColors.Length;
                _regionColors[currentRegion] = _predefinedColors[colorIndex];
            }
            
            var regionColor = _regionColors[currentRegion];
            var selectedPos = selectedVolume.transform.position;
            var selectedScale = selectedVolume.transform.localScale;
            
            // Priority 3: Draw other connections first (same region) - red dotted lines
            foreach (var zone in currentRegion.zonesInThisRegion)
            {
                if (zone == null || zone == currentZone) continue;
                
                foreach (var volumeAuth in allVolumeAuthorings)
                {
                    if (volumeAuth == null || volumeAuth.zone == null) continue;
                    
                    if (volumeAuth.zone.id.Equals(zone.id))
                    {
                        var otherPos = volumeAuth.transform.position;
                        var otherScale = volumeAuth.transform.localScale;
                        
                        // Draw the volume
                        Handles.color = regionColor;
                        Handles.DrawWireCube(otherPos, otherScale);
                        
                        // Draw potential connection (red dotted line)
                        Handles.color = Color.red;
                        Handles.DrawDottedLine(selectedPos, otherPos, 5f);
                        break; // Only draw one volume per zone
                    }
                }
            }
            
            // Show actual landing zone connectivity - yellow solid lines
            var landingConnection = _foundConnectivity.landingZones.FirstOrDefault(l => l.landingZone == currentZone);
            if (landingConnection != null)
            {
                // This volume IS a landing zone - show connections to OTHER regions
                foreach (var region in _foundConnectivity.activeRegions)
                {
                    if (region == null || region.id.Equals(currentRegion.id)) continue; // Skip same region
                    
                    if (!_regionColors.ContainsKey(region))
                    {
                        var colorIndex = _regionColors.Count % _predefinedColors.Length;
                        _regionColors[region] = _predefinedColors[colorIndex];
                    }
                    
                    foreach (var zone in region.zonesInThisRegion)
                    {
                        if (zone == null) continue;
                        
                        foreach (var volumeAuth in allVolumeAuthorings)
                        {
                            if (volumeAuth == null || volumeAuth.zone == null) continue;
                            
                            if (volumeAuth.zone.id.Equals(zone.id))
                            {
                                var otherPos = volumeAuth.transform.position;
                                var otherScale = volumeAuth.transform.localScale;
                                
                                // Draw the target volume
                                Handles.color = _regionColors[region];
                                Handles.DrawWireCube(otherPos, otherScale);
                                
                                // Draw ACTUAL landing zone connection (yellow solid line)
                                Handles.color = Color.yellow;
                                Handles.DrawLine(selectedPos, otherPos);
                                break; // Only draw one volume per zone
                            }
                        }
                    }
                }
            }
            
            // Show connections FROM other landing zones TO this volume
            foreach (var landing in _foundConnectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;
                if (landing.landingZone == currentZone) continue; // Skip self
                
                // Find the landing zone volume
                foreach (var volumeAuth in allVolumeAuthorings)
                {
                    if (volumeAuth == null || volumeAuth.zone == null) continue;
                    
                    if (volumeAuth.zone.id.Equals(landing.landingZone.id))
                    {
                        var landingPos = volumeAuth.transform.position;
                        var landingScale = volumeAuth.transform.localScale;
                        
                        // Draw ACTUAL connection from landing zone to current volume (yellow solid line)
                        Handles.color = Color.yellow;
                        Handles.DrawLine(landingPos, selectedPos);
                        break;
                    }
                }
            }
            
            // Priority 2: Draw landing zones - blue boxes, same size as volume
            foreach (var landing in _foundConnectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;
                if (landing.landingZone == currentZone) continue; // Skip self (will be drawn as selected)
                
                // Find the landing zone volume
                foreach (var volumeAuth in allVolumeAuthorings)
                {
                    if (volumeAuth == null || volumeAuth.zone == null) continue;
                    
                    if (volumeAuth.zone.id.Equals(landing.landingZone.id))
                    {
                        var landingPos = volumeAuth.transform.position;
                        var landingScale = volumeAuth.transform.localScale;
                        
                        // Draw landing zone in blue, same size as volume
                        Handles.color = Color.blue;
                        Handles.DrawWireCube(landingPos, landingScale);
                        break;
                    }
                }
            }
            
            // Priority 1: Draw selected volume last - yellow box, actual size
            Handles.color = Color.yellow;
            Handles.DrawWireCube(selectedPos, selectedScale);
            Handles.Label(selectedPos + Vector3.up * (selectedScale.y * 0.7f), 
                $"SELECTED\n{currentZone.zoneName}", 
                new GUIStyle(GUI.skin.label) { 
                    normal = { textColor = Color.yellow },
                    fontStyle = FontStyle.Bold
                });
        }
    }
    
    [CustomPropertyDrawer(typeof(LandingZoneData))]
    public class LandingZoneDataDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            
            var regionProp = property.FindPropertyRelative("region");
            var landingZoneProp = property.FindPropertyRelative("landingZone");
            
            var regionName = regionProp.objectReferenceValue != null ? 
                ((Region)regionProp.objectReferenceValue).levelName : "No Region";
            
            position.height = EditorGUIUtility.singleLineHeight;
            property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, 
                $"Landing Zone: {regionName}");
            
            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                position.y += EditorGUIUtility.singleLineHeight + 2;
                EditorGUI.PropertyField(position, regionProp, new GUIContent("Region"));
                
                position.y += EditorGUIUtility.singleLineHeight + 2;
                EditorGUI.PropertyField(position, landingZoneProp, new GUIContent("Landing Zone"));
                EditorGUI.indentLevel--;
            }
            
            EditorGUI.EndProperty();
        }
        
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!property.isExpanded)
                return EditorGUIUtility.singleLineHeight;
            
            return EditorGUIUtility.singleLineHeight * 3 + 4;
        }
    }
}
#endif