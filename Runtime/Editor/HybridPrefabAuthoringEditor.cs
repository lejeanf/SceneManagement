#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement.editor
{
    /// <summary>
    /// Makes the sweep auditable before any bake: which subtrees will be respawned in the main
    /// world, and which GameObject-world components (Canvas/AudioSource/SteamAudio*) stay in baked
    /// territory where baking strips or companion-bakes them. The lists come from
    /// <see cref="HybridPrefabScan"/> — the same code the baker runs — so what you read here is
    /// what the bake does.
    /// </summary>
    [CustomEditor(typeof(HybridPrefabAuthoring))]
    public class HybridPrefabAuthoringEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            // A [CustomEditor] replaces the validationTools fallback inspector, so restore the
            // framework's orange issues banner explicitly (hierarchy dot / console log still work).
            jeanf.validationTools.ValidationUi.DrawIssuesBanner(target as Component);
            DrawDefaultInspector();

            var authoring = (HybridPrefabAuthoring)target;

            EditorGUILayout.Space();
            var spawnRoots = new List<Transform>();
            HybridPrefabScan.CollectSpawnRoots(authoring.transform, authoring, spawnRoots);

            if (authoring.prefab != null)
            {
                EditorGUILayout.HelpBox($"Explicit prefab: '{authoring.prefab.name}' is spawned at this object's " +
                                        "pose, in addition to the swept subtrees below.", MessageType.Info);
            }

            if (!authoring.IsValid)
            {
                EditorGUILayout.HelpBox("This object is not part of a prefab, so swept subtrees have no asset " +
                                        "counterpart to respawn. Make it a prefab, or assign an explicit prefab above.",
                    MessageType.Error);
            }

            EditorGUILayout.LabelField($"Respawned in the main world ({spawnRoots.Count})", EditorStyles.boldLabel);
            if (spawnRoots.Count == 0)
            {
                EditorGUILayout.HelpBox("The sweep found nothing. It auto-detects subtrees that contain a Canvas " +
                                        "or an AudioSource and no Renderer; 'Additional Subtrees' force-includes a " +
                                        "subtree it cannot detect (e.g. an empty SteamAudioDynamicObject proxy), " +
                                        "'Excluded Subtrees' vetoes a detection so it bakes normally. Both lists " +
                                        "can usually stay empty.",
                    authoring.prefab != null ? MessageType.Info : MessageType.Warning);
            }
            foreach (var root in spawnRoots)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.ObjectField(root, typeof(Transform), true);
                    if (GUILayout.Button("Ping", GUILayout.Width(45))) EditorGUIUtility.PingObject(root);
                }
            }

            var stranded = new List<Component>();
            HybridPrefabScan.FindStrandedComponents(authoring.transform, spawnRoots, stranded);
            if (stranded.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Left in baked territory ({stranded.Count})", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("These GameObject-world components sit under baked geometry, so the sweep " +
                                        "cannot respawn them: baking strips them (SteamAudioGeometry, Canvas) or " +
                                        "companion-bakes them (AudioSource). If one must work at runtime, move it " +
                                        "to its own child object and add that to Additional Subtrees, or handle it " +
                                        "in the owning system (doors).", MessageType.Warning);
                foreach (var group in stranded.GroupBy(c => c.transform))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.ObjectField(group.Key, typeof(Transform), true);
                        EditorGUILayout.LabelField(string.Join(", ", group.Select(c => c.GetType().Name)),
                            EditorStyles.miniLabel);
                    }
                }
            }
        }
    }
}
#endif
