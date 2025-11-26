using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace jeanf.SceneManagement
{
    public class SectionRangeAuthoring : MonoBehaviour
    {
        public float[] sectionDistances = new float[] { 50f, 100f, 200f };
        public int[] sectionIndices = new int[] { 0, 1, 2 };

        class Baker : Baker<SectionRangeAuthoring>
        {
            public override void Bake(SectionRangeAuthoring authoring)
            {
                if (authoring.sectionDistances.Length != authoring.sectionIndices.Length)
                {
                    Debug.LogError($"SectionRangeAuthoring: sectionDistances and sectionIndices must have the same length on {authoring.gameObject.name}");
                    return;
                }

                if (authoring.sectionDistances.Length == 0)
                {
                    Debug.LogError($"SectionRangeAuthoring: Must have at least one section level on {authoring.gameObject.name}");
                    return;
                }

                var entity = GetEntity(TransformUsageFlags.None);
                
                AddComponent(entity, new SectionRange
                {
                    Center = GetComponent<Transform>().position,
                    SectionCount = authoring.sectionDistances.Length
                });

                var sectionBuffer = AddBuffer<SectionLevelData>(entity);
                for (int i = 0; i < authoring.sectionDistances.Length; i++)
                {
                    sectionBuffer.Add(new SectionLevelData
                    {
                        MaxDistance = authoring.sectionDistances[i],
                        SectionIndex = authoring.sectionIndices[i]
                    });
                }
            }
        }

        private void OnValidate()
        {
            if (sectionDistances.Length != sectionIndices.Length)
            {
                Debug.LogWarning($"SectionRangeAuthoring: sectionDistances and sectionIndices should have the same length on {gameObject.name}");
            }

            for (int i = 1; i < sectionDistances.Length; i++)
            {
                if (sectionDistances[i] <= sectionDistances[i - 1])
                {
                    Debug.LogWarning($"SectionRangeAuthoring: Section distances should be in ascending order on {gameObject.name}");
                }
            }
        }
    }

    public struct SectionRange : IComponentData
    {
        public float3 Center;
        public int SectionCount;
    }

    public struct SectionLevelData : IBufferElementData
    {
        public float MaxDistance;
        public int SectionIndex;
    }

    #if UNITY_EDITOR
    [CustomEditor(typeof(SectionRangeAuthoring))]
    public class SectionRangeAuthoringEditor : Editor
    {
        private void OnSceneGUI()
        {
            SectionRangeAuthoring sectionRange = (SectionRangeAuthoring)target;
            Transform transform = sectionRange.transform;

            for (int i = 0; i < sectionRange.sectionDistances.Length; i++)
            {
                float t = i / (float)Mathf.Max(1, sectionRange.sectionDistances.Length - 1);
                Color color = Color.Lerp(Color.green, Color.red, t);
                Handles.color = color;
                
                Handles.DrawWireDisc(transform.position, Vector3.up, sectionRange.sectionDistances[i]);
                
                Vector3 labelPos = transform.position + Vector3.right * sectionRange.sectionDistances[i];
                Handles.Label(labelPos, $"Level{i} (Section {sectionRange.sectionIndices[i]})\nDist: {sectionRange.sectionDistances[i]}m");
            }

            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < sectionRange.sectionDistances.Length; i++)
            {
                float t = i / (float)Mathf.Max(1, sectionRange.sectionDistances.Length - 1);
                Color color = Color.Lerp(Color.green, Color.red, t);
                Handles.color = color;
                
                float newDistance = Handles.RadiusHandle(Quaternion.identity, transform.position, sectionRange.sectionDistances[i]);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(sectionRange, "Change Section Distance");
                    sectionRange.sectionDistances[i] = newDistance;
                    EditorGUI.BeginChangeCheck();
                }
            }
        }
    }
    #endif
}
