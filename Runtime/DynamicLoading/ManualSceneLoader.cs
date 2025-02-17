using System.Collections.Generic;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Scenes;
using UnityEngine;

namespace uvs.Loading
{
    public class ManualSceneLoader : MonoBehaviour
    {
        [SerializeField] private List<EntitySceneReference> sceneReferences = new List<EntitySceneReference>();
        private readonly Dictionary<string, EntitySceneReference> _sceneReferencesDictionary = new Dictionary<string, EntitySceneReference>();
        private List<Entity> _loadedScenes = new List<Entity>();

        private void Awake()
        {
            /*
            foreach (var scene in sceneReferences)
            {
                var sceneName = scene.Id.GlobalId.AssetGUID;
                Debug.Log($"adding {sceneName} to sceneReferencesDictionary");
                _sceneReferencesDictionary.Add(sceneName ,scene);
            }
            */
        }

        public void LoadScene(int nb)
        {
            Debug.Log($"Load for {nb} requested");
            if (_loadedScenes.Count > 0) UnloadAll();
            var loadHandle = SceneSystem.LoadSceneAsync(World.DefaultGameObjectInjectionWorld.Unmanaged, sceneReferences[nb]);
            _loadedScenes.Add(loadHandle);
        }
        public void LoadScene(string sceneName)
        {
            Debug.Log($"Load for {sceneName} requested");
            if (_loadedScenes.Count > 0) UnloadAll();
            var loadHandle = SceneSystem.LoadSceneAsync(World.DefaultGameObjectInjectionWorld.Unmanaged, _sceneReferencesDictionary[sceneName]);
            _loadedScenes.Add(loadHandle);
        }
        public void UnloadLoadScene(Entity scene)
        {
            SceneSystem.UnloadScene(World.DefaultGameObjectInjectionWorld.Unmanaged, scene);
        }

        public void UnloadAll()
        {
            foreach (var scene in _loadedScenes)
            {
                UnloadLoadScene(scene);
            }
        }
    }
}

