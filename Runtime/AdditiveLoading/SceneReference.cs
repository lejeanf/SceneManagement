using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [System.Serializable]
    public class SceneReference
    {
        [SerializeField] private Object sceneAsset; // Scene file to drag in Inspector
        [SerializeField] private string sceneName;  // Automatically extracted scene name

        // Public getter for the scene name
        public string SceneName => sceneName;

        public SceneReference(string sceneName)
        {
            this.sceneName = sceneName;
        }
        
        #if UNITY_EDITOR
        // Méthode pour forcer la mise à jour du nom
        public void UpdateSceneName()
        {
            if (sceneAsset != null && sceneAsset is SceneAsset)
            {
                sceneName = sceneAsset.name;
            }
            else
            {
                sceneName = "";
            }
        }
        #endif
    }
    
    #if UNITY_EDITOR
    [CustomPropertyDrawer(typeof(SceneReference))]
    public class SceneReferenceDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            SerializedProperty sceneAssetProp = property.FindPropertyRelative("sceneAsset");
            SerializedProperty sceneNameProp = property.FindPropertyRelative("sceneName");

            // Vérifier si le nom a changé
            if (sceneAssetProp.objectReferenceValue != null)
            {
                string currentName = sceneAssetProp.objectReferenceValue.name;
                if (sceneNameProp.stringValue != currentName)
                {
                    sceneNameProp.stringValue = currentName;
                }
            }

            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);
        
            EditorGUI.BeginChangeCheck();
            Object sceneAsset = EditorGUI.ObjectField(position, sceneAssetProp.objectReferenceValue, typeof(SceneAsset), false);

            if (EditorGUI.EndChangeCheck())
            {
                sceneAssetProp.objectReferenceValue = sceneAsset;
                sceneNameProp.stringValue = sceneAsset != null ? sceneAsset.name : "";
            }

            EditorGUI.EndProperty();
        }
    }

    // AssetPostprocessor pour détecter les renommages de scènes
    public class SceneReferenceAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            bool sceneRenamed = false;

            // Vérifier si des scènes ont été déplacées/renommées
            for (int i = 0; i < movedAssets.Length; i++)
            {
                if (movedAssets[i].EndsWith(".unity"))
                {
                    sceneRenamed = true;
                    break;
                }
            }

            if (sceneRenamed)
            {
                // Forcer la mise à jour de tous les objets contenant des SceneReference
                RefreshAllSceneReferences();
            }
        }

        private static void RefreshAllSceneReferences()
        {
            // Trouver tous les MonoBehaviour et ScriptableObject dans le projet
            string[] guids = AssetDatabase.FindAssets("t:MonoBehaviour t:ScriptableObject");
        
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Object obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            
                if (obj != null)
                {
                    SerializedObject serializedObject = new SerializedObject(obj);
                    SerializedProperty property = serializedObject.GetIterator();
                    bool hasChanges = false;

                    while (property.NextVisible(true))
                    {
                        if (property.type == "SceneReference")
                        {
                            SerializedProperty sceneAssetProp = property.FindPropertyRelative("sceneAsset");
                            SerializedProperty sceneNameProp = property.FindPropertyRelative("sceneName");

                            if (sceneAssetProp != null && sceneAssetProp.objectReferenceValue != null)
                            { 
                                string currentName = sceneAssetProp.objectReferenceValue.name;
                                if (sceneNameProp.stringValue != currentName)
                                {
                                    sceneNameProp.stringValue = currentName;
                                    hasChanges = true;
                                }
                            }
                        }
                    }

                    if (hasChanges)
                    {
                        serializedObject.ApplyModifiedProperties();
                        EditorUtility.SetDirty(obj);
                    }
                }
            }

            AssetDatabase.SaveAssets();
        }
    }
    #endif
}