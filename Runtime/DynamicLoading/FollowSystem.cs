using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [BurstCompile]
    public partial struct FollowSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<FollowComponent>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (Camera.main == null) return;
            
            // Cache the camera position to avoid repeated calls
            float3 cameraPosition = Camera.main.transform.position;

            foreach (var (transform, _) in SystemAPI.Query<RefRW<LocalTransform>, RefRO<FollowComponent>>())
            {
                transform.ValueRW.Position = cameraPosition;
            }
        }
        
        [BurstCompile]
        public void OnDestroy(ref SystemState state) { }
    }
    
    // Keep the component definition the same
    public struct FollowComponent : IComponentData { }
}