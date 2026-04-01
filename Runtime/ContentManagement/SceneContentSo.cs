using jeanf.scenemanagement;
using UnityEngine;

namespace jeanf.ContentManagement
{
    [CreateAssetMenu(menuName = "SceneManagment/New Scene Info", fileName = "Empty Scene Info")]
    public class SceneContentSo : GameContentSo
    {
        public SceneReference Scene;
        public bool IsActive = true;
        [TextArea(3, 10)]
        public string Comments;
    }
}
