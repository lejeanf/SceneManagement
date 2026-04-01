using Cysharp.Threading.Tasks;

namespace jeanf.ContentManagement
{
    public interface IContentRegistry
    {
        UniTask Initialize();
        void Dispose();
    }
}
