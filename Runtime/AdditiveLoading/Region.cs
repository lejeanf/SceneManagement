using System.Collections.Generic;
using UnityEngine;
using jeanf.propertyDrawer;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace jeanf.scenemanagement
{
    [ScriptableObjectDrawer]
    [CreateAssetMenu(fileName = "Region", menuName = "LoadingSystem/Region")]
    public class Region : ScriptableObject
    {
        [Header("Region settings")]
        public Id id;
        public Coordinate Coordinate;
        public string levelName;
        public int level;
        public List<Scenario> scenariosInThisRegion;
        public List<Zone> zonesInThisRegion;
        public List<SceneReference> dependenciesInThisRegion;
        [Header("Player spawn settings")]
        [Tooltip("used when a manual region change request is emitted (eg: in the elevator).")]
        public SpawnPos SpawnPosOnRegionChangeRequest;
        public bool isUsingOnInitSpawnPos = false;
        [Tooltip("[OPTIONAL] only used for the game init or restart.")]
        public SpawnPos SpawnPosOnInit;
    }
    
    [System.Serializable]   
    public struct Coordinate
    {
        public int x;
        public int y;
    }   
    
    [System.Serializable]   
    public struct SpawnPos
    {
        public Vector3 position;
        public Vector3 rotation;

        public SpawnPos(Vector3 position, Vector3 rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }
    }
    
 
}