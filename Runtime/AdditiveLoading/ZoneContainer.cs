using System;
using UnityEngine;

namespace jeanf.scenemanagement
{
    [RequireComponent(typeof(BoxCollider))]
    public class ZoneContainer : MonoBehaviour
    {
        public Zone Zone;
        public delegate void BroadcastObject(GameObject gameObject, Zone zone);
        public static BroadcastObject broadcastObject;

        private void OnTriggerEnter(Collider other)
        {
            broadcastObject?.Invoke(other.gameObject, this.Zone);
        }

    }
}