using UnityEngine;
using UnityEngine.Serialization;

namespace jeanf.scenemanagement
{
    [CreateAssetMenu(fileName = "App_", menuName = "LoadingSystem/App")]
    public class AppType: ScriptableObject
    {
        [FormerlySerializedAs("name")] [Header("App Information")]
        public string appName;
        public string appTitle;
        public Texture2D icon;
    }
}