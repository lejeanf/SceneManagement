using Unity.Entities;
using UnityEngine;

namespace Streaming.SceneManagement.Common
{
    // Authoring class to mark an entity as relevant. This is used in samples where the position of an entity
    // (e.g. the player or camera) indicates which scene/sections to load.
    public class PlayerAuthoring : MonoBehaviour
    {
        class Baker : Baker<PlayerAuthoring>
        {
            public override void Bake(PlayerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<Player>(entity);
            }
        }
    }

    public struct Player : IComponentData
    {
    }
}