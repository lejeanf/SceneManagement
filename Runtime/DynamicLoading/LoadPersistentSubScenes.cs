using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Unity.Entities;
using Unity.Scenes;
using UnityEngine;

namespace jeanf.scenemanagement
{
    public class LoadPersistentSubScenes : MonoBehaviour
    {
        [SerializeField] private bool isLoadSequential = false;
        [Header("The order of subScenes will define their order of load")]
        public List<SubScene> subScenes;
        public delegate void PersistentLoadingCompleteDelegate(bool status);

        public static PersistentLoadingCompleteDelegate PersistentLoadingComplete;

        private List<Entity> listOfCreatedEntities = new List<Entity>();
        
        private WorldUnmanaged world;
        private async void OnEnable()
        {   
            PersistentLoadingComplete?.Invoke(false);
            await UniTask.Delay(100);
            world = World.DefaultGameObjectInjectionWorld.Unmanaged;

            // Subscenes give no percentage of their own, but the LIST is known — so
            // completed/total is real progress, not a guess.
            var total = Mathf.Max(1, subScenes.Count);
            var completed = 0;
            LoadingInformation.ReportProgress(0f);

            if (isLoadSequential)
            {
                foreach (var s in subScenes)
                {
                    await LoadSubScene(s, world);
                    LoadingInformation.ReportProgress((float)++completed / total);
                }
            }
            else
            {
                var loadTasks = new List<UniTask>();
                foreach (var s in subScenes)
                {
                    loadTasks.Add(LoadSubSceneTracked(s, world, () =>
                        LoadingInformation.ReportProgress((float)System.Threading.Interlocked.Increment(ref completed) / total)));
                }
                await UniTask.WhenAll(loadTasks);
            }

            LoadingInformation.LoadingStatus?.Invoke($"All subScenes loaded successfully.");
            LoadingInformation.ReportProgress(1f);
            PersistentLoadingComplete?.Invoke(true);
        }

        private void OnDestroy()
        {
            if (World.DefaultGameObjectInjectionWorld == null || !World.DefaultGameObjectInjectionWorld.IsCreated)
                return;

            foreach (var entity in listOfCreatedEntities.Where(entity => world.IsCreated && entity != Entity.Null))
            {
                try
                {
                    SceneSystem.UnloadScene(world, entity);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        /// <summary>LoadSubScene plus a completion tick, so the parallel path can report
        /// progress as each subscene lands (order of completion is irrelevant — it counts).</summary>
        private async UniTask LoadSubSceneTracked(SubScene subScene, WorldUnmanaged world, Action onDone)
        {
            await LoadSubScene(subScene, world);
            onDone?.Invoke();
        }

        private async UniTask LoadSubScene(SubScene subScene, WorldUnmanaged world)
        {
            LoadingInformation.LoadingStatus?.Invoke($"Loading subScene: {subScene.name}.");
            var guid = subScene.SceneGUID;
            Entity subSceneEntity;
            bool useSections = subScene.GetComponent<UseSectionStreaming>() != null;

            if (useSections)
            {
                subSceneEntity = SceneSystem.LoadSceneAsync(world, guid);
                listOfCreatedEntities.Add(subSceneEntity);

                var entityManager = world.EntityManager;
                while (!entityManager.Exists(subSceneEntity))
                {
                    await UniTask.Yield();
                }

                await UniTask.Yield();

                LoadingInformation.LoadingStatus?.Invoke($"SubScene {subScene.name} ready (sections managed by SectionRangeSystem).");
            }
            else
            {
                subSceneEntity = SceneSystem.LoadSceneAsync(world, guid);
                listOfCreatedEntities.Add(subSceneEntity);

                while (!SceneSystem.IsSceneLoaded(world, subSceneEntity))
                {
                    await UniTask.Yield();
                }

                LoadingInformation.LoadingStatus?.Invoke($"SubScene {subScene.name} loaded successfully.");
            }
        }
    }
}

