using System.Collections.Generic;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class LoadingProgressUI : MonoBehaviour
    {
        [SerializeField] private LoadingGroupUI cosmeticGroup;
        [SerializeField] private LoadingGroupUI worldGroup;
        [SerializeField] private LoadingGroupUI systemsGroup;

        private void OnEnable()
        {
            InitializationCoordinator.OnProgressChanged += Refresh;
            InitializationCoordinator.OnInitComplete    += Hide;
        }

        private void OnDisable()
        {
            InitializationCoordinator.OnProgressChanged -= Refresh;
            InitializationCoordinator.OnInitComplete    -= Hide;
        }

        private void Refresh()
        {
            var entries = InitializationCoordinator.Entries;
            if (entries == null) return;

            cosmeticGroup.Render(Filter(entries, "Content"));
            worldGroup.Render(Filter(entries, "World"));
            systemsGroup.Render(Filter(entries, "Systems"));
        }

        private void Hide() => gameObject.SetActive(false);

        private static IEnumerable<LoadingEntry> Filter(
            IEnumerable<LoadingEntry> entries, string group)
        {
            foreach (var e in entries)
                if (e.Group == group) yield return e;
        }
    }
}
