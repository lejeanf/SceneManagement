# Proposal — ECS-driven object zone tracking

**Status:** proposal (not implemented)
**Date:** 2026-08-12
**Scope:** `jeanf.scenemanagement` (new bridge + shared math) and uvs (`ListObjectsWithinBoundary` migration)

## 1. Goal

Replace the PhysX-based `ListObjectsWithinBoundary` (uvs, `Assets/Scripts/CoreSystems/`) with a
zone-assignment service that reuses the **World SubScene volumes** already consumed by
`VolumeSystem` for player location. One geometry source, one containment algorithm, Burst-parallel
evaluation, no `Physics.Overlap*`, no per-zone trigger boxes duplicated in classic scenes.

The bridge follows the two patterns already proven in this codebase:

- **GO → ECS:** `FollowSystem` pushes `Camera.main` position to the player entity.
- **ECS → GO:** `SeatDataBridge` polls entity queries on an interval and reconciles GameObject state.

Here the bridge *reads* volume entities out of the ECS world and never creates entities:
interactables register a plain `Transform`, a Burst job assigns zones, results are dispatched to
the existing MonoBehaviour consumers (`IZoneId`, iPad lists). No `FollowComponent` per object, no
structural changes, no per-frame world sync.

## 2. Current setup — analysis

### 2.1 Anatomy of `ListObjectsWithinBoundary`

One instance = one box (`transform.localScale`) bound to a serialized `Region` + `Zone`, with a
trigger `Collider`. Detection runs through **two parallel paths**:

1. **Event/interval path** — `Physics.OverlapBoxNonAlloc` over `layersOfInterest`, fired when:
   - the player enters the boundary's region (`WorldManager.PublishCurrentRegionId`),
   - the `requestObjectPositionDetection` `VoidEventChannelSO` is raised,
   - every `updateInterval` (0.2 s default) *only* while `checkForProximity` is on and the player
     is in-region.
2. **Trigger path** — `OnTriggerEnter/Exit` on the boundary's collider (catches movers with
   rigidbodies between interval ticks).

For every hit it writes to **all** `IZoneId` components on the object
(`RoomId = zone.zoneNb`, `ZoneId = zone.id`), maintains `listOfObjectsPresentInRoom`, and in
proximity mode toggles `AbstractListInteractable.CustomVisibility(obj, visible)` by camera
distance (`proximityTreshold`, default 2 m).

### 2.2 Instance census (uvs repo, 2026-08-12)

| Container | Instances |
|---|---|
| `RegionData/Region_000_Level_01/Colliders_Region_000_Level_01.prefab` | 9 |
| `RegionData/Region_000_Level_02..05/Colliders_*.prefab` | 3 + 2 + 4 + 4 |
| `RegionData/Region_000_Level_00_Clinic/ZonesColliders_*.prefab` | 1 |
| `RegionData/Region_001_Level_00/ZonesColliders_Region_001.prefab` | 1 |
| `SOURCES/.../Rooms/Bedroom102.prefab` | 1 |
| `Scenes/Main.unity`, `05_UVCHIR.unity`, `Cata-01_QuestSystem.unity` | 1 + n |

≈ 26 instances. The `Colliders_Region_*` prefabs exist **solely** to host these boxes — they are a
hand-maintained duplicate of the zone geometry that already exists as `VolumeAuthoring` boxes in
the World SubScene. Two sources of truth for "where is zone X".

### 2.3 Consumers of the outputs

| Output | Consumers | Notes |
|---|---|---|
| `IZoneId.ZoneId` (string) | `AbstractInteractableObject.ReturnSelf` filters `zoneId != pos` → what the iPad room list shows; `Item.cs`; 16 direct implementers (`CurtainChangeState`, `GameManager*`, `ZoneThermometer`, spawners, …) | The load-bearing output. |
| `IZoneId.RoomId` (int) | `AbstractListInteractable.ReturnAppStatus` (`zoneNb == WorldManager.CurrentPlayerZone.zoneNb`); `StethoscopeTriggersOnStateChange`; scenario code | |
| `CustomFunctionOnZoneIdSet()` | `ChariotReanimation`, `GameManager_MI_BissonMarcel_01`, `EvaluateTriagePatients` | Side effects fire on **every ZoneId set**, and the current system re-sets on every detection pass. |
| `CustomVisibility` | `AbstractListInteractable.UpdateElementVisibility` → iPad element visibility | Only from instances with `checkForProximity && setCustomList`. |
| `listOfObjectsPresentInRoom` | **No code readers found** — inspector debugging only. | |

