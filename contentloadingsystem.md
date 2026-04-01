# Addressable Content Loading System

### jeanf.ContentManagement

A Unity addressable-based content loading system with ScriptableObject data containers,
a generic async loader base class, and concrete implementations for cosmetics and scenes.

---

## Architecture Overview

```
App Start (Single Point of Execution)
         │
         ▼
 ClientAppStartup
         │  reads GameInitConfig (startRegion, startZone, systemsConfig)
         ▼
 InitializationCoordinator  ◄──── IInitializable systems Register() in Awake
         │  owns fade, loading messages, completion gate
         │
         │  [Phase 1]  ContentRegistry.Initialize()
         │               ├─ CosmeticContentLoader  (label: "cosmetic")
         │               └─ SceneContentLoader     (label: "scene" — metadata & report only)
         │
         │  [Phase 2]  WorldManager.LoadWorldDependencies()
         │
         │  [Phase 3]  WorldManager.LoadRegion(startRegion)
         │               └─ SceneLoader → Region.dependenciesInThisRegion  (unchanged)
         │
         │  [Phase 4]  Player.Spawn(startRegion.SpawnPosOnInit)
         │
         │  [Phase 5]  WorldManager.SetInitialLocation(startRegion, startZone)
         │               └─ VolumeSystem enabled  (transition-only from this point)
         │
         │  [Phase 6]  Await all RequiredSystemsConfig.requiredSystemIds → ReportReady()
         │
         └──  FadeIn → InitComplete event → gameplay
```

```
IContentRegistry  ◄──────────────────────
     │                                 │
     ▼                                 │
ContentRegistry ───────────────────────┘
  │              │
  ▼              ▼
CosmeticContent  SceneContent
  Loader          Loader
  │                │
  ▼                ▼
CosmeticContent  SceneContent        ← metadata / report only
    SO               SO              ← NOT the runtime scene-loading path
   └───────┬──────────┘
           ▼
      GameContentSo
```

**Runtime scene loading path** (unchanged):

```
WorldManager → SceneLoader → Region.dependenciesInThisRegion (List<SceneReference>)
```

---

## Initialization Design

### Phased Loading Sequence

| Phase | Owner                       | Action                                                                                                            | Blocks next phase |
| ----- | --------------------------- | ----------------------------------------------------------------------------------------------------------------- | ----------------- |
| 1     | `InitializationCoordinator` | `ContentRegistry.Initialize()` — loads all Addressable SO metadata                                                | Yes               |
| 2     | `WorldManager`              | `LoadWorldDependencies()` — persistent subscenes                                                                  | Yes               |
| 3     | `WorldManager`              | `LoadRegion(startRegion)` — region dependency scenes via `Region.dependenciesInThisRegion`                        | Yes               |
| 4     | `WorldManager`              | `SpawnPlayer(startRegion.SpawnPosOnInit)`                                                                         | Yes               |
| 5     | `WorldManager`              | `SetInitialLocation(startRegion, startZone)` — force-sets `currentRegion` / `currentZone`, enables `VolumeSystem` | Yes               |
| 6     | `InitializationCoordinator` | Await all `requiredSystemIds` in `RequiredSystemsConfig` reporting ready                                          | Yes               |

**Key invariant:** `VolumeSystem` only detects zone _transitions_. It is NOT responsible for setting the initial zone. Phase 5 sets the initial state explicitly before the ECS system is enabled.

### Who Owns What

| Responsibility                                            | Owner                                                                                            |
| --------------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| Sequence orchestration, loading messages, completion gate | `InitializationCoordinator`                                                                      |
| Loading state signal (drives FadeMask)                    | `NoPeeking.SetIsLoadingState(bool)`                                                              |
| Visual fade animation                                     | `FadeMask.SetStateLoading/Clear/HeadInWall()`                                                    |
| Custom pass fade overlay (region transitions)             | `BoolFloatEventChannelSO FadeEventChannel` in `WorldManager`                                     |
| Head-in-wall desaturation                                 | `NoPeeking` (FixedUpdate physics check)                                                          |
| Content metadata (cosmetics, scene SOs)                   | `ContentRegistry`                                                                                |
| Scene loading (additive Unity scenes)                     | `WorldManager` → `SceneLoader`                                                                   |
| Player spawn position                                     | `WorldManager` (reads `Region.SpawnPosOnInit`)                                                   |
| Initial zone/region state                                 | `WorldManager.SetInitialLocation()`                                                              |
| Zone transition detection (ongoing)                       | `VolumeSystem` (ECS, post-init only)                                                             |
| Cross-region doorway definition                           | `RegionConnectivity.zoneConnections` (cross-region `ZoneNeighborData`)                           |
| Zone lock state & access check                            | `Zone.contentId` + `WorldManager._zoneRegistry`                                                  |
| ECS-triggered (walkthrough) region transition             | `WorldManager.OnRegionChangedFromECS()` — no fade, no teleport, no unload, loads new scenes only |
| Manual (elevator/UI) region transition                    | `WorldManager.OnRegionChange()` — full fade + unload + load + teleport                           |
| Map broadcast on region change                            | `WorldManager.PublishCurrentRegion` delegate                                                     |
| Start region/zone configuration                           | `GameInitConfig` SO                                                                              |
| Required systems list                                     | `RequiredSystemsConfig` SO                                                                       |
| Per-scenario fade enable/disable                          | `Scenario.enableFadeOnLoad` (new field)                                                          |

### Migration Path — Existing Region/Zone SOs

**Two additive fields added to existing SOs:** `Scenario.enableFadeOnLoad` and `Zone.contentId` (zone access control). All other SO fields are untouched.

- `Region.dependenciesInThisRegion: List<SceneReference>` remains the runtime scene-loading source
- `SceneContentSo` is a **new, additive metadata layer** — it describes scenes for reporting and addressable asset management but does NOT replace `SceneReference` in regions
- `SceneContentLoader` loads `SceneContentSo` assets by label for querying and report generation only
- All 3 classes (`Region`, `Zone`, `Scenario`) keep their existing fields and serialized data

**Optional future migration** (not required for launch):
Replace `List<SceneReference>` in `Region` with `List<SceneContentSo>` to unify the two layers.
This can be done region-by-region without breaking anything — `SceneContentSo` wraps `SceneReference`.

---

## Fade System

Two parallel mechanisms handle all visual fade/loading states. Do not confuse them — they serve different purposes and must both be preserved.

### Mechanism 1 — FadeMask + NoPeeking (post-processing)

**Files:** `FadeMask.cs`, `NoPeeking.cs` (in `VR_Player` package)

`FadeMask` drives URP/HDRP `ColorAdjustments` via reflection. It has three visual states animated by LitMotion at `_fadeTime` (default `0.2s`):

