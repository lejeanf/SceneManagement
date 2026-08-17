using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Bridges props baked by <see cref="StaticColliderAuthoring"/> back into the GameObject world.
    /// Baking strips <see cref="Collider"/> components and this project has no ECS physics, so a prop
    /// inside a SubScene is rendered geometry the player walks straight through. This queries the
    /// baked shapes and spawns plain PhysX colliders over the props near the player — which is what
    /// the <c>CharacterController</c> (and every raycast) actually collides with.
    ///
    /// Props are static, so this is a light reconcile — place once, drop when the prop leaves range
    /// or its section streams out — rather than a per-frame moving-collider pool. Mirrors
    /// SeatDataBridge and the door system's data bridge + collider pool.
    ///
    /// Drop one on an always-loaded GameObject; it's a singleton. Without it, baked props do not
    /// collide at all.
    /// </summary>
    public class StaticColliderBridge : MonoBehaviour
    {
        private const string LogPrefix = "[SceneManagement]";

        public static StaticColliderBridge Instance { get; private set; }

        [SerializeField] private bool isDebug = false;

        [Tooltip("Seconds between re-scans of baked props (handles SubScene streaming) and proxy reconciles. Props never move, so this can be slow.")]
        [SerializeField] private float refreshInterval = 0.25f;

        [Tooltip("Only props within this distance of the camera get live colliders. <= 0 means no limit.")]
        [SerializeField] private float cullingDistance = 30f;

        [Tooltip("Hard cap on simultaneously spawned props. The nearest ones win. <= 0 means no cap. Raise it if props stop blocking on a dense floor.")]
        [SerializeField] private int maxProxies = 128;

        private struct PropInfo
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 LossyScale;
        }

        private EntityManager _em;
        private EntityQuery _propQuery;
        private bool _worldReady;
        private float _timer;
        private int _lastLoggedCount = -1;
        private bool _capWarned;

        private Transform _container;
        private Transform _camera;

        private readonly Dictionary<Entity, PropInfo> _props = new Dictionary<Entity, PropInfo>(128);
        private readonly Dictionary<Entity, GameObject> _proxies = new Dictionary<Entity, GameObject>(128);
        private readonly List<Entity> _candidates = new List<Entity>(128);
        private readonly List<float> _candidateDistances = new List<float>(128);
        private readonly List<int> _selected = new List<int>(128);
        private readonly List<Entity> _toRemove = new List<Entity>(32);
        private readonly HashSet<Entity> _seen = new HashSet<Entity>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            // A root container at identity scale, so a proxy's localScale == the prop's lossy scale.
            _container = new GameObject("StaticColliderProxy_Pool").transform;
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
            _propQuery = _em.CreateEntityQuery(ComponentType.ReadOnly<ProxyColliderElement>());
            _worldReady = true;
        }

        private void Update()
        {
            if (!_worldReady)
            {
                TryInitWorld();
                if (!_worldReady) return;
            }
            if (_camera == null && Camera.main != null) _camera = Camera.main.transform;

            _timer += Time.deltaTime;
            if (_timer < refreshInterval) return;
            _timer = 0f;

            Refresh();
            Reconcile();
        }

        // --- Query baked props -------------------------------------------------

        private void Refresh()
        {
            _props.Clear();

            var entities = _propQuery.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!_em.HasComponent<LocalToWorld>(e)) continue;

                var m = _em.GetComponentData<LocalToWorld>(e).Value;
                _props[e] = new PropInfo
                {
                    Position = m.c3.xyz,
                    Rotation = ExtractRotation(m),
                    LossyScale = new Vector3(math.length(m.c0.xyz), math.length(m.c1.xyz), math.length(m.c2.xyz)),
                };
            }
            entities.Dispose();

            if (isDebug && _props.Count != _lastLoggedCount)
            {
                _lastLoggedCount = _props.Count;
                Debug.Log($"{LogPrefix} StaticColliderBridge: {_props.Count} baked prop(s) available.", this);
            }
        }

        private static Quaternion ExtractRotation(float4x4 m)
        {
            var axisX = m.c0.xyz;
            var axisY = m.c1.xyz;
            var axisZ = m.c2.xyz;
            var scaleX = math.length(axisX);
            var scaleY = math.length(axisY);
            var scaleZ = math.length(axisZ);
            axisX = scaleX > 1e-8f ? axisX / scaleX : new float3(1f, 0f, 0f);
            axisY = scaleY > 1e-8f ? axisY / scaleY : new float3(0f, 1f, 0f);
            axisZ = scaleZ > 1e-8f ? axisZ / scaleZ : new float3(0f, 0f, 1f);
            return new quaternion(new float3x3(axisX, axisY, axisZ));
        }

        // --- Spawn / drop GameObject collider proxies --------------------------

        private void Reconcile()
        {
            var haveCam = _camera != null;
            var camPos = haveCam ? _camera.position : Vector3.zero;
            var cull = cullingDistance > 0f && haveCam;
            var cullSqr = cullingDistance * cullingDistance;

            _candidates.Clear();
            _candidateDistances.Clear();
            foreach (var kv in _props)
            {
                var distanceSq = haveCam ? (kv.Value.Position - camPos).sqrMagnitude : 0f;
                if (cull && distanceSq > cullSqr) continue;
                _candidates.Add(kv.Key);
                _candidateDistances.Add(distanceSq);
            }

            SelectNearest(_candidateDistances, maxProxies, _selected);

            // A silently truncated pool reads as "collision is broken" and is miserable to diagnose,
            // so say it out loud — once, not every reconcile.
            if (maxProxies > 0 && _candidates.Count > maxProxies)
            {
                if (!_capWarned)
                {
                    _capWarned = true;
                    Debug.LogWarning($"{LogPrefix} StaticColliderBridge: {_candidates.Count} props are in range but " +
                        $"Max Proxies is {maxProxies} — the {_candidates.Count - maxProxies} furthest do NOT block the " +
                        "player. Raise Max Proxies, or lower Culling Distance so fewer compete.", this);
                }
            }
            else
            {
                _capWarned = false;
            }

            _seen.Clear();
            for (var i = 0; i < _selected.Count; i++)
            {
                var entity = _candidates[_selected[i]];
                _seen.Add(entity);
                if (_proxies.TryGetValue(entity, out var existing) && existing != null) continue; // static: place once
                _proxies[entity] = CreateProxy(entity, _props[entity]);
            }

            _toRemove.Clear();
            foreach (var kv in _proxies)
                if (kv.Value == null || !_seen.Contains(kv.Key)) _toRemove.Add(kv.Key);

            for (var i = 0; i < _toRemove.Count; i++)
            {
                if (_proxies.TryGetValue(_toRemove[i], out var go) && go != null) Destroy(go);
                _proxies.Remove(_toRemove[i]);
            }
        }

        /// <summary>
        /// Picks which candidates get a proxy when more are in range than the cap allows: nearest
        /// first, ties broken by index so the choice is stable frame to frame. A cap of 0 or less
        /// means "no cap". Pure and static so it can be unit tested without a world.
        /// </summary>
        public static void SelectNearest(List<float> distancesSq, int maxCount, List<int> result)
        {
            result.Clear();
            if (distancesSq == null) return;

            for (var i = 0; i < distancesSq.Count; i++) result.Add(i);
            if (maxCount <= 0 || result.Count <= maxCount) return;

            result.Sort((a, b) =>
            {
                var compare = distancesSq[a].CompareTo(distancesSq[b]);
                return compare != 0 ? compare : a.CompareTo(b);
            });
            result.RemoveRange(maxCount, result.Count - maxCount);
        }

        private GameObject CreateProxy(Entity entity, in PropInfo info)
        {
            var root = new GameObject("StaticColliderProxy");
            root.transform.SetParent(_container, false);
            root.transform.SetPositionAndRotation(info.Position, info.Rotation);
            root.transform.localScale = info.LossyScale;

            var buffer = _em.GetBuffer<ProxyColliderElement>(entity, true);
            for (var i = 0; i < buffer.Length; i++)
            {
                var element = buffer[i];

                // One child per baked collider: each carries its own rotation relative to the prop,
                // which a shared GameObject could not express.
                var child = new GameObject("Collider");
                child.layer = element.Layer;
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = element.LocalPosition;
                child.transform.localRotation = element.LocalRotation;
                child.transform.localScale = element.LocalScale;

                AttachCollider(child, element);
            }

            return root;
        }

        private static void AttachCollider(GameObject target, in ProxyColliderElement element)
        {
            var isTrigger = element.IsTrigger == 1;
            switch (element.Shape)
            {
                case ProxyColliderShape.Sphere:
                    var sphere = target.AddComponent<SphereCollider>();
                    sphere.center = element.Center;
                    sphere.radius = element.Radius;
                    sphere.isTrigger = isTrigger;
                    break;
                case ProxyColliderShape.Capsule:
                    var capsule = target.AddComponent<CapsuleCollider>();
                    capsule.center = element.Center;
                    capsule.radius = element.Radius;
                    capsule.height = element.Height;
                    capsule.direction = element.Direction;
                    capsule.isTrigger = isTrigger;
                    break;
                default:
                    var box = target.AddComponent<BoxCollider>();
                    box.center = element.Center;
                    box.size = element.Size;
                    box.isTrigger = isTrigger;
                    break;
            }
        }

        /// <summary>Diagnostics: how many props are currently backed by real colliders.</summary>
        public int ActiveProxyCount => _proxies.Count;
    }
}
