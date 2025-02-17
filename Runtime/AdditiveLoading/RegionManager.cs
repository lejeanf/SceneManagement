using UnityEngine;

namespace jeanf.SceneManagment
{
    public class RegionManager : MonoBehaviour
    {
        public Region region;
        
        public void LoadRegion()
        {
            var regionId = region.id;
            WorldManager.RequestRegionChange(regionId);
        }
    }
}

