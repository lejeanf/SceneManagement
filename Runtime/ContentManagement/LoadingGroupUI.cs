using System.Collections.Generic;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class LoadingGroupUI : MonoBehaviour
    {
        [SerializeField] private LoadingRowUI rowPrefab;
        [SerializeField] private Transform rowParent;

        private readonly Dictionary<string, LoadingRowUI> _rows = new();

        public void Render(IEnumerable<LoadingEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (!_rows.TryGetValue(entry.Id, out var row))
                {
                    row = Instantiate(rowPrefab, rowParent);
                    _rows[entry.Id] = row;
                }
                row.Render(entry);
            }
        }
    }
}
