using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace jeanf.scenemanagement
{
    public class VolumeAuthoring : MonoBehaviour
    {
        public bool isDebug = false;
        public class Baker : Baker<VolumeAuthoring>
        {
            public override void Bake(VolumeAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Renderable);

                // Add the Volume component
                AddComponent(entity, new Volume
                {
                    Scale = GetComponent<Transform>().localScale
                });

                // Add the StreamingGO component
                AddComponent(entity, new StreamingGO
                {
                    InstanceID = authoring.gameObject.GetInstanceID()
                });
                if(authoring.isDebug) AddComponent<VolumeDebugTag>(entity);
            }
        }
    }

    public struct Volume : IComponentData
    {
        public float3 Scale;
        public FixedString128Bytes ZoneId;
    }

    public struct StreamingGO : IComponentData
    {
        public int InstanceID;
    }
}
