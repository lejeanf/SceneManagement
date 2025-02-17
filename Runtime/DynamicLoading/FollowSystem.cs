using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Streaming.SceneManagement.Common
{
    [BurstCompile]
    public partial class  FollowSystem : SystemBase
    {
        [BurstCompile]
        protected override void OnUpdate()
        {
            if (Camera.main == null) return;
            float3 cameraPosition = Camera.main.transform.position;

            var followJob = new FollowJob 
            { 
                CameraPosition = cameraPosition 
            };

            this.Dependency = followJob.Schedule(this.Dependency);
        }
        
        [BurstCompile]
        private partial struct FollowJob : IJobEntity
        {
            public float3 CameraPosition;

            public void Execute(ref LocalTransform transform)
            {
                transform.Position = CameraPosition;
            }
        }
    }   
    public struct FollowComponent : IComponentData { }
}
