using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace jeanf.ContentManagement
{
    public class LoadingRowUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameLabel;
        [SerializeField] private Slider progressBar;
        [SerializeField] private TMP_Text stateLabel;

        public void Render(LoadingEntry entry)
        {
            nameLabel.text    = entry.DisplayName;
            progressBar.value = entry.Progress;

            stateLabel.text = entry.State switch
            {
                LoadingState.Pending  => "Pending",
                LoadingState.Loading  => entry.TotalCount > 0
                    ? $"{entry.LoadedCount} / {entry.TotalCount}"
                    : "Loading...",
                LoadingState.Complete => "✓",
                LoadingState.Failed   => "✗",
                _                    => string.Empty
            };
        }
    }
}
