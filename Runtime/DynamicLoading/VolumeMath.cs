using Unity.Mathematics;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Single source of truth for zone-volume containment. Both the player test
    /// (<see cref="VolumeSystem"/>) and the object test (<see cref="ObjectZoneTrackingBridge"/>)
    /// go through here so the two can never drift apart.
    ///
    /// Convention (inherited from the original VolumeSystem implementation): a volume is an
    /// oriented box whose orientation and position come from its LocalToWorld matrix but whose
    /// extents come exclusively from <c>Volume.Scale</c> (baked from the authoring transform's
    /// localScale) — the matrix scale is deliberately ignored, distances are metric.
    /// </summary>
    public static class VolumeMath
    {
        /// <summary>The point expressed in the volume's rotation-only local frame (metric units).</summary>
        public static float3 ToLocal(in float4x4 localToWorld, in float3 worldPoint)
        {
            var pos = localToWorld.c3.xyz;
            var rot = math.quaternion(localToWorld);
            var rotationOnly = float4x4.TRS(pos, rot, new float3(1f, 1f, 1f));
            var worldToLocal = math.inverse(rotationOnly);
            return math.transform(worldToLocal, worldPoint);
        }

        public static bool ContainsPoint(in float4x4 localToWorld, in float3 scale, in float3 worldPoint)
        {
            return ContainsPoint(localToWorld, scale, worldPoint, 0f);
        }

        /// <summary>
        /// Containment with the box extents expanded by <paramref name="tolerance"/> meters on
        /// every axis (the epsilon pass of the coverage failsafe chain). Strict comparison,
        /// matching the original player test.
        /// </summary>
        public static bool ContainsPoint(in float4x4 localToWorld, in float3 scale, in float3 worldPoint, float tolerance)
        {
            var local = ToLocal(localToWorld, worldPoint);
            var range = scale * 0.5f + tolerance;
            var distance = math.abs(local);
            return math.all(distance < range);
        }

        /// <summary>
        /// Vertical overshoot (meters, volume-local up) of the point above the box top when its
        /// X/Z footprint is inside the box — the ceiling-lifted-object case. Returns
        /// <see cref="float.PositiveInfinity"/> when the footprint misses the box; zero or
        /// negative when the point is not above the top (exact containment covers those).
        /// Downward overshoot is deliberately not forgiven: an object under a box belongs to
        /// the storey below, not to that box.
        /// </summary>
        public static float LiftAbove(in float4x4 localToWorld, in float3 scale, in float3 worldPoint)
        {
            var local = ToLocal(localToWorld, worldPoint);
            var range = scale * 0.5f;
            if (math.abs(local.x) >= range.x || math.abs(local.z) >= range.z) return float.PositiveInfinity;
            return local.y - range.y;
        }

        /// <summary>Squared metric distance from the point to the box surface; 0 when inside.</summary>
        public static float DistanceSq(in float4x4 localToWorld, in float3 scale, in float3 worldPoint)
        {
            var local = ToLocal(localToWorld, worldPoint);
            var d = math.max(math.abs(local) - scale * 0.5f, float3.zero);
            return math.lengthsq(d);
        }
    }
}
