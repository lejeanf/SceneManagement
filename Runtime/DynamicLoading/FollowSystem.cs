using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct FollowSystem : ISystem
    {
        private float3 _lastCameraPosition;
        private const float MIN_POSITION_CHANGE = 0.1f;
        private const float MIN_POSITION_CHANGE_SQ = MIN_POSITION_CHANGE * MIN_POSITION_CHANGE;
        private bool _isInitialized;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<FollowComponent>();
            _lastCameraPosition = float3.zero;
            _isInitialized = false;
        }
        
        public void OnUpdate(ref SystemState state)
        {
            if (Camera.main == null) return;
            
            var currentCameraPosition = (float3)Camera.main.transform.position;
            
            if (!_isInitialized)
            {
                _lastCameraPosition = currentCameraPosition;
                _isInitialized = true;
                
                state.Dependency = new FollowJob
                {
                    CameraPosition = currentCameraPosition
                }.ScheduleParallel(state.Dependency);
                
                return;
            }
            
            float distanceSq = math.distancesq(_lastCameraPosition, currentCameraPosition);
            
            if (distanceSq >= MIN_POSITION_CHANGE_SQ)
            {
                _lastCameraPosition = currentCameraPosition;
                
                state.Dependency = new FollowJob
                {
                    CameraPosition = currentCameraPosition
                }.ScheduleParallel(state.Dependency);
            }
        }
        
        [BurstCompile]
        public void OnDestroy(ref SystemState state) { }
        
        [BurstCompile]
        private partial struct FollowJob : IJobEntity
        {
            [ReadOnly]
            public float3 CameraPosition;
            
            public void Execute(ref LocalTransform transform, in FollowComponent _)
            {
                transform.Position = CameraPosition;
            }
        }
    }
    
    public struct FollowComponent : IComponentData { }
}