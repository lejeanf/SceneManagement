using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using jeanf.universalplayer;
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

        private bool _isInitialLoadComplete = false;
        public delegate void IsInitialDepedencyLoadCompleteDelegate(bool loadingState);
        public static IsInitialDepedencyLoadCompleteDelegate IsInitialLoadComplete;
        public static IsInitialDepedencyLoadCompleteDelegate LoadComplete;
        
        public delegate void LoadScene(string sceneName);
        public LoadScene LoadSceneRequest;
        public LoadScene UnLoadSceneRequest;
    
        public delegate void UnloadAllScenesDelegate();
        public UnloadAllScenesDelegate UnloadAllScenesRequest;
        
        public delegate void FlushScenesDelegate();
        public FlushScenesDelegate FlushScenesRequest;
        private readonly struct SceneOperation
        {
            public readonly SceneOperationType Type;
            public readonly string SceneName;
            
            public SceneOperation(SceneOperationType type, string sceneName)
            {
                Type = type;
                SceneName = sceneName;
            }
        }

        private readonly ConcurrentQueue<SceneOperation> _loadQueue = new();
        private readonly ConcurrentQueue<SceneOperation> _unloadQueue = new();
        private readonly HashSet<string> _loadedScenes = new();
        private readonly HashSet<string> _processingScenes = new();
        
        private readonly List<UniTask> _operationBuffer = new();
        private readonly List<string> _scenesToUnload = new();
        
        private bool _isProcessingLoadQueue = false;
        private bool _isProcessingUnloadQueue = false;
        private bool _isFlushingMemory = false;

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
            FlushScenesRequest = null;
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
            while (_loadQueue.TryDequeue(out _)) { }
            
            _scenesToUnload.Clear();
            foreach (var sceneName in _loadedScenes)
            {
                _scenesToUnload.Add(sceneName);
            }
            
            for (int i = 0; i < _scenesToUnload.Count; i++)
            {
                _unloadQueue.Enqueue(new SceneOperation(SceneOperationType.Unload, _scenesToUnload[i]));
            }
            
            ProcessUnloadQueue().Forget();
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
                    _operationBuffer.Clear();
                    
                    while (_operationBuffer.Count < maxConcurrentLoads && _loadQueue.TryDequeue(out var operation))
                    {
                        if (_processingScenes.Contains(operation.SceneName) || _loadedScenes.Contains(operation.SceneName))
                            continue;
                            
                        _processingScenes.Add(operation.SceneName);
                        _operationBuffer.Add(LoadSceneAsync(operation.SceneName, token));
                        LoadingInformation.LoadingStatus($"Loading: {operation.SceneName}");
                    }

                    if (_operationBuffer.Count > 0)
                    {
                        await UniTask.WhenAll(_operationBuffer);
                        if (!_isInitialLoadComplete)
                        {
                            LoadingInformation.LoadingStatus($"All dependencies were successfully loaded.");
                            _isInitialLoadComplete = true;
                            IsInitialLoadComplete?.Invoke(_isInitialLoadComplete);
                        }
                        else
                        {
                            LoadingInformation.LoadingStatus($"");
                        }
                    }
                    
                    await UniTask.Yield();
                    LoadingInformation.LoadingStatus($"");
                }
            }
            finally
            {
                _isProcessingLoadQueue = false;
                if (_loadQueue.Count == 0)
                {
                    IsLoading?.Invoke(false);
                }
                
                await UniTask.WaitUntil(() => !_isProcessingUnloadQueue, cancellationToken: token);
                LoadComplete?.Invoke(true);
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
                    _operationBuffer.Clear();
                    
                    while (_operationBuffer.Count < maxConcurrentLoads && _unloadQueue.TryDequeue(out var operation))
                    {
                        if (!_loadedScenes.Contains(operation.SceneName) || _processingScenes.Contains(operation.SceneName))
                            continue;
                            
                        _processingScenes.Add(operation.SceneName);
                        _operationBuffer.Add(UnloadSceneAsync(operation.SceneName, token));
                    }

                    if (_operationBuffer.Count > 0)
                    {
                        await UniTask.WhenAll(_operationBuffer);
                    }
                    
                    await UniTask.Yield();
                }
            }
            finally
            {
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
                loadOperation.allowSceneActivation = false;
        
                while (!loadOperation.isDone && loadOperation.progress < 0.9f)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                }
        
                await UniTask.NextFrame(cancellationToken);
                loadOperation.allowSceneActivation = true;
        
                while (!loadOperation.isDone)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                }
        
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

        public bool IsCurrentlyLoading()
        {
            return _isProcessingLoadQueue || _isProcessingUnloadQueue || _isFlushingMemory;
        }

        public int GetLoadedSceneCount()
        {
            return _loadedScenes.Count;
        }

        public int GetPendingOperationCount()
        {
            return _loadQueue.Count + _unloadQueue.Count;
        }
    }
}