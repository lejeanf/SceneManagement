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
    
    // Add method to unload all scenes
    public delegate void UnloadAllScenesDelegate();
    public UnloadAllScenesDelegate UnloadAllScenesRequest;
        
        public delegate void FlushScenesDelegate();
        public FlushScenesDelegate FlushScenesRequest;
        
        private record SceneOperation(SceneOperationType Type, string SceneName)
        {
            public SceneOperationType Type { get; } = Type;
            public string SceneName { get; } = SceneName;
        }

        private readonly ConcurrentQueue<SceneOperation> _loadQueue = new();
        private readonly ConcurrentQueue<SceneOperation> _unloadQueue = new();
        private readonly HashSet<string> _loadedScenes = new();
        private readonly HashSet<string> _processingScenes = new();
        private bool _isProcessingLoadQueue = false;
        private bool _isProcessingUnloadQueue = false;

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();

        private void Subscribe()
        {
            LoadSceneRequest += QueueLoadScene;
            UnLoadSceneRequest += QueueUnloadScene;
            UnloadAllScenesRequest += QueueUnloadAllScenes;
        }

        private void Unsubscribe()
        {
            LoadSceneRequest -= QueueLoadScene;
            UnLoadSceneRequest -= QueueUnloadScene;
            UnloadAllScenesRequest -= QueueUnloadAllScenes;
        }
        
        private enum SceneOperationType { Load, Unload }

        private void QueueLoadScene(string sceneName)
        {
            _loadQueue.Enqueue(new SceneOperation(SceneOperationType.Load, sceneName));
            ProcessLoadQueue().Forget();
        }

        private void QueueUnloadScene(string sceneName)
        {
            _unloadQueue.Enqueue(new SceneOperation(SceneOperationType.Unload, sceneName));
            ProcessUnloadQueue().Forget();
        }
        
        private void QueueUnloadAllScenes()
        {
            if (isDebug) Debug.Log("Unloading all scenes");
            
            _loadQueue.Clear();
            
            foreach (var sceneName in new HashSet<string>(_loadedScenes))
            {
                _unloadQueue.Enqueue(new SceneOperation(SceneOperationType.Unload, sceneName));
            }
            
            ProcessUnloadQueue().Forget();
        }
        
        private async UniTask FlushMemoryAsync(CancellationToken cancellationToken)
        {
            if (isDebug) Debug.Log("Flushing memory");
            
            // Force garbage collection to reclaim memory
            await Resources.UnloadUnusedAssets().ToUniTask(cancellationToken: cancellationToken);
            GC.Collect();
            
            if (isDebug) Debug.Log("Memory flush complete");
        }

        private async UniTaskVoid ProcessLoadQueue()
        {
            if (_isProcessingLoadQueue) return;
            _isProcessingLoadQueue = true;
            
            if (_queueCts == null || _queueCts.IsCancellationRequested)
            {
                _queueCts = new CancellationTokenSource();
            }
            var token = _queueCts.Token;
            
            IsLoading?.Invoke(true);

            try
            {
                while (_loadQueue.Count > 0 && !token.IsCancellationRequested)
                {
                    var operations = new List<UniTask>();
                    
                    while (operations.Count < maxConcurrentLoads && _loadQueue.TryDequeue(out var operation))
                    {
                        if (_processingScenes.Contains(operation.SceneName) || _loadedScenes.Contains(operation.SceneName))
                            continue;
                            
                        _processingScenes.Add(operation.SceneName);
                        
                        if (isDebug) Debug.Log($"Loading scene: {operation.SceneName}");
                        operations.Add(LoadSceneAsync(operation.SceneName, token));
                    }

                    if (operations.Count > 0)
                    {
                        await UniTask.WhenAll(operations);
                    }
                    
                    // Small delay to prevent overwhelming the system
                    await UniTask.Yield();
                }
            }
            finally
            {
                _isProcessingLoadQueue = false;
                if (!_isProcessingUnloadQueue && _unloadQueue.Count == 0)
                {
                    IsLoading?.Invoke(false);
                }
            }
        }
        
        private async UniTaskVoid ProcessUnloadQueue()
        {
            if (_isProcessingUnloadQueue) return;
            _isProcessingUnloadQueue = true;
            
            if (_queueCts == null || _queueCts.IsCancellationRequested)
            {
                _queueCts = new CancellationTokenSource();
            }
            var token = _queueCts.Token;
            
            IsLoading?.Invoke(true);

            try
            {
                while (_unloadQueue.Count > 0 && !token.IsCancellationRequested)
                {
                    var operations = new List<UniTask>();
                    
                    while (operations.Count < maxConcurrentLoads && _unloadQueue.TryDequeue(out var operation))
                    {
                        if (!_loadedScenes.Contains(operation.SceneName) || _processingScenes.Contains(operation.SceneName))
                            continue;
                            
                        _processingScenes.Add(operation.SceneName);
                        
                        if (isDebug) Debug.Log($"Unloading scene: {operation.SceneName}");
                        operations.Add(UnloadSceneAsync(operation.SceneName, token));
                    }

                    if (operations.Count > 0)
                    {
                        await UniTask.WhenAll(operations);
                    }
                    
                    // Small delay to prevent overwhelming the system
                    await UniTask.Yield();
                }
            }
            finally
            {
                if (_loadedScenes.Count == 0)
                {
                    await FlushMemoryAsync(token);
                }
                
                _isProcessingUnloadQueue = false;
                if (!_isProcessingLoadQueue && _loadQueue.Count == 0)
                {
                    IsLoading?.Invoke(false);
                }
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
                if (isDebug) Debug.Log($"Scene loaded: {sceneName}");
            }
            catch (Exception e)
            {
                if (isDebug) Debug.LogError($"Error loading scene {sceneName}: {e}");
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
                if (isDebug) Debug.Log($"Scene unloaded: {sceneName}");
            }
            catch (Exception e)
            {
                if (isDebug) Debug.LogError($"Error unloading scene {sceneName}: {e}");
            }
            finally
            {
                _processingScenes.Remove(sceneName);
            }
        }
    }
}