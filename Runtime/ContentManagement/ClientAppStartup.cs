using Cysharp.Threading.Tasks;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class ClientAppStartup : MonoBehaviour
    {
        [SerializeField] private InitializationCoordinator _coordinator;

        private ContentRegistry _contentRegistry;

        private async void Start()
        {
            _contentRegistry = new ContentRegistry();
            await _coordinator.Run(_contentRegistry);
        }

        private void OnDestroy()
        {
            _contentRegistry?.Dispose();
        }
    }
}
