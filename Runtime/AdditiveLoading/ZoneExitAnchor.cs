using System.Collections.Generic;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Hand-placed way out of one or more zones, for the cases the doors cannot cover: a zone
    /// with no door, a door whose sides are ambiguous, or a scenario needing a specific exit.
    /// <para>
    /// Place it OUTSIDE the zones it evacuates - the gizmo says which zone it actually resolves
    /// to (same chain the runtime uses) and turns red when it sits in a zone it is supposed to
    /// empty. Put it in a scene that stays loaded while the scenario runs (a region scene or a
    /// persistent scene), not in the scenario scene being unloaded.
    /// </para>
    /// </summary>
    [AddComponentMenu("Scene Management/Zone Exit Anchor")]
    public class ZoneExitAnchor : MonoBehaviour, IZoneEvacuationProvider
    {
        [Tooltip("Zones this anchor can evacuate. The player standing in any of them and caught by a scenario unload is sent here.")]
        [SerializeField] private List<Zone> zonesServed = new List<Zone>();

        [Tooltip("OPTIONAL: zone this anchor stands in. When set, the anchor withdraws itself if that zone is ALSO being locked by the same unload (it would just move the problem). Use the 'Detect zone' button to fill it.")]
        [SerializeField] private Zone destinationZone;

        [Tooltip("On: the player is dropped facing this transform's blue axis - point it away from the door. Off: identity rotation.")]
        [SerializeField] private bool useTransformRotation = true;

        public IReadOnlyList<Zone> ZonesServed => zonesServed;
        public Zone DestinationZone { get => destinationZone; set => destinationZone = value; }

        private void OnEnable() => ZoneEvacuationRegistry.Register(this);
        private void OnDisable() => ZoneEvacuationRegistry.Unregister(this);

        public bool TryGetExit(string zoneId, Vector3 from, IReadOnlyCollection<string> blockedZoneIds, out ZoneExit exit)
        {
            exit = default;
            if (string.IsNullOrEmpty(zoneId)) return false;

            var serves = false;
            for (var i = 0; i < zonesServed.Count; i++)
            {
                if (zonesServed[i] == null || zonesServed[i].id != zoneId) continue;
                serves = true;
                break;
            }
            if (!serves) return false;

            // Standing in a zone that is being locked too: this anchor is no way out.
            if (destinationZone != null && ZoneEvacuationRegistry.IsBlocked(blockedZoneIds, destinationZone.id))
                return false;

            exit = new ZoneExit(transform.position,
                useTransformRotation ? transform.rotation : Quaternion.identity,
                destinationZone != null ? destinationZone.id.ToString() : string.Empty,
                name);
            return true;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            var resolved = ResolveZoneForGizmo(out var kind);
            var evacuatesItsOwnZone = resolved != null && ServesZone(resolved);

            var color = evacuatesItsOwnZone ? new Color(1f, .25f, .2f) : new Color(.2f, .8f, 1f);
            Gizmos.color = color;
            Gizmos.DrawWireSphere(transform.position, .4f);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.8f);
            Gizmos.DrawRay(transform.position + Vector3.up * .1f, transform.forward);

            var served = zonesServed.Count == 0 ? "<no zone assigned>" : string.Empty;
            for (var i = 0; i < zonesServed.Count; i++)
            {
                if (zonesServed[i] == null) continue;
                served += (served.Length > 0 ? ", " : string.Empty) + zonesServed[i].zoneName;
            }

            var resolvedLabel = resolved != null ? $"{resolved.zoneName} ({kind})" : "none (open the SubScene holding the volumes)";
            var warning = evacuatesItsOwnZone
                ? "\nERROR: this anchor stands in a zone it evacuates - move it out."
                : string.Empty;

            UnityEditor.Handles.color = color;
            UnityEditor.Handles.Label(transform.position + Vector3.up * 2f,
                $"Zone exit\nevacuates: {served}\nstands in: {resolvedLabel}{warning}");
        }

        private bool ServesZone(Zone zone)
        {
            for (var i = 0; i < zonesServed.Count; i++)
                if (zonesServed[i] == zone) return true;
            return false;
        }

        // FindObjectsByType on every gizmo repaint is too costly with hundreds of volumes, so the
        // resolution is cached and only refreshed a few times per second while something moves.
        private static double _gizmoCacheTime;
        private static List<VolumeAuthoring> _gizmoVolumes;
        private Vector3 _lastResolvedPosition = new Vector3(float.MaxValue, 0f, 0f);
        private Zone _lastResolvedZone;
        private ObjectZoneTrackingBridge.MatchKind _lastResolvedKind;

        private Zone ResolveZoneForGizmo(out ObjectZoneTrackingBridge.MatchKind kind)
        {
            var now = UnityEditor.EditorApplication.timeSinceStartup;
            if (_gizmoVolumes == null || now - _gizmoCacheTime > 2d)
            {
                _gizmoVolumes = EditorZoneResolver.GatherVolumes();
                _gizmoCacheTime = now;
                _lastResolvedPosition = new Vector3(float.MaxValue, 0f, 0f);
            }

            if ((transform.position - _lastResolvedPosition).sqrMagnitude > 1e-4f)
            {
                var resolution = EditorZoneResolver.Resolve(transform.position, _gizmoVolumes,
                    EditorZoneResolver.SceneChainParams());
                _lastResolvedPosition = transform.position;
                _lastResolvedZone = resolution.Zone;
                _lastResolvedKind = resolution.Kind;
            }

            kind = _lastResolvedKind;
            return _lastResolvedZone;
        }

        /// <summary>Editor helper: fills <see cref="destinationZone"/> from where the anchor stands.</summary>
        public Zone DetectDestinationZone()
        {
            var volumes = EditorZoneResolver.GatherVolumes();
            var resolution = EditorZoneResolver.Resolve(transform.position, volumes, EditorZoneResolver.SceneChainParams());
            return resolution.Zone;
        }
#endif
    }
}
