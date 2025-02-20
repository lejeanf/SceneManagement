using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace jeanf.scenemanagement
{
    public class SceneLoader : MonoBehaviour
    {
        public bool isDebug = false;
        private CancellationTokenSource _queueCts;
        [SerializeField] private int maxConcurrentLoads = 2; 
        public delegate void IsLoadingDelegate(bool loadingState);
        public static IsLoadingDelegate IsLoading;
        
        public delegate void LoadScene(string sceneName);
        public LoadScene LoadSceneRequest;
        public LoadScene UnLoadSceneRequest;
        
        private record SceneOperation(SceneOperationType Type, string SceneName)
        {
            public SceneOperationType Type { get; } = Type;
            public string SceneName { get; } = SceneName;
        }

        private readonly ConcurrentQueue<SceneOperation> _sceneQueue = new();
        private readonly HashSet<string> _loadedScenes = new();
        private readonly HashSet<string> _processingScenes = new();
        private bool _isProcessingQueue = false;

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();

        private void Subscribe()
        {
            LoadSceneRequest += QueueLoadScene;
            UnLoadSceneRequest += QueueUnloadScene;
        }

        private void Unsubscribe()
        {
            LoadSceneRequest -= QueueLoadScene;
            UnLoadSceneRequest -= QueueUnloadScene;
        }
        
        private enum SceneOperationType { Load, Unload }

        private void QueueLoadScene(string sceneName)
        {
            _sceneQueue.Enqueue(new SceneOperation(SceneOperationType.Load, sceneName));
            ProcessQueue().Forget();
        }

        private void QueueUnloadScene(string sceneName)
        {
            _sceneQueue.Enqueue(new SceneOperation(SceneOperationType.Unload, sceneName));
            ProcessQueue().Forget();
        }

        private async UniTaskVoid ProcessQueue()
        {
            if (_isProcessingQueue) return;
            _isProcessingQueue = true;
            
            _queueCts?.Cancel();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;
            
            IsLoading?.Invoke(true);

            try
            {
                while (_sceneQueue.Count > 0 && !token.IsCancellationRequested)
                {
                    var operations = new List<UniTask>();
                    
                    // Process multiple operations in parallel up to maxConcurrentLoads
                    while (operations.Count < maxConcurrentLoads && _sceneQueue.TryDequeue(out var operation))
                    {
                        if (_processingScenes.Contains(operation.SceneName))
                            continue;
                            
                        _processingScenes.Add(operation.SceneName);
                        
                        var task = operation.Type switch
                        {
                            SceneOperationType.Load => LoadSceneAsync(operation.SceneName, token),
                            SceneOperationType.Unload => UnloadSceneAsync(operation.SceneName, token),
                            _ => throw new ArgumentOutOfRangeException()
                        };
                        
                        operations.Add(task);
                    }

                    if (operations.Count > 0)
                    {
                        // Wait for all current operations to complete
                        await UniTask.WhenAll(operations);
                    }
                    
                    // Small delay to prevent overwhelming the system
                    await UniTask.Yield();
                }
            }
            finally
            {
                _isProcessingQueue = false;
                IsLoading?.Invoke(false);
            }
        }

        private async UniTask LoadSceneAsync(string sceneName, CancellationToken cancellationToken)
        {
            try
            {
                var loadOperation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                loadOperation!.allowSceneActivation = true;
                await loadOperation.ToUniTask(cancellationToken: cancellationToken);
                _loadedScenes.Add(sceneName);
            }
            finally
            {
                _processingScenes.Remove(sceneName);
            }
        }

        private async UniTask UnloadSceneAsync(string sceneName, CancellationToken cancellationToken)
        {
            try
            {
                var unloadOperation = SceneManager.UnloadSceneAsync(sceneName);
                await unloadOperation.ToUniTask(cancellationToken: cancellationToken);
                _loadedScenes.Remove(sceneName);
            }
            finally
            {
                _processingScenes.Remove(sceneName);
            }
        }
    }
}