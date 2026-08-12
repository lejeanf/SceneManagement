using System;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Drop-on component registering this object with the <see cref="ObjectZoneTrackingBridge"/>.
    /// For code-side consumers prefer calling <see cref="ObjectZoneTrackingBridge.Register"/>
    /// directly with a callback; this component covers objects wired in the inspector.
    /// </summary>
    public class ZoneTrackedObject : MonoBehaviour
    {
        [Tooltip("Check for objects that move at runtime (characters, carried props). Static machines leave this off — they are tested once at load.")]
        [SerializeField] private bool isDynamic = false;

        public Zone CurrentZone { get; private set; }
        public event Action<Zone> ZoneChanged;

        private void OnEnable() => ObjectZoneTrackingBridge.Register(transform, HandleZone, isDynamic);
        private void OnDisable() => ObjectZoneTrackingBridge.Unregister(transform);

        /// <summary>Call after teleporting/carrying this object to force an immediate re-test.</summary>
        public void MarkDirty() => ObjectZoneTrackingBridge.MarkDirty(transform);

        private void HandleZone(Zone zone)
        {
            CurrentZone = zone;
            ZoneChanged?.Invoke(zone);
        }
    }
}
