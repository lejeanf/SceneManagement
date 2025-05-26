using Unity.Entities;
using UnityEngine;

namespace jeanf.scenemanagement
{
    public class PlayerAuthoring : MonoBehaviour
    {
        class Baker : Baker<PlayerAuthoring>
        {
            public override void Bake(PlayerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<Player>(entity);
                AddComponent<Relevant>(entity);
                AddComponent<FollowComponent>(entity);
            }
        }
    }

    public struct Player : IComponentData
    {
    }
}