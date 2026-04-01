using System.Collections.Generic;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class CosmeticContentLoader : ContentLoader<CosmeticContentSo>
    {
        public const int EMPTY_COSMETIC = 0;
        public const int BOT_SKIN       = 1;

        private readonly Dictionary<CosmeticType, List<CosmeticContentSo>> _cosmeticTypeMap = new();
        private readonly Dictionary<int, CosmeticContentSo>                _cosmeticList    = new();

        public CosmeticContentLoader(params string[] tags) : base(tags) { }

        protected override void HandleLoadResource(CosmeticContentSo resource)
        {
            if (_cosmeticTypeMap.TryGetValue(resource.Type, out var list))
            {
                list.Add(resource);
            }
            else
            {
                _cosmeticTypeMap.Add(resource.Type, new List<CosmeticContentSo> { resource });
            }

            if (!_cosmeticList.TryAdd(resource.Id, resource))
            {
                Debug.LogError("Duplicated Cosmetic!!");
            }
        }

        public CosmeticContentSo Find(int key)
            => _cosmeticList.GetValueOrDefault(key);

        public IReadOnlyList<CosmeticContentSo> FindByType(CosmeticType cosmeticType)
            => _cosmeticTypeMap.GetValueOrDefault(cosmeticType).AsReadOnly();

        public override void Dispose()
        {
            if (!Loaded) return;
            _cosmeticTypeMap.Clear();
            _cosmeticList.Clear();
            base.Dispose();
        }
    }
}