| State        | `colorFilter` | `saturation` | Trigger                  |
| ------------ | ------------- | ------------ | ------------------------ |
| `Loading`    | `Color.black` | `0`          | Screen is fully black    |
| `Clear`      | `Color.white` | `0`          | Normal view              |
| `HeadInWall` | `Color.white` | `-100`       | Fully desaturated / gray |

`NoPeeking` runs in `FixedUpdate` and is the **only** class that should call `FadeMask.SetState*()` after init. It reads a static `_isSceneLoading` flag and a `Physics.CheckSphere` head-position check:

```
_isSceneLoading == true            →  FadeMask.SetStateLoading()
_isSceneLoading == false
  + head inside wall collider      →  FadeMask.SetStateHeadInWall()
  + head clear                     →  FadeMask.SetStateClear()
```

State transitions are deduplicated — `NoPeeking` only calls `FadeMask` when the derived state changes.

**Public control API (call sites only):**

```csharp
NoPeeking.SetIsLoadingState(bool isLoading)   // the single knob for loading vs. clear
NoPeeking.IsCurrentlyLoading()                // read-only
```

`_isSceneLoading` is initialized to `true` at field declaration, so the screen starts black before any code runs.

### Mechanism 2 — FadeEventChannel (BoolFloatEventChannelSO)

**Field in `WorldManager`:** `[SerializeField] private BoolFloatEventChannelSO FadeEventChannel`

An event channel added during URP/HDRP compatibility work. It raises `bool + float` (show/hide,
duration) alongside the `FadeMask` calls during region transitions. It does **not** replace or
duplicate the `FadeMask + NoPeeking` logic — the core fade behavior remains owned by that system.
Do not remove or reroute these calls; they are part of the existing fade coordination.

```csharp
FadeEventChannel?.RaiseEvent(false, 1.0f)   // show = false (fade out), 1s
FadeEventChannel?.RaiseEvent(true,  0.1f)   // show = true (fade in), 0.1s
```

### Current Fade Flows

#### Game initialization

```
FadeMask.Awake()
  → SetStateLoadingImmediate()         — instant black, no animation
WorldManager.Awake()
  → NoPeeking.SetIsLoadingState(true)  — sync NoPeeking state
  → FadeMask.SetStateLoading()         — redundant guard
... all phases complete ...
  → NoPeeking.SetIsLoadingState(false) — NoPeeking sees clear → FadeMask.SetStateClear() (animated)
  → FadeEventChannel?.RaiseEvent(false, 1.0f)
```

#### Region transition (post-init)

```
OnRegionChange()
  → FadeMask.SetStateLoading()             — immediate black
  → await 0.2s
  → FadeEventChannel?.RaiseEvent(true, 0.1f)
  → _isRegionTransitioning = true
... scene unload/load ...
  → CompleteRegionTransition()
      → FadeMask.SetStateClear()           — animated clear
  → FadeOnRegionChange() coroutine (1s delay)
      → FadeEventChannel?.RaiseEvent(false, 1.0f)
```

`NoPeeking.SetIsLoadingState()` is NOT called during region transitions — `_isRegionTransitioning`
guards against `ScenarioManager` interfering with the loading state during this window.

#### Scenario load/unload

```
LoadScenario() — transitioning from an active scenario
  → NoPeeking.SetIsLoadingState(true)    — screen goes black
  ... unload old, load new scenes ...
  → NoPeeking.SetIsLoadingState(false)   — NoPeeking drives fade back to clear
                                           (only if !IsRegionTransitioning)

ScenarioRestartAsync()
  → NoPeeking.SetIsLoadingState(true)
  → UnloadScenario() + await 500ms + LoadScenario()
  → NoPeeking.SetIsLoadingState(false)
```

#### Head in wall (ongoing, always active after init)

```
NoPeeking.FixedUpdate()
  → Physics.CheckSphere(headPosition, sphereCheckSize, collisionLayer)
  → true  → FadeMask.SetStateHeadInWall()  — desaturate to -100
  → false → FadeMask.SetStateClear()        — restore saturation to 0
```

### Per-Scenario Fade Config (new)

Add `enableFadeOnLoad` to `Scenario.cs`. `ScenarioManager` checks this flag before calling
`NoPeeking.SetIsLoadingState()`. When disabled, scene loading still happens — only the fade
is skipped.

**Change to `Scenario.cs`** (one new field, no other changes):

```csharp
public bool enableFadeOnLoad = true;
```

**Change to `ScenarioManager`** — wrap all `NoPeeking.SetIsLoadingState(true)` calls at
scenario load time:

```csharp
if (scenario.enableFadeOnLoad)
    NoPeeking.SetIsLoadingState(true);

// ... load scenes ...

if (scenario.enableFadeOnLoad)
    NoPeeking.SetIsLoadingState(false);
```

The `ScenarioRestartAsync` path follows the same guard.

---

## Adjacent Regions & Cross-Region Doorways

### Two kinds of region transition

| Trigger                      | Path                                    | Fade        | Teleport                              | Scene unload      | Scene load                                    |
| ---------------------------- | --------------------------------------- | ----------- | ------------------------------------- | ----------------- | --------------------------------------------- |
| Elevator / UI request        | `OnRegionChange(region)` — existing     | Yes (black) | Yes (`SpawnPosOnRegionChangeRequest`) | Old region scenes | New region scenes                             |
| Player walks through doorway | `OnRegionChangedFromECS()` — simplified | **None**    | **None**                              | **None**          | New dependencies only (if not already loaded) |

If the player walked there, they are already there. No fade, no teleport, no unload. The ECS `VolumeSystem` already creates `RegionChangeNotificationComponent` when the player crosses into a zone that belongs to a different region. The only fix needed is that `WorldManager.OnRegionChangedFromECS()` currently calls `OnRegionChange(region)` unconditionally, which triggers the full transition. It must be its own lightweight path.

### Doorway zones

A cross-region doorway is defined by a `ZoneNeighborData` entry in `RegionConnectivity` where `zoneA` and `zoneB` belong to different regions. Multiple entries between the same region pair model multiple doorways — there is no limit.

```
Region A                 Region B
 ┌────────────┐           ┌────────────┐
 │  zone_a1   │◄─────────►│  zone_b1   │  ← doorway 1
 │  zone_a2   │           │  zone_b2   │
 │  zone_a3   │◄─────────►│  zone_b3   │  ← doorway 2
 └────────────┘           └────────────┘
```

For `VolumeSystem` to detect cross-region doorway zones, the `PrecomputedVolumeData` generator must include the neighbor zone in each doorway zone's `checkableZoneIds`. If `zone_a1` ↔ `zone_b1` is a `ZoneNeighborData` connection, `zone_b1` must appear in `zone_a1`'s checkable set and vice versa. The existing generator already handles this if the connections are configured.

