using Unity.Entities;
using Unity.Collections;
using Unity.Transforms;
using Unity.Mathematics;
using UnityEngine;

namespace jeanf.scenemanagement
{
    // Add this component to your VolumeSets for debugging
    public struct VolumeDebugTag : IComponentData { }

    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial class VolumeDebugSystem : SystemBase
    {
        private EntityQuery _relevantQuery;
        private EntityQuery _volumeSetQuery;
        private EntityQuery _volumeQuery;
        
        private float _lastLogTime = 0f;
        private const float LOG_INTERVAL = 2.0f; // Log every 2 seconds
        
        protected override void OnCreate()
        {
            _relevantQuery = GetEntityQuery(typeof(Relevant), typeof(LocalToWorld));
            _volumeSetQuery = GetEntityQuery(typeof(LevelInfo), typeof(VolumeBuffer));
            _volumeQuery = GetEntityQuery(typeof(Volume), typeof(LocalToWorld));
            
            // Only enable once you add the debug tag to entities you want to debug
            RequireForUpdate<VolumeDebugTag>();
        }
        
        protected override void OnUpdate()
        {
            // Only log periodically to avoid spamming
            if (SystemAPI.Time.ElapsedTime - _lastLogTime < LOG_INTERVAL) return;
            _lastLogTime = (float)SystemAPI.Time.ElapsedTime;
            
            // Get the player position
            var playerPositions = _relevantQuery.ToComponentDataArray<LocalToWorld>(Allocator.Temp);
            if (playerPositions.Length == 0)
            {
                Debug.LogWarning("No relevant entity (player) found!");
                playerPositions.Dispose();
                return;
            }
            
            var playerPosition = playerPositions[0].Position;
            playerPositions.Dispose();
            
            Debug.Log($"==== VOLUME DEBUG [{SystemAPI.Time.ElapsedTime:F1}s] ====");
            Debug.Log($"Player position: {playerPosition}");
            
            // Get all volume sets
            var volumeSets = _volumeSetQuery.ToEntityArray(Allocator.Temp);
            var volumeBufferLookup = GetBufferLookup<VolumeBuffer>(true);
            var levelInfoLookup = GetComponentLookup<LevelInfo>(true);
            
            Debug.Log($"Total volume sets: {volumeSets.Length}");
            
            // Check which volumes contain the player
            var volumeEntities = _volumeQuery.ToEntityArray(Allocator.Temp);
            var volumeLookup = GetComponentLookup<Volume>(true);
            var volumeTransformLookup = GetComponentLookup<LocalToWorld>(true);
            
            // Log active and pending scenes
            var activeScenes = new NativeHashSet<Entity>(10, Allocator.Temp);
            var preloadingScenes = new NativeHashSet<Entity>(10, Allocator.Temp);
            
            foreach (var volumeEntity in volumeEntities)
            {
                if (!volumeTransformLookup.HasComponent(volumeEntity) || 
                    !volumeLookup.HasComponent(volumeEntity)) continue;
                    
                var volumeTransform = volumeTransformLookup[volumeEntity];
                var volumeData = volumeLookup[volumeEntity];
                var range = volumeData.Scale / 2f;
                
                var distance = math.abs(playerPosition - volumeTransform.Position);
                bool isInside = distance.x <= range.x && 
                               distance.y <= range.y && 
                               distance.z <= range.z;
                
                // Find which volume set this belongs to
                for (int i = 0; i < volumeSets.Length; i++)
                {
                    var volumeSet = volumeSets[i];
                    if (!volumeBufferLookup.HasBuffer(volumeSet)) continue;
                    
                    var buffer = volumeBufferLookup[volumeSet];
                    bool foundInSet = false;
                    
                    foreach (var volume in buffer)
                    {
                        if (volume.volumeEntity.Equals(volumeEntity))
                        {
                            foundInSet = true;
                            break;
                        }
                    }
                    
                    if (foundInSet)
                    {
                        if (isInside)
                        {
                            activeScenes.Add(volumeSet);
                            Debug.Log($"Volume {volumeEntity.Index} CONTAINS player - should activate set {volumeSet.Index}");
                        }
                        else
                        {
                            // Check if it's nearby for preloading
                            const float preloadHMult = 1.0f;
                            const float preloadVMult = 1.0f;
                            
                            bool isNearHorizontally = distance.x <= range.x * preloadHMult && 
                                                     distance.z <= range.z * preloadHMult;
                            bool isNearVertically = distance.y <= range.y * preloadVMult;
                            
                            if (isNearHorizontally && isNearVertically)
                            {
                                preloadingScenes.Add(volumeSet);
                                Debug.Log($"Volume {volumeEntity.Index} is NEAR player - should preload set {volumeSet.Index}");
                            }
                        }
                    }
                }
            }
            
            // Log actual scene status
            Debug.Log($"Expected active scenes: {activeScenes.Count}");
            foreach (var scene in activeScenes)
            {
                if (levelInfoLookup.HasComponent(scene))
                {
                    var levelInfo = levelInfoLookup[scene];
                    string status = levelInfo.runtimeEntity != Entity.Null ? "LOADED" : "NOT LOADED";
                    Debug.Log($"  Scene {scene.Index}: {status}");
                }
            }
            
            Debug.Log($"Expected preloading scenes: {preloadingScenes.Count}");
            foreach (var scene in preloadingScenes)
            {
                if (levelInfoLookup.HasComponent(scene))
                {
                    var levelInfo = levelInfoLookup[scene];
                    string status = levelInfo.runtimeEntity != Entity.Null ? "LOADED" : "NOT LOADED";
                    Debug.Log($"  Scene {scene.Index}: {status}");
                }
            }
            
            // Cleanup
            volumeSets.Dispose();
            volumeEntities.Dispose();
            activeScenes.Dispose();
            preloadingScenes.Dispose();
        }
    }
}