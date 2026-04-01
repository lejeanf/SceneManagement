namespace jeanf.ContentManagement
{
    public interface IInitializable
    {
        string SystemId { get; }
        string DisplayName => SystemId;
        float Progress => 0f;
    }
}
