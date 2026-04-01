using Cysharp.Threading.Tasks;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class ContentRegistry : IContentRegistry
    {
        private const string TAG_COSMETIC = "cosmetic";
        private const string TAG_SCENE    = "scene";

        public CosmeticContentLoader Cosmetics { get; private set; }
        public SceneContentLoader    Scenes    { get; private set; }

        public ContentRegistry()
        {
            Cosmetics = new CosmeticContentLoader(TAG_COSMETIC);
            Scenes    = new SceneContentLoader(TAG_SCENE);
        }

        public int ContentLoadedCount => Cosmetics.LoadedCount + Scenes.LoadedCount;
        public int ContentTotalCount  => Cosmetics.TotalCount  + Scenes.TotalCount;
        public float ContentProgress  => ContentTotalCount > 0
            ? (float)ContentLoadedCount / ContentTotalCount
            : 0f;

        public async UniTask Initialize()
        {
            await UniTask.WhenAll(
                Cosmetics.Initialize(),
                Scenes.Initialize()
            );

            Debug.Log("[ContentRegistry] All content loaded.");
        }

        public void Dispose()
        {
            Cosmetics?.Dispose();
            Scenes?.Dispose();
        }
    }
}