### Simplified `OnRegionChangedFromECS`

ECS-triggered = player physically walked there = already positioned = no transition overhead needed.

```csharp
private void OnRegionChangedFromECS(FixedString128Bytes id)
{
    var regionId = id.ToString();
    if (regionId == _lastNotifiedRegion) return;
    if (string.IsNullOrEmpty(regionId)) return;
    if (!_regionDictionary.TryGetValue(regionId, out var region)) return;

    _lastNotifiedRegion  = regionId;
    _currentPlayerRegion = region;

    PublishCurrentRegionId?.Invoke(region.id);
    PublishCurrentRegion?.Invoke(region);
    RequestLoadForRegionDependencies(region);
}
```

`RequestLoadForRegionDependencies(region)` is safe to call even if scenes are already loaded —
`SceneLoader` deduplicates requests. The old region's scenes are kept loaded (player may walk back).
Only the manual `OnRegionChange` path (elevator/UI) unloads the previous region.

### Map per region

Each region carries its own floor map. `WorldManager` broadcasts the full `Region` object whenever
the current region changes so any subscriber (map UI, minimap, etc.) can react.

**New field on `Region.cs`:**

```csharp
[Header("Map")]
public Sprite map;
```

**New delegate on `WorldManager`:**

```csharp
public delegate void BroadcastRegion(Region region);
public static BroadcastRegion PublishCurrentRegion;
```

`PublishCurrentRegion?.Invoke(region)` is fired alongside `PublishCurrentRegionId` in:

- `OnRegionChange()` (manual/elevator transition)
- `OnRegionChangedFromECS()` — walkthrough path
- `SetInitialLocation()` — init

**Map UI usage:**

```csharp
private void OnEnable()  => WorldManager.PublishCurrentRegion += OnRegionChanged;
private void OnDisable() => WorldManager.PublishCurrentRegion -= OnRegionChanged;

private void OnRegionChanged(Region region)
{
    mapImage.sprite = region.map;
}
```

### Setup checklist

- [ ] Add cross-region `ZoneNeighborData` entries in `RegionConnectivity` for every physical doorway between regions (bidirectional, one entry per doorway opening)
- [ ] Regenerate `PrecomputedVolumeData` after adding any cross-region zone connections
- [ ] Assign a `Sprite` to `Region.map` for every region that should display a map
- [ ] Subscribe to `WorldManager.PublishCurrentRegion` in your map UI

---

## Zone Access Control

Zones may be locked — only accessible once the player's progression has unlocked them. Locking affects _zone tracking and content interaction_; it does NOT affect the ECS-level scene streaming, which remains position-based only.

### Data — `Zone.cs`

One additive field using the same `UnlockableContent` enum as `GameContentSo`:

```csharp
public UnlockableContent contentId = UnlockableContent.UC_UNLOCKED;
```

`UC_UNLOCKED = 0` means always accessible. Any other value is checked against `ContentUnlockService`:

```csharp
public bool IsAccessible()
{
    if (contentId == UnlockableContent.UC_UNLOCKED) return true;
    return ContentUnlockService.IsUnlocked((int)contentId);
}
```

### Zone Registry

`WorldManager` builds `_zoneRegistry : Dictionary<string, Zone>` when regions load.
Source: `Region.zonesInThisRegion` from each loaded region. Keys are `zone.id.ToString()`.

### Lock Check on Zone Entry

When `WorldManager` processes a `ZoneChangeNotificationComponent`:

1. Look up `Zone` SO from `_zoneRegistry` by zone ID string
2. If not found or `zone.IsAccessible()` → proceed normally (update current zone, load content)
3. If `!zone.IsAccessible()` → fire `ZoneLockedEventChannel?.RaiseEvent(zone)`, do **not** update `_currentPlayerZone`

`VolumeSystem` and physical scene streaming are unaffected — they load/unload by position regardless of lock state.

### Unlock Flow

When progression unlocks a zone:

1. `ZoneUnlockBroadcastSO` event channel raises the unlocked `Zone`
2. `WorldManager` subscribes and re-evaluates the player's current physical zone against the updated lock state
3. If the player is already inside the now-unlocked zone, the zone change is processed immediately as if the player just entered

### Who Owns What (zone locking)

| Responsibility                      | Owner                                                                |
| ----------------------------------- | -------------------------------------------------------------------- |
| Zone lock state                     | `Zone.contentId` + `ContentUnlockService.IsUnlocked()`               |
| Zone registry (id → SO)             | `WorldManager._zoneRegistry` (built from `Region.zonesInThisRegion`) |
| Lock check on ECS zone entry        | `WorldManager` (after `ZoneChangeNotificationComponent`)             |
| Locked zone UI feedback             | `ZoneLockedEventChannel` subscriber                                  |
| Unlock notification to WorldManager | `ZoneUnlockBroadcastSO` → `WorldManager`                             |

### Setup checklist (zone locking)

- [ ] Add `UnlockableContent contentId` and `IsAccessible()` to `Zone.cs`
- [ ] Add `_zoneRegistry` build logic to `WorldManager` (populate from each loaded region)
- [ ] Wire `ZoneLockedEventChannel` for UI feedback when player enters a locked zone
- [ ] Wire `ZoneUnlockBroadcastSO` for re-evaluation on unlock

---

## 1. Enums

### UnlockableContent.cs

```csharp
namespace jeanf.ContentManagement.ProgressionSystem.Data
{
    public enum UnlockableContent
    {
        UC_UNLOCKED = 0,
        // Add your unlockable content IDs here
        UC_COSMETIC_001,
        UC_COSMETIC_002,
        // ...
    }
}
```

### ContentUnlockService.cs

Static bridge between content lock checks and whatever persistence system provides unlock state.
Defaults to "all unlocked" until a persistence system (SaveSystem or equivalent) wires in its check.

```csharp
using System;

namespace jeanf.ContentManagement
{
    public static class ContentUnlockService
    {
        public static Func<int, bool> CheckUnlocked = _ => true;

        public static bool IsUnlocked(int contentId) => CheckUnlocked(contentId);
    }
}
```

When a SaveSystem is added, wire it at startup:

```csharp
ContentUnlockService.CheckUnlocked = id => SaveSystem.CheckContent(id);
```

---

## 2. ScriptableObject Data Containers

### GameContentSo.cs

```csharp
using jeanf.ContentManagement.ProgressionSystem.Data;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class GameContentSo : ScriptableObject
    {
        public UnlockableContent ContentId = UnlockableContent.UC_UNLOCKED;

        public bool IsUnlocked()
        {
            if (ContentId == UnlockableContent.UC_UNLOCKED) return true;
            return ContentUnlockService.IsUnlocked((int)ContentId);
        }

        public bool IsBaseUnlocked() => ContentId == UnlockableContent.UC_UNLOCKED;
    }
}
```

