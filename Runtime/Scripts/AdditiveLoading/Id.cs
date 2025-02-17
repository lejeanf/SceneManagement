#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;

namespace jeanf.SceneManagment
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

            // Draw the text field for the "id" string
            idProperty.stringValue = EditorGUI.TextField(fieldRect, idProperty.stringValue);

            // Draw the "Generate" button
            if (GUI.Button(buttonRect, "Generate"))
            {
                idProperty.stringValue = System.Guid.NewGuid().ToString();
            }

            // End property drawing
            EditorGUI.EndProperty();
        }
    }

    #endif
}