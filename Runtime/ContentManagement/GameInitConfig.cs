using jeanf.scenemanagement;
using UnityEngine;

namespace jeanf.ContentManagement
{
    [CreateAssetMenu(menuName = "SceneManagment/Game Init Config", fileName = "GameInitConfig")]
    public class GameInitConfig : ScriptableObject
    {
        public Region startRegion;
        public Zone startZone;
        public RequiredSystemsConfig systemsConfig;
    }
}