### 2.4 Defects and quirks in the current system

These matter because "before/after works the same" must not mean "bug-for-bug identical" without
deciding so explicitly:

1. **Rotation bug** — `Physics.OverlapBoxNonAlloc` is called with `Quaternion.identity`; a rotated
   boundary box detects an axis-aligned region instead. (`VolumeSystem` handles rotation
   correctly.)
2. **Scale bug** — `_halfExtents` uses `transform.localScale`, ignoring parent scale, while the
   trigger-collider path uses the true collider. The two paths can disagree.
3. **Repeated side effects** — every detection pass re-sets `ZoneId`, so
   `CustomFunctionOnZoneIdSet` fires repeatedly (every 0.2 s in proximity mode). Overrides survive
   only because they happen to be idempotent-ish.
4. **Proximity-mode leak** — objects beyond the threshold are removed from the list, but their
   `RoomId`/`ZoneId` are still (re)written by the loop below the distance check.
   **Confirmed as an active issue in production (2026-08-12).** Root cause: one code path serves
   two unrelated concerns (zone assignment and proximity visibility), so the distance filter can
   leak into assignment. The new design makes this impossible structurally — see §3.4.
5. **Exit keeps zone** — `OnTriggerExit` removes from the list but never clears the object's
   zone id. (Probably desirable — an object keeps its last room — but it is implicit today.)
6. **Ordering race → stale iPad page** — `FindObjectsWithinBoundaries` and
   `AbstractListInteractable.UpdateLists` both subscribe to `PublishCurrentRegionId`; whether the
   iPad list sees fresh zone ids on a region change depends on delegate invocation order.
   **Confirmed user-visible symptom (2026-08-12): the iPad sometimes shows the previous zone's
   page.** Mechanism: when the race is lost (or an interactable's scene finishes async-loading
   after the rebuild), the new-zone list comes out empty, and
   `BroadcastListOfAbstractInteractableObject` early-returns on an empty list
   (`if (!(components?.Count > 0)) return;`, AbstractListInteractable.cs:279) — nothing is sent,
   so the previously pushed page silently stays on screen. See §3.5 for the fix.
7. **Coverage gaps** — objects are only (re)scanned on region entry / event / proximity tick.
   A machine spawned or moved after the region-entry scan (outside proximity mode) is missed until
   the next event.

## 3. Proposed design

### 3.1 Components

All in `jeanf.scenemanagement` (Runtime), consumers stay in uvs:

```
 classic additive scenes                     World SubScene (ECS)
┌─────────────────────────┐                ┌──────────────────────────┐
│ AbstractInteractableObj │ register(T)    │  Volume + LocalToWorld   │
│ ZoneTrackedObject (few) ├──────┐         │  entities (stream in/out)│
└─────────────────────────┘      ▼         └───────────┬──────────────┘
                        ┌─────────────────┐ EntityQuery│ ToComponentDataArray
                        │ ObjectZone      │◄───────────┘
                        │ TrackingBridge  │──► Burst IJobParallelFor:
                        │ (singleton MB)  │    dirty objects × candidate volumes
                        └───────┬─────────┘    (VolumeMath.ContainsPoint)
                    on change   │
                                ▼
                 IZoneId writes, per-zone lists,
                 CustomVisibility, C# enter/exit events
```

- **`VolumeMath`** (static, Burst-friendly): `ContainsPoint(in LocalToWorld l2w, in float3 scale,
  in float3 point)` — the rotation-only inverse-TRS test extracted verbatim from
  `VolumeSystem.CheckVolumesForPlayerZone` so the player test and the object test cannot drift.
  `VolumeSystem` is refactored to call it too.
- **`ZoneTrackedObject`** (tiny MonoBehaviour): registers its transform with the bridge in
  `OnEnable`, unregisters in `OnDisable`. Flags: `isDynamic` (characters, carried props),
  `MarkDirty()` public hook (call from grab/teleport interactions). Most interactables will NOT
  need it — see §5 phase 2.
- **`ObjectZoneTrackingBridge`** (singleton MonoBehaviour, same pattern as `SeatDataBridge`):
  owns the registry, the candidate-volume cache, the dirty/pending lists, job scheduling and
  main-thread dispatch. Public surface:
  - `Register(Transform t, IZoneAssignable target, bool isDynamic)` / `Unregister(...)`
  - `event Action<GameObject, Zone, Zone> ZoneChanged` (old, new)
  - `IReadOnlyList<GameObject> GetObjectsInZone(string zoneId)` (replaces
    `listOfObjectsPresentInRoom`, also shown in a custom inspector for debugging)
  - listens to `requestObjectPositionDetection` (compat) → marks everything dirty.
