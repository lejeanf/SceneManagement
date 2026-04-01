using UnityEngine;

#if UNITY_EDITOR
using UnityEditor.AddressableAssets;
using UnityEditor;
#endif

namespace jeanf.scenemanagement
{
    [System.Serializable]
    public class SceneReference
    {
        [SerializeField] private Object sceneAsset;
        [SerializeField] private string address;
        [SerializeField] public string Name;

        public string Address => address;

        public SceneReference(string address)
        {
            this.address = address;
        }

        #if UNITY_EDITOR
        public void UpdateAddress()
        {
            if (sceneAsset == null || sceneAsset is not SceneAsset)
            {
                address = "";
                return;
            }

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                address = "";
                return;
            }

            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(sceneAsset));
            var entry = settings.FindAssetEntry(guid);
            address = entry?.address ?? "";
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
            SerializedProperty addressProp = property.FindPropertyRelative("address");

            if (sceneAssetProp.objectReferenceValue != null)
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings != null)
                {
                    var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(sceneAssetProp.objectReferenceValue));
                    var entry = settings.FindAssetEntry(guid);
                    var resolvedAddress = entry?.address ?? "";

                    if (addressProp.stringValue != resolvedAddress)
                        addressProp.stringValue = resolvedAddress;
                }
            }

            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);

            float fieldWidth = position.width;
            float warningWidth = 0f;

            var currentSettings = AddressableAssetSettingsDefaultObject.Settings;
            bool isNotAddressable = false;

            if (sceneAssetProp.objectReferenceValue != null && currentSettings != null)
            {
                var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(sceneAssetProp.objectReferenceValue));
                isNotAddressable = currentSettings.FindAssetEntry(guid) == null;

                if (isNotAddressable)
                {
                    warningWidth = 20f;
                    fieldWidth -= warningWidth + 4f;
                }
            }

            var fieldRect = new Rect(position.x, position.y, fieldWidth, position.height);

            EditorGUI.BeginChangeCheck();
            Object sceneAsset = EditorGUI.ObjectField(fieldRect, sceneAssetProp.objectReferenceValue, typeof(SceneAsset), false);

            if (EditorGUI.EndChangeCheck())
            {
                sceneAssetProp.objectReferenceValue = sceneAsset;

                if (sceneAsset != null && currentSettings != null)
                {
                    var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(sceneAsset));
                    var entry = currentSettings.FindAssetEntry(guid);
                    addressProp.stringValue = entry?.address ?? "";
                }
                else
                {
                    addressProp.stringValue = "";
                }
            }

            if (isNotAddressable)
            {
                var warningRect = new Rect(position.x + fieldWidth + 4f, position.y, warningWidth, position.height);
                EditorGUI.LabelField(warningRect, new GUIContent("⚠", "This scene is not marked as Addressable."));
            }

            EditorGUI.EndProperty();
        }
    }

    public class SceneReferenceAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            bool sceneRenamed = false;

            for (int i = 0; i < movedAssets.Length; i++)
            {
                if (movedAssets[i].EndsWith(".unity"))
                {
                    sceneRenamed = true;
                    break;
                }
            }

            if (sceneRenamed)
                RefreshAllSceneReferences();
        }

        private static void RefreshAllSceneReferences()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;

            string[] guids = AssetDatabase.FindAssets("t:MonoBehaviour t:ScriptableObject");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Object obj = AssetDatabase.LoadAssetAtPath<Object>(path);

                if (obj == null) continue;

                SerializedObject serializedObject = new SerializedObject(obj);
                SerializedProperty property = serializedObject.GetIterator();
                bool hasChanges = false;

                while (property.NextVisible(true))
                {
                    if (property.type != "SceneReference") continue;

                    SerializedProperty sceneAssetProp = property.FindPropertyRelative("sceneAsset");
                    SerializedProperty addressProp = property.FindPropertyRelative("address");

                    if (sceneAssetProp?.objectReferenceValue == null) continue;

                    var assetGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(sceneAssetProp.objectReferenceValue));
                    var entry = settings.FindAssetEntry(assetGuid);
                    var resolvedAddress = entry?.address ?? "";

                    if (addressProp.stringValue != resolvedAddress)
                    {
                        addressProp.stringValue = resolvedAddress;
                        hasChanges = true;
                    }
                }

                if (hasChanges)
                {
                    serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(obj);
                }
            }

            AssetDatabase.SaveAssets();
        }
    }
    #endif
}