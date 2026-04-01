using System;

namespace jeanf.ContentManagement
{
    public static class ContentUnlockService
    {
        public static Func<int, bool> CheckUnlocked = _ => true;

        public static bool IsUnlocked(int contentId) => CheckUnlocked(contentId);
    }
}
