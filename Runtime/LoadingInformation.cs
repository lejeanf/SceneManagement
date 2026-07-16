using UnityEngine;
using jeanf.EventSystem;
using jeanf.validationTools;

namespace jeanf.scenemanagement
{
    public class LoadingInformation : MonoBehaviour
    {
        public bool isDebug = false;
        public delegate void LoadingStatusDelegate(string status);
        public static LoadingStatusDelegate LoadingStatus;

        /// <summary>
        /// Loading progress, 0..1. REAL progress, not an estimate: the additive loader
        /// reports Addressables' AsyncOperationHandle.PercentComplete, and the subscene
        /// loader reports completed/total over its known subscene list. Raised whenever
        /// it advances; 1 when a load session finishes.
        /// </summary>
        public delegate void LoadingProgressDelegate(float progress01);
        public static LoadingProgressDelegate LoadingProgress;

        /// <summary>Raise progress, clamped — one funnel so every loader reports the same way.</summary>
        public static void ReportProgress(float progress01) =>
            LoadingProgress?.Invoke(Mathf.Clamp01(progress01));

        [Header("Broadcasting on:")]
        [Validation("The loading status channel is required — without it nothing can display the loading text. " +
                    "Assign the same StringEventChannelSO the HUD listens on.")]
        [Tooltip("Loading status text. Lets any HUD (e.g. UniversalPlayer's UI Toolkit HUD) show it WITHOUT this package being referenced in code.")]
        [SerializeField] private StringEventChannelSO loadingStatusChannel;
        [Validation("The loading progress channel is required — without it the loading bar can never move. " +
                    "Assign the same FloatEventChannelSO the HUD listens on.")]
        [Tooltip("Loading progress 0..1 — real progress from Addressables / the subscene count, not an estimate.")]
        [SerializeField] private FloatEventChannelSO loadingProgressChannel;

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();

        private void Subscribe()
        {
            LoadingStatus += UpdateLoadingText;
            LoadingProgress += BroadcastProgress;
            LoadPersistentSubScenes.PersistentLoadingComplete += OnPersistentLoadingComplete;
        }

        private void Unsubscribe()
        {
            LoadingStatus -= UpdateLoadingText;
            LoadingProgress -= BroadcastProgress;
            LoadPersistentSubScenes.PersistentLoadingComplete -= OnPersistentLoadingComplete;
        }

        // The loaders speak in static delegates (no wiring); this component is the bridge
        // that re-broadcasts them on SO channels, so a HUD in another package can listen
        // without referencing this assembly.
        private void BroadcastProgress(float progress01)
        {
            if (loadingProgressChannel != null) loadingProgressChannel.RaiseEvent(progress01);
        }

        // Named handler: the previous lambda subscribe/unsubscribe pair removed a
        // DIFFERENT delegate instance, so destroyed objects stayed subscribed and the
        // next loading status update threw a NullReferenceException.
        private void OnPersistentLoadingComplete(bool _) => UpdateLoadingText("");

        // Channel-only: the label is gone. Whoever wants to show this (the UI Toolkit HUD,
        // a project's own widget) listens on the channel — no reference to this package.
        private void UpdateLoadingText(string status)
        {
            if (loadingStatusChannel != null) loadingStatusChannel.RaiseEvent(status);
            if (isDebug && status != "") Debug.Log($"[{Time.time}] - {status}");
        }
    }
}
