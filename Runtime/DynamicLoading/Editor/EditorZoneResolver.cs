#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Edit-mode prediction of the zone <see cref="ObjectZoneTrackingBridge"/> would assign to a
    /// world position in play mode. Tests against every <see cref="VolumeAuthoring"/> in the OPEN
    /// scenes (open the SubScene holding the volumes for edit, or nothing is found) and runs the
    /// exact same failsafe chain through the same <see cref="VolumeMath"/>:
    /// exact → soft (±tolerance) → lifted (pivot above a volume's top) → nearest fallback → none.
    /// The chain parameters are read from the scene's bridge instance when one exists, so tuning
    /// the bridge component re-tunes the prediction.
    /// </summary>
    public static class EditorZoneResolver
    {
        public readonly struct Resolution
        {
            public readonly Zone Zone;
            public readonly ObjectZoneTrackingBridge.MatchKind Kind;
            public readonly VolumeAuthoring Volume;
            /// <summary>Meters: lift overshoot for Lifted, surface distance for Fallback, 0 otherwise.</summary>
            public readonly float Distance;
            /// <summary>Volumes whose tolerance-expanded box contains the point — >1 means the
            /// assignment is ambiguous (pivot within tolerance of a neighboring room).</summary>
            public readonly int AmbiguousMatches;

            public bool HasZone => Zone != null;

            public Resolution(Zone zone, ObjectZoneTrackingBridge.MatchKind kind, VolumeAuthoring volume,
                float distance, int ambiguousMatches)
            {
                Zone = zone; Kind = kind; Volume = volume; Distance = distance; AmbiguousMatches = ambiguousMatches;
            }
        }

        public readonly struct ChainParams
        {
            public readonly float Tolerance;
            public readonly float MaxLiftAbove;
            public readonly float MaxFallbackDistance;

            public ChainParams(float tolerance, float maxLiftAbove, float maxFallbackDistance)
            {
                Tolerance = tolerance; MaxLiftAbove = maxLiftAbove; MaxFallbackDistance = maxFallbackDistance;
            }
        }

        /// <summary>Chain parameters of the bridge in the open scenes, or the bridge's serialized defaults.</summary>
        public static ChainParams SceneChainParams()
        {
            var bridge = Object.FindFirstObjectByType<ObjectZoneTrackingBridge>(FindObjectsInactive.Include);
            return bridge != null
                ? new ChainParams(bridge.CoverageTolerance, bridge.MaxLiftAboveVolume, bridge.MaxFallbackDist)
                : new ChainParams(0.25f, 4f, 2f);
        }

        /// <summary>Every volume with a zone in the open scenes (SubScenes must be open for edit).</summary>
        public static List<VolumeAuthoring> GatherVolumes()
        {
            var found = Object.FindObjectsByType<VolumeAuthoring>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var kept = new List<VolumeAuthoring>(found.Length);
            foreach (var volume in found)
                if (volume.zone != null) kept.Add(volume);
            return kept;
        }

        /// <summary>Same convention as the baker/runtime: orientation and position from the
        /// transform, extents from localScale, matrix scale ignored.</summary>
        private static float4x4 VolumeLocalToWorld(Transform t)
            => float4x4.TRS(t.position, t.rotation, new float3(1f));

        public static Resolution Resolve(Vector3 worldPosition, IReadOnlyList<VolumeAuthoring> volumes, in ChainParams chain)
        {
            // Ambiguity census first — independent of which pass lands the assignment.
            var ambiguous = 0;
            for (var v = 0; v < volumes.Count; v++)
            {
                var t = volumes[v].transform;
                if (VolumeMath.ContainsPoint(VolumeLocalToWorld(t), t.localScale, worldPosition, chain.Tolerance))
                    ambiguous++;
            }

            // 1) exact containment
            for (var v = 0; v < volumes.Count; v++)
            {
                var t = volumes[v].transform;
                if (VolumeMath.ContainsPoint(VolumeLocalToWorld(t), t.localScale, worldPosition))
                    return new Resolution(volumes[v].zone, ObjectZoneTrackingBridge.MatchKind.Exact, volumes[v], 0f, ambiguous);
            }

            // 2) epsilon pass
            if (chain.Tolerance > 0f)
            {
                for (var v = 0; v < volumes.Count; v++)
                {
                    var t = volumes[v].transform;
                    if (VolumeMath.ContainsPoint(VolumeLocalToWorld(t), t.localScale, worldPosition, chain.Tolerance))
                        return new Resolution(volumes[v].zone, ObjectZoneTrackingBridge.MatchKind.Soft, volumes[v], 0f, ambiguous);
                }
            }

            // 3) lifted pass: smallest overshoot above a volume whose X/Z footprint contains the point
            if (chain.MaxLiftAbove > 0f)
            {
                var best = -1;
                var bestOvershoot = chain.MaxLiftAbove;
                for (var v = 0; v < volumes.Count; v++)
                {
                    var t = volumes[v].transform;
                    var overshoot = VolumeMath.LiftAbove(VolumeLocalToWorld(t), t.localScale, worldPosition);
                    if (overshoot <= 0f || overshoot >= bestOvershoot) continue;
                    bestOvershoot = overshoot;
                    best = v;
                }
                if (best >= 0)
                    return new Resolution(volumes[best].zone, ObjectZoneTrackingBridge.MatchKind.Lifted, volumes[best], bestOvershoot, ambiguous);
            }

            // 4) nearest candidate within limit
            if (chain.MaxFallbackDistance > 0f)
            {
                var best = -1;
                var bestDistSq = chain.MaxFallbackDistance * chain.MaxFallbackDistance;
                for (var v = 0; v < volumes.Count; v++)
                {
                    var t = volumes[v].transform;
                    var distSq = VolumeMath.DistanceSq(VolumeLocalToWorld(t), t.localScale, worldPosition);
                    if (distSq >= bestDistSq) continue;
                    bestDistSq = distSq;
                    best = v;
                }
                if (best >= 0)
                    return new Resolution(volumes[best].zone, ObjectZoneTrackingBridge.MatchKind.Fallback, volumes[best], Mathf.Sqrt(bestDistSq), ambiguous);
            }

            // 5) pending
            return new Resolution(null, ObjectZoneTrackingBridge.MatchKind.None, null, 0f, ambiguous);
        }

        /// <summary>Transforms of every <see cref="IZoneTrackedObject"/> in the open scenes (one entry per GameObject).</summary>
        public static List<Transform> GatherTrackedObjects()
        {
            var behaviours = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var seen = new HashSet<GameObject>();
            var result = new List<Transform>();
            foreach (var behaviour in behaviours)
            {
                if (behaviour is not IZoneTrackedObject) continue;
                if (!seen.Add(behaviour.gameObject)) continue;
                result.Add(behaviour.transform);
            }
            return result;
        }
    }
}
#endif
