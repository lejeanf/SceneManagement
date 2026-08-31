using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement.Editor
{
    /// <summary>
    /// Project-wide version of the SubScene inspector's "Reimport" button: marks every entity
    /// scene in the import cache dirty so the asset pipeline re-runs baking for all of them,
    /// instead of only the SubScenes open in the current scene.
    /// </summary>
    public static class SubSceneReimportTool
    {
        private const string LogPrefix = "[SceneManagement]";

        // Entities writes one <guid>.sceneWithBuildSettings per (scene, build config, editor/player)
        // combination it has imported; rewriting an entry's DirtyValue is the official reimport trigger.
        private const string CachePath = "Assets/SceneDependencyCache";

        [MenuItem("Tools/Jeanf/SceneManagement/Re-import All SubScenes")]
        public static void ReimportAllSubScenes()
        {
            var files = Directory.Exists(CachePath)
                ? Directory.GetFiles(CachePath, "*.sceneWithBuildSettings")
                : Array.Empty<string>();
            if (files.Length == 0)
            {
                Debug.Log($"{LogPrefix} {CachePath} holds no entity scene cache entries — no subscene has been imported yet, nothing to re-import.");
                return;
            }

            // SceneWithBuildConfigurationGUIDs is internal to Unity.Scenes; ReadFromFile + Dirty are
            // the same entry points SubSceneInspectorUtility.ForceReimport goes through.
            var type = typeof(Unity.Scenes.SubScene).Assembly.GetType("Unity.Scenes.SceneWithBuildConfigurationGUIDs");
            var readFromFile = type?.GetMethod("ReadFromFile", BindingFlags.Public | BindingFlags.Static);
            var dirty = type?.GetMethod("Dirty", BindingFlags.Public | BindingFlags.Static);
            var sceneGuidField = type?.GetField("SceneGUID");
            var buildConfigField = type?.GetField("BuildConfiguration");
            if (readFromFile == null || dirty == null || sceneGuidField == null || buildConfigField == null)
            {
                Debug.LogError($"{LogPrefix} com.unity.entities internals changed (SceneWithBuildConfigurationGUIDs) — cannot force a project-wide reimport. Re-import subscenes one by one via the SubScene inspector's Reimport button instead.");
                return;
            }

            // Dirty() rewrites both the editor and player variant of a (scene, config) pair, so
            // dedupe to avoid touching each pair twice.
            var seen = new HashSet<(string scene, string config)>();
            var dirtied = 0;
            try
            {
                for (var i = 0; i < files.Length; i++)
                {
                    EditorUtility.DisplayProgressBar("Re-import All SubScenes", files[i], i / (float)files.Length);

                    object entry;
                    try
                    {
                        entry = readFromFile.Invoke(null, new object[] { files[i] });
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"{LogPrefix} Skipping unreadable cache entry '{files[i]}': {e.Message}");
                        continue;
                    }

                    var sceneGuid = sceneGuidField.GetValue(entry);
                    var buildConfig = buildConfigField.GetValue(entry);
                    if (!seen.Add((sceneGuid.ToString(), buildConfig.ToString())))
                        continue;

                    if ((bool)dirty.Invoke(null, new[] { sceneGuid, buildConfig }))
                        dirtied++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (dirtied > 0)
                AssetDatabase.Refresh();

            Debug.Log($"{LogPrefix} Marked {dirtied} subscene(s) for re-import. Open subscenes rebake now; closed ones rebake the next time they are loaded.");
        }
    }
}
