using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Assigns zones to GameObjects by testing their pivot against the World-SubScene volume
    /// entities — the same volumes and the same <see cref="VolumeMath"/> test the player uses.
    /// Replaces the PhysX boundary-box approach (uvs ListObjectsWithinBoundary).
    ///
    /// Reads the ECS world through an EntityQuery (SeatDataBridge pattern); creates no entities.
    /// Objects register a Transform + callback; evaluation is event-driven (registration, moves,
    /// MarkDirty, volume streaming), Burst-parallel, and change-only on dispatch. Assignment and
    /// proximity visibility are computed as independent results — a distance check can never
    /// affect a zone write.
    ///
    /// Drop one on a persistent GameObject; it's a singleton. Ships in Shadow mode: assignments
    /// are computed and observable (parity recorders, inspector) but no callbacks fire.
    /// </summary>
    public class ObjectZoneTrackingBridge : MonoBehaviour
    {
        private const string LogPrefix = "[SceneManagement]";

        public enum BridgeMode { Shadow, Active }
        public enum MatchKind : byte { None = 0, Exact = 1, Soft = 2, Fallback = 3 }

        public static ObjectZoneTrackingBridge Instance { get; private set; }

        [SerializeField] private bool isDebug = false;
        [SerializeField] private BridgeMode mode = BridgeMode.Shadow;
        [Tooltip("Seconds between dynamic-object position checks (hot tier: objects in the player's current zone or unassigned).")]
        [SerializeField] private float checkInterval = 0.2f;
        [Tooltip("Cold-tier dynamics (in another zone than the player) are checked every Nth interval.")]
        [SerializeField] private int coldTierMultiplier = 8;
        [Tooltip("Minimum movement before a dynamic object is re-tested.")]
        [SerializeField] private float minPositionChange = 0.1f;
        [Header("Coverage failsafe chain:")]
        [Tooltip("Epsilon pass: volume extents expanded by this many meters. Matches are logged as soft assignments.")]
        [SerializeField] private float coverageTolerance = 0.25f;
        [Tooltip("Nearest-candidate pass: max distance to the closest volume for a fallback assignment. <= 0 disables.")]
        [SerializeField] private float maxFallbackDistance = 2f;
        [Header("Zone-scoped proximity visibility:")]
        [Tooltip("Objects assigned to these zones get proximity visibility toggled by player distance (ProximityVisibilityChanged). Replaces the old per-boundary checkForProximity + setCustomList config (e.g. the RueDesCapucines street zone, threshold 3).")]
        [SerializeField] private List<ProximityZoneConfig> proximityVisibilityZones = new List<ProximityZoneConfig>();

        [Serializable]
        public struct ProximityZoneConfig
        {
            public Zone zone;
            [Range(0.1f, 10f)] public float threshold;
        }

        /// <summary>Fired on real assignment changes, Active mode only: (object, oldZone, newZone).</summary>
        public static event Action<GameObject, Zone, Zone> ZoneChanged;
        /// <summary>Fired in every mode whenever an assignment is computed (changed or not) — parity/T3 hook.</summary>
        public static event Action<GameObject, string, MatchKind> AssignmentComputed;
        /// <summary>Zone-scoped proximity visibility (the CustomVisibility replacement), Active mode only.</summary>
        public static event Action<GameObject, bool> ProximityVisibilityChanged;

        public BridgeMode Mode { get => mode; set => mode = value; }

        private class Entry
        {
            public Transform Tf;
            public GameObject Go; // cached so a destroyed Transform can still be removed from zone lists
            public Action<Zone> OnZoneChanged;
            public Action<bool> OnProximityChanged;
            public bool IsDynamic;
            public float ProximitySqr;
            public bool ProximityVisible;
            public Vector3 LastCheckedPos;
            public float LastCheckTime;
            public string ZoneId;          // empty = unassigned (pending)
            public Zone Zone;
            public MatchKind Kind;
            public bool WarnedNoMatch;
        }

        private struct PendingRegistration
        {
            public Transform Tf;
            public Action<Zone> OnZoneChanged;
            public bool IsDynamic;
            public Action<bool> OnProximityChanged;
            public float ProximityThreshold;
        }

        private static readonly List<PendingRegistration> PreRegistrations = new List<PendingRegistration>(64);

        private readonly List<Entry> _entries = new List<Entry>(256);
        private readonly Dictionary<Transform, Entry> _byTransform = new Dictionary<Transform, Entry>(256);
        private readonly List<Entry> _dirty = new List<Entry>(64);
        private readonly Dictionary<string, List<GameObject>> _objectsByZone = new Dictionary<string, List<GameObject>>(32);
        private static readonly List<GameObject> EmptyZoneList = new List<GameObject>();

        // Candidate volumes (current region, unlocked zones), rebuilt on region/streaming change.
        private NativeArray<float4x4> _volL2W;
        private NativeArray<float3> _volScale;
        private string[] _volZoneIds = Array.Empty<string>();
        private Zone[] _volZones = Array.Empty<Zone>();
        private bool _candidatesValid;

        private readonly Dictionary<string, string> _zoneToRegion = new Dictionary<string, string>(64);
        private readonly Dictionary<string, float> _proximityZoneSqr = new Dictionary<string, float>(8);
        private readonly HashSet<string> _candidateZoneIds = new HashSet<string>();

        private EntityManager _em;
        private EntityQuery _volumeQuery;
        private bool _worldReady;
        private int _lastVolumeCount = -1;

        private string _currentRegionId = "";
        private string _currentZoneId = "";

        private int _dynamicCursor;
        private float _sliceAccumulator;
        private float _lastProximityPass;

        private Transform _player;

        #region Registration API

        public static void Register(Transform transform, Action<Zone> onZoneChanged,
            bool isDynamic = false, Action<bool> onProximityChanged = null, float proximityThreshold = 0f)
        {
            if (transform == null) return;
            if (Instance != null)
            {
                Instance.DoRegister(transform, onZoneChanged, isDynamic, onProximityChanged, proximityThreshold);
                return;
            }
            PreRegistrations.Add(new PendingRegistration
            {
                Tf = transform, OnZoneChanged = onZoneChanged, IsDynamic = isDynamic,
                OnProximityChanged = onProximityChanged, ProximityThreshold = proximityThreshold
            });
        }

        public static void Unregister(Transform transform)
        {
            if (transform == null) return;
            if (Instance != null) { Instance.DoUnregister(transform); return; }
            for (var i = PreRegistrations.Count - 1; i >= 0; i--)
                if (PreRegistrations[i].Tf == transform) PreRegistrations.RemoveAt(i);
        }

        /// <summary>Force a re-test of one object (call after teleporting/carrying it).</summary>
        public static void MarkDirty(Transform transform)
        {
            if (Instance == null || transform == null) return;
            if (Instance._byTransform.TryGetValue(transform, out var e)) Instance.QueueDirty(e);
        }

        /// <summary>Re-test everything (compat hook for the old requestObjectPositionDetection channel).</summary>
        public static void RequestFullRetest()
        {
            if (Instance == null) return;
            Instance._candidatesValid = false;
            foreach (var e in Instance._entries) Instance.QueueDirty(e);
        }

        /// <summary>Current computed assignment (also valid in Shadow mode). False if unregistered/unassigned.</summary>
        public bool TryGetAssignment(Transform transform, out Zone zone, out MatchKind kind)
        {
            zone = null; kind = MatchKind.None;
            if (transform == null || !_byTransform.TryGetValue(transform, out var e) || e.Zone == null) return false;
            zone = e.Zone; kind = e.Kind;
            return true;
        }

        /// <summary>Objects currently assigned to a zone (replaces listOfObjectsPresentInRoom).</summary>
        public IReadOnlyList<GameObject> GetObjectsInZone(string zoneId)
        {
            return _objectsByZone.TryGetValue(zoneId, out var list) ? list : EmptyZoneList;
        }

        /// <summary>Zone ids that currently have at least one assigned object (debug/inspector).</summary>
        public IEnumerable<string> ZoneIdsWithObjects
        {
            get
            {
                foreach (var kv in _objectsByZone)
                    if (kv.Value.Count > 0) yield return kv.Key;
            }
        }

        /// <summary>Total registered objects (debug/inspector).</summary>
        public int TrackedObjectCount => _entries.Count;

        /// <summary>Objects that matched no volume yet — the coverage-gap alarm list.</summary>
        public IEnumerable<GameObject> PendingObjects
        {
            get
            {
                foreach (var e in _entries)
                    if (string.IsNullOrEmpty(e.ZoneId) && e.Tf != null)
                        yield return e.Tf.gameObject;
            }
        }

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnEnable()
        {
            WorldManager.PublishCurrentRegionId += OnRegionChanged;
            WorldManager.PublishCurrentZoneId += OnPlayerZoneChanged;
            RebuildProximityZoneLookup();
            TryInitWorld();
            DrainPreRegistrations();
        }

        private void RebuildProximityZoneLookup()
        {
            _proximityZoneSqr.Clear();
            foreach (var cfg in proximityVisibilityZones)
            {
                if (cfg.zone == null || cfg.threshold <= 0f) continue;
                _proximityZoneSqr[$"{cfg.zone.id}"] = cfg.threshold * cfg.threshold;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying) RebuildProximityZoneLookup();
        }
#endif

        private void OnDisable()
        {
            WorldManager.PublishCurrentRegionId -= OnRegionChanged;
            WorldManager.PublishCurrentZoneId -= OnPlayerZoneChanged;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            DisposeCandidates();
        }

        private void DisposeCandidates()
        {
            if (_volL2W.IsCreated) _volL2W.Dispose();
            if (_volScale.IsCreated) _volScale.Dispose();
        }

        private void TryInitWorld()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) { _worldReady = false; return; }
            _em = world.EntityManager;
            _volumeQuery = _em.CreateEntityQuery(ComponentType.ReadOnly<Volume>(), ComponentType.ReadOnly<LocalToWorld>());
            _worldReady = true;
        }

        private void DrainPreRegistrations()
        {
            if (PreRegistrations.Count == 0) return;
            foreach (var p in PreRegistrations)
            {
                if (p.Tf == null) continue;
                DoRegister(p.Tf, p.OnZoneChanged, p.IsDynamic, p.OnProximityChanged, p.ProximityThreshold);
            }
            PreRegistrations.Clear();
        }

        private void DoRegister(Transform tf, Action<Zone> onZoneChanged, bool isDynamic,
            Action<bool> onProximityChanged, float proximityThreshold)
        {
            if (_byTransform.TryGetValue(tf, out var existing))
            {
                existing.OnZoneChanged = onZoneChanged;
                existing.OnProximityChanged = onProximityChanged;
                existing.IsDynamic = isDynamic;
                existing.ProximitySqr = proximityThreshold * proximityThreshold;
                QueueDirty(existing);
                return;
            }

            var e = new Entry
            {
                Tf = tf, Go = tf.gameObject, OnZoneChanged = onZoneChanged, OnProximityChanged = onProximityChanged,
                IsDynamic = isDynamic, ProximitySqr = proximityThreshold * proximityThreshold,
                LastCheckedPos = tf.position, ZoneId = "",
            };
            _entries.Add(e);
            _byTransform.Add(tf, e);
            QueueDirty(e);
        }

        private void DoUnregister(Transform tf)
        {
            if (!_byTransform.TryGetValue(tf, out var e)) return;
            _byTransform.Remove(tf);
            _entries.Remove(e);
            _dirty.Remove(e);
            RemoveFromZoneList(e);
        }

        #endregion

        #region Events

        private void OnRegionChanged(string regionId)
        {
            _currentRegionId = regionId ?? "";
            _candidatesValid = false;
            // Statics keep their (correct) last zone; only never-assigned objects and movers
            // can be affected by a region change.
            foreach (var e in _entries)
                if (string.IsNullOrEmpty(e.ZoneId) || e.IsDynamic) QueueDirty(e);
        }

        private void OnPlayerZoneChanged(string zoneId)
        {
            _currentZoneId = zoneId ?? "";
        }

        private void QueueDirty(Entry e)
        {
            if (!_dirty.Contains(e)) _dirty.Add(e);
        }

        #endregion

        #region Update loop

        private void Update()
        {
            if (!_worldReady)
            {
                TryInitWorld();
                if (!_worldReady) return;
            }
            if (_player == null && Camera.main != null) _player = Camera.main.transform;

            DetectVolumeStreamingChanges();
            WalkDynamicObjects();

            if (_dirty.Count > 0)
            {
                if (!_candidatesValid) RebuildCandidates();
                if (_candidatesValid && _volZoneIds.Length > 0) EvaluateDirty();
                else if (_volZoneIds.Length == 0) _dirty.Clear(); // no volumes streamed in yet; retried on streaming change
            }

            UpdateProximity();
        }

        /// <summary>SubScene sections streaming in/out change the volume set → re-test pending + dynamics.</summary>
        private void DetectVolumeStreamingChanges()
        {
            var count = _volumeQuery.CalculateEntityCount();
            if (count == _lastVolumeCount) return;
            _lastVolumeCount = count;
            _candidatesValid = false;
            foreach (var e in _entries)
                if (string.IsNullOrEmpty(e.ZoneId) || e.IsDynamic) QueueDirty(e);
        }

        /// <summary>
        /// Staggered cursor over dynamic entries: each frame visits a slice sized so the whole
        /// list is covered once per interval; a visited entry is only position-compared when its
        /// own tier cadence (hot = every interval, cold = every Nth) says it is due.
        /// </summary>
        private void WalkDynamicObjects()
        {
            var dynamicCount = 0;
            foreach (var e in _entries) if (e.IsDynamic) dynamicCount++;
            if (dynamicCount == 0) return;

            _sliceAccumulator += dynamicCount * (Time.deltaTime / Mathf.Max(0.01f, checkInterval));
            var steps = (int)_sliceAccumulator;
            if (steps <= 0) return;
            _sliceAccumulator -= steps;
            steps = Mathf.Min(steps, dynamicCount);

            var now = Time.time;
            var visited = 0;
            for (var i = 0; i < _entries.Count && visited < steps; i++)
            {
                _dynamicCursor = (_dynamicCursor + 1) % _entries.Count;
                var e = _entries[_dynamicCursor];
                if (!e.IsDynamic) continue;
                visited++;

                if (e.Tf == null) continue;
                var isHot = string.IsNullOrEmpty(e.ZoneId) || e.ZoneId == _currentZoneId;
                var due = now - e.LastCheckTime >= (isHot ? checkInterval : checkInterval * Mathf.Max(1, coldTierMultiplier));
                if (!due) continue;

                e.LastCheckTime = now;
                var pos = e.Tf.position;
                if ((pos - e.LastCheckedPos).sqrMagnitude < minPositionChange * minPositionChange) continue;
                e.LastCheckedPos = pos;
                QueueDirty(e);
            }
        }

        #endregion

        #region Candidate volumes

        private void RebuildCandidates()
        {
            DisposeCandidates();
            _volZoneIds = Array.Empty<string>();
            _volZones = Array.Empty<Zone>();

            var zoneDict = WorldManager.GetZoneDictionary();
            if (zoneDict == null) return;
            RefreshZoneToRegionMap();

            var l2ws = _volumeQuery.ToComponentDataArray<LocalToWorld>(Allocator.Temp);
            var volumes = _volumeQuery.ToComponentDataArray<Volume>(Allocator.Temp);

            var keptL2W = new List<float4x4>(l2ws.Length);
            var keptScale = new List<float3>(l2ws.Length);
            var keptZoneIds = new List<string>(l2ws.Length);
            var keptZones = new List<Zone>(l2ws.Length);

            for (var i = 0; i < volumes.Length; i++)
            {
                if (volumes[i].ZoneId.IsEmpty) continue;
                var zoneId = volumes[i].ZoneId.ToString();

                if (!zoneDict.TryGetValue(zoneId, out var zone) || zone == null) continue;
                if (!zone.IsAccessible()) continue; // locked zones drop out
                // Region filter: keep volumes of the current region; with no region yet (startup),
                // keep everything unlocked — mirrors VolumeSystem's empty-zone behavior.
                if (!string.IsNullOrEmpty(_currentRegionId) &&
                    _zoneToRegion.TryGetValue(zoneId, out var regionId) &&
                    regionId != _currentRegionId) continue;

                keptL2W.Add(l2ws[i].Value);
                keptScale.Add(volumes[i].Scale);
                keptZoneIds.Add(zoneId);
                keptZones.Add(zone);
            }

            l2ws.Dispose();
            volumes.Dispose();

            _volL2W = new NativeArray<float4x4>(keptL2W.ToArray(), Allocator.Persistent);
            _volScale = new NativeArray<float3>(keptScale.ToArray(), Allocator.Persistent);
            _volZoneIds = keptZoneIds.ToArray();
            _volZones = keptZones.ToArray();
            _candidateZoneIds.Clear();
            foreach (var id in keptZoneIds) _candidateZoneIds.Add(id);
            _candidatesValid = true;

            if (isDebug) Debug.Log($"{LogPrefix} ObjectZoneTrackingBridge: {_volZoneIds.Length} candidate volume(s) for region '{_currentRegionId}'.", this);
        }

        /// <summary>Zone→region map from the same precomputed buffer VolumeSystem uses.</summary>
        private void RefreshZoneToRegionMap()
        {
            if (_zoneToRegion.Count > 0) return;
            var q = _em.CreateEntityQuery(ComponentType.ReadOnly<PrecomputedVolumeDataBuffer>());
            if (q.CalculateEntityCount() == 0) return;
            var buffer = _em.GetBuffer<PrecomputedVolumeDataBuffer>(q.GetSingletonEntity(), true);
            for (var i = 0; i < buffer.Length; i++)
            {
                var entry = buffer[i];
                if (entry.isZoneRegionMapping && !entry.zoneId.IsEmpty && !entry.regionId.IsEmpty)
                    _zoneToRegion[entry.zoneId.ToString()] = entry.regionId.ToString();
            }
        }

        #endregion

        #region Evaluation

        [BurstCompile]
        private struct AssignZoneJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<float4x4> VolL2W;
            [ReadOnly] public NativeArray<float3> VolScale;
            public float Tolerance;
            public float MaxFallbackDistSq;

            public NativeArray<int> ResultVolume;
            public NativeArray<byte> ResultKind;

            public void Execute(int index)
            {
                var p = Positions[index];

                // 1) exact containment
                for (var v = 0; v < VolL2W.Length; v++)
                {
                    if (!VolumeMath.ContainsPoint(VolL2W[v], VolScale[v], p)) continue;
                    ResultVolume[index] = v;
                    ResultKind[index] = (byte)MatchKind.Exact;
                    return;
                }

                // 2) epsilon pass
                if (Tolerance > 0f)
                {
                    for (var v = 0; v < VolL2W.Length; v++)
                    {
                        if (!VolumeMath.ContainsPoint(VolL2W[v], VolScale[v], p, Tolerance)) continue;
                        ResultVolume[index] = v;
                        ResultKind[index] = (byte)MatchKind.Soft;
                        return;
                    }
                }

                // 3) nearest candidate within limit
                if (MaxFallbackDistSq > 0f)
                {
                    var best = -1;
                    var bestDistSq = MaxFallbackDistSq;
                    for (var v = 0; v < VolL2W.Length; v++)
                    {
                        var distSq = VolumeMath.DistanceSq(VolL2W[v], VolScale[v], p);
                        if (distSq >= bestDistSq) continue;
                        bestDistSq = distSq;
                        best = v;
                    }
                    if (best >= 0)
                    {
                        ResultVolume[index] = best;
                        ResultKind[index] = (byte)MatchKind.Fallback;
                        return;
                    }
                }

                // 4) pending + alarm (handled on dispatch)
                ResultVolume[index] = -1;
                ResultKind[index] = (byte)MatchKind.None;
            }
        }

        private void EvaluateDirty()
        {
            // Compact out dead transforms first.
            for (var i = _dirty.Count - 1; i >= 0; i--)
                if (_dirty[i].Tf == null) { DoUnregister(_dirty[i].Tf); _dirty.RemoveAt(i); }
            if (_dirty.Count == 0) return;

            var count = _dirty.Count;
            var positions = new NativeArray<float3>(count, Allocator.TempJob);
            var resultVolume = new NativeArray<int>(count, Allocator.TempJob);
            var resultKind = new NativeArray<byte>(count, Allocator.TempJob);
            for (var i = 0; i < count; i++) positions[i] = _dirty[i].Tf.position;

            var job = new AssignZoneJob
            {
                Positions = positions,
                VolL2W = _volL2W,
                VolScale = _volScale,
                Tolerance = coverageTolerance,
                MaxFallbackDistSq = maxFallbackDistance > 0f ? maxFallbackDistance * maxFallbackDistance : 0f,
                ResultVolume = resultVolume,
                ResultKind = resultKind,
            };
            job.Schedule(count, 8).Complete();

            for (var i = 0; i < count; i++) DispatchResult(_dirty[i], resultVolume[i], (MatchKind)resultKind[i]);
            _dirty.Clear();

            positions.Dispose();
            resultVolume.Dispose();
            resultKind.Dispose();
        }

        private void DispatchResult(Entry e, int volumeIndex, MatchKind kind)
        {
            if (volumeIndex < 0)
            {
                // Failsafe step 4: stays pending (retried on every streaming/region change), never silent.
                if (!e.WarnedNoMatch)
                {
                    e.WarnedNoMatch = true;
                    Debug.LogWarning($"{LogPrefix} ObjectZoneTrackingBridge: '{e.Tf.name}' matched no volume " +
                                     $"(coverage gap at {e.Tf.position}) — object is missing from zone lists until coverage is fixed.", e.Tf);
                }
                AssignmentComputed?.Invoke(e.Go, "", MatchKind.None);
                return;
            }

            var zoneId = _volZoneIds[volumeIndex];
            var zone = _volZones[volumeIndex];
            e.WarnedNoMatch = false;
            e.Kind = kind;

            if (kind == MatchKind.Soft)
                Debug.Log($"{LogPrefix} ObjectZoneTrackingBridge: soft assignment '{e.Tf.name}' → '{zoneId}' " +
                          $"(within {coverageTolerance}m tolerance) — consider enlarging the volume.", e.Tf);
            else if (kind == MatchKind.Fallback)
                Debug.LogWarning($"{LogPrefix} ObjectZoneTrackingBridge: fallback assignment '{e.Tf.name}' → '{zoneId}' " +
                                 $"(nearest volume within {maxFallbackDistance}m) — coverage gap, fix the volume.", e.Tf);

            AssignmentComputed?.Invoke(e.Go, zoneId, kind);

            if (e.ZoneId == zoneId) return; // change-only dispatch
            var oldZone = e.Zone;
            RemoveFromZoneList(e);
            e.ZoneId = zoneId;
            e.Zone = zone;
            AddToZoneList(e);

            if (isDebug) Debug.Log($"{LogPrefix} ObjectZoneTrackingBridge: '{e.Tf.name}' → zone '{zoneId}' ({kind}).", e.Tf);

            if (mode != BridgeMode.Active) return; // Shadow: computed + observable, but writes nothing
            e.OnZoneChanged?.Invoke(zone);
            ZoneChanged?.Invoke(e.Go, oldZone, zone);
        }

        private void AddToZoneList(Entry e)
        {
            if (string.IsNullOrEmpty(e.ZoneId)) return;
            if (!_objectsByZone.TryGetValue(e.ZoneId, out var list))
            {
                list = new List<GameObject>(16);
                _objectsByZone.Add(e.ZoneId, list);
            }
            if (!list.Contains(e.Go)) list.Add(e.Go);
        }

        private void RemoveFromZoneList(Entry e)
        {
            if (string.IsNullOrEmpty(e.ZoneId)) return;
            if (_objectsByZone.TryGetValue(e.ZoneId, out var list)) list.Remove(e.Go);
        }

        /// <summary>
        /// Proximity visibility — independent of assignment by construction: reads player
        /// distance only, writes visibility only. Two config paths: per-registration callback
        /// (explicit threshold) and zone-scoped config (proximityVisibilityZones → static
        /// ProximityVisibilityChanged, the CustomVisibility replacement). Gated on the entry's
        /// zone being in the current candidate set — same scope as the old
        /// isPlayerInSameRegion gate.
        /// </summary>
        private void UpdateProximity()
        {
            if (_player == null) return;
            if (Time.time - _lastProximityPass < checkInterval) return;
            _lastProximityPass = Time.time;

            var playerPos = _player.position;
            foreach (var e in _entries)
            {
                if (e.Tf == null || string.IsNullOrEmpty(e.ZoneId)) continue;

                var thresholdSqr = 0f;
                var viaZoneConfig = false;
                if (e.OnProximityChanged != null && e.ProximitySqr > 0f)
                {
                    thresholdSqr = e.ProximitySqr;
                }
                else if (_proximityZoneSqr.TryGetValue(e.ZoneId, out var zoneSqr))
                {
                    thresholdSqr = zoneSqr;
                    viaZoneConfig = true;
                }
                if (thresholdSqr <= 0f) continue;

                var inRegion = _candidateZoneIds.Contains(e.ZoneId);
                var visible = inRegion && (e.Tf.position - playerPos).sqrMagnitude <= thresholdSqr;
                if (visible == e.ProximityVisible) continue;
                e.ProximityVisible = visible;

                if (mode != BridgeMode.Active) continue;
                if (viaZoneConfig) ProximityVisibilityChanged?.Invoke(e.Go, visible);
                else e.OnProximityChanged?.Invoke(visible);
            }
        }

        #endregion
    }
}
