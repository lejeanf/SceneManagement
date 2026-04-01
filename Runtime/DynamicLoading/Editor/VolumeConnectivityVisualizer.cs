#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace jeanf.scenemanagement
{
    static class VolumeEditorShared
    {
        internal static readonly Color[] PredefinedColors = {
            Color.red, Color.blue, Color.green, Color.yellow, Color.cyan,
            Color.magenta, Color.white, new Color(1f, 0.5f, 0f)
        };

        internal static Color GetOrAddColor(Dictionary<Region, Color> colorMap, Region region)
        {
            if (!colorMap.ContainsKey(region))
                colorMap[region] = PredefinedColors[colorMap.Count % PredefinedColors.Length];
            return colorMap[region];
        }

        internal static Vector3 CalculateZoneCenter(List<VolumeAuthoring> volumes)
        {
            if (volumes.Count == 0) return Vector3.zero;
            if (volumes.Count == 1) return volumes[0].transform.position;
            var sum = Vector3.zero;
            foreach (var v in volumes) sum += v.transform.position;
            return sum / volumes.Count;
        }
    }

    [CustomEditor(typeof(RegionConnectivityAuthoring))]
    public class RegionConnectivityAuthoringEditor : Editor
    {
        private bool _showVisualization = true;
        private Dictionary<Region, Color> _regionColors = new Dictionary<Region, Color>();
        private Dictionary<string, bool> _regionFoldouts = new Dictionary<string, bool>();

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
                SceneView.RepaintAll();

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

                var key = region.id.ToString();
                if (!_regionFoldouts.ContainsKey(key)) _regionFoldouts[key] = false;
                _regionFoldouts[key] = EditorGUILayout.Foldout(_regionFoldouts[key],
                    $"{region.levelName}  ({region.zonesInThisRegion.Count} zones)", true);

                if (!_regionFoldouts[key]) continue;

                EditorGUI.indentLevel++;
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone != null)
                        EditorGUILayout.LabelField($"{zone.zoneName}  ({zone.id})");
                }
                EditorGUI.indentLevel--;
            }

            if (connectivity.landingZones.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Landing Zones", EditorStyles.boldLabel);
                foreach (var landing in connectivity.landingZones)
                {
                    if (landing.landingZone != null && landing.region != null)
                        EditorGUILayout.LabelField($"{landing.region.levelName} \u2192 {landing.landingZone.zoneName}");
                }
            }
        }

        private void DrawVolumeConnections(RegionConnectivity connectivity)
        {
            var volumeAuthorings = Object.FindObjectsByType<VolumeAuthoring>(FindObjectsSortMode.None);
            var regionVolumeMap = new Dictionary<Region, List<VolumeAuthoring>>();

            foreach (var region in connectivity.activeRegions)
            {
                if (region == null) continue;

                VolumeEditorShared.GetOrAddColor(_regionColors, region);
                regionVolumeMap[region] = new List<VolumeAuthoring>();

                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone == null) continue;
                    foreach (var volumeAuth in volumeAuthorings)
                    {
                        if (volumeAuth.zone != null && volumeAuth.zone.id.Equals(zone.id))
                            regionVolumeMap[region].Add(volumeAuth);
                    }
                }
            }

            foreach (var kvp in regionVolumeMap)
            {
                var region = kvp.Key;
                var volumes = kvp.Value;
                var color = _regionColors[region];

                foreach (var volume in volumes)
                {
                    if (volume == null) continue;
                    Handles.color = color;
                    Handles.DrawWireCube(volume.transform.position, volume.transform.localScale);
                }

                var zoneVolumeGroups = new Dictionary<string, List<VolumeAuthoring>>();
                foreach (var volume in volumes)
                {
                    if (volume?.zone == null) continue;
                    var zoneId = volume.zone.id.ToString();
                    if (!zoneVolumeGroups.ContainsKey(zoneId))
                        zoneVolumeGroups[zoneId] = new List<VolumeAuthoring>();
                    zoneVolumeGroups[zoneId].Add(volume);
                }

                var zoneIds = zoneVolumeGroups.Keys.ToList();
                for (int i = 0; i < zoneIds.Count; i++)
                {
                    for (int j = i + 1; j < zoneIds.Count; j++)
                    {
                        var centerA = VolumeEditorShared.CalculateZoneCenter(zoneVolumeGroups[zoneIds[i]]);
                        var centerB = VolumeEditorShared.CalculateZoneCenter(zoneVolumeGroups[zoneIds[j]]);
                        Handles.color = Color.red;
                        Handles.DrawDottedLine(centerA, centerB, 5f);
                    }
                }
            }

            foreach (var landing in connectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;

                var landingVolumes = System.Array.FindAll(volumeAuthorings,
                    v => v.zone != null && v.zone.id.Equals(landing.landingZone.id)).ToList();

                if (landingVolumes.Count == 0) continue;

                var landingCenter = VolumeEditorShared.CalculateZoneCenter(landingVolumes);

                foreach (var kvp in regionVolumeMap)
                {
                    if (kvp.Key.id.Equals(landing.region.id)) continue;

                    var zoneGroups = kvp.Value.GroupBy(v => v.zone?.id.ToString()).Where(g => !string.IsNullOrEmpty(g.Key));
                    foreach (var zoneGroup in zoneGroups)
                    {
                        Handles.color = Color.yellow;
                        Handles.DrawLine(landingCenter, VolumeEditorShared.CalculateZoneCenter(zoneGroup.ToList()));
                    }
                }
            }

            foreach (var landing in connectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;

                var landingVolumes = System.Array.FindAll(volumeAuthorings,
                    v => v.zone != null && v.zone.id.Equals(landing.landingZone.id)).ToList();

                foreach (var landingVolume in landingVolumes)
                {
                    Handles.color = Color.blue;
                    Handles.DrawWireCube(landingVolume.transform.position, landingVolume.transform.localScale);
                }
            }
        }
    }

    [CustomEditor(typeof(RegionConnectivity))]
    public class RegionConnectivityEditor : Editor
    {
        private bool _showVisualization = true;
        private Dictionary<Region, Color> _regionColors = new Dictionary<Region, Color>();
        private Dictionary<string, bool> _regionFoldouts = new Dictionary<string, bool>();

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            _showVisualization = EditorGUILayout.Toggle("Show Connectivity Visualization", _showVisualization);

            if (GUILayout.Button("Refresh Visualization"))
                SceneView.RepaintAll();

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

                var key = region.id.ToString();
                if (!_regionFoldouts.ContainsKey(key)) _regionFoldouts[key] = false;
                _regionFoldouts[key] = EditorGUILayout.Foldout(_regionFoldouts[key],
                    $"{region.levelName}  ({region.zonesInThisRegion.Count} zones)", true);

                if (!_regionFoldouts[key]) continue;

                EditorGUI.indentLevel++;
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone != null)
                        EditorGUILayout.LabelField($"{zone.zoneName}  ({zone.id})");
                }
                EditorGUI.indentLevel--;
            }

            if (connectivity.landingZones.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Landing Zones", EditorStyles.boldLabel);
                foreach (var landing in connectivity.landingZones)
                {
                    if (landing.landingZone != null && landing.region != null)
                        EditorGUILayout.LabelField($"{landing.region.levelName} \u2192 {landing.landingZone.zoneName}");
                }
            }
        }

        private void OnSceneGUI()
        {
            if (!_showVisualization) return;
            DrawVolumeConnections((RegionConnectivity)target);
        }

        private void DrawVolumeConnections(RegionConnectivity connectivity)
        {
            var volumeAuthorings = Object.FindObjectsByType<VolumeAuthoring>(FindObjectsSortMode.None);
            var regionVolumeMap = new Dictionary<Region, List<VolumeAuthoring>>();

            foreach (var region in connectivity.activeRegions)
            {
                if (region == null) continue;

                VolumeEditorShared.GetOrAddColor(_regionColors, region);
                regionVolumeMap[region] = new List<VolumeAuthoring>();

                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone == null) continue;
                    foreach (var volumeAuth in volumeAuthorings)
                    {
                        if (volumeAuth.zone != null && volumeAuth.zone.id.Equals(zone.id))
                            regionVolumeMap[region].Add(volumeAuth);
                    }
                }
            }

            foreach (var kvp in regionVolumeMap)
            {
                var region = kvp.Key;
                var volumes = kvp.Value;
                var color = _regionColors[region];

                Handles.color = color;

                foreach (var volume in volumes)
                {
                    if (volume == null) continue;

                    Handles.DrawWireCube(volume.transform.position, volume.transform.localScale);

                    var isFirstVolumeOfZone = volumes.Where(v => v?.zone?.id == volume.zone?.id).First() == volume;
                    if (isFirstVolumeOfZone)
                    {
                        Handles.Label(volume.transform.position + Vector3.up * (volume.transform.localScale.y * 0.6f),
                            $"{region.levelName}\n{volume.zone.zoneName}",
                            new GUIStyle(GUI.skin.label) { normal = { textColor = color } });
                    }
                }

                var zoneVolumeGroups = new Dictionary<string, List<VolumeAuthoring>>();
                foreach (var volume in volumes)
                {
                    if (volume?.zone == null) continue;
                    var zoneId = volume.zone.id.ToString();
                    if (!zoneVolumeGroups.ContainsKey(zoneId))
                        zoneVolumeGroups[zoneId] = new List<VolumeAuthoring>();
                    zoneVolumeGroups[zoneId].Add(volume);
                }

                var zoneIds = zoneVolumeGroups.Keys.ToList();
                for (int i = 0; i < zoneIds.Count; i++)
                {
                    for (int j = i + 1; j < zoneIds.Count; j++)
                    {
                        var centerA = VolumeEditorShared.CalculateZoneCenter(zoneVolumeGroups[zoneIds[i]]);
                        var centerB = VolumeEditorShared.CalculateZoneCenter(zoneVolumeGroups[zoneIds[j]]);
                        Handles.DrawDottedLine(centerA, centerB, 5f);
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

                var landingVolumes = System.Array.FindAll(volumeAuthorings,
                    v => v.zone != null && v.zone.id.Equals(landing.landingZone.id)).ToList();

                if (landingVolumes.Count == 0) continue;

                var landingCenter = VolumeEditorShared.CalculateZoneCenter(landingVolumes);

                foreach (var landingVolume in landingVolumes)
                {
                    Handles.color = Color.white;
                    Handles.DrawWireCube(landingVolume.transform.position, landingVolume.transform.localScale * 1.2f);
                }

                Handles.Label(landingCenter + Vector3.up * 2f,
                    $"LANDING\n{landing.landingZone.zoneName}\n({landingVolumes.Count} volumes)",
                    new GUIStyle(GUI.skin.label) {
                        normal = { textColor = Color.white },
                        fontStyle = FontStyle.Bold
                    });

                foreach (var kvp in regionVolumeMap)
                {
                    if (kvp.Key.id.Equals(landing.region.id)) continue;

                    var zoneGroups = kvp.Value.GroupBy(v => v.zone?.id.ToString()).Where(g => !string.IsNullOrEmpty(g.Key));
                    foreach (var zoneGroup in zoneGroups)
                    {
                        Handles.color = Color.yellow;
                        Handles.DrawLine(landingCenter, VolumeEditorShared.CalculateZoneCenter(zoneGroup.ToList()));
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
                DrawVolumeConnectivityInfo(volumeAuth);
            else
                EditorGUILayout.HelpBox("No RegionConnectivity found in scene. Add a GameObject with RegionConnectivityAuthoring.", MessageType.Warning);
        }

        private void FindRegionConnectivity()
        {
            if (_foundConnectivity != null) return;
            var connectivityAuthoring = Object.FindFirstObjectByType<RegionConnectivityAuthoring>();
            if (connectivityAuthoring != null && connectivityAuthoring.regionConnectivity != null)
                _foundConnectivity = connectivityAuthoring.regionConnectivity;
        }

        private void DrawVolumeConnectivityInfo(VolumeAuthoring volumeAuth)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Volume Connectivity", EditorStyles.boldLabel);

            var currentZone = volumeAuth.zone;
            Region currentRegion = null;

            foreach (var region in _foundConnectivity.activeRegions)
            {
                if (region != null && region.zonesInThisRegion.Contains(currentZone))
                {
                    currentRegion = region;
                    break;
                }
            }

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
                        EditorGUILayout.LabelField($"  - {zone.zoneName}");
                }

                var landingConnections = _foundConnectivity.landingZones.Where(l => l.landingZone == currentZone).ToList();
                if (landingConnections.Count > 0)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Landing Zone Connections:", EditorStyles.boldLabel);
                    foreach (var landing in landingConnections)
                    {
                        if (landing.region == null) continue;
                        EditorGUILayout.LabelField($"  Landing for: {landing.region.levelName}");
                        var targetRegions = _foundConnectivity.activeRegions.Where(r => r != landing.region && r != null).ToList();
                        foreach (var targetRegion in targetRegions)
                            EditorGUILayout.LabelField($"    \u2192 {targetRegion.levelName} ({targetRegion.zonesInThisRegion.Count} zones)");
                    }
                }

                EditorGUILayout.EndVertical();
            }
            else
            {
                EditorGUILayout.HelpBox($"Zone '{currentZone.zoneName}' is not found in any active region!", MessageType.Error);
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
            var allVolumeAuthorings = Object.FindObjectsByType<VolumeAuthoring>(FindObjectsSortMode.None);
            var currentZone = selectedVolume.zone;
            Region currentRegion = null;

            foreach (var region in _foundConnectivity.activeRegions)
            {
                if (region != null && region.zonesInThisRegion.Contains(currentZone))
                {
                    currentRegion = region;
                    break;
                }
            }

            if (currentRegion == null) return;

            var regionColor = VolumeEditorShared.GetOrAddColor(_regionColors, currentRegion);

            var selectedZoneVolumes = System.Array.FindAll(allVolumeAuthorings,
                v => v.zone != null && v.zone.id.Equals(currentZone.id)).ToList();
            var selectedZoneCenter = VolumeEditorShared.CalculateZoneCenter(selectedZoneVolumes);

            foreach (var zone in currentRegion.zonesInThisRegion)
            {
                if (zone == null || zone == currentZone) continue;

                var zoneVolumes = System.Array.FindAll(allVolumeAuthorings,
                    v => v.zone != null && v.zone.id.Equals(zone.id)).ToList();

                if (zoneVolumes.Count == 0) continue;

                var zoneCenter = VolumeEditorShared.CalculateZoneCenter(zoneVolumes);

                foreach (var volume in zoneVolumes)
                {
                    Handles.color = regionColor;
                    Handles.DrawWireCube(volume.transform.position, volume.transform.localScale);
                }

                Handles.color = Color.red;
                Handles.DrawDottedLine(selectedZoneCenter, zoneCenter, 5f);
            }

            var landingConnection = _foundConnectivity.landingZones.FirstOrDefault(l => l.landingZone == currentZone);
            if (landingConnection != null)
            {
                foreach (var region in _foundConnectivity.activeRegions)
                {
                    if (region == null || region.id.Equals(currentRegion.id)) continue;

                    foreach (var zone in region.zonesInThisRegion)
                    {
                        if (zone == null) continue;

                        var zoneVolumes = System.Array.FindAll(allVolumeAuthorings,
                            v => v.zone != null && v.zone.id.Equals(zone.id)).ToList();

                        if (zoneVolumes.Count == 0) continue;

                        var zoneCenter = VolumeEditorShared.CalculateZoneCenter(zoneVolumes);

                        foreach (var volume in zoneVolumes)
                        {
                            Handles.color = VolumeEditorShared.GetOrAddColor(_regionColors, region);
                            Handles.DrawWireCube(volume.transform.position, volume.transform.localScale);
                        }

                        Handles.color = Color.yellow;
                        Handles.DrawLine(selectedZoneCenter, zoneCenter);
                    }
                }
            }

            foreach (var landing in _foundConnectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;
                if (landing.landingZone == currentZone) continue;

                var landingVolumes = System.Array.FindAll(allVolumeAuthorings,
                    v => v.zone != null && v.zone.id.Equals(landing.landingZone.id)).ToList();

                if (landingVolumes.Count == 0) continue;

                var landingCenter = VolumeEditorShared.CalculateZoneCenter(landingVolumes);

                foreach (var volume in landingVolumes)
                {
                    Handles.color = Color.blue;
                    Handles.DrawWireCube(volume.transform.position, volume.transform.localScale);
                }

                Handles.color = Color.yellow;
                Handles.DrawLine(landingCenter, selectedZoneCenter);
            }

            foreach (var selectedVol in selectedZoneVolumes)
            {
                Handles.color = Color.yellow;
                Handles.DrawWireCube(selectedVol.transform.position, selectedVol.transform.localScale);
            }

            Handles.Label(selectedZoneCenter + Vector3.up * 2f,
                $"SELECTED ZONE\n{currentZone.zoneName}\n({selectedZoneVolumes.Count} volumes)",
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
