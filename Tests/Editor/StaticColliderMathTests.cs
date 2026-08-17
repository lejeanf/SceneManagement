using System.Collections.Generic;
using jeanf.scenemanagement;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace jeanf.scenemanagement.Tests
{
    /// <summary>
    /// Locks the two pure functions behind static collider proxies. The placement math is the
    /// regression-prone part: get it wrong and every proxy is silently misplaced — the exact bug
    /// class the seat baker documents on itself.
    /// </summary>
    public class StaticColliderMathTests
    {
        private const float Tolerance = 1e-3f;

        private static void AssertApproximately(float3 expected, float3 actual, string label)
        {
            Assert.That(math.distance(expected, actual), Is.LessThan(Tolerance),
                $"{label}: expected {expected}, got {actual}");
        }

        [Test]
        public void DecomposeTrs_IdentityMatrix_YieldsIdentityTrs()
        {
            StaticColliderBake.DecomposeTrs(float4x4.identity, out var position, out var rotation, out var scale);

            AssertApproximately(float3.zero, position, "position");
            AssertApproximately(new float3(1f, 1f, 1f), scale, "scale");
            Assert.That(math.abs(math.abs(rotation.value.w) - 1f), Is.LessThan(Tolerance), "rotation should be identity");
        }

        [Test]
        public void DecomposeTrs_RoundTripsTranslationRotationScale()
        {
            var expectedPosition = new float3(1.5f, -2.25f, 0.75f);
            var expectedRotation = quaternion.Euler(math.radians(20f), math.radians(-35f), math.radians(10f));
            var expectedScale = new float3(2f, 2f, 2f);
            var m = float4x4.TRS(expectedPosition, expectedRotation, expectedScale);

            StaticColliderBake.DecomposeTrs(m, out var position, out var rotation, out var scale);

            AssertApproximately(expectedPosition, position, "position");
            AssertApproximately(expectedScale, scale, "scale");
            // Compare the rotations by their effect: q and -q describe the same rotation.
            AssertApproximately(math.mul(expectedRotation, math.right()), math.mul(rotation, math.right()), "rotated axis");
        }

        /// <summary>
        /// The case that matters in practice: a collider on a rotated, offset, scaled CHILD of the
        /// authoring transform must resolve to a local TRS that reproduces the collider's world pose
        /// when applied under a proxy placed at the authoring's world pose.
        /// </summary>
        [Test]
        public void DecomposeTrs_ChildRelativeToAuthoring_ReproducesWorldPose()
        {
            var authoring = float4x4.TRS(
                new float3(10f, 3f, -4f),
                quaternion.Euler(0f, math.radians(45f), 0f),
                new float3(1f, 1f, 1f));
            var colliderWorld = float4x4.TRS(
                new float3(11.5f, 3.5f, -4.25f),
                quaternion.Euler(math.radians(15f), math.radians(80f), 0f),
                new float3(1f, 1f, 1f));

            var local = math.mul(math.inverse(authoring), colliderWorld);
            StaticColliderBake.DecomposeTrs(local, out var position, out var rotation, out var scale);

            // Re-composing under the authoring transform must land back on the collider's world pose.
            var recomposed = math.mul(authoring, float4x4.TRS(position, rotation, scale));
            AssertApproximately(colliderWorld.c3.xyz, recomposed.c3.xyz, "recomposed world position");
            AssertApproximately(
                math.mul(colliderWorld, new float4(0f, 0f, 1f, 0f)).xyz,
                math.mul(recomposed, new float4(0f, 0f, 1f, 0f)).xyz,
                "recomposed world forward");
        }

        [Test]
        public void IsLossyPlacement_UniformScale_IsNeverLossy()
        {
            var rotated = quaternion.Euler(math.radians(30f), math.radians(60f), 0f);
            Assert.IsFalse(StaticColliderBake.IsLossyPlacement(rotated, new float3(3f, 3f, 3f)));
        }

        [Test]
        public void IsLossyPlacement_NonUniformScaleWithoutRotation_IsNotLossy()
        {
            Assert.IsFalse(StaticColliderBake.IsLossyPlacement(quaternion.identity, new float3(1f, 5f, 2f)));
        }

        [Test]
        public void IsLossyPlacement_NonUniformScaleWithRotation_IsLossy()
        {
            var rotated = quaternion.Euler(0f, math.radians(45f), 0f);
            Assert.IsTrue(StaticColliderBake.IsLossyPlacement(rotated, new float3(1f, 5f, 2f)));
        }

        [Test]
        public void TryDescribe_PrimitiveColliders_AreSupported()
        {
            var go = new GameObject("probe");
            try
            {
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(2f, 3f, 4f);
                Assert.IsTrue(StaticColliderBake.TryDescribe(box, out var boxElement));
                Assert.AreEqual(ProxyColliderShape.Box, boxElement.Shape);
                AssertApproximately(new float3(2f, 3f, 4f), boxElement.Size, "box size");

                var capsule = go.AddComponent<CapsuleCollider>();
                capsule.direction = 2;
                capsule.height = 5f;
                Assert.IsTrue(StaticColliderBake.TryDescribe(capsule, out var capsuleElement));
                Assert.AreEqual(ProxyColliderShape.Capsule, capsuleElement.Shape);
                Assert.AreEqual(2, capsuleElement.Direction);
                Assert.That(capsuleElement.Height, Is.EqualTo(5f).Within(Tolerance));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TryDescribe_MeshCollider_IsRejected()
        {
            var go = new GameObject("probe");
            try
            {
                var mesh = go.AddComponent<MeshCollider>();
                Assert.IsFalse(StaticColliderBake.TryDescribe(mesh, out _),
                    "MeshCollider must be rejected so the baker can warn instead of baking a wrong shape.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SelectNearest_UnderCap_KeepsEveryCandidateInOrder()
        {
            var distances = new List<float> { 9f, 1f, 4f };
            var result = new List<int>();

            StaticColliderBridge.SelectNearest(distances, 10, result);

            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, result);
        }

        [Test]
        public void SelectNearest_NoCap_KeepsEveryCandidate()
        {
            var distances = new List<float> { 9f, 1f, 4f };
            var result = new List<int>();

            StaticColliderBridge.SelectNearest(distances, 0, result);

            Assert.AreEqual(3, result.Count);
        }

        [Test]
        public void SelectNearest_OverCap_KeepsTheNearest()
        {
            var distances = new List<float> { 100f, 1f, 50f, 4f, 25f };
            var result = new List<int>();

            StaticColliderBridge.SelectNearest(distances, 3, result);

            Assert.AreEqual(3, result.Count);
            CollectionAssert.AreEquivalent(new[] { 1, 3, 4 }, result, "the three nearest candidates must win");
            CollectionAssert.DoesNotContain(result, 0, "the furthest candidate must be dropped");
        }

        [Test]
        public void SelectNearest_TiedDistances_BreaksTiesByIndexForStability()
        {
            var distances = new List<float> { 5f, 5f, 5f, 5f };
            var result = new List<int>();

            StaticColliderBridge.SelectNearest(distances, 2, result);

            CollectionAssert.AreEqual(new[] { 0, 1 }, result,
                "ties must resolve deterministically, or proxies churn between reconciles");
        }
    }
}