### CosmeticContentSo.cs

```csharp
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Localization;

namespace jeanf.ContentManagement
{
    public enum CosmeticType
    {
        CHARACTER,
        OBJECT,
        ENVIRONMENT,
        UI,
        VFX,
        DISCUSSION,
        QUEST
    }

    [CreateAssetMenu(fileName = "Empty Cosmetic Config", menuName = "SceneManagment/New Cosmetic Config")]
    public class CosmeticContentSo : GameContentSo
    {
        public int Id;
        public CosmeticType Type;
        public AssetReference CosmeticPrefab;
        public LocalizedString slug;
        public Sprite Icon;
    }
}
```

### SceneContentSo.cs

Metadata-only layer for Addressable scene assets. Does NOT replace `Region.dependenciesInThisRegion`.
Adapt `SceneReference` as needed — see https://github.com/starikcetin/Eflatun.SceneReference for reference.

```csharp
using jeanf.SceneManagement;
using UnityEngine;

namespace jeanf.ContentManagement
{
    [CreateAssetMenu(menuName = "SceneManagment/New Scene Info", fileName = "Empty Scene Info")]
    public class SceneContentSo : GameContentSo
    {
        public SceneReference Scene;
        public bool IsActive = true;
        [TextArea(3, 10)]
        public string Comments;
    }
}
```

### GameInitConfig.cs

Defines where and how the game starts. Attach to `ClientAppStartup`.

```csharp
using jeanf.SceneManagement;
using UnityEngine;

namespace jeanf.ContentManagement
{
    [CreateAssetMenu(menuName = "SceneManagment/Game Init Config", fileName = "GameInitConfig")]
    public class GameInitConfig : ScriptableObject
    {
        public Region startRegion;
        public Zone startZone;
        public RequiredSystemsConfig systemsConfig;
    }
}
```

`startZone` may be null. If null, `WorldManager.SetInitialLocation` falls back to the first zone in `startRegion.zonesInThisRegion`.

### RequiredSystemsConfig.cs

SO that defines the ordered init sequence and the completion gate.
Configure one per game mode or context (main game, editor test, tutorial, etc.).

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace jeanf.ContentManagement
{
    [CreateAssetMenu(menuName = "SceneManagment/Required Systems Config", fileName = "RequiredSystemsConfig")]
    public class RequiredSystemsConfig : ScriptableObject
    {
        [Serializable]
        public struct SystemEntry
        {
            public string systemId;
            public string loadingMessage;
        }

        public List<SystemEntry> requiredSystems;
    }
}
```

Each `SystemEntry.systemId` must match the `SystemId` property of a registered `IInitializable`.
`loadingMessage` is shown on the loading screen while that system is initializing.

---

## 3. Content Loader Base Class

### ContentLoader.cs

```csharp
using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace jeanf.ContentManagement
{
    public abstract class ContentLoader<T> : IDisposable
    {
        public bool Loaded { get; private set; }
        public int TotalCount { get; private set; }
        public int LoadedCount { get; private set; }
        public float Progress => TotalCount > 0 ? (float)LoadedCount / TotalCount : (Loaded ? 1f : 0f);

        public event Action OnProgressChanged;

        private readonly string[] _contentTags;
        private UniTask<bool>? _loadingTask;
        private AsyncOperationHandle<IList<T>> _assetHandle;

        protected ContentLoader(params string[] tags)
        {
            _contentTags = tags;
        }

        // ──────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────

        public async UniTask Initialize()
        {
            var isCancelled = await InternalInitialize();
            if (isCancelled)
            {
                Debug.LogError("Failed to load cosmetics");
                return;
            }

            Loaded = true;
        }

        // ──────────────────────────────────────────────────────────────
        // Internal initialization — deduplicates concurrent calls
        // ──────────────────────────────────────────────────────────────

        private async UniTask<bool> InternalInitialize()
        {
            // Already fully loaded — no cancellation
            if (Loaded) return false;

            // A load is already in-flight — await it instead of starting a new one
            if (_loadingTask.HasValue)
            {
                return await _loadingTask.Value;
            }

            _loadingTask = LoadResource().SuppressCancellationThrow();
            return await _loadingTask.Value;
        }

        // ──────────────────────────────────────────────────────────────
        // Addressables loading
        // ──────────────────────────────────────────────────────────────

        private async UniTask LoadResource()
        {
            // 1. Resolve addresses matching ALL supplied tags (intersection)
            var locHandle = Addressables.LoadResourceLocationsAsync(
                _contentTags,
                Addressables.MergeMode.Intersection,
                typeof(T));

            if (!locHandle.IsValid())
            {
                throw new Exception("Failed to load Loc Handle for " + typeof(T).Name);
            }

            await locHandle.Task.AsUniTask();

            if (locHandle.Status != AsyncOperationStatus.Succeeded)
            {
                Addressables.Release(locHandle);
                throw new Exception("Status not succeeded for Loc Handle " + typeof(T).Name);
            }

            // 2. Load all assets at those locations, routing each one through the callback
            TotalCount = locHandle.Result.Count;
            var assetHandle = Addressables.LoadAssetsAsync<T>(locHandle.Result, resource =>
            {
                HandleLoadResource(resource);
                LoadedCount++;
                OnProgressChanged?.Invoke();
            });

            if (!assetHandle.IsValid())
            {
                Addressables.Release(locHandle);
                throw new Exception("Failed to load asset handle for " + typeof(T).Name);
            }

            await assetHandle.Task.AsUniTask();

            // Location handle is no longer needed after assets start loading
            Addressables.Release(locHandle);

            if (assetHandle.Status != AsyncOperationStatus.Succeeded)
            {
                assetHandle.Release();
                Debug.LogError(assetHandle.OperationException.Message);
                throw new Exception("Status not succeeded for asset handle" + typeof(T).Name);
            }

            OnPostProcessResource();

            _assetHandle = assetHandle;
        }

        // ──────────────────────────────────────────────────────────────
        // Abstract / virtual hooks for subclasses
        // ──────────────────────────────────────────────────────────────

        protected abstract void HandleLoadResource(T resource);

        protected virtual void OnPostProcessResource() { }

        // ──────────────────────────────────────────────────────────────
        // IDisposable
        // ──────────────────────────────────────────────────────────────

        public virtual void Dispose()
        {
            if (!Loaded) return;

            if (_assetHandle.IsValid())
                Addressables.Release(_assetHandle);

            Loaded = false;
            _loadingTask = null;
            TotalCount = 0;
            LoadedCount = 0;
        }
    }
}
```

---

## 4. Concrete Loaders

### CosmeticContentLoader.cs

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class CosmeticContentLoader : ContentLoader<CosmeticContentSo>
    {
        public const int EMPTY_COSMETIC = 0;
        public const int BOT_SKIN       = 1;

        private readonly Dictionary<CosmeticType, List<CosmeticContentSo>> _cosmeticTypeMap = new();
        private readonly Dictionary<int, CosmeticContentSo>                _cosmeticList    = new();

        public CosmeticContentLoader(params string[] tags) : base(tags) { }

        // ── HandleLoadResource ─────────────────────────────────────────

        protected override void HandleLoadResource(CosmeticContentSo resource)
        {
            // Index by type
            if (_cosmeticTypeMap.TryGetValue(resource.Type, out var list))
            {
                list.Add(resource);
            }
            else
            {
                _cosmeticTypeMap.Add(resource.Type, new List<CosmeticContentSo> { resource });
            }

            // Index by unique ID — log if a duplicate is found
            if (!_cosmeticList.TryAdd(resource.Id, resource))
            {
                Debug.LogError("Duplicated Cosmetic!!");
            }
        }

        // ── Query API ─────────────────────────────────────────────────
        public CosmeticContentSo Find(int key)
            => _cosmeticList.GetValueOrDefault(key);

        public IReadOnlyList<CosmeticContentSo> FindByType(CosmeticType cosmeticType)
            => _cosmeticTypeMap.GetValueOrDefault(cosmeticType).AsReadOnly();

        // ── IDisposable ───────────────────────────────────────────────
        public override void Dispose()
        {
            if (!Loaded) return;
            _cosmeticTypeMap.Clear();
            _cosmeticList.Clear();
            base.Dispose();
        }
    }
}
```

### SceneContentLoader.cs

Loads `SceneContentSo` assets for metadata queries and report generation.
Does NOT load actual Unity scenes — that remains `WorldManager` → `SceneLoader`.

```csharp
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class SceneContentLoader : ContentLoader<SceneContentSo>
    {
        private readonly List<SceneContentSo> _scenes = new();

        public SceneContentLoader(params string[] tags) : base(tags) { }

        // ── HandleLoadResource ─────────────────────────────────────────

        protected override void HandleLoadResource(SceneContentSo resource)
        {
            if (!resource.IsActive) return;
            _scenes.Add(resource);
        }

        // ── Query API ─────────────────────────────────────────────────

        public IReadOnlyList<SceneContentSo> GetAll() => _scenes;

        public SceneContentSo Find(string sceneName)
            => _scenes.Find(s => s.Scene.Name == sceneName);

        // ── Reporting ─────────────────────────────────────────────────

        public void WriteReport(StringBuilder csv)
        {
            csv.AppendLine("SceneName,ContentId,IsActive,Comments");
            foreach (var info in _scenes)
            {
                csv.AppendLine($"{info.Scene.Name},{info.ContentId},{info.IsActive},{info.Comments}");
            }
        }

        // ── IDisposable ───────────────────────────────────────────────

        public override void Dispose()
        {
            if (!Loaded) return;
            _scenes.Clear();
            base.Dispose();
        }
    }
}
```

---

## 5. Content Registry

The `ContentRegistry` is an **in-memory asset registry**: all Addressable SOs are loaded once at startup and queried synchronously at runtime. Nothing is reloaded during gameplay. `IContentRegistry` exists for testability — swap with a stub in tests.

### IContentRegistry.cs

```csharp
using Cysharp.Threading.Tasks;

namespace jeanf.ContentManagement
{
    public interface IContentRegistry
    {
        UniTask Initialize();
        void Dispose();
    }
}
```

### ContentRegistry.cs

```csharp
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class ContentRegistry : IContentRegistry
    {
        // ── Addressable label constants ────────────────────────────────
        private const string TAG_COSMETIC = "cosmetic";
        private const string TAG_SCENE    = "scene";

        // ── Loaders ───────────────────────────────────────────────────
        public CosmeticContentLoader Cosmetics { get; private set; }
        public SceneContentLoader    Scenes    { get; private set; }

        public ContentRegistry()
        {
            Cosmetics = new CosmeticContentLoader(TAG_COSMETIC);
            Scenes    = new SceneContentLoader(TAG_SCENE);
        }

        // ── Aggregated progress (for loading UI) ──────────────────────
        public int ContentLoadedCount => Cosmetics.LoadedCount + Scenes.LoadedCount;
        public int ContentTotalCount  => Cosmetics.TotalCount  + Scenes.TotalCount;
        public float ContentProgress  => ContentTotalCount > 0
            ? (float)ContentLoadedCount / ContentTotalCount
            : 0f;

        // ── IContentRegistry ──────────────────────────────────────────

        public async UniTask Initialize()
        {
            // Load both loaders in parallel
            await UniTask.WhenAll(
                Cosmetics.Initialize(),
                Scenes.Initialize()
            );

            Debug.Log("[ContentRegistry] All content loaded.");
        }

        public void Dispose()
        {
            Cosmetics?.Dispose();
            Scenes?.Dispose();
        }
    }
}
```

---

## 6. Initialization System

### IInitializable.cs

Any system that needs to participate in the initialization gate implements this interface.
Call `InitializationCoordinator.Register(this)` in `Awake`.
Call `InitializationCoordinator.ReportReady(SystemId)` when initialization is complete.
Call `InitializationCoordinator.ReportProgress(SystemId, 0f–1f)` during work to feed the loading UI.

`DisplayName` and `Progress` have default implementations — systems that don't report granular
progress can ignore them entirely.

```csharp
namespace jeanf.ContentManagement
{
    public interface IInitializable
    {
        string SystemId { get; }
        string DisplayName => SystemId;
        float Progress => 0f;
    }
}
```

### LoadingEntry.cs

Read-only snapshot of a single entry in the loading UI list.

```csharp
namespace jeanf.ContentManagement
{
    public enum LoadingState { Pending, Loading, Complete, Failed }

    public class LoadingEntry
    {
        public string Id           { get; internal set; }
        public string DisplayName  { get; internal set; }
        public string Group        { get; internal set; }
        public LoadingState State  { get; internal set; }
        public float Progress      { get; internal set; }
        public int LoadedCount     { get; internal set; }
        public int TotalCount      { get; internal set; }
    }
}
```

| Group       | Entries                                                                     |
| ----------- | --------------------------------------------------------------------------- |
| `"Content"` | One entry per `ContentLoader` subtype (Cosmetics, Scene Metadata)           |
| `"World"`   | Three entries: World Dependencies, Region (`startRegion.levelName`), Player |
| `"Systems"` | One entry per registered `IInitializable` in `RequiredSystemsConfig`        |

### InitializationCoordinator.cs

Orchestrates the full init sequence: fade, phased loading, loading messages, completion gate,
and all loading progress state consumed by `LoadingProgressUI`.

```csharp
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using jeanf.EventSystem;
using jeanf.SceneManagement;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class InitializationCoordinator : MonoBehaviour
    {
        [SerializeField] private GameInitConfig _config;
        [SerializeField] private StringEventChannelSO _loadingMessageChannel;

        private static InitializationCoordinator _instance;

        private ContentRegistry _contentRegistry;
        private readonly Dictionary<string, IInitializable> _registered = new();
        private readonly HashSet<string> _readySystems = new();
        private readonly Dictionary<string, LoadingEntry> _entries = new();

        public static event System.Action OnInitComplete;
        public static event System.Action OnProgressChanged;
        public static IReadOnlyCollection<LoadingEntry> Entries
            => _instance?._entries.Values;

        private void Awake()
        {
            _instance = this;
        }

        // ── Registration ──────────────────────────────────────────────

        public static void Register(IInitializable system)
        {
            if (_instance == null) return;
            _instance._registered[system.SystemId] = system;
        }

        public static void ReportReady(string systemId)
        {
            if (_instance == null) return;
            _instance._readySystems.Add(systemId);
            _instance.SetEntry(systemId, LoadingState.Complete, 1f);
            _instance.CheckComplete();
        }

        public static void ReportProgress(string systemId, float progress)
        {
            if (_instance == null) return;
            _instance.SetEntry(systemId, LoadingState.Loading, progress);
        }

        // ── Sequence ──────────────────────────────────────────────────

        public async UniTask Run(ContentRegistry contentRegistry)
        {
            _contentRegistry = contentRegistry;
            BuildEntries();
            SubscribeToContentProgress();
            await RunSequence();
        }

        private async UniTask RunSequence()
        {
            NoPeeking.SetIsLoadingState(true);

            SetEntry("content.cosmetics",    LoadingState.Loading, 0f);
            SetEntry("content.scenes",       LoadingState.Loading, 0f);
            BroadcastMessage("Loading content...");
            await _contentRegistry.Initialize();
            SetEntry("content.cosmetics",    LoadingState.Complete, 1f);
            SetEntry("content.scenes",       LoadingState.Complete, 1f);

            SetEntry("world.dependencies",   LoadingState.Loading, 0f);
            BroadcastMessage("Loading world...");
            await WorldManager.LoadWorldDependencies();
            SetEntry("world.dependencies",   LoadingState.Complete, 1f);

            SetEntry("world.region",         LoadingState.Loading, 0f);
            BroadcastMessage($"Loading {_config.startRegion.levelName}...");
            await WorldManager.LoadRegion(_config.startRegion);
            SetEntry("world.region",         LoadingState.Complete, 1f);

            SetEntry("world.player",         LoadingState.Loading, 0f);
            BroadcastMessage("Placing player...");
            WorldManager.SpawnPlayer(_config.startRegion.SpawnPosOnInit);
            WorldManager.SetInitialLocation(_config.startRegion, _config.startZone);
            SetEntry("world.player",         LoadingState.Complete, 1f);

            BroadcastMessage("Initializing systems...");
            await WaitForRequiredSystems();

            NoPeeking.SetIsLoadingState(false);
            OnInitComplete?.Invoke();
        }

        // ── Entry management ──────────────────────────────────────────

        private void BuildEntries()
        {
            AddEntry("content.cosmetics",  "Cosmetics",                      "Content");
            AddEntry("content.scenes",     "Scene Metadata",                 "Content");
            AddEntry("world.dependencies", "World Dependencies",             "World");
            AddEntry("world.region",       _config.startRegion.levelName,    "World");
            AddEntry("world.player",       "Player",                         "World");

            foreach (var entry in _config.systemsConfig.requiredSystems)
            {
                var displayName = _registered.TryGetValue(entry.systemId, out var sys)
                    ? sys.DisplayName
                    : entry.systemId;
                AddEntry(entry.systemId, displayName, "Systems");
            }
        }

        private void SubscribeToContentProgress()
        {
            _contentRegistry.Cosmetics.OnProgressChanged += () =>
            {
                SetEntry("content.cosmetics", LoadingState.Loading,
                    _contentRegistry.Cosmetics.Progress,
                    _contentRegistry.Cosmetics.LoadedCount,
                    _contentRegistry.Cosmetics.TotalCount);
            };

            _contentRegistry.Scenes.OnProgressChanged += () =>
            {
                SetEntry("content.scenes", LoadingState.Loading,
                    _contentRegistry.Scenes.Progress,
                    _contentRegistry.Scenes.LoadedCount,
                    _contentRegistry.Scenes.TotalCount);
            };
        }

        private void AddEntry(string id, string displayName, string group)
        {
            _entries[id] = new LoadingEntry
            {
                Id = id, DisplayName = displayName, Group = group,
                State = LoadingState.Pending, Progress = 0f
            };
        }

        private void SetEntry(string id, LoadingState state, float progress,
            int loaded = 0, int total = 0)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            entry.State      = state;
            entry.Progress   = progress;
            entry.LoadedCount = loaded;
            entry.TotalCount  = total;
            OnProgressChanged?.Invoke();
        }

        // ── Completion gate ───────────────────────────────────────────

        private async UniTask WaitForRequiredSystems()
        {
            var required = _config.systemsConfig.requiredSystems.Select(e => e.systemId).ToList();
            if (required.All(id => _readySystems.Contains(id))) return;

            foreach (var entry in _config.systemsConfig.requiredSystems)
            {
                if (!_readySystems.Contains(entry.systemId))
                    BroadcastMessage(entry.loadingMessage);
            }

            await UniTask.WaitUntil(() => required.All(id => _readySystems.Contains(id)));
        }

        private void CheckComplete()
        {
            var required = _config.systemsConfig.requiredSystems.Select(e => e.systemId);
            if (!required.All(id => _readySystems.Contains(id))) return;
            NoPeeking.SetIsLoadingState(false);
            OnInitComplete?.Invoke();
        }

        private void BroadcastMessage(string message)
        {
            _loadingMessageChannel?.RaiseEvent(message);
        }
    }
}
```

---

## 7. Loading Progress UI

### LoadingProgressUI.cs

Subscribes to `InitializationCoordinator.OnProgressChanged` and renders one row per `LoadingEntry`,
grouped by `Entry.Group`. Purely a reader — no coupling back to the coordinator or loaders.

Expected UI layout:

```
┌─ Content ──────────────────────────────────────────┐
│  Cosmetics        [████████░░]  8 / 10             │
│  Scene Metadata   [██████████]  5 / 5   ✓          │
├─ World ────────────────────────────────────────────┤
│  World Dependencies  [██████████]  ✓               │
│  Forest Region       [████░░░░░░]  Loading...      │
│  Player              [░░░░░░░░░░]  Pending         │
├─ Systems ──────────────────────────────────────────┤
│  VolumeSystem     [████████░░]  Loading...         │
│  MyGameSystem     [██████████]  ✓                  │
└────────────────────────────────────────────────────┘
```

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class LoadingProgressUI : MonoBehaviour
    {
        [SerializeField] private LoadingGroupUI cosmeticGroup;
        [SerializeField] private LoadingGroupUI worldGroup;
        [SerializeField] private LoadingGroupUI systemsGroup;

        private void OnEnable()
        {
            InitializationCoordinator.OnProgressChanged += Refresh;
            InitializationCoordinator.OnInitComplete    += Hide;
        }

        private void OnDisable()
        {
            InitializationCoordinator.OnProgressChanged -= Refresh;
            InitializationCoordinator.OnInitComplete    -= Hide;
        }

        private void Refresh()
        {
            var entries = InitializationCoordinator.Entries;
            if (entries == null) return;

            cosmeticGroup.Render(Filter(entries, "Content"));
            worldGroup.Render(Filter(entries, "World"));
            systemsGroup.Render(Filter(entries, "Systems"));
        }

        private void Hide() => gameObject.SetActive(false);

        private static IEnumerable<LoadingEntry> Filter(
            IEnumerable<LoadingEntry> entries, string group)
        {
            foreach (var e in entries)
                if (e.Group == group) yield return e;
        }
    }
}
```

### LoadingGroupUI.cs

Renders a single group heading and one `LoadingRowUI` per entry.
Spawns/recycles rows from a pool keyed by `entry.Id`.

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class LoadingGroupUI : MonoBehaviour
    {
        [SerializeField] private LoadingRowUI rowPrefab;
        [SerializeField] private Transform rowParent;

        private readonly Dictionary<string, LoadingRowUI> _rows = new();

        public void Render(IEnumerable<LoadingEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (!_rows.TryGetValue(entry.Id, out var row))
                {
                    row = Instantiate(rowPrefab, rowParent);
                    _rows[entry.Id] = row;
                }
                row.Render(entry);
            }
        }
    }
}
```

### LoadingRowUI.cs

One row: display name on the left, progress bar in the middle, state label on the right.
The `X / Y` count is shown only when `TotalCount > 0`.

```csharp
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace jeanf.ContentManagement
{
    public class LoadingRowUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameLabel;
        [SerializeField] private Slider progressBar;
        [SerializeField] private TMP_Text stateLabel;

        public void Render(LoadingEntry entry)
        {
            nameLabel.text    = entry.DisplayName;
            progressBar.value = entry.Progress;

            stateLabel.text = entry.State switch
            {
                LoadingState.Pending  => "Pending",
                LoadingState.Loading  => entry.TotalCount > 0
                    ? $"{entry.LoadedCount} / {entry.TotalCount}"
                    : "Loading...",
                LoadingState.Complete => "✓",
                LoadingState.Failed   => "✗",
                _                    => string.Empty
            };
        }
    }
}
```

---

## 8. WorldManager Integration

New public surface required on `WorldManager`. All existing region-change and scenario logic is unchanged except for `OnRegionChangedFromECS` which gets the adjacency branch (see Adjacent Regions section).

```csharp
// ── Init sequence hooks (called by InitializationCoordinator) ─────────────

public static UniTask LoadWorldDependencies()  { ... }
public static UniTask LoadRegion(Region region) { ... }
public static void SpawnPlayer(SpawnPos spawnPos) { ... }

public static void SetInitialLocation(Region region, Zone zone)
{
    _currentPlayerRegion = region;
    _currentPlayerZone   = zone ?? region.zonesInThisRegion.FirstOrDefault();

    PublishCurrentRegionId?.Invoke(region.id);
    PublishCurrentRegion?.Invoke(region);
    PublishCurrentZoneId?.Invoke(_currentPlayerZone?.id ?? default);
    IsInitialized = true;
}

// ── ECS-triggered region change (walkthrough — no fade, no teleport) ─────

public static bool IsInitialized { get; private set; }

public delegate void BroadcastRegion(Region region);
public static BroadcastRegion PublishCurrentRegion;

private void OnRegionChangedFromECS(FixedString128Bytes id)
{
    var regionId = id.ToString();
    if (regionId == _lastNotifiedRegion) return;
    if (string.IsNullOrEmpty(regionId)) return;
    if (!_regionDictionary.TryGetValue(regionId, out var region)) return;

    _lastNotifiedRegion  = regionId;
    _currentPlayerRegion = region;

    PublishCurrentRegionId?.Invoke(region.id);
    PublishCurrentRegion?.Invoke(region);
    RequestLoadForRegionDependencies(region);
}
```

`SetInitialLocation` bypasses the ECS event path entirely — sets current state directly and sets
`IsInitialized = true`. `VolumeSystem` checks this flag before processing any notifications.

`PublishCurrentRegion` is fired alongside `PublishCurrentRegionId` in all three region-change
paths: init, walkthrough, and manual transition. Map UIs and any other Region-aware subscriber
should bind to this delegate instead of `PublishCurrentRegionId`.

---

## 9. App Entry Point

### ClientAppStartup.cs

Thin entry point — creates the `ContentRegistry` and hands off to `InitializationCoordinator`.

```csharp
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class ClientAppStartup : MonoBehaviour
    {
        [SerializeField] private InitializationCoordinator _coordinator;

        private ContentRegistry _contentRegistry;

        private async void Start()
        {
            _contentRegistry = new ContentRegistry();
            await _coordinator.Run(_contentRegistry);
        }

        private void OnDestroy()
        {
            _contentRegistry?.Dispose();
        }
    }
}
```

---

## 10. Scene Report

Combines `SceneContentLoader` metadata with runtime state from `WorldManager` to produce a full
asset inventory report. Useful for auditing what loaded and what didn't.

```csharp
using System.Text;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public static class SceneReport
    {
        public static string Generate(ContentRegistry registry)
        {
            var csv = new StringBuilder();
            registry.Scenes.WriteReport(csv);
            return csv.ToString();
        }

        public static void WriteToFile(ContentRegistry registry, string path)
        {
            System.IO.File.WriteAllText(path, Generate(registry));
        }
    }
}
```

---

## 11. Usage Examples

### Accessing cosmetics at runtime

```csharp
var accessory = contentRegistry.Cosmetics.Find(cosmeticId);
var allCharacters = contentRegistry.Cosmetics.FindByType(CosmeticType.CHARACTER);
```

