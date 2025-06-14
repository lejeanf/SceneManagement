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
    
        private bool _isPersistentLoadingComplete = false;
        public delegate void PersistentLoadingCompleteDelegate(bool status);

        public static PersistentLoadingCompleteDelegate PersistentLoadingComplete;
        private async void OnEnable()
        {   
            PersistentLoadingComplete?.Invoke(_isPersistentLoadingComplete = false);
            await UniTask.Delay(100);
            var world = World.DefaultGameObjectInjectionWorld.Unmanaged;
            
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
            PersistentLoadingComplete?.Invoke(_isPersistentLoadingComplete = true);
        }
        
        private async UniTask LoadSubScene(SubScene subScene, WorldUnmanaged world)
        {
            LoadingInformation.LoadingStatus?.Invoke($"Loading subScene: {subScene.SceneName}.");
            var guid = subScene.SceneGUID;
            var subSceneEntity = SceneSystem.LoadSceneAsync(world, guid);
        
            while (!SceneSystem.IsSceneLoaded(world, subSceneEntity))
            {
                await UniTask.Yield();
            }
            LoadingInformation.LoadingStatus?.Invoke($"SubScene {subScene.SceneName} loaded successfully.");
        }
    }
}

