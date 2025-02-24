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
        // Store camera position for use in Burst-compiled job
        private float3 _currentCameraPosition;
        private float3 _lastCameraPosition;
        private const float MIN_POSITION_CHANGE = 0.1f;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<FollowComponent>();
            _lastCameraPosition = float3.zero;
        }
        
        // This method cannot be Burst-compiled because it uses managed Camera type
        public void OnUpdate(ref SystemState state)
        {
            // Get camera position - this must be done on the main thread
            if (Camera.main == null) return;
            
            // Get current camera position
            _currentCameraPosition = Camera.main.transform.position;
            
            // Check if this is the first update or if camera has moved significantly
            bool isFirstUpdate = math.all(math.abs(_lastCameraPosition) < 0.001f);
            float distanceSq = math.distancesq(_lastCameraPosition, _currentCameraPosition);
            
            if (isFirstUpdate || distanceSq >= MIN_POSITION_CHANGE * MIN_POSITION_CHANGE)
            {
                // Update cached position
                _lastCameraPosition = _currentCameraPosition;
                
                // Schedule the Burst-compiled job to update transforms
                new FollowJob
                {
                    CameraPosition = _currentCameraPosition
                }.ScheduleParallel(state.Dependency).Complete();
                
                // Log for debugging
                if (isFirstUpdate)
                {
                    UnityEngine.Debug.Log($"FollowSystem: Initial position update at {_currentCameraPosition}");
                }
            }
        }
        
        [BurstCompile]
        public void OnDestroy(ref SystemState state) { }
        
        // This job can be Burst-compiled for better performance
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