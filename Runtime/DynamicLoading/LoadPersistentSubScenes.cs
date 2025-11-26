using System;
using System.Collections.Generic;
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
            
            if (isLoadSequential)
            {
                foreach (var s in subScenes)
                {
                    await LoadSubScene(s, world);
                }
            }
            else
            {
                var loadTasks = new List<UniTask>();
                foreach (var s in subScenes)
                {
                    loadTasks.Add(LoadSubScene(s, world));
                }
                await UniTask.WhenAll(loadTasks);
            }
        
            LoadingInformation.LoadingStatus?.Invoke($"All subScenes loaded successfully.");
            PersistentLoadingComplete?.Invoke(true);
        }

        private void OnDestroy()
        {
            foreach (var entity in listOfCreatedEntities)
            {
                if(world.IsCreated && entity != Entity.Null) SceneSystem.UnloadScene(world, entity);
            }
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

