using System;
using System.Collections.Generic;
using UnityEngine;

namespace jeanf.ContentManagement
{
    [CreateAssetMenu(menuName = "SceneManagment/Required Systems Config", fileName = "RequiredSystemsConfig")]
    public class RequiredSystemsConfig : ScriptableObject
    {
        [Serializable]
        public struct SystemEntry
        {
            public string systemId;
            public string loadingMessage;
        }

        public List<SystemEntry> requiredSystems;
    }
}
