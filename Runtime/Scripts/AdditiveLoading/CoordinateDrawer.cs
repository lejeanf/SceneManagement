#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace jeanf.SceneManagment
{
    [CustomPropertyDrawer(typeof(Coordinate))]
    public class CoordinateDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Start drawing the property
            EditorGUI.BeginProperty(position, label, property);

            // Create a label
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);

            // Reserve space for the fields
            var width = position.width / 2; // Half width for each field
            var xRect = new Rect(position.x, position.y, width - 5, position.height);
            var yRect = new Rect(position.x + width + 5, position.y, width - 5, position.height);

            // Get x and y properties
            var xProperty = property.FindPropertyRelative("x");
            var yProperty = property.FindPropertyRelative("y");

            // Draw draggable IntFields for x and y
            xProperty.intValue = EditorGUI.IntField(xRect, xProperty.intValue);
            yProperty.intValue = EditorGUI.IntField(yRect, yProperty.intValue);

            // End drawing the property
            EditorGUI.EndProperty();
        }
    }
}
#endif