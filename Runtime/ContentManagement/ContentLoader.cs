using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace jeanf.ContentManagement
{
    public abstract class ContentLoader<T> : IDisposable
    {
        public bool Loaded { get; private set; }
        public int TotalCount { get; private set; }
        public int LoadedCount { get; private set; }
        public float Progress => TotalCount > 0 ? (float)LoadedCount / TotalCount : (Loaded ? 1f : 0f);

        public event Action OnProgressChanged;

        private readonly string[] _contentTags;
        private UniTask<bool>? _loadingTask;
        private AsyncOperationHandle<IList<T>> _assetHandle;

        protected ContentLoader(params string[] tags)
        {
            _contentTags = tags;
        }

        public async UniTask Initialize()
        {
            var isCancelled = await InternalInitialize();
            if (isCancelled)
            {
                Debug.LogError("Failed to load cosmetics");
                return;
            }

            Loaded = true;
        }

        private async UniTask<bool> InternalInitialize()
        {
            if (Loaded) return false;

            if (_loadingTask.HasValue)
            {
                return await _loadingTask.Value;
            }

            _loadingTask = LoadResource().SuppressCancellationThrow();
            return await _loadingTask.Value;
        }

        private async UniTask LoadResource()
        {
            var locHandle = Addressables.LoadResourceLocationsAsync(
                _contentTags,
                Addressables.MergeMode.Intersection,
                typeof(T));

            if (!locHandle.IsValid())
            {
                throw new Exception("Failed to load Loc Handle for " + typeof(T).Name);
            }

            await locHandle.Task.AsUniTask();

            if (locHandle.Status != AsyncOperationStatus.Succeeded)
            {
                Addressables.Release(locHandle);
                throw new Exception("Status not succeeded for Loc Handle " + typeof(T).Name);
            }

            TotalCount = locHandle.Result.Count;
            var assetHandle = Addressables.LoadAssetsAsync<T>(locHandle.Result, resource =>
            {
                HandleLoadResource(resource);
                LoadedCount++;
                OnProgressChanged?.Invoke();
            });

            if (!assetHandle.IsValid())
            {
                Addressables.Release(locHandle);
                throw new Exception("Failed to load asset handle for " + typeof(T).Name);
            }

            await assetHandle.Task.AsUniTask();

            Addressables.Release(locHandle);

            if (assetHandle.Status != AsyncOperationStatus.Succeeded)
            {
                assetHandle.Release();
                Debug.LogError(assetHandle.OperationException.Message);
                throw new Exception("Status not succeeded for asset handle" + typeof(T).Name);
            }

            OnPostProcessResource();

            _assetHandle = assetHandle;
        }

        protected abstract void HandleLoadResource(T resource);

        protected virtual void OnPostProcessResource() { }

        public virtual void Dispose()
        {
            if (!Loaded) return;

            if (_assetHandle.IsValid())
                Addressables.Release(_assetHandle);

            Loaded = false;
            _loadingTask = null;
            TotalCount = 0;
            LoadedCount = 0;
        }
    }
}
