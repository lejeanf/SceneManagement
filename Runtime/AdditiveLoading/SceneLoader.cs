using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using jeanf.universalplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThreadPriority = System.Threading.ThreadPriority;

namespace jeanf.scenemanagement
{
    public class SceneLoader : MonoBehaviour
    {
        public bool isDebug = false;
        private CancellationTokenSource _queueCts;
        
        [SerializeField] private int maxConcurrentLoads = 2;
        private UnityEngine.ThreadPriority loadingThreadPriority = UnityEngine.ThreadPriority.BelowNormal;
        [SerializeField] private int maxFrameTimeMs = 16; // Target max time per frame for loading
        [SerializeField] private int loadCompleteDebounceFrames = 3; // Frames to wait before calling LoadComplete
        
        // Background worker thread pool for auxiliary tasks
        private static readonly int BackgroundThreadCount = Math.Max(1, Environment.ProcessorCount / 4);
        private readonly ConcurrentQueue<Action> _backgroundWorkQueue = new();
        private readonly List<Thread> _backgroundThreads = new();
        private volatile bool _isRunning = true;
        
        // Debounce tracking for LoadComplete
        private int _emptyQueueFrameCount = 3;
        private bool _hasCalledLoadComplete = false;

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
        
        // For validation results from background threads
        private readonly ConcurrentDictionary<string, bool> _sceneValidationCache = new();
        
        private readonly List<UniTask> _operationBuffer = new();
        private readonly List<string> _scenesToUnload = new();
        
        private bool _isProcessingLoadQueue = false;
        private bool _isProcessingUnloadQueue = false;
        private bool _isFlushingMemory = false;

        #if UNITY_EDITOR
        [Tooltip("This is only for devs in the Editor, will not be included in any build, not even alpha.")]
        [SerializeField] private List<SceneReference> devScenes = new List<SceneReference>();

        private void Awake()
        {
            InitializeBackgroundThreads();
            
            foreach (var devScene in devScenes)
            {
                QueueLoadScene(devScene.SceneName);
            }
        }
        #else
        private void Awake()
        {
            InitializeBackgroundThreads();
        }
        #endif

        private void InitializeBackgroundThreads()
        {
            for (int i = 0; i < BackgroundThreadCount; i++)
            {
                var thread = new Thread(BackgroundWorker)
                {
                    IsBackground = true,
                    Priority = (ThreadPriority) loadingThreadPriority,
                    Name = $"SceneLoader-Background-{i}"
                };
                _backgroundThreads.Add(thread);
                thread.Start();
            }
            
            if (isDebug)
            {
                Debug.Log($"[SceneLoader] Initialized {BackgroundThreadCount} background threads");
            }
        }

        private void BackgroundWorker()
        {
            while (_isRunning)
            {
                if (_backgroundWorkQueue.TryDequeue(out var work))
                {
                    try
                    {
                        work?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[SceneLoader] Background work error: {ex}");
                    }
                }
                else
                {
                    // Sleep briefly to avoid spinning
                    Thread.Sleep(10);
                }
            }
        }

        private void QueueBackgroundWork(Action work)
        {
            _backgroundWorkQueue.Enqueue(work);
        }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        
        private void OnDestroy()
        {
            Unsubscribe();
            _isRunning = false;
            
            // Clean up background threads
            foreach (var thread in _backgroundThreads)
            {
                if (thread.IsAlive)
                {
                    thread.Join(1000); // Wait up to 1 second
                }
            }
            _backgroundThreads.Clear();
        }

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
            // Reset LoadComplete flag since we're adding new work
            _hasCalledLoadComplete = false;
            
            // Validate scene name on background thread before queueing
            QueueBackgroundWork(() =>
            {
                bool isValid = ValidateSceneName(sceneName);
                _sceneValidationCache[sceneName] = isValid;
                
                if (isValid)
                {
                    _loadQueue.Enqueue(new SceneOperation(SceneOperationType.Load, sceneName));
                    // Trigger processing on main thread
                    UnityMainThreadDispatcher.Enqueue(() => ProcessLoadQueue().Forget());
                }
                else if (isDebug)
                {
                    Debug.LogWarning($"[SceneLoader] Scene validation failed: {sceneName}");
                }
            });
        }

        private void QueueUnloadScene(string sceneName)
        {
            // Reset LoadComplete flag since we're adding new work
            _hasCalledLoadComplete = false;
            
            _unloadQueue.Enqueue(new SceneOperation(SceneOperationType.Unload, sceneName));
            ProcessUnloadQueue().Forget();
        }
        
