using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Bridges SubScene-authored GameObject constructs (<see cref="HybridPrefabAuthoring"/>) back
    /// into the GameObject world. A baked <see cref="HybridPrefabSpawnElement"/> buffer is a list
    /// of spawn records on one root entity: while the root exists (its section is streamed in)
    /// this bridge keeps an instance of each recorded prefab subtree alive at rootL2W × LocalFromRoot
    /// in the main world; streaming out destroys them. Mirrors <c>TooltipDataBridge</c>'s
    /// reconcile: spawns never move and only exist while their SubScene is loaded, so a slow
    /// re-scan is enough. Drop one on a persistent GameObject; it's a singleton.
    /// </summary>
    public class HybridPrefabBridge : MonoBehaviour
    {
        public static HybridPrefabBridge Instance { get; private set; }

        [SerializeField] private bool isDebug = false;
        [Tooltip("Seconds between re-scans of baked spawn entities (handles SubScene streaming). Spawns never move, so this can be slow.")]
        [SerializeField] private float refreshInterval = 0.25f;

        private EntityManager _em;
        private EntityQuery _query;
        private bool _worldReady;
        private float _timer;
        private int _lastLoggedCount = -1;

        private Transform _container;

        private readonly Dictionary<Entity, GameObject[]> _instances = new Dictionary<Entity, GameObject[]>(32);
        private readonly HashSet<Entity> _seen = new HashSet<Entity>();
        private readonly HashSet<Entity> _invalid = new HashSet<Entity>();
        private readonly List<Entity> _toRemove = new List<Entity>(8);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            // A root container at identity scale, so an instance's resolved scale is applied verbatim.
            _container = new GameObject("SubSceneHybridPrefabs").transform;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_container != null) Destroy(_container.gameObject);
        }

        private void OnEnable()
        {
            _timer = float.MaxValue; // scan on the first Update
            TryInitWorld();
        }

        private void TryInitWorld()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) { _worldReady = false; return; }
            _em = world.EntityManager;
            // IncludeDisabledEntities: the authoring root may be deactivated in a variant; the
            // spawn records must still be seen so behaviour matches the enabled case.
            _query = _em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<HybridPrefabSpawnElement>() },
                Options = EntityQueryOptions.IncludeDisabledEntities,
            });
            _worldReady = true;
        }

        private void Update()
        {
            if (!_worldReady)
            {
                TryInitWorld();
                if (!_worldReady) return;
            }

            _timer += Time.deltaTime;
            if (_timer < refreshInterval) return;
            _timer = 0f;

            Reconcile();
        }

        private void Reconcile()
        {
            _seen.Clear();
            var entities = _query.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                _seen.Add(e);
                if (_invalid.Contains(e)) continue;
                if (_instances.ContainsKey(e)) continue; // static: place once
                if (!_em.HasComponent<LocalToWorld>(e)) continue; // pose not resolved yet — retry next tick

                var buffer = _em.GetBuffer<HybridPrefabSpawnElement>(e);
                if (buffer.Length == 0) { _invalid.Add(e); continue; } // sweep found nothing; already warned at bake

                var rootL2W = _em.GetComponentData<LocalToWorld>(e);
                var spawned = new GameObject[buffer.Length];
                var missing = false;
                for (var j = 0; j < buffer.Length; j++)
                {
                    var element = buffer[j];
                    if (element.Prefab.Value == null)
                    {
                        // The baker refuses unresolvable sources, so this means the asset went missing after baking.
                        missing = true;
                        Debug.LogWarning($"{HybridPrefabBake.LogPrefix} HybridPrefabBridge: baked spawn e{e.Index}[{j}] has no prefab — re-bake its SubScene.", this);
                        continue;
                    }
                    spawned[j] = Spawn(element, rootL2W);
                }
                _instances[e] = spawned;
                if (missing) _invalid.Add(e); // keep what did spawn, but flag the record
            }
            entities.Dispose();

            if (isDebug && _instances.Count != _lastLoggedCount)
            {
                _lastLoggedCount = _instances.Count;
                Debug.Log($"{HybridPrefabBake.LogPrefix} HybridPrefabBridge: {_instances.Count} SubScene hybrid root(s) spawned.", this);
            }

            _toRemove.Clear();
            foreach (var kv in _instances)
                if (!_seen.Contains(kv.Key)) _toRemove.Add(kv.Key);

            for (var i = 0; i < _toRemove.Count; i++)
            {
                if (_instances.TryGetValue(_toRemove[i], out var gos))
                    foreach (var go in gos)
                        if (go != null) Destroy(go);
                _instances.Remove(_toRemove[i]);
            }
            _invalid.RemoveWhere(e => !_seen.Contains(e)); // re-warn if a fixed bake reloads
        }

        private GameObject Spawn(in HybridPrefabSpawnElement element, in LocalToWorld rootL2W)
        {
            var prefab = element.Prefab.Value;
            var instance = Instantiate(prefab, _container);
            var t = instance.transform;

            var world = math.mul(rootL2W.Value, element.LocalFromRoot);
            StaticColliderBake.DecomposeTrs(world, out var position, out var rotation, out var scale);
            t.SetPositionAndRotation(position, rotation);
            t.localScale = HybridPrefabBake.ResolveInstanceScale(
                new Vector3(scale.x, scale.y, scale.z), t.localScale, element.ComposePrefabScale == 1);

            // A subtree deactivated in its prefab still means "spawn me"; an instance that stays
            // inactive forever would be pointless.
            if (!instance.activeSelf) instance.SetActive(true);

            if (isDebug) Debug.Log($"{HybridPrefabBake.LogPrefix} HybridPrefabBridge: spawned '{prefab.name}' at {(Vector3)position}.", this);
            return instance;
        }
    }
}
