using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct FollowSystem : ISystem
    {
        private float3 _currentCameraPosition;
        private float3 _lastCameraPosition;
        private const float MIN_POSITION_CHANGE = 0.1f;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<FollowComponent>();
            _lastCameraPosition = float3.zero;
        }
        
        public void OnUpdate(ref SystemState state)
        {
            if (Camera.main == null) return;
            
            _currentCameraPosition = Camera.main.transform.position;
            
            bool isFirstUpdate = math.all(math.abs(_lastCameraPosition) < 0.001f);
            float distanceSq = math.distancesq(_lastCameraPosition, _currentCameraPosition);
            
            if (isFirstUpdate || distanceSq >= MIN_POSITION_CHANGE * MIN_POSITION_CHANGE)
            {
                _lastCameraPosition = _currentCameraPosition;
                
                new FollowJob
                {
                    CameraPosition = _currentCameraPosition
                }.ScheduleParallel(state.Dependency).Complete();
                
                if (isFirstUpdate)
                {
                    UnityEngine.Debug.Log($"FollowSystem: Initial position update at {_currentCameraPosition}");
                }
            }
        }
        
        [BurstCompile]
        public void OnDestroy(ref SystemState state) { }
        
        [BurstCompile]
        private partial struct FollowJob : IJobEntity
        {
            public float3 CameraPosition;
            
            public void Execute(ref LocalTransform transform, in FollowComponent _)
            {
                transform.Position = CameraPosition;
            }
        }
    }
    
    public struct FollowComponent : IComponentData { }
}