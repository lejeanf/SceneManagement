using UnityEditor;
using UnityEngine;

namespace jeanf.SceneManagment
{
    [System.Serializable]
    public class SceneReference
    {
        [SerializeField] private Object sceneAsset; // Scene file to drag in Inspector
        [SerializeField] private string sceneName;  // Automatically extracted scene name

        // Public getter for the scene name
        public string SceneName => sceneName;

        #if UNITY_EDITOR
        // Called automatically by Unity when the object is modified in the editor
        public void OnValidate()
        {
            // Ensure the sceneName is updated when the sceneAsset changes
            if (sceneAsset != null)
            {
                if (sceneAsset is SceneAsset)
                {
                    sceneName = sceneAsset.name; // Extract scene name from the asset
                }
                else
                {
                    Debug.LogWarning("Assigned object is not a SceneAsset!");
                    sceneAsset = null; // Reset if it's not a valid SceneAsset
                    sceneName = "";
                }
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

            // Get the serialized properties
            SerializedProperty sceneAssetProp = property.FindPropertyRelative("sceneAsset");
            SerializedProperty sceneNameProp = property.FindPropertyRelative("sceneName");

            // Draw the SceneAsset field with a label
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);
            EditorGUI.BeginChangeCheck();

            // Restrict the object field to accept only SceneAssets
            Object sceneAsset = EditorGUI.ObjectField(position, sceneAssetProp.objectReferenceValue, typeof(SceneAsset), false);

            // If the scene asset changes, update the scene name
            if (EditorGUI.EndChangeCheck())
            {
                sceneAssetProp.objectReferenceValue = sceneAsset;
                sceneNameProp.stringValue = sceneAsset != null ? sceneAsset.name : "";
            }

            EditorGUI.EndProperty();
        }
    }
    #endif

}