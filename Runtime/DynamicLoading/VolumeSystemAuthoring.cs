using Unity.Entities;
using UnityEngine;

namespace jeanf.scenemanagement
{
    public class VolumeSystemAuthoring : MonoBehaviour
    {
        public float preloadHorizontalMultiplier = 2.0f;
        public float preloadVerticalMultiplier = 1.0f;
        public int maxOperationsPerFrame = 2;

        class Baker : Baker<VolumeSystemAuthoring>
        {
            public override void Bake(VolumeSystemAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new VolumeSystemConfig
                {
                    PreloadHorizontalMultiplier = authoring.preloadHorizontalMultiplier,
                    PreloadVerticalMultiplier = authoring.preloadVerticalMultiplier,
                    MaxOperationsPerFrame = authoring.maxOperationsPerFrame
                });
            }
        }
    }

    public struct VolumeSystemConfig : IComponentData
    {
        public float PreloadHorizontalMultiplier;
        public float PreloadVerticalMultiplier;
        public int MaxOperationsPerFrame;
    }
}