using UnityEngine;

namespace jeanf.scenemanagement
{
    [CreateAssetMenu(fileName = "App_", menuName = "LoadingSystem/App")]
    public class AppType: ScriptableObject
    {
        [Header("App Information")]
        public string name;
        public Texture2D icon;
    }
}