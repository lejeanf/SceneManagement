using System.Collections.Generic;
using jeanf.validationTools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// One baked spawn record: a GameObject-world subtree (world-space UI, audio rig — anything
    /// baking would strip) that <see cref="HybridPrefabBridge"/> must keep alive in the main world,
    /// posed relative to the authoring root's entity, while that entity's section is streamed in.
    /// </summary>
    public struct HybridPrefabSpawnElement : IBufferElementData
    {
        /// <summary>The prefab (or prefab-asset subtree) to instantiate. A <see cref="UnityObjectRef{T}"/>
        /// survives SubScene serialization, so no companion object is needed in the main scene.</summary>
        public UnityObjectRef<GameObject> Prefab;

        /// <summary>Subtree pose relative to the authoring root at bake time. Instance world pose =
        /// root LocalToWorld × this.</summary>
        public float4x4 LocalFromRoot;

        /// <summary>1 = explicit-prefab mode: the prefab root's own localScale composes with the
        /// resolved world scale (the record is a placement marker that knows nothing of the prefab).
        /// 0 = self-referencing mode: LocalFromRoot already contains the subtree root's own local
        /// TRS — composing again would double-apply it.</summary>
        public byte ComposePrefabScale;
    }

    /// <summary>
    /// Entities to remove from the baked world because the bridge respawns their GameObject
    /// counterparts — leaving them baked would duplicate them (Entities Graphics companion-bakes
    /// AudioSource, for instance, so a baked ambiance loop would play against the spawned copy).
    /// Mirrors Unity's own BakingOnlyEntityAuthoringBaker.BakingOnlyChildren pattern: a baker may
    /// only touch its own entity, so the strip is applied by <see cref="HybridPrefabStripBakingSystem"/>.
    /// </summary>
    [TemporaryBakingType]
    public struct HybridStrippedEntity : IBufferElementData
    {
        public Entity Value;
    }

    /// <summary>
    /// Keeps the GameObject-world parts of a prefab alive through SubScene baking. Put ONE of these
    /// on the prefab root (e.g. the Elevator): at bake time it sweeps the hierarchy for subtrees
    /// that belong to the GameObject world — any subtree containing a Canvas or an AudioSource and
    /// no Renderer (meshes stay baked) — and for each one it records the corresponding subtree of
    /// the PREFAB ASSET plus its pose relative to this root. At runtime
    /// <see cref="HybridPrefabBridge"/> instantiates those subtrees in the main world while the
    /// section is streamed in, and every swept entity is stripped from the baked world
    /// (<see cref="BakingOnlyEntity"/>) so nothing exists twice — no companion AudioSources, no
    /// dead UI entities.
    ///
    /// The subtrees stay real children of the prefab: fully visible and editable in the editor,
    /// and functional as-is in a classic additive scene (bakers never run there — dual-world for
    /// free). Subtrees the sweep can't see (a rig with neither Canvas nor AudioSource, or a
    /// SteamAudio proxy) go in <see cref="additionalSubtrees"/>; <see cref="excludedSubtrees"/>
    /// vetoes a detected one. The inspector lists what is detected, and which GameObject-world
    /// components remain in baked territory (SteamAudioGeometry on meshes, for instance) so
    /// nothing disappears silently.
    ///
    /// Additionally, <see cref="prefab"/> spawns an explicit prefab at this object's pose,
    /// TooltipAuthoring-style. It COMPOSES with the sweep: a childless marker object just spawns
    /// the prefab, while a prefab root can carry both an explicit prefab and swept subtrees.
    /// (Don't assign a prefab whose content also lives under this object — that spawns it twice;
    /// exclude or delete the live subtree instead.)
    ///
    /// Caveat of the sweep: per-instance overrides on a swept subtree are NOT spawned — the
    /// prefab-asset counterpart is. Apply overrides to the prefab itself.
    /// </summary>
    [DisallowMultipleComponent]
    public class HybridPrefabAuthoring : MonoBehaviour, IValidatable
    {
        [Tooltip("Optional explicit prefab to spawn at this object's pose, in ADDITION to the sweep below. " +
                 "On a childless placement marker this is the only spawn; on a prefab root it composes with " +
                 "the swept subtrees. Don't reference a prefab whose content also lives under this object — " +
                 "that would spawn it twice.")]
        public GameObject prefab;

        [Tooltip("Subtrees the sweep cannot detect (no Canvas, AudioSource or SteamAudioDynamicObject inside) " +
                 "that must still be respawned in the main world. Usually stays empty.")]
        public List<Transform> additionalSubtrees = new List<Transform>();

        [Tooltip("Detected subtrees that must NOT be respawned. The sweep skips them and they bake normally.")]
        public List<Transform> excludedSubtrees = new List<Transform>();

        /// <summary>
        /// Valid when every spawn source is resolvable: swept subtrees need this object to be part
        /// of a prefab (their asset counterparts must exist at bake time), while an explicit prefab
        /// needs nothing. Detection results and stranded GameObject-world components are surfaced
        /// by the custom inspector, not here.
        /// </summary>
        public bool IsValid
        {
            get
            {
#if UNITY_EDITOR
                if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(gameObject)) return true;
                if (UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject) != null) return true;

                // Not part of a prefab: only the explicit-prefab record can spawn, so this is valid
                // exactly when the sweep has nothing to respawn and a prefab is assigned.
                var spawnRoots = new List<Transform>();
                HybridPrefabScan.CollectSpawnRoots(transform, this, spawnRoots);
                return spawnRoots.Count == 0 && prefab != null;
#else
                return true;
#endif
            }
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireCube(transform.position, Vector3.one * 0.15f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.25f);
        }

        class Baker : Baker<HybridPrefabAuthoring>
        {
            public override void Bake(HybridPrefabAuthoring authoring)
            {
                // Renderable = guaranteed LocalToWorld; the bridge poses every spawn off this root.
                var entity = GetEntity(TransformUsageFlags.Renderable);
                var spawns = AddBuffer<HybridPrefabSpawnElement>(entity);

                // Explicit prefab: one marker record at this pose. Composes with the sweep below.
                if (authoring.prefab != null)
                {
                    DependsOn(authoring.prefab);
                    spawns.Add(new HybridPrefabSpawnElement
                    {
                        Prefab = authoring.prefab,
                        LocalFromRoot = float4x4.identity,
                        ComposePrefabScale = 1,
                    });
                }

                // Sweep: always runs, so a prefab root can carry both kinds of spawn.
                var spawnRoots = new List<Transform>();
                HybridPrefabScan.CollectSpawnRoots(authoring.transform, authoring, spawnRoots);
                if (spawnRoots.Count == 0)
                {
                    if (authoring.prefab == null)
                    {
                        Debug.LogWarning($"{HybridPrefabBake.LogPrefix} HybridPrefabAuthoring on '{authoring.name}': the sweep " +
                            "found no GameObject-world subtree (Canvas or AudioSource, no Renderer), no additionalSubtrees " +
                            "are set and no explicit prefab is assigned — nothing will be respawned in the main world.",
                            authoring.gameObject);
                    }
                    return;
                }

                var stripped = AddBuffer<HybridStrippedEntity>(entity);
                var rootWorldToLocal = (float4x4)authoring.transform.worldToLocalMatrix;

                foreach (var spawnRoot in spawnRoots)
                {
                    GameObject source = null;
#if UNITY_EDITOR
                    source = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(spawnRoot.gameObject);
#endif
                    if (source == null)
                    {
                        Debug.LogError($"{HybridPrefabBake.LogPrefix} HybridPrefabAuthoring on '{authoring.name}': subtree " +
                            $"'{spawnRoot.name}' has no prefab-asset counterpart (is '{authoring.name}' a prefab?), so it " +
                            "cannot be respawned — it will be missing from the main world.", spawnRoot.gameObject);
                        continue;
                    }

                    DependsOn(source);
                    spawns.Add(new HybridPrefabSpawnElement
                    {
                        Prefab = source,
                        LocalFromRoot = math.mul(rootWorldToLocal, (float4x4)spawnRoot.localToWorldMatrix),
                        ComposePrefabScale = 0,
                    });

                    // Everything under a respawned subtree leaves the baked world, or it exists twice
                    // (companion AudioSources would even keep playing with their Samplers stripped).
                    foreach (var child in spawnRoot.GetComponentsInChildren<Transform>(true))
                        stripped.Add(new HybridStrippedEntity { Value = GetEntity(child.gameObject, TransformUsageFlags.Dynamic) });
                }
            }
        }
    }

    /// <summary>
    /// Applies <see cref="BakingOnlyEntity"/> to every entity recorded in a
    /// <see cref="HybridStrippedEntity"/> buffer, removing the swept subtrees from the baked world.
    /// Runs in the baking world only; the buffer itself is [TemporaryBakingType] and never ships.
    /// Same structure as Unity's BakingOnlyEntityAuthoringBakingSystem.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial class HybridPrefabStripBakingSystem : SystemBase
    {
        private EntityQuery _query;

        protected override void OnCreate()
        {
            _query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<HybridStrippedEntity>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                .Build(this);
        }

        protected override void OnUpdate()
        {
            var roots = _query.ToEntityArray(Allocator.Temp);
            var toStrip = new NativeList<Entity>(64, Allocator.Temp);
            foreach (var root in roots)
            {
                var buffer = EntityManager.GetBuffer<HybridStrippedEntity>(root);
                foreach (var element in buffer)
                    if (element.Value != Entity.Null && EntityManager.Exists(element.Value))
                        toStrip.Add(element.Value);
            }
            EntityManager.AddComponent<BakingOnlyEntity>(toStrip.AsArray());
            toStrip.Dispose();
            roots.Dispose();
        }
    }

    /// <summary>
    /// The sweep rules, kept static and UnityEditor-free so the baker, the custom inspector and
    /// edit-mode tests all agree on what gets respawned.
    /// </summary>
    public static class HybridPrefabScan
    {
        /// <summary>
        /// A subtree belongs wholly to the GameObject world when it renders nothing that baking
        /// keeps (no Renderer anywhere — CanvasRenderer is not a Renderer, so UI passes) and
        /// contains at least one component baking is known to break: a Canvas, an AudioSource, or
        /// a SteamAudioDynamicObject (an empty proxy carrying exported Steam Audio geometry —
        /// matched by type name, this package has no SteamAudio dependency).
        /// </summary>
        public static bool SubtreeQualifies(Transform node)
        {
            if (node.GetComponentInChildren<Renderer>(true) != null) return false;
            if (node.GetComponentInChildren<Canvas>(true) != null) return true;
            if (node.GetComponentInChildren<AudioSource>(true) != null) return true;

            foreach (var component in node.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().Name == "SteamAudioDynamicObject")
                    return true;
            return false;
        }

        /// <summary>
        /// Walks down from <paramref name="root"/>, collecting the HIGHEST subtrees to respawn:
        /// manual includes and qualifying subtrees are taken whole (no descent inside), excluded
        /// subtrees are skipped whole, and a descendant carrying its own
        /// <see cref="HybridPrefabAuthoring"/> is left alone — it bakes itself.
        /// </summary>
        public static void CollectSpawnRoots(Transform root, HybridPrefabAuthoring authoring, List<Transform> results)
        {
            Collect(root, root, authoring, results);
        }

        private static void Collect(Transform node, Transform root, HybridPrefabAuthoring authoring, List<Transform> results)
        {
            if (authoring.excludedSubtrees != null && authoring.excludedSubtrees.Contains(node)) return;
            if (node != root && node.GetComponent<HybridPrefabAuthoring>() != null) return;

            var included = authoring.additionalSubtrees != null && authoring.additionalSubtrees.Contains(node);
            if (included || SubtreeQualifies(node))
            {
                results.Add(node);
                return;
            }

            for (var i = 0; i < node.childCount; i++)
                Collect(node.GetChild(i), root, authoring, results);
        }

        /// <summary>
        /// GameObject-world components that stay in BAKED territory — not under any collected
        /// subtree — and therefore get stripped (or companion-baked) without a respawned
        /// counterpart: Canvas, AudioSource, and anything SteamAudio* (matched by type name; this
        /// package has no SteamAudio dependency). Surfaced by the inspector so a door's
        /// SteamAudioDynamicObject or a cage's SteamAudioGeometry never vanishes silently.
        /// </summary>
        public static void FindStrandedComponents(Transform root, List<Transform> spawnRoots, List<Component> results)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                if (!(component is Canvas || component is AudioSource ||
                      component.GetType().Name.StartsWith("SteamAudio"))) continue;

                var covered = false;
                foreach (var spawnRoot in spawnRoots)
                {
                    if (!component.transform.IsChildOf(spawnRoot)) continue;
                    covered = true;
                    break;
                }
                if (!covered) results.Add(component);
            }
        }
    }

    /// <summary>
    /// Pure helpers, static and side-effect free so they can be unit tested without a bake or a
    /// live world (same convention as <see cref="StaticColliderBake"/>).
    /// </summary>
    public static class HybridPrefabBake
    {
        public const string LogPrefix = "[SceneManagement]";

        /// <summary>
        /// The localScale a spawned instance's root should get (it is parented under an
        /// identity-scale container), from the scale decomposed out of rootL2W × LocalFromRoot.
        /// Explicit-prefab mode composes the prefab root's authored localScale on top; sweep mode
        /// uses the decomposed scale alone, because LocalFromRoot already carries the subtree
        /// root's own local TRS.
        /// </summary>
        public static Vector3 ResolveInstanceScale(Vector3 decomposedScale, Vector3 prefabRootLocalScale, bool composePrefabScale)
        {
            return composePrefabScale ? Vector3.Scale(prefabRootLocalScale, decomposedScale) : decomposedScale;
        }
    }
}