        private void QueueUnloadAllScenes()
        {
            // Reset LoadComplete flag since we're adding new work
            _hasCalledLoadComplete = false;
            
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

        private bool ValidateSceneName(string sceneName)
        {
            // This runs on background thread - do NOT call Unity APIs here
            // Only do thread-safe validation
            if (string.IsNullOrEmpty(sceneName))
                return false;
            
            // Add your own validation logic here (file checks, name validation, etc.)
            // For example, you could check if the scene exists in your build settings
            // by maintaining a cached list
            
            return true;
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
                    
                    // Process queued operations
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
                    
                    // Yield to prevent frame blocking
                    await UniTask.Yield(PlayerLoopTiming.Update, token);
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
                
                // Start monitoring for LoadComplete with debounce
                MonitorLoadComplete(token).Forget();
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
                    
                    // Yield to prevent frame blocking
                    await UniTask.Yield(PlayerLoopTiming.Update, token);
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

        private async UniTaskVoid MonitorLoadComplete(CancellationToken cancellationToken)
        {
            try
            {
                // Wait until both load and unload queues are done processing
                await UniTask.WaitUntil(() => !_isProcessingLoadQueue && !_isProcessingUnloadQueue, 
                    cancellationToken: cancellationToken);
                
                // Reset the frame counter and flag
                _emptyQueueFrameCount = 0;
                _hasCalledLoadComplete = false;

                // Monitor the queues for stability
                while (!_hasCalledLoadComplete && !cancellationToken.IsCancellationRequested)
                {
                    bool queuesAreEmpty = _loadQueue.Count == 0 && 
                                         _unloadQueue.Count == 0 && 
                                         !_isProcessingLoadQueue && 
                                         !_isProcessingUnloadQueue;

                    if (queuesAreEmpty)
                    {
                        _emptyQueueFrameCount++;
                        
                        if (_emptyQueueFrameCount >= loadCompleteDebounceFrames)
                        {
                            // Queues have been empty for enough frames, it's safe to call LoadComplete
                            _hasCalledLoadComplete = true;
                            LoadComplete?.Invoke(true);
                            
                            if (isDebug)
                            {
                                Debug.Log($"[SceneLoader] LoadComplete invoked after {_emptyQueueFrameCount} stable frames");
                            }
                            break;
                        }
                    }
                    else
                    {
                        // Something was added back to the queue, reset counter
                        if (_emptyQueueFrameCount > 0 && isDebug)
                        {
                            Debug.Log($"[SceneLoader] Queue stability broken, resetting debounce counter");
                        }
                        _emptyQueueFrameCount = 0;
                        
                        // Wait for processing to finish again
                        await UniTask.WaitUntil(() => !_isProcessingLoadQueue && !_isProcessingUnloadQueue, 
                            cancellationToken: cancellationToken);
                    }
                    
                    // Wait one frame before checking again
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Operation was cancelled, this is expected
                if (isDebug)
                {
                    Debug.Log("[SceneLoader] LoadComplete monitoring cancelled");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SceneLoader] Error in MonitorLoadComplete: {ex}");
            }
        }

        private async UniTask LoadSceneAsync(string sceneName, CancellationToken cancellationToken)
        {
            try
            {
                var startTime = Time.realtimeSinceStartup;
                
                // Lower thread priority during loading to reduce main thread impact
                Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.Low;
                
                var loadOperation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                loadOperation.allowSceneActivation = false;
                
                // Load in background, yielding frequently
                while (!loadOperation.isDone && loadOperation.progress < 0.9f)
                {
                    // Check frame time and yield if we're taking too long
                    if ((Time.realtimeSinceStartup - startTime) * 1000 > maxFrameTimeMs)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                        startTime = Time.realtimeSinceStartup;
                    }
                    else
                    {
                        await UniTask.NextFrame(cancellationToken);
                    }
                }
                
                // Wait an extra frame before activation to ensure smooth transition
                await UniTask.NextFrame(cancellationToken);
                
                // Activate the scene
                loadOperation.allowSceneActivation = true;
                
                // Wait for activation to complete
                while (!loadOperation.isDone)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                }
                
                _loadedScenes.Add(sceneName);
                
                // Restore thread priority
                Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.Normal;
                
                if (isDebug)
                {
                    Debug.Log($"[SceneLoader] Loaded scene: {sceneName}");
                }
            }
            catch (OperationCanceledException)
            {
                if (isDebug)
                {
                    Debug.Log($"[SceneLoader] Load cancelled: {sceneName}");
                }
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SceneLoader] Failed to load scene {sceneName}: {ex}");
            }
            finally
            {
                _processingScenes.Remove(sceneName);
                Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.Normal;
            }
        }

        private async UniTask UnloadSceneAsync(string sceneName, CancellationToken cancellationToken)
        {
            try
            {
                var unloadOperation = SceneManager.UnloadSceneAsync(sceneName);
                
                // Monitor unload progress and yield to prevent blocking
                while (unloadOperation != null && !unloadOperation.isDone)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                }
                
                _loadedScenes.Remove(sceneName);
                
                if (isDebug)
                {
                    Debug.Log($"[SceneLoader] Unloaded scene: {sceneName}");
                }
            }
            catch (OperationCanceledException)
            {
                if (isDebug)
                {
                    Debug.Log($"[SceneLoader] Unload cancelled: {sceneName}");
                }
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SceneLoader] Failed to unload scene {sceneName}: {ex}");
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

        public int GetBackgroundQueueCount()
        {
            return _backgroundWorkQueue.Count;
        }
    }

    // Helper class to dispatch work back to Unity's main thread
    public static class UnityMainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> _executionQueue = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            var go = new GameObject("MainThreadDispatcher");
            go.AddComponent<MainThreadExecutor>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        public static void Enqueue(Action action)
        {
            _executionQueue.Enqueue(action);
        }

        private class MainThreadExecutor : MonoBehaviour
        {
            private void Update()
            {
                while (_executionQueue.TryDequeue(out var action))
                {
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[MainThreadDispatcher] Error: {ex}");
                    }
                }
            }
        }
    }
}