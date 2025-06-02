#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;

namespace jeanf.scenemanagement
{
    [System.Serializable]
    public class Id 
    {
        public string id;
        public override string ToString()
        {
            return id;
        }

        public Id(Id id)
        {
            this.id = id;
        }

        public Id(string value)
        {
            id = value;
        }
        
        // Implicit conversion from string to Id
        public static implicit operator Id(string value)
        {
            return new Id(value);
        }

        // Implicit conversion from Id to string
        public static implicit operator string(Id id)
        {
            return id?.id;
        }
    }

    #if UNITY_EDITOR
    [CustomPropertyDrawer(typeof(Id))]
    public class IdPropertyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Start property drawing
            EditorGUI.BeginProperty(position, label, property);

            // Calculate positions for the label, field, and button
            float labelWidth = EditorGUIUtility.labelWidth;
            float fieldWidth = position.width - labelWidth - 85; // Reserve space for button
            float buttonWidth = 80;

            Rect labelRect = new Rect(position.x, position.y, labelWidth, position.height);
            Rect fieldRect = new Rect(position.x + labelWidth, position.y, fieldWidth, position.height);
            Rect buttonRect = new Rect(position.x + labelWidth + fieldWidth + 5, position.y, buttonWidth, position.height);

            // Draw the label
            EditorGUI.LabelField(labelRect, label);

            // Access the "id" field within the "Id" class
            SerializedProperty idProperty = property.FindPropertyRelative("id");

            // Show mixed value indicator if multiple objects have different values
            EditorGUI.showMixedValue = idProperty.hasMultipleDifferentValues;
            
            // Draw the text field for the "id" string
            EditorGUI.BeginChangeCheck();
            string newValue = EditorGUI.TextField(fieldRect, idProperty.stringValue);
            if (EditorGUI.EndChangeCheck())
            {
                idProperty.stringValue = newValue;
            }

            // Reset mixed value display
            EditorGUI.showMixedValue = false;

            // Draw the "Generate" button
            if (GUI.Button(buttonRect, "Generate"))
            {
                // Handle multi-object selection properly
                GenerateUniqueIdsForAllTargets(property);
            }

            // End property drawing
            EditorGUI.EndProperty();
        }

        private void GenerateUniqueIdsForAllTargets(SerializedProperty property)
        {
            // Get all target objects (handles multi-selection)
            UnityEngine.Object[] targets = property.serializedObject.targetObjects;
            
            // Record undo for all targets
            Undo.RecordObjects(targets, "Generate Unique IDs");
            
            // Generate unique ID for each target
            foreach (UnityEngine.Object target in targets)
            {
                SerializedObject targetSerializedObject = new SerializedObject(target);
                SerializedProperty targetProperty = targetSerializedObject.FindProperty(property.propertyPath);
                SerializedProperty targetIdProperty = targetProperty.FindPropertyRelative("id");
                
                // Generate unique GUID for each object
                targetIdProperty.stringValue = System.Guid.NewGuid().ToString();
                
                // Apply changes to this specific target
                targetSerializedObject.ApplyModifiedProperties();
                
                // Mark the asset as dirty to ensure it saves
                EditorUtility.SetDirty(target);
            }
            
            // Save all modified assets
            AssetDatabase.SaveAssets();
            
            // Refresh the inspector to show updated values
            property.serializedObject.Update();
        }
    }
    #endif
}