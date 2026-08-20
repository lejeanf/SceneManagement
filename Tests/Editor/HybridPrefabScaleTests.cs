using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace jeanf.scenemanagement.Tests
{
    /// <summary>
    /// Locks the two pure parts of hybrid prefab spawning: the sweep (which subtrees leave the
    /// baked world and get respawned — get it wrong and audio/UI silently vanish or exist twice)
    /// and the pose/scale rule (composing in sweep mode double-applies the subtree root's local
    /// TRS, replacing in marker mode loses the prefab's authored scale).
    /// </summary>
    public class HybridPrefabScaleTests
    {
        private const float Tolerance = 1e-3f;
        private readonly List<GameObject> _spawned = new List<GameObject>();

        private GameObject New(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            if (parent == null) _spawned.Add(go);
            return go;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private static void AssertApproximately(Vector3 expected, Vector3 actual, string label)
        {
            Assert.That(Vector3.Distance(expected, actual), Is.LessThan(Tolerance),
                $"{label}: expected {expected}, got {actual}");
        }

        // ---- sweep ----

        private HybridPrefabAuthoring BuildElevatorLike(out Transform audio, out Transform ui, out Transform cage)
        {
            var root = New("Elevator");
            var authoring = root.AddComponent<HybridPrefabAuthoring>();

            cage = New("ElevatorCage", root.transform).transform;
            cage.gameObject.AddComponent<MeshRenderer>();

            var doors = New("ElevatorDoors", root.transform).transform;
            New("Door_Right", doors).AddComponent<MeshRenderer>();

            audio = New("ElevatorAudioAmbiance", root.transform).transform;
            New("AudioSource_ACVent", audio).AddComponent<AudioSource>();

            ui = New("ElevatorUI", root.transform).transform;
            New("CanvasRoot", ui).AddComponent<Canvas>();

            return authoring;
        }

        [Test]
        public void Sweep_SelectsAudioAndUiSubtrees_SkipsMeshSubtrees()
        {
            var authoring = BuildElevatorLike(out var audio, out var ui, out _);

            var results = new List<Transform>();
            HybridPrefabScan.CollectSpawnRoots(authoring.transform, authoring, results);

            Assert.That(results, Is.EquivalentTo(new[] { audio, ui }));
        }

        [Test]
        public void Sweep_ExcludedSubtreeIsSkippedWhole()
        {
            var authoring = BuildElevatorLike(out var audio, out var ui, out _);
            authoring.excludedSubtrees.Add(ui);

            var results = new List<Transform>();
            HybridPrefabScan.CollectSpawnRoots(authoring.transform, authoring, results);

            Assert.That(results, Is.EquivalentTo(new[] { audio }));
        }

        [Test]
        public void Sweep_AdditionalSubtreeIsTakenEvenWithoutTriggerComponents()
        {
            var authoring = BuildElevatorLike(out var audio, out var ui, out _);
            var proxy = New("SteamAudioProxy", authoring.transform).transform;
            authoring.additionalSubtrees.Add(proxy);

            var results = new List<Transform>();
            HybridPrefabScan.CollectSpawnRoots(authoring.transform, authoring, results);

            Assert.That(results, Is.EquivalentTo(new[] { audio, ui, proxy }));
        }

        [Test]
        public void Sweep_TakesHighestQualifyingSubtree_NotItsChildren()
        {
            var authoring = BuildElevatorLike(out var audio, out _, out _);

            var results = new List<Transform>();
            HybridPrefabScan.CollectSpawnRoots(authoring.transform, authoring, results);

            Assert.That(results, Has.Member(audio));
            Assert.That(results, Has.No.Member(audio.GetChild(0)));
        }

        [Test]
        public void Sweep_SubtreeWithRendererNeverQualifies_ComponentIsReportedStranded()
        {
            var authoring = BuildElevatorLike(out _, out _, out var cage);
            // An AudioSource living directly on baked geometry cannot be respawned.
            cage.gameObject.AddComponent<AudioSource>();

            var results = new List<Transform>();
            HybridPrefabScan.CollectSpawnRoots(authoring.transform, authoring, results);
            Assert.That(results, Has.No.Member(cage));

            var stranded = new List<Component>();
            HybridPrefabScan.FindStrandedComponents(authoring.transform, results, stranded);
            Assert.That(stranded, Has.Some.Matches<Component>(c => c is AudioSource && c.transform == cage));
        }

        // ---- pose / scale ----

        [Test]
        public void SpawnPose_RootTimesLocal_ReproducesSubtreeWorldPose()
        {
            var rootL2W = float4x4.TRS(new float3(10f, 0f, 5f), quaternion.RotateY(math.radians(90f)), new float3(1f));
            var localFromRoot = float4x4.TRS(new float3(0f, 2f, 1f), quaternion.identity, new float3(2f, 2f, 2f));

            var world = math.mul(rootL2W, localFromRoot);
            StaticColliderBake.DecomposeTrs(world, out var position, out var rotation, out var scale);

            // Y-rotated root: local (0,2,1) lands at root + (1,2,0).
            AssertApproximately(new Vector3(11f, 2f, 5f), new Vector3(position.x, position.y, position.z), "position");
            AssertApproximately(new Vector3(2f, 2f, 2f), new Vector3(scale.x, scale.y, scale.z), "scale");
            var forward = math.mul(rotation, new float3(0f, 0f, 1f));
            AssertApproximately(new Vector3(1f, 0f, 0f), new Vector3(forward.x, forward.y, forward.z), "forward");
        }

        [Test]
        public void SweepMode_UsesDecomposedScaleAlone()
        {
            var scale = HybridPrefabBake.ResolveInstanceScale(new Vector3(2f, 3f, 4f), new Vector3(9f, 9f, 9f), composePrefabScale: false);
            AssertApproximately(new Vector3(2f, 3f, 4f), scale, "prefab root scale must be ignored");
        }

        [Test]
        public void MarkerMode_ComposesPrefabScale()
        {
            var scale = HybridPrefabBake.ResolveInstanceScale(new Vector3(2f, 2f, 2f), new Vector3(0.5f, 1f, 3f), composePrefabScale: true);
            AssertApproximately(new Vector3(1f, 2f, 6f), scale, "prefab root scale must compose");
        }
    }
}
