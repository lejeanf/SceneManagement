# Hybrid prefabs — GameObject constructs inside baked SubScenes

**Package:** `fr.jeanf.scenemanagement` (SceneManagement)
**Status:** shipped in **1.5.0**

## Problem

Baking a SubScene strips every component that has no baker: `Canvas`, `CanvasRenderer`,
TextMeshPro, `Sampler`, custom MonoBehaviours, SteamAudio components. A prefab like the
elevator — meshes, automatic doors, an interactive world-space UI, an ambiance audio rig —
loses its UI and audio the moment it is authored inside a SubScene. `AudioSource` is worse
than stripped: Entities Graphics bakes it as a **companion object** (`AudioSourceCompanionBaker`),
so a Play-On-Awake source keeps playing with nothing (its Sampler is gone) able to stop it.

The requirement: keep such prefabs as **one prefab**, put **one component on its root**, and
have every GameObject-only part under it come back at runtime — nothing baked twice, nothing
lost silently.

## Approach

The pattern already proven by `TooltipDataBridge`, `SeatDataBridge` and `DoorDataBridge`: bake
**placement records**, respawn the real thing in the main world while the record's section is
streamed in. Plus one new move: every respawned subtree is **removed from the baked world**
(`BakingOnlyEntity`, applied via the same TemporaryBakingType-buffer pattern Unity's own
`BakingOnlyEntityAuthoring` uses) so it cannot exist twice.

- `HybridPrefabAuthoring` — ONE per prefab root. At bake it **sweeps** the hierarchy: every
  highest subtree containing a `Canvas` or an `AudioSource` and **no `Renderer`** is recorded
  (prefab-asset counterpart + pose relative to the root) and stripped from the bake.
  `additionalSubtrees` force-includes subtrees the sweep can't see (e.g. an empty proxy carrying
  a `SteamAudioDynamicObject`); `excludedSubtrees` vetoes detections. Assigning the `prefab`
  field instead turns the object into a plain placement marker (TooltipAuthoring-style, no sweep).
- `HybridPrefabBridge` — singleton on a persistent GameObject; reconciles every `refreshInterval`
  (default 0.25 s): for each root entity, spawns every record at `rootL2W × LocalFromRoot` under
  a `SubSceneHybridPrefabs` container; streaming out destroys them.
- `HybridPrefabStripBakingSystem` — baking-world system that applies `BakingOnlyEntity` to every
  swept entity.

The subtrees stay real children of the prefab: fully visible and editable in the editor, and
functional as-is in a classic additive scene (bakers never run there — dual-world, zero
per-world variants). The custom inspector lists what the sweep detected and — critically —
which GameObject-world components are **left in baked territory** (a `SteamAudioGeometry` on a
mesh, an `AudioSource` sharing a GameObject with a Renderer) so nothing vanishes silently.

## Elevator worked example

| Subtree | Sweep result | Why |
|---|---|---|
| `ElevatorCage` (meshes, colliders, SteamAudioGeometry) | baked | has Renderers; SteamAudioGeometry is reported stranded — see below |
| `ElevatorDoors` | baked | has Renderers; door behaviour/audio belongs to AutomaticDoorSystem authoring |
| `ElevatorAudioAmbiance` (AudioSources, Sampler, FloorAnnouncement, SteamAudioSource, controllers) | **respawned** | contains AudioSource, no Renderer |
| `ElevatorUI` (world-space Canvas, TMP, raycasters) | **respawned** | contains Canvas, no Renderer |

## Rules & caveats

- **Per-instance overrides on a swept subtree are not spawned** — the prefab-asset counterpart
  is. Apply overrides to the prefab, or switch that placement to explicit-prefab mode.
- **Play On Awake is fine** on swept AudioSources: the baked copy is stripped entirely, so only
  the bridge-spawned instance plays.
- **SteamAudio on baked geometry** is out of this component's reach: a `SteamAudioGeometry` on a
  baked mesh is stripped (its editor-time bake data still exports, but subscenes never load as
  Unity scenes at runtime, so scene-level static geometry does not load). Options: carry room
  response in the additive dependency scene, or give the geometry a `SteamAudioDynamicObject`
  with exported serialized data on an empty proxy child and add it to `additionalSubtrees`.
  Door meshes that move under ECS need their SteamAudio handled by the door system's pools.
- The spawned instance is force-activated; spawns are static (placed once, never followed).
- No scene references inside a swept subtree — everything must be prefab-local or self-wiring,
  same rule as tooltips and trash.

## Non-goals

Making UGUI/audio bake natively, per-frame transform sync, pooling (spawn counts are tiny), and
SteamAudio proxies for ECS-animated door panels (AutomaticDoorSystem's job).