- **`IZoneAssignable`** — thin adapter interface in scenemanagement; uvs implements it once next
  to `IZoneId` (scenemanagement cannot reference uvs types).

### 3.2 The three cost filters

1. **Event-driven dirty list — objects are only tested for a cause:**
   - registration (scene loaded / object spawned),
   - `MarkDirty()` (grabbed, teleported),
   - dynamic objects whose cached position moved > 0.1 m (checked on the interval),
   - volume-set change (SubScene section streamed in/out — detected by comparing the volume
     query's `CalculateEntityCount()` / order version) → retests only the **pending** list
     (objects with no assignment yet) plus dynamics.
   - Steady state = empty dirty list = **no job scheduled at all**.
2. **Candidate volumes, not all volumes:** a `NativeArray<(float4x4, float3 scale, int zoneIdx)>`
   rebuilt only on region change / unlock change: volumes whose `ZoneId` maps (via
   `WorldManager.GetZoneDictionary()`) to a zone of the current region **and** passing
   `Zone.IsAccessible()`. Locked zones drop out here. Region-wide (not just the player-adjacent
   checkable set) because the iPad lists rooms the player hasn't visited yet.
3. **Stagger the dynamic checks, in two tiers:** a rotating cursor walks `ceil(count / K)`
   dynamic objects per frame, K = frames per interval. Every object is still checked once per
   its tier's interval; no frame does the whole list. Tiers (decided from an object's *last
   known* zone vs the player's current zone):
   - **Hot tier** — dynamics in the player's current zone (or with no assignment yet): full
     interval (0.1–0.2 s). This is what scenario queries ("characters present in this room")
     actually consume.
   - **Cold tier** — dynamics elsewhere: every N intervals (default N = 8, ≈ 1–1.5 s). Still
     tracked, just lazily; crossing into the player's zone promotes them to the hot tier on the
     tick that detects the move.
   Registration bursts (scene load) are NOT staggered — one whole job, results next frame.

Zone id strings stay `FixedString128Bytes` on the job side; the job outputs a winning **volume
index** per object (`-1` = none), and only the main-thread dispatch touches managed strings /
`Zone` assets — and only for objects whose assignment changed.

### 3.3 Semantics decisions (before/after contract)

| Behavior | Old | New | Decision |
|---|---|---|---|
| Containment test | collider overlap, axis-aligned (bug §2.4-1) | pivot point-in-OBB, rotation-correct | Accept the change; validate geometry with tool T2. An object is in exactly one zone (matches `ReturnSelf`'s single-string compare, which never supported two zones anyway). |
| Zone on exit / no match | keeps last zone | keeps last zone (pending list only for never-assigned) | Preserve. |
| `ZoneId` re-set frequency | every pass | on change only | Intentional improvement. **Audit done (2026-08-12): no override depends on the repeated cadence.** `EvaluateTriagePatients` and `GameManager_MI_BissonMarcel_01` overrides are no-ops (empty / debug log). `ChariotReanimation.CustomFunctionOnZoneIdSet` → `InitCharriot()` re-resolves references and re-binds via fixed indices (`BindProperty(..., 0..4)` / `BindDelegate(..., idx)`), so repeats overwrite rather than accumulate — it needs to fire on zone *change* (chariot teleported/carried between rooms), which change-only dispatch preserves; today's every-0.2 s re-init was pure waste. `reassertOnRegionChange` stays available as a safety valve but ships **off**. |
| Assignment timing | after region entry (delegate order race §2.4-6) | at registration, before first region broadcast reaches the iPad | Improvement; removes the race. Parity harness must confirm the iPad list is never *emptier* than before. |
| Proximity visibility | per-boundary threshold, camera distance, 0.2 s | same threshold semantics, distance computed in the same job against the player position | Preserve; thresholds collected per instance by tool T1 (they may differ per room — bridge supports per-registration threshold override). |
| Mover latency | trigger-instant (if rigidbody) + 0.2 s interval | ≤ 1 interval (0.1–0.2 s), staggered | Accept: no consumer reacts faster than the iPad UI. Flag in playtest if anything feels late. |
| `requestObjectPositionDetection` | full rescan | full re-test (all dirty) | Preserve channel compatibility. |

### 3.4 Assignment/visibility separation, and coverage failsafes

Two hard guarantees address the confirmed defects §2.4-2 (scale) and §2.4-4 (proximity leak),
plus the iPad's requirement that **every zone's content is known** so menu pages build correctly.

**Guarantee 1 — assignment and visibility are independent outputs.** The job produces two
unrelated results per object: `zoneIndex` (from volume containment only) and `withinProximity`
(from player distance only). They dispatch through separate paths — `IZoneId` writes can never
be filtered, delayed, or removed by a distance check, and visibility toggles can never write a
zone. The §2.4-4 leak class is unrepresentable, not just fixed.

**Guarantee 2 — one scale/rotation convention.** The scale bug (§2.4-2) came from the old system
computing its box two different ways (buggy `localScale` overlap box vs true trigger collider).
The new system has exactly one containment definition: `VolumeMath.ContainsPoint` over the baked
`Volume.Scale` + `LocalToWorld` — identical to the player test, rotation-correct, parent-scale
handled at bake time by the transform hierarchy. There is no second geometry path to disagree
with. Migration-side handling: T1 reports *both* old boxes (buggy overlap box and true collider
box) per instance; T2 validates volumes against the **true collider box** and separately flags
where the two old boxes disagreed — those spots never worked consistently, and get explicit
sign-off instead of silent parity.

**Coverage failsafe chain** — an object must end up in a zone, or fail *loudly*; the iPad menu
being silently short one machine is the worst outcome. Assignment tries, in order:

1. **Exact containment** against the candidate volumes (normal path).
2. **Epsilon pass** — volumes re-tested with extents expanded by `coverageTolerance`
   (default 0.25 m). Matches are applied but logged as *soft assignments* — the log is the
   fix-the-volume work list. Covers machines against walls whose pivot pokes just outside a
   slightly-tight volume.
3. **Nearest-candidate fallback** — if still nothing, the nearest candidate volume within
   `maxFallbackDistance` (default 2 m) wins; logged as *fallback assignment* (warning level).
4. **Pending + alarm** — beyond that the object stays in the pending list (retried on every
   volume-set change), is surfaced in the bridge inspector, and in dev builds raises an
   on-screen warning. It is never silently dropped.

Prevention beats all of the above at runtime, so add an **editor validation** (same standard as
the existing custom-setup validators): a check that every registered interactable / `IZoneId`
carrier in a region's scenes sits inside (or within `coverageTolerance` of) a volume of that
region. Runs from a menu and in CI; T2 provides the geometry engine for it. Coverage gaps then
get caught at author time, and the runtime chain is genuinely a failsafe rather than a crutch.

### 3.5 Fixing the stale iPad page (§2.4-6)

The race dies in two layers:

1. **Assignment decoupled from broadcast timing.** Objects get their zone at registration
   (scene load / `OnEnable`), driven by the dirty list — not by a region-change delegate racing
   the iPad's rebuild. By the time `PublishCurrentZoneId` triggers a page rebuild, every loaded
   object already holds a correct zone id. The losing side of the race no longer exists.
2. **Late loaders covered by `ZoneChanged`.** The one case the old system could never handle:
   the player is already in the zone when an interactable's additive scene finishes loading —
   the page was already built without it, and no re-scan trigger existed. Fix: the iPad
   subscribes to the bridge's `ZoneChanged(obj, oldZone, newZone)` and, when
   `newZone == WorldManager.CurrentPlayerZone`, raises the existing `_refreshContent` rebuild.
   Pages become eventually consistent regardless of load order.

Out of scope but recorded: for a zone with genuinely zero interactables, the
`components.Count > 0` early-return still leaves the previous page on screen. That is iPad-side
behavior, unchanged by this migration; if "empty page / hide app" is preferred, it is a small
separate uvs fix, and T3's parity runs will show whether the case occurs in practice.

## 4. Transition plan

Phased so that the old system keeps running until parity is proven **per region**, mirroring the
2-phase style used for the channel-hub migration.

**Phase 0 — foundations (no behavior change)**
- Extract `VolumeMath`, refactor `VolumeSystem` to use it.
- EditMode tests: point-in-OBB incl. rotated volumes, boundary epsilon, scale from `Volume.Scale`
  vs `LocalToWorld` (lock the exact semantics `VolumeSystem` has today, including its
  rotation-only, unscaled-matrix choice).
- Ship in a scenemanagement minor version; uvs updates; player location must behave identically
  (covered by the existing `VolumeSystem` behavior + tests).

**Phase 1 — bridge in shadow mode**
- Implement `ZoneTrackedObject`, `ObjectZoneTrackingBridge`, `IZoneAssignable` + registration in
  `AbstractInteractableObject.Subscribe()` (one code touch covers most objects; census tool T1
  lists the stragglers among the 16 direct `IZoneId` implementers).
- Bridge runs with `mode = Shadow`: computes assignments, **writes nothing**, feeds recorder T3.
- ~~Audit the three `CustomFunctionOnZoneIdSet` overrides for repeated-call reliance.~~
  **Done 2026-08-12, ahead of schedule — no cadence dependency found** (details in the §3.3
  table); the chariot must be registered `isDynamic` (or `MarkDirty()` called from
  `TeleportationRoom`) so it re-fires on room change.
- Implement the §3.4 failsafe chain + the editor coverage validation.
- Add the §3.5 iPad hook (uvs): `ZoneChanged` → `_refreshContent` when the changed object landed
  in the player's current zone. Inert while the bridge is in shadow mode; becomes the stale-page
  fix at cutover.
- EditMode/PlayMode tests: registry lifecycle (enable/disable/destroy), dirty-list causes,
  stagger coverage (every dynamic object tested within one interval), candidate rebuild on
  region/unlock change.

**Phase 2 — parity verification**
- Run tool T2 (geometry validator) per region; fix World-SubScene volumes where the old collider
  boxes and volumes disagree (T2 can generate `VolumeAuthoring` boxes from boundary boxes for
  missing coverage).
- Play through the standard scenario set per region with T3 recording. Acceptance: zero
  unexplained diffs for a full region (explained diffs = the §2.4 bugs, listed and signed off).

**Phase 3 — cutover, per region**
- Bridge `mode = Active` for regions on an allowlist; on those regions the
  `ListObjectsWithinBoundary` instances are disabled (single bool on the prefab root /
  T1-generated checklist). Other regions unchanged.
- T3 keeps running inverted (old system in shadow) for one release cycle.

**Phase 4 — removal**
- Delete `ListObjectsWithinBoundary`, the `Colliders_Region_*` / `ZonesColliders_*` prefab
  contents (zone geometry now lives only in the World SubScene), the `layersOfInterest` layer
  usage, and the T3 recorder hooks. Keep `requestObjectPositionDetection` (bridge listens).

Rollback at any phase = flip the region allowlist back; the old prefabs are untouched until
phase 4.

## 5. Migration tooling

- **T1 — Boundary census extractor** (editor menu, uvs): scans prefabs/scenes for
  `ListObjectsWithinBoundary` (script GUID `268f12709a0dc2c4b9e8da1535701284`), dumps per
  instance: container, zone/region ids, world box (pos/rot/size incl. parent scale), layer mask,
  `checkForProximity`, `proximityTreshold`, `setCustomList`, `updateInterval`, event asset refs.
  Output: markdown report → the phase-3 cutover checklist and the per-room threshold table.
  Also lists `IZoneId` implementers not covered by `AbstractInteractableObject` registration.
- **T2 — Volume coverage validator** (editor, scenemanagement): for each censused boundary box,
  grid-samples points inside its **true collider box** (not the buggy `localScale` overlap box —
  §3.4) and asserts the same-zone volume(s) contain them (and the inverse: volume samples inside
  some boundary of the same zone). Separately flags spots where the two old boxes disagreed, for
  explicit sign-off. Reports gaps/overhangs with scene-view gizmos; can emit `VolumeAuthoring`
  boxes from a boundary box to close a gap. This is the "geometry single-source-of-truth" gate,
  and its geometry engine powers the §3.4 editor coverage validation that stays after migration.
- **T3 — Runtime parity recorder** (dev-only MonoBehaviour): hooks both systems' assignment
  streams (`IZoneId` writes vs bridge results), logs `object, old-system zone, new-system zone,
  frame` diffs to file. Zero-diff runs are the phase-2/3 acceptance evidence.

## 6. Improvements over the current system (summary)

1. Single geometry source (World SubScene volumes); ~26 hand-placed boxes + 8 collider prefabs
   deleted.
2. Rotation- and scale-correct containment (fixes §2.4-1/2) shared with the player test.
3. Event-driven + staggered evaluation: steady-state cost ≈ zero; no `Physics.Overlap`, no
   `GetComponents` per hit, no trigger colliders/rigidbody requirements.
4. Change-only dispatch: `CustomFunctionOnZoneIdSet` fires once per actual change (compat flag
   available).
5. Registration replaces layer-mask discovery: coverage is explicit, validated by T1, works for
   objects spawned at any time (fixes §2.4-7).
6. Locked-zone and region filtering built in via `Zone.IsAccessible()` + region candidate set.
7. Deterministic ordering: zone ids are assigned before iPad list broadcasts, and late-loading
   objects trigger a page refresh via `ZoneChanged` — fixes the user-visible "iPad shows the
   previous zone's page" bug (§2.4-6, §3.5).
8. New capability for free: `ZoneChanged` C# event usable by tooltip/AI/audio systems.

## 7. T1/T2 results — 2026-08-12 (phase-2 evidence)

First runs of the migration tools (T1 census over 25 prefab instances, T2 coverage over 175
volumes / 118 `IZoneId` carriers in the open scenes):

- **T2: 0 coverage gaps, 6 soft.** The volume geometry fully covers the old boundary boxes —
  the single-source-of-truth switch is safe. All 6 SOFT items are logic objects
  (`SettingBinder`, `ScenarioListManager`, `DiscussionTreeBuilder`, `Bedroom`, 2× `Assistant`)
  sitting at y ≈ 3.34–3.50, at/above the top face of their 3 m volumes. Cause (confirmed):
  they were **deliberately ceiling-mounted to keep their colliders out of the player's way**
  under the old physics-overlap system. The new system needs no colliders, so after cutover
  they can return to natural heights — until then the runtime epsilon pass assigns them
  correctly (with a soft-assignment log line each). Do NOT grow the volumes' height as a fix:
  the same volumes drive player detection and taller boxes on stacked floors risk cross-floor
  bleed.
- **T1: the scale bug (§2.4-2) is live in 3 places.** `Floor_01_Office`, `Floor_05_Corridor`,
  `Bedroom102` have buggy overlap boxes of (1,1,1) vs true colliders up to (3,3,30.5): their
  overlap path scans ~1 m³, statics there are effectively undetected today.
- **T1: broken configs the census surfaced:** `Room_419`/`Room_420` have no Zone asset (NRE in
  `SetRoomIDToFoundObjects` when their overlap fires); `Floor_02_Office` has layer mask "none"
  (detects nothing); `Bedroom102` has neither zone nor region (`IsPlayerInSameRegion` NREs on
  every region change) — relic confirmed, delete at cutover.
- **T1: `setCustomList` is False on all 25 *prefab* instances.** NOT yet a dead-code verdict:
  the first census run only tabulated prefabs — scene-hosted instances (Main, 05_UVCHIR,
  Cata-01, and anything a scenario loads additively) were not read. T1 now also tabulates
  open-scene instances; rerun it per scenario (triage etc.) before concluding anything about
  the `CustomVisibility` path. If a scenario instance does use it, the bridge's proximity
  output (`onProximityChanged` + threshold at registration) is the replacement wiring.
- **T1: 27 `IZoneId` implementers outside the `AbstractInteractableObject` hierarchy** (the
  `GameManager*` family + `uvs.Interactions` zone tools + 2 legacy classes). Remaining wiring
  before cutover: guarded `Register` calls in their shared bases (needs the versionDefine gate
  on `uvs.Scenarios` / `uvs.Interactions`) or `ZoneTrackedObject` components on their prefabs.

## 8. Open questions — resolved 2026-08-12

1. ~~Is ≤ 0.2 s zone-change latency acceptable for carried objects?~~ **Yes** — confirmed
   acceptable, no constraint here.
2. ~~Do any `CustomFunctionOnZoneIdSet` overrides depend on the repeated-set cadence?~~
   **No** — code audit done (see §3.3 table). Change-only dispatch is safe;
   `reassertOnRegionChange` ships off.
3. ~~Register all NPCs as `isDynamic`, or a subset?~~ **All NPCs register, with tiered cadence:**
   the requirement is "query characters in the player's current zone at full freshness, other
   zones still tracked but much less often" — exactly the hot/cold tier design in §3.2-3. No
   per-NPC opt-in decision needed; the tiers handle it automatically from each NPC's last known
   zone.
4. ~~What is `Bedroom102.prefab`'s embedded boundary for?~~ **Presumed relic** ("ruin of the
   past") — nobody knows what it serves. T1 will report its config; unless the report shows a
   live consumer, it is deleted in phase 3 with its region's cutover rather than migrated.
