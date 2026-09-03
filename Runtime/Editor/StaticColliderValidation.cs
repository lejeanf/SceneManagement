using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement.Editor
{
    /// <summary>
    /// Catches the setup mistakes that make baked prop collision look broken rather than misconfigured:
    /// a missing <see cref="StaticColliderBridge"/> (correct prefabs, no colliders, no clue why),
    /// unsupported collider types, and layers that cannot collide with the player.
    /// </summary>
    [InitializeOnLoad]
    public static class StaticColliderValidation
    {
        private const string LogPrefix = "[SceneManagement]";

        static StaticColliderValidation()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode) WarnIfBridgeMissing();
            };
        }

        // Closed SubScenes hide their contents at edit time, so "SubScenes are in use" is the signal
        // that baked props may exist — we cannot enumerate them to be sure.
        private static void WarnIfBridgeMissing()
        {
            var usesSubScenes = Object.FindObjectsByType<Unity.Scenes.SubScene>(
                FindObjectsInactive.Include).Length > 0;
            if (!usesSubScenes) return;

            if (Object.FindAnyObjectByType<StaticColliderBridge>(FindObjectsInactive.Include) != null) return;

            Debug.LogWarning($"{LogPrefix} SubScenes are in use but there is no StaticColliderBridge in the loaded " +
                "scenes — every prop baked with a StaticColliderAuthoring is NON-SOLID: the player walks through it. " +
                "Add a StaticColliderBridge component to an always-loaded GameObject (e.g. a manager in your main scene).");
        }

        [MenuItem("Tools/Jeanf/SceneManagement/Validate Static Colliders")]
        public static void ValidatePrefabs()
        {
            var guids = AssetDatabase.FindAssets("t:Prefab");
            var noSupported = new List<string>();
            var unsupported = new List<string>();
            var badLayer = new List<string>();
            var checkedCount = 0;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var authorings = prefab.GetComponentsInChildren<StaticColliderAuthoring>(true);
                if (authorings == null || authorings.Length == 0) continue;

                foreach (var authoring in authorings)
                {
                    checkedCount++;
                    var colliders = authoring.IncludeChildren
                        ? authoring.GetComponentsInChildren<Collider>(true)
                        : authoring.GetComponents<Collider>();

                    var supported = 0;
                    foreach (var collider in colliders)
                    {
                        if (collider == null) continue;
                        if (collider.isTrigger && !authoring.IncludeTriggers) continue;

                        if (StaticColliderBake.TryDescribe(collider, out _))
                        {
                            supported++;
                            if (!CollidesWithAnything(collider.gameObject.layer))
                                badLayer.Add($"{path} → '{collider.name}' on layer '{LayerMask.LayerToName(collider.gameObject.layer)}'");
                        }
                        else
                        {
                            unsupported.Add($"{path} → '{collider.name}' ({collider.GetType().Name})");
                        }
                    }

                    if (supported == 0) noSupported.Add(path);
                }
            }

            if (checkedCount == 0)
            {
                Debug.Log($"{LogPrefix} Validate Static Colliders: no prefab uses StaticColliderAuthoring yet. " +
                    "Add it to a prop prefab so its colliders survive being baked into a SubScene.");
                return;
            }

            var report = new StringBuilder();
            if (noSupported.Count > 0)
                report.AppendLine($"No bakeable collider (nothing will block the player): {string.Join(", ", noSupported)}");
            if (unsupported.Count > 0)
                report.AppendLine($"Unsupported collider type — only Box, Sphere and Capsule are baked: {string.Join(", ", unsupported)}");
            if (badLayer.Count > 0)
                report.AppendLine($"Layer collides with NOTHING in the Physics Layer Collision Matrix, so the proxy cannot block anything: {string.Join(", ", badLayer)}");

            if (report.Length == 0)
                Debug.Log($"{LogPrefix} Validate Static Colliders: {checkedCount} StaticColliderAuthoring component(s) checked, all good.");
            else
                Debug.LogWarning($"{LogPrefix} Validate Static Colliders ({checkedCount} checked):\n{report}");
        }

        private static bool CollidesWithAnything(int layer)
        {
            for (var i = 0; i < 32; i++)
                if (!Physics.GetIgnoreLayerCollision(layer, i)) return true;
            return false;
        }
    }
}
