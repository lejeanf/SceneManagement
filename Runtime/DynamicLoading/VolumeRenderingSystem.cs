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
        private NativeHashSet<int> selectedGameObjectsIds;
        private EntityQuery volumeQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<Volume>();
            
            volumeQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<Volume>(),
                ComponentType.ReadOnly<LocalToWorld>(),
                ComponentType.ReadOnly<StreamingGO>()
            );
            
            selectedGameObjectsIds = new NativeHashSet<int>(100, Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            
            if (selectedGameObjectsIds.IsCreated)
            {
                selectedGameObjectsIds.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            
            if (!selectedGameObjectsIds.IsCreated)
            {
                selectedGameObjectsIds = new NativeHashSet<int>(100, Allocator.Persistent);
            }
            
            selectedGameObjectsIds.Clear();
            
            #if UNITY_EDITOR
            GameObject[] selectedObjs = Selection.gameObjects;
            if (selectedObjs != null && selectedObjs.Length > 0)
            {
                foreach (var selected in selectedObjs)
                {
                    if (selected != null)
                    {
                        selectedGameObjectsIds.Add(selected.GetInstanceID());
                    }
                }
            }
            #endif

            var dependency = state.Dependency;
            
            dependency = new DrawBBJob
            {
                selectedGOIds = selectedGameObjectsIds,
                checkGO = true,
                color = Color.yellow
            }.ScheduleParallel(dependency);

            dependency = new DrawBBSceneJob
            {
                selectedGOIds = selectedGameObjectsIds,
                checkGO = true,
                color = Color.green,
                localToWorldLookUp = SystemAPI.GetComponentLookup<LocalToWorld>(true),
                streamingVolumeLookUp = SystemAPI.GetComponentLookup<Volume>(true),
            }.ScheduleParallel(dependency);
            
            state.Dependency = dependency;
        }

        public static void DrawAABB(in float3 pos, in float3 min, in float3 max, in Color color)
        {
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