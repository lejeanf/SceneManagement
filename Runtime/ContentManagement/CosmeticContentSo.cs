using UnityEngine;
using UnityEngine.AddressableAssets;

namespace jeanf.ContentManagement
{
    public enum CosmeticType
    {
        CHARACTER,
        OBJECT,
        ENVIRONMENT,
        UI,
        VFX,
        DISCUSSION,
        QUEST
    }

    [CreateAssetMenu(fileName = "Empty Cosmetic Config", menuName = "SceneManagment/New Cosmetic Config")]
    public class CosmeticContentSo : GameContentSo
    {
        public int Id;
        public CosmeticType Type;
        public AssetReference CosmeticPrefab;
        public string slug;
        public Sprite Icon;
    }
}
