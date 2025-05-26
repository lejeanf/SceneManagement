using Unity.Entities;
using UnityEngine;

namespace jeanf.scenemanagement
{
    public class RelevantAuthoring : MonoBehaviour
    {
        private class Baker : Baker<RelevantAuthoring>
        {
            public override void Bake(RelevantAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<Relevant>(entity);
            }
        }
    }

    public struct Relevant : IComponentData
    {
    }
}
