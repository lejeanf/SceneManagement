using NUnit.Framework;
using Unity.Mathematics;

namespace jeanf.scenemanagement.tests
{
    /// <summary>
    /// Locks the exact containment semantics shared by the player test (VolumeSystem) and the
    /// object test (ObjectZoneTrackingBridge): rotation-only local frame, extents from
    /// Volume.Scale (matrix scale ignored), strict comparison.
    /// </summary>
    public class VolumeMathTests
    {
        private static float4x4 Box(float3 pos, float yawDegrees = 0f)
        {
            return float4x4.TRS(pos, quaternion.RotateY(math.radians(yawDegrees)), new float3(1f));
        }

        [Test]
        public void ContainsPoint_InsideIdentityBox()
        {
            var l2w = Box(new float3(10f, 0f, 10f));
            var scale = new float3(4f, 2f, 4f);
            Assert.IsTrue(VolumeMath.ContainsPoint(l2w, scale, new float3(10f, 0f, 10f)));
            Assert.IsTrue(VolumeMath.ContainsPoint(l2w, scale, new float3(11.9f, 0.9f, 8.1f)));
        }

        [Test]
        public void ContainsPoint_OutsideIdentityBox()
        {
            var l2w = Box(new float3(10f, 0f, 10f));
            var scale = new float3(4f, 2f, 4f);
            Assert.IsFalse(VolumeMath.ContainsPoint(l2w, scale, new float3(12.1f, 0f, 10f)));
            Assert.IsFalse(VolumeMath.ContainsPoint(l2w, scale, new float3(10f, 1.1f, 10f)));
        }

        [Test]
        public void ContainsPoint_BoundaryIsStrict()
        {
            // Matches the original VolumeSystem comparison: distance < range, not <=.
            var l2w = Box(float3.zero);
            var scale = new float3(2f, 2f, 2f);
            Assert.IsFalse(VolumeMath.ContainsPoint(l2w, scale, new float3(1f, 0f, 0f)));
            Assert.IsTrue(VolumeMath.ContainsPoint(l2w, scale, new float3(0.999f, 0f, 0f)));
        }

        [Test]
        public void ContainsPoint_RespectsRotation()
        {
            // 4x1x1 box rotated 90° around Y: its long axis now runs along Z.
            var l2w = Box(float3.zero, 90f);
            var scale = new float3(4f, 1f, 1f);
            Assert.IsTrue(VolumeMath.ContainsPoint(l2w, scale, new float3(0f, 0f, 1.5f)));
            Assert.IsFalse(VolumeMath.ContainsPoint(l2w, scale, new float3(1.5f, 0f, 0f)));
        }

        [Test]
        public void ContainsPoint_IgnoresMatrixScale()
        {
            // The convention inherited from VolumeSystem: extents come exclusively from
            // Volume.Scale — a scaled LocalToWorld must not change the result.
            var scaled = float4x4.TRS(float3.zero, quaternion.identity, new float3(100f));
            var scale = new float3(2f, 2f, 2f);
            Assert.IsTrue(VolumeMath.ContainsPoint(scaled, scale, new float3(0.9f, 0f, 0f)));
            Assert.IsFalse(VolumeMath.ContainsPoint(scaled, scale, new float3(1.1f, 0f, 0f)));
        }

        [Test]
        public void ContainsPoint_ToleranceExpandsExtents()
        {
            var l2w = Box(float3.zero);
            var scale = new float3(2f, 2f, 2f);
            var justOutside = new float3(1.2f, 0f, 0f);
            Assert.IsFalse(VolumeMath.ContainsPoint(l2w, scale, justOutside));
            Assert.IsTrue(VolumeMath.ContainsPoint(l2w, scale, justOutside, 0.25f));
            Assert.IsFalse(VolumeMath.ContainsPoint(l2w, scale, new float3(1.3f, 0f, 0f), 0.25f));
        }

        [Test]
        public void DistanceSq_ZeroInside_MetricOutside()
        {
            var l2w = Box(new float3(5f, 0f, 0f));
            var scale = new float3(2f, 2f, 2f);
            Assert.AreEqual(0f, VolumeMath.DistanceSq(l2w, scale, new float3(5f, 0f, 0f)));
            // 3m from center along X, box face at 1m → 2m outside → 4 squared.
            Assert.AreEqual(4f, VolumeMath.DistanceSq(l2w, scale, new float3(8f, 0f, 0f)), 1e-4f);
        }

        [Test]
        public void DistanceSq_RespectsRotation()
        {
            var l2w = Box(float3.zero, 90f);
            var scale = new float3(4f, 1f, 1f);
            // Long axis along Z after rotation: 3m along Z is only 1m past the 2m face.
            Assert.AreEqual(1f, VolumeMath.DistanceSq(l2w, scale, new float3(0f, 0f, 3f)), 1e-4f);
            // Along X the face is at 0.5m: 3m out → 2.5m outside → 6.25 squared.
            Assert.AreEqual(6.25f, VolumeMath.DistanceSq(l2w, scale, new float3(3f, 0f, 0f)), 1e-4f);
        }
    }
}
