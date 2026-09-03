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

        private const string Pref = "jeanf.scenemanagement.viz.";

        internal static bool ShowVolumes
        {
            get => EditorPrefs.GetBool(Pref + "volumes", true);
            set => EditorPrefs.SetBool(Pref + "volumes", value);
        }
        internal static bool ShowZoneLinks
        {
            get => EditorPrefs.GetBool(Pref + "zoneLinks", false);
            set => EditorPrefs.SetBool(Pref + "zoneLinks", value);
        }
        internal static bool ShowLandingLinks
        {
            get => EditorPrefs.GetBool(Pref + "landingLinks", false);
            set => EditorPrefs.SetBool(Pref + "landingLinks", value);
        }
        internal static bool ShowLabels
        {
            get => EditorPrefs.GetBool(Pref + "labels", true);
            set => EditorPrefs.SetBool(Pref + "labels", value);
        }
        internal static bool FocusSelectedOnly
        {
            get => EditorPrefs.GetBool(Pref + "focus", true);
            set => EditorPrefs.SetBool(Pref + "focus", value);
        }
        internal static float MaxDrawDistance
        {
            get => EditorPrefs.GetFloat(Pref + "distance", 120f);
            set => EditorPrefs.SetFloat(Pref + "distance", value);
        }

        internal static void DrawSettingsGUI()
        {
            EditorGUILayout.LabelField("Visualization Layers", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.BeginHorizontal();
            ShowVolumes = EditorGUILayout.ToggleLeft("Volumes", ShowVolumes, GUILayout.Width(120));
            ShowLabels = EditorGUILayout.ToggleLeft("Labels", ShowLabels, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            ShowZoneLinks = EditorGUILayout.ToggleLeft("Zone Links", ShowZoneLinks, GUILayout.Width(120));
            ShowLandingLinks = EditorGUILayout.ToggleLeft("Landing Links", ShowLandingLinks, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            FocusSelectedOnly = EditorGUILayout.ToggleLeft("Focus selected only", FocusSelectedOnly);
            MaxDrawDistance = EditorGUILayout.Slider("Max Draw Distance", MaxDrawDistance, 10f, 1000f);

            if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();
        }

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

        internal static bool InRange(Vector3 worldPos)
        {
            var sceneView = SceneView.currentDrawingSceneView;
            if (sceneView == null || sceneView.camera == null) return true;
            return Vector3.Distance(sceneView.camera.transform.position, worldPos) <= MaxDrawDistance;
        }

        internal static Dictionary<string, List<VolumeAuthoring>> GroupVolumesByZone(VolumeAuthoring[] volumes)
        {
            var map = new Dictionary<string, List<VolumeAuthoring>>();
            foreach (var v in volumes)
            {
                if (v == null || v.zone == null) continue;
                var key = v.zone.id.ToString();
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<VolumeAuthoring>();
                    map[key] = list;
                }
                list.Add(v);
            }
            return map;
        }

        internal static void DrawConnectivity(RegionConnectivity connectivity, Dictionary<Region, Color> regionColors, Region focusRegion, Zone focusZone)
        {
            if (connectivity == null) return;

            var allVolumes = Object.FindObjectsByType<VolumeAuthoring>(FindObjectsInactive.Exclude);
            var volumesByZone = GroupVolumesByZone(allVolumes);

            var zoneToRegion = new Dictionary<string, Region>();
            foreach (var region in connectivity.activeRegions)
            {
                if (region?.zonesInThisRegion == null) continue;
                GetOrAddColor(regionColors, region);
                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone != null) zoneToRegion[zone.id.ToString()] = region;
                }
            }

            var focused = FocusSelectedOnly && focusRegion != null;

            if (ShowVolumes || ShowLabels)
                DrawVolumesAndLabels(connectivity, regionColors, volumesByZone, focusRegion, focused);

            if (ShowZoneLinks)
                DrawZoneLinks(connectivity, volumesByZone, focusZone);

            if (ShowLandingLinks)
                DrawLandingLinks(connectivity, volumesByZone, focusRegion, focused);
        }

        private static void DrawVolumesAndLabels(RegionConnectivity connectivity, Dictionary<Region, Color> regionColors,
            Dictionary<string, List<VolumeAuthoring>> volumesByZone, Region focusRegion, bool focused)
        {
            foreach (var region in connectivity.activeRegions)
            {
                if (region?.zonesInThisRegion == null) continue;
                if (focused && region != focusRegion) continue;

                var color = GetOrAddColor(regionColors, region);

                foreach (var zone in region.zonesInThisRegion)
                {
                    if (zone == null) continue;
                    if (!volumesByZone.TryGetValue(zone.id.ToString(), out var volumes)) continue;

                    var center = CalculateZoneCenter(volumes);
                    if (!InRange(center)) continue;

                    if (ShowVolumes)
                    {
                        Handles.color = color;
                        foreach (var volume in volumes)
                        {
                            if (volume == null) continue;
                            Handles.DrawWireCube(volume.transform.position, volume.transform.localScale);
                        }
                    }

                    if (ShowLabels)
                    {
                        Handles.Label(center + Vector3.up * 1.5f,
                            $"{region.levelName}\n{zone.zoneName}",
                            new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = color } });
                    }
                }
            }
        }

        private static void DrawZoneLinks(RegionConnectivity connectivity, Dictionary<string, List<VolumeAuthoring>> volumesByZone, Zone focusZone)
        {
            foreach (var adjacency in CollectAdjacencyAssets(connectivity))
            {
                foreach (var link in adjacency.links)
                {
                    if (link?.touchingZones == null) continue;

                    var linkColor = link.connectionType == RegionConnectionType.Elevator
                        ? Color.magenta
                        : Color.cyan;

                    foreach (var pair in link.touchingZones)
                    {
                        if (pair?.zoneOnA == null || pair.zoneOnB == null) continue;
                        if (focusZone != null && FocusSelectedOnly && pair.zoneOnA != focusZone && pair.zoneOnB != focusZone) continue;

                        if (!volumesByZone.TryGetValue(pair.zoneOnA.id.ToString(), out var va)) continue;
                        if (!volumesByZone.TryGetValue(pair.zoneOnB.id.ToString(), out var vb)) continue;

                        var a = CalculateZoneCenter(va);
                        var b = CalculateZoneCenter(vb);
                        if (!InRange(a) && !InRange(b)) continue;

                        Handles.color = linkColor;
                        Handles.DrawDottedLine(a, b, 4f);
                    }
                }
            }
        }

        internal static List<RegionAdjacency> CollectAdjacencyAssets(RegionConnectivity connectivity)
        {
            var result = new List<RegionAdjacency>();
            if (connectivity == null) return result;
            foreach (var region in connectivity.activeRegions)
            {
                if (region?.regionAdjacency == null) continue;
                if (!result.Contains(region.regionAdjacency)) result.Add(region.regionAdjacency);
            }
            return result;
        }

        private static void DrawLandingLinks(RegionConnectivity connectivity, Dictionary<string, List<VolumeAuthoring>> volumesByZone,
            Region focusRegion, bool focused)
        {
            foreach (var landing in connectivity.landingZones)
            {
                if (landing.landingZone == null || landing.region == null) continue;
                if (focused && landing.region != focusRegion) continue;
                if (!volumesByZone.TryGetValue(landing.landingZone.id.ToString(), out var landingVolumes)) continue;

                var landingCenter = CalculateZoneCenter(landingVolumes);
                if (!InRange(landingCenter)) continue;

                Handles.color = Color.white;
                foreach (var volume in landingVolumes)
                {
                    if (volume == null) continue;
                    Handles.DrawWireCube(volume.transform.position, volume.transform.localScale * 1.15f);
                }

                if (ShowLabels)
                {
                    Handles.Label(landingCenter + Vector3.up * 2.5f,
                        $"LANDING → {landing.region.levelName}\n{landing.landingZone.zoneName}",
                        new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Color.white } });
                }

                var targetCenter = RegionCenter(landing.region, volumesByZone);
                if (targetCenter.HasValue)
                {
                    Handles.color = Color.yellow;
                    Handles.DrawLine(landingCenter, targetCenter.Value);
                }
            }
        }

        private static Vector3? RegionCenter(Region region, Dictionary<string, List<VolumeAuthoring>> volumesByZone)
        {
            if (region?.zonesInThisRegion == null) return null;
            var sum = Vector3.zero;
            var count = 0;
            foreach (var zone in region.zonesInThisRegion)
            {
                if (zone == null) continue;
                if (!volumesByZone.TryGetValue(zone.id.ToString(), out var volumes)) continue;
                sum += CalculateZoneCenter(volumes);
                count++;
            }
            return count == 0 ? (Vector3?)null : sum / count;
        }
    }

    [CustomEditor(typeof(RegionConnectivityAuthoring))]
    public class RegionConnectivityAuthoringEditor : Editor
    {
        private readonly Dictionary<Region, Color> _regionColors = new Dictionary<Region, Color>();
        private readonly Dictionary<string, bool> _regionFoldouts = new Dictionary<string, bool>();

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
            VolumeEditorShared.DrawSettingsGUI();

            EditorGUILayout.Space();
            ConnectivityInfoGUI.Draw(authoring.regionConnectivity, _regionFoldouts);
        }

        private void OnSceneGUI()
        {
            var authoring = (RegionConnectivityAuthoring)target;
            VolumeEditorShared.DrawConnectivity(authoring.regionConnectivity, _regionColors, null, null);
        }
    }

    [CustomEditor(typeof(RegionConnectivity))]
    public class RegionConnectivityEditor : Editor
    {
        private readonly Dictionary<Region, Color> _regionColors = new Dictionary<Region, Color>();
        private readonly Dictionary<string, bool> _regionFoldouts = new Dictionary<string, bool>();

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            VolumeEditorShared.DrawSettingsGUI();

            EditorGUILayout.Space();
            ConnectivityInfoGUI.Draw((RegionConnectivity)target, _regionFoldouts);
        }

        private void OnSceneGUI()
        {
            VolumeEditorShared.DrawConnectivity((RegionConnectivity)target, _regionColors, null, null);
        }
    }

    static class ConnectivityInfoGUI
    {
        internal static void Draw(RegionConnectivity connectivity, Dictionary<string, bool> regionFoldouts)
        {
            EditorGUILayout.LabelField("Connectivity Overview", EditorStyles.boldLabel);

            foreach (var region in connectivity.activeRegions)
            {
                if (region == null) continue;

                var key = region.id.ToString();
                if (!regionFoldouts.ContainsKey(key)) regionFoldouts[key] = false;
                regionFoldouts[key] = EditorGUILayout.Foldout(regionFoldouts[key],
                    $"{region.levelName}  ({region.zonesInThisRegion.Count} zones)", true);

                if (!regionFoldouts[key]) continue;

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
                        EditorGUILayout.LabelField($"{landing.region.levelName} → {landing.landingZone.zoneName}");
                }
            }
        }
    }

    [CustomEditor(typeof(VolumeAuthoring))]
    public class VolumeAuthoringEditor : Editor
    {
        private RegionConnectivity _foundConnectivity;
        private readonly Dictionary<Region, Color> _regionColors = new Dictionary<Region, Color>();

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
            VolumeEditorShared.DrawSettingsGUI();

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
            var connectivityAuthoring = Object.FindAnyObjectByType<RegionConnectivityAuthoring>();
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

                var crossLinks = GetCrossRegionLinksForZone(currentRegion, currentZone);
                if (crossLinks.Count > 0)
                {
                    EditorGUILayout.LabelField($"Connects across {crossLinks.Count} border(s):");
                    foreach (var s in crossLinks)
                        EditorGUILayout.LabelField($"  - {s}");
                }
                else
                {
                    EditorGUILayout.HelpBox("This zone has no cross-region link. Add a touching-zone pair in the region's RegionAdjacency asset.", MessageType.Info);
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

        private static List<string> GetCrossRegionLinksForZone(Region currentRegion, Zone currentZone)
        {
            var result = new List<string>();
            if (currentRegion == null || currentRegion.regionAdjacency == null) return result;

            foreach (var link in currentRegion.regionAdjacency.links)
            {
                if (link?.touchingZones == null) continue;

                Region other = link.regionA == currentRegion ? link.regionB
                    : (link.regionB == currentRegion ? link.regionA : null);
                if (other == null) continue;

                foreach (var pair in link.touchingZones)
                {
                    if (pair == null) continue;

                    Zone theirs = null;
                    if (pair.zoneOnA == currentZone) theirs = pair.zoneOnB;
                    else if (pair.zoneOnB == currentZone) theirs = pair.zoneOnA;
                    if (theirs == null) continue;

                    var typeLabel = link.connectionType == RegionConnectionType.Elevator ? "elevator" : "doorway";
                    result.Add($"{theirs.zoneName} in {other.levelName} [{typeLabel}]");
                }
            }
            return result;
        }

        private void OnSceneGUI()
        {
            FindRegionConnectivity();
            if (_foundConnectivity == null) return;

            var volumeAuth = (VolumeAuthoring)target;
            if (volumeAuth.zone == null) return;

            Region currentRegion = null;
            foreach (var region in _foundConnectivity.activeRegions)
            {
                if (region != null && region.zonesInThisRegion.Contains(volumeAuth.zone))
                {
                    currentRegion = region;
                    break;
                }
            }

            VolumeEditorShared.DrawConnectivity(_foundConnectivity, _regionColors, currentRegion, volumeAuth.zone);
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
