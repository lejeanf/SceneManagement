using UnityEngine;
using TMPro;
using jeanf.validationTools;

namespace jeanf.scenemanagement
{
    public class LoadingInformation : MonoBehaviour
    {
        public bool isDebug = false;
        public delegate void LoadingStatusDelegate(string status);
        public static LoadingStatusDelegate LoadingStatus;

        [Validation("A TextMeshProUGUI is required — loading status text cannot be displayed without it.")]
        [SerializeField] private TextMeshProUGUI tmp;
        private bool _missingTmpWarned;

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();

        private void Subscribe()
        {
            LoadingStatus += UpdateLoadingText;
            LoadPersistentSubScenes.PersistentLoadingComplete += OnPersistentLoadingComplete;
        }

        private void Unsubscribe()
        {
            LoadingStatus -= UpdateLoadingText;
            LoadPersistentSubScenes.PersistentLoadingComplete -= OnPersistentLoadingComplete;
        }

        // Named handler: the previous lambda subscribe/unsubscribe pair removed a
        // DIFFERENT delegate instance, so destroyed objects stayed subscribed and the
        // next loading status update threw a NullReferenceException.
        private void OnPersistentLoadingComplete(bool _) => UpdateLoadingText("");

        private void UpdateLoadingText(string status)
        {
            if (tmp == null)
            {
                if (!_missingTmpWarned)
                {
                    _missingTmpWarned = true;
                    Debug.LogWarning($"[SceneManagement] LoadingInformation on '{name}': no TextMeshProUGUI assigned — " +
                        "loading status text cannot be displayed. Assign the loading label to the 'tmp' field.", this);
                }
                return;
            }
            tmp.text = status;
            if (isDebug && status != "") Debug.Log($"[{Time.time}] - {status}");
        }
    }
}