### Querying scene metadata at runtime

```csharp
var scenes = contentRegistry.Scenes.GetAll();
foreach (var scene in scenes)
{
    Debug.Log($"{scene.Scene.Name} — Active: {scene.IsActive}");
}
```

### Generating a scene report

```csharp
SceneReport.WriteToFile(contentRegistry, "scene_report.csv");
```

### Registering a custom system with the init gate

```csharp
public class MyGameSystem : MonoBehaviour, IInitializable
{
    public string SystemId    => "MyGameSystem";
    public string DisplayName => "My Game System";

    private void Awake()
    {
        InitializationCoordinator.Register(this);
    }

    private async void Start()
    {
        for (int i = 0; i < steps.Length; i++)
        {
            await steps[i].Execute();
            InitializationCoordinator.ReportProgress(SystemId, (float)(i + 1) / steps.Length);
        }
        InitializationCoordinator.ReportReady(SystemId);
    }
}
```

Add `"MyGameSystem"` to the `RequiredSystemsConfig` SO in the inspector to include it in the gate.

### Spawning in a specific region (designer workflow)

Create a `GameInitConfig` SO per starting context (main game, test scene, tutorial):

- Set `startRegion` to any configured `Region` SO
- Set `startZone` to a specific zone, or leave null for the region's first zone
- Assign the appropriate `RequiredSystemsConfig`

---

## 12. Dependencies

| Package                  | Purpose                      |
| ------------------------ | ---------------------------- |
| `com.unity.addressables` | Async asset loading          |
| `com.cysharp.unitask`    | UniTask async/await          |
| `com.unity.localization` | LocalizedString on cosmetics |

---

## 13. Addressables Setup Checklist

- [ ] Tag all `CosmeticContentSo` assets with the label **`cosmetic`**
- [ ] Tag all `SceneContentSo` assets with the label **`scene`**
- [ ] Ensure label names match the constants in `ContentRegistry`
- [ ] `MergeMode.Intersection` is intentional — assets need **all** supplied tags to be loaded
- [ ] Add your bootstrap scene to **Build Settings** (not addressables)
- [ ] Attach `ClientAppStartup` + `InitializationCoordinator` to a `DontDestroyOnLoad` GameObject in the bootstrap scene
- [ ] Create at least one `GameInitConfig` SO and assign it to `InitializationCoordinator`
- [ ] Create at least one `RequiredSystemsConfig` SO and assign it to `GameInitConfig`
- [ ] Existing `Region` SOs require no changes

---

## 14. Implementation Plan

All work is greenfield except where marked **modify existing**.

### Chunk A — Core types (no dependencies, parallel-safe)

| File | Action | Notes |
|---|---|---|
| `UnlockableContent.cs` | Create | Enum only |
| `ContentUnlockService.cs` | Create | Static delegate bridge |
| `IContentRegistry.cs` | Create | Interface: `Initialize()` + `Dispose()` |
| `IInitializable.cs` | Create | Interface with default `DisplayName` + `Progress` |
| `LoadingState.cs` + `LoadingEntry.cs` | Create | Can be one file |
| `Zone.cs` | **Modify existing** | Add `contentId` field + `IsAccessible()` |
| `Scenario.cs` | **Modify existing** | Add `enableFadeOnLoad` field |
| `Region.cs` | **Modify existing** | Add `map: Sprite` field |

### Chunk B — ScriptableObject data containers (depends on Chunk A)

| File | Action | Notes |
|---|---|---|
| `GameContentSo.cs` | Create | Base SO with `IsUnlocked()` via `ContentUnlockService` |
| `CosmeticContentSo.cs` | Create | Extends `GameContentSo`; `CosmeticType` enum lives here |
| `SceneContentSo.cs` | Create | Extends `GameContentSo`; metadata only |
| `GameInitConfig.cs` | Create | SO: `startRegion`, `startZone`, `systemsConfig` |
| `RequiredSystemsConfig.cs` | Create | SO: `List<SystemEntry>` |

### Chunk C — Content loaders + registry (depends on Chunk B)

| File | Action | Notes |
|---|---|---|
| `ContentLoader<T>.cs` | Create | Abstract generic Addressables loader base |
| `CosmeticContentLoader.cs` | Create | Extends `ContentLoader<CosmeticContentSo>` |
| `SceneContentLoader.cs` | Create | Extends `ContentLoader<SceneContentSo>` |
| `ContentRegistry.cs` | Create | Implements `IContentRegistry`; owns both loaders |

### Chunk D — Initialization + startup (depends on Chunk C)

| File | Action | Notes |
|---|---|---|
| `InitializationCoordinator.cs` | Create | MonoBehaviour; owns phased sequence |
| `ClientAppStartup.cs` | Create | MonoBehaviour; creates `ContentRegistry`, calls coordinator |
| `ScenarioManager.cs` | **Modify existing** | Wrap `NoPeeking.SetIsLoadingState` with `enableFadeOnLoad` guard |

### Chunk E — WorldManager additions (depends on Chunk A; parallel with C/D)

| File | Action | Notes |
|---|---|---|
| `WorldManager.cs` | **Modify existing** | Add `_zoneRegistry`, zone lock check in `OnZoneChangedFromECS`, fix `OnRegionChangedFromECS` to lightweight path, add `PublishCurrentRegion` delegate, add `SetInitialLocation`, `IsInitialized` |

### Chunk F — Loading UI (depends on Chunk D)

| File | Action | Notes |
|---|---|---|
| `LoadingProgressUI.cs` | Create | MonoBehaviour; reads `InitializationCoordinator.Entries` |
| `LoadingGroupUI.cs` | Create | Renders one group + spawns rows |
| `LoadingRowUI.cs` | Create | One row: name + progress bar + state |

### Chunk G — Scene report (depends on Chunk C)

| File | Action | Notes |
|---|---|---|
| `SceneReport.cs` | Create | Static utility; calls `ContentRegistry.Scenes.WriteReport()` |

### Parallelization strategy

```
Chunk A  ──────────────────────────────────────────────────────────► agent-1
Chunk B  (starts when A done) ─────────────────────────────────────► agent-1
Chunk C  (starts when B done) ─────────────────────────────────────► agent-1

Chunk E  (only needs A, starts in parallel with B) ────────────────► agent-2
Chunk D  (starts when C done + E done) ────────────────────────────► agent-1
Chunk F  (starts when D done) ─────────────────────────────────────► agent-3
Chunk G  (starts when C done) ─────────────────────────────────────► agent-3
```

Agents 1, 2, 3 can run simultaneously once their dependencies are ready. Estimated order: A → B+E parallel → C → D → F+G parallel.
