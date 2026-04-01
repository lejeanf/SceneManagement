namespace jeanf.ContentManagement
{
    public enum LoadingState { Pending, Loading, Complete, Failed }

    public class LoadingEntry
    {
        public string Id           { get; internal set; }
        public string DisplayName  { get; internal set; }
        public string Group        { get; internal set; }
        public LoadingState State  { get; internal set; }
        public float Progress      { get; internal set; }
        public int LoadedCount     { get; internal set; }
        public int TotalCount      { get; internal set; }
    }
}
