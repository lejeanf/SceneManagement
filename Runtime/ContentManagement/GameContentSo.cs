using jeanf.ContentManagement.ProgressionSystem.Data;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class GameContentSo : ScriptableObject
    {
        public UnlockableContent ContentId = UnlockableContent.UC_UNLOCKED;

        public bool IsUnlocked()
        {
            if (ContentId == UnlockableContent.UC_UNLOCKED) return true;
            return ContentUnlockService.IsUnlocked((int)ContentId);
        }

        public bool IsBaseUnlocked() => ContentId == UnlockableContent.UC_UNLOCKED;
    }
}
