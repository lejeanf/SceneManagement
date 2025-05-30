using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial struct VolumeRenderingSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<Volume>();
        }

        public void OnUpdate(ref SystemState state)
        {
            NativeHashSet<int> selectedGameObjectsIds = new NativeHashSet<int>(100, Allocator.TempJob);
    
#if UNITY_EDITOR
            // Use Selection.objects instead of Selection.gameObjects to avoid array allocation
            // and cache the count to avoid multiple property calls
            var selectedObjects = Selection.objects;
            int selectionCount = Selection.count;
    
            // Alternative approach: Use a static list to avoid allocations
            // This requires adding a static field to your class:
            // private static readonly List<GameObject> tempGameObjectList = new List<GameObject>();
    
            for (int i = 0; i < selectionCount; i++)
            {
                var selectedObj = selectedObjects[i];
                if (selectedObj is GameObject gameObject)
                {
                    selectedGameObjectsIds.Add(gameObject.GetInstanceID());
                }
            }
#endif

            state.Dependency = new DrawBBJob
            {
                selectedGOIds = selectedGameObjectsIds,
                checkGO = true,
                color = Color.yellow
            }.ScheduleParallel(state.Dependency);

            state.Dependency = new DrawBBSceneJob
            {
                selectedGOIds = selectedGameObjectsIds,
                checkGO = true,
                color = Color.green,
                localToWorldLookUp = SystemAPI.GetComponentLookup<LocalToWorld>(true),
                streamingVolumeLookUp = SystemAPI.GetComponentLookup<Volume>(true),
            }.ScheduleParallel(state.Dependency);

            selectedGameObjectsIds.Dispose(state.Dependency);
        }

        public static void DrawAABB(in float3 pos, in float3 min, in float3 max, in Color color)
        {
            // Draw a bounding box
            Debug.DrawLine(pos + new float3(min.x, min.y, min.z), pos + new float3(max.x, min.y, min.z), color);
            Debug.DrawLine(pos + new float3(min.x, max.y, min.z), pos + new float3(max.x, max.y, min.z), color);
            Debug.DrawLine(pos + new float3(min.x, min.y, min.z), pos + new float3(min.x, max.y, min.z), color);
            Debug.DrawLine(pos + new float3(max.x, min.y, min.z), pos + new float3(max.x, max.y, min.z), color);

            Debug.DrawLine(pos + new float3(min.x, min.y, max.z), pos + new float3(max.x, min.y, max.z), color);
            Debug.DrawLine(pos + new float3(min.x, max.y, max.z), pos + new float3(max.x, max.y, max.z), color);
            Debug.DrawLine(pos + new float3(min.x, min.y, max.z), pos + new float3(min.x, max.y, max.z), color);
            Debug.DrawLine(pos + new float3(max.x, min.y, max.z), pos + new float3(max.x, max.y, max.z), color);

            Debug.DrawLine(pos + new float3(min.x, min.y, min.z), pos + new float3(min.x, min.y, max.z), color);
            Debug.DrawLine(pos + new float3(max.x, min.y, min.z), pos + new float3(max.x, min.y, max.z), color);
            Debug.DrawLine(pos + new float3(min.x, max.y, min.z), pos + new float3(min.x, max.y, max.z), color);
            Debug.DrawLine(pos + new float3(max.x, max.y, min.z), pos + new float3(max.x, max.y, max.z), color);
        }

        [BurstCompile]
        public partial struct DrawBBJob : IJobEntity
        {
            [ReadOnly] public NativeHashSet<int> selectedGOIds;
            public bool checkGO;
            public Color color;

            public void Execute(in Volume meshBB, in LocalToWorld t, in StreamingGO go)
            {
                if (checkGO && selectedGOIds.IsCreated && !selectedGOIds.Contains(go.InstanceID))
                {
                    return;
                }

                var max = meshBB.Scale / 2f;
                var min = -max;

                DrawAABB(t.Position, min, max, color);
            }
        }

        [BurstCompile]
        public partial struct DrawBBSceneJob : IJobEntity
        {
            [ReadOnly] public NativeHashSet<int> selectedGOIds;
            [ReadOnly] public ComponentLookup<Volume> streamingVolumeLookUp;
            [ReadOnly] public ComponentLookup<LocalToWorld> localToWorldLookUp;
            public bool checkGO;
            public Color color;

            public void Execute(in DynamicBuffer<VolumeBuffer> volumes, in StreamingGO go)
            {
                if (checkGO && selectedGOIds.IsCreated && !selectedGOIds.Contains(go.InstanceID))
                {
                    return;
                }

                foreach (var volume in volumes)
                {
                    var volumeEntity = volume.volumeEntity;

                    var max = streamingVolumeLookUp[volumeEntity].Scale / 2f;
                    var min = -max;

                    DrawAABB(localToWorldLookUp[volumeEntity].Position, min, max, color);
                }
            }
        }
    }
}
