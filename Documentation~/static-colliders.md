# Plan — static collider proxies for baked SubScene props

**Package:** `fr.jeanf.scenemanagement` (SceneManagement)
**Status:** planned — minor, additive
**Target version:** 1.3.2 → **1.4.0**

> This doc lives in the UniversalPlayer docs folder for convenience during planning; move it to
> `SceneManagement/Documentation~/` when work starts, alongside `proposal-object-zone-tracking.md`.

## Problem

Props authored inside a SubScene (chairs, tables, crates) are baked to entities and their
`Collider` components are **stripped**. The project has no `com.unity.physics`, so nothing
replaces them: the geometry renders via Entities Graphics and the player's PhysX
`CharacterController` walks straight through it.

Today the only baked things that block the player are `Seat`s, via
`SeatBaker` → `SeatComponent` → `SeatDataBridge` spawning `BoxCollider` proxies. A plain
prop with a collider gets nothing, because `SeatBaker` is a `Baker<Seat>`.

The requirement: **a level designer adds a collider to the prop prefab and the player is
blocked** — no invisible companion prefab, no per-instance work, no second thing to keep in
sync when a chair moves.

## Approach

Reuse the pattern already proven three times in this repo (`DoorDataBridge` +
`BoxColliderPoolManager`, `SeatDataBridge`, `ObjectZoneTrackingBridge`): **bake the collider
shapes to data, spawn PhysX proxies near the player at runtime.** No ECS physics, no
migration of the player motion stack.

Explicitly **not** in scope: making ECS the owner of collision, adding `com.unity.physics`,
or touching `PlayerMovement`. Those stay deferred.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Opt-in | `StaticColliderAuthoring` component in the prefab | Explicit, so triggers/doors/seats are never proxied by accident. One component per **prefab** covers every instance on every floor. |
| Prefab structure | **Agnostic** — component may sit anywhere in the hierarchy | Requirement. Offsets are resolved relative to the component's own transform at bake time. |
| Shapes | Box + Sphere + Capsule | Covers primitive-authored props; all blittable, no asset refs. `MeshCollider` is out of scope (warned at bake). |
| Home | **SceneManagement** | Already owns SubScene streaming, the follow entity, and `ObjectZoneTrackingBridge`. Needs no new assembly and no new package edge. |
| Pool | Distance-culled, capped, **nearest-first** eviction | Chairs across all floors can be hundreds in range — unlike doors (~25) or sparse seats. |

## Design

### Authoring → bake

`StaticColliderAuthoring` (MonoBehaviour, no fields required):

- `bool includeChildren = true` — collect colliders on descendants too
- `bool includeTriggers = false` — triggers are not blockers; off by default

The baker collects `BoxCollider` / `SphereCollider` / `CapsuleCollider` from the component's
own GameObject and (optionally) its descendants. For **each** collider it bakes the transform
**relative to the authoring transform**:

```
float4x4 local = authoring.transform.worldToLocalMatrix * collider.transform.localToWorldMatrix
```

into `DynamicBuffer<ProxyColliderElement>`:

- `Shape` (enum: Box / Sphere / Capsule)
- local position / rotation / scale (decomposed from `local`)
- the collider's own `center`, plus `size` (box) or `radius` (+ `height`, `direction` for capsule)
- `Layer`, `IsTrigger`

The buffer sits on the authoring GameObject's entity
(`GetEntity(TransformUsageFlags.Dynamic)`), so its runtime `LocalToWorld` **is** the authoring
transform's world pose. That is what makes this structure-agnostic: nesting depth and
placement within the prefab don't matter, and it inherently fixes the known child-offset bug
that `SeatBaker` documents ("*a child collider is still baked but may be offset*").

`DependsOn(collider)` per collider so edits re-bake.

### Runtime bridge

`StaticColliderBridge` — singleton MonoBehaviour on an always-loaded GameObject, modelled
directly on `SeatDataBridge`:

- queries entities with `ProxyColliderElement` + `LocalToWorld`
- reconciles every `refreshInterval` (default 0.25 s) — props are static, so **place once**
  and destroy on out-of-range or stream-out, no per-frame follow
- culls by `cullingDistance` (default 30 m) from `Camera.main`
- **caps** at `maxProxies`; when more candidates are in range, keeps the **nearest** and
  `Debug.Log`s once that the cap was hit (a silently truncated pool reads as "collision is
  broken" and is miserable to diagnose)
- each proxy = a root GameObject at the entity's `LocalToWorld`, one child per buffer element
  carrying the baked local TRS + the primitive collider + layer

The entity's own lossy scale is applied at the proxy root, as `SeatDataBridge` already does.

### Dual-world behaviour

In a **classic additive scene** the component is inert — the real colliders are already
there and already work. Same dual-world property as `Seat`: one prefab, correct in either
world, nothing to switch.

## Files

New, under `SceneManagement/Runtime/StaticColliders/`:

1. `StaticColliderAuthoring.cs` — component + baker + `ProxyColliderElement` (one file; the
   seat equivalents are split across three, which is more than this needs)
2. `StaticColliderBridge.cs` — the reconcile/pool loop

**No new runtime assembly.** `jeanf.scenemanagement.asmdef` sits at `Runtime/` root and already
references Entities, Transforms, Collections, Burst and Mathematics, so a new subfolder is
covered automatically. (This was the one real complication of putting it in UniversalPlayer:
that package's entities asmdef lives *inside* `Runtime/scripts/Sitting/Entities/` and cannot
host non-sitting code without either mis-filing it or breaking the seat build.)

For validation:

3. `SceneManagement/Runtime/Editor/StaticColliderValidation.cs` — goes into the **existing**
   `jeanf.scenemanagement.editor` assembly at `Runtime/Editor/`, which needs `Unity.Scenes` (for
   `SubScene`) and `Unity.Mathematics` added to its references. Do **not** create a second editor
   asmdef: Unity rejects duplicate assembly names and the error aborts the whole compile, which
   looks like "the new code is broken" rather than "there are two asmdefs".

Modified:

5. `package.json` — **1.3.2 → 1.4.0**
6. `Documentation~/static-colliders.md` + a README line

## Validation

**UniversalPlayer's `ProjectSetupChecks` cannot host these checks.** `fr.jeanf.universal.player`
does not depend on `fr.jeanf.scenemanagement` (verified in both `package.json` files), and
adding that dependency to reach a validator would be the wrong direction — the player package
should not require the streaming package. So SceneManagement validates its own feature. The
cost is two validation entry points; the benefit is correct layering.

No need to replicate UniversalPlayer's `SetupValidator` window. A single small editor script:

- **`playModeStateChanged` hook** — on entering play mode, if the scene contains any
  `Unity.Scenes.SubScene` but **no `StaticColliderBridge`**, log a warning. This is the failure
  that looks exactly like "ECS collision doesn't work": correctly authored props, no bridge, no
  colliders at all, no clue why. Mirrors the intent of the existing `CheckSeatDataBridge`.
- **`[MenuItem]` scan** — prefabs with `StaticColliderAuthoring` but zero *supported* colliders,
  or with a `MeshCollider` under the component (unsupported, named explicitly). Menu path per the
  project convention `Tools/[PackageName]/[Function]`:
  **`Tools/SceneManagement/Validate Static Colliders`** — no new menu root. See *Menu paths* below.
- **layer warning** — authored collider layers that don't collide with the player's layer in the
  Physics Layer Collision Matrix. Complements UniversalPlayer's existing
  `CheckPlayerGroundCollision`, which only covers the player's side.

The baker also warns directly (with object context) for unsupported colliders, so the common
mistake surfaces at bake time without anyone running a menu item.

## Tests

Per the project standard (validation for setup + tests to lock behaviour), structured so the
risky parts are testable without a baked SubScene:

**Must have** — into the existing `jeanf.scenemanagement.tests.editor` assembly, whose
`VolumeMathTests.cs` is exactly this shape (pure functions, no scene):

- offset composition extracted as a *static pure function*: assert a collider on a rotated,
  offset, scaled child resolves to the correct local TRS. This is the most regression-prone
  part and the exact bug class `SeatBaker` already has.
- nearest-first selection under `maxProxies` as a pure function over (position, distance)
  pairs: cap respected, nearest kept, deterministic.

**Nice to have** — a PlayMode test (build entities with the buffer directly in a test world,
run the bridge, assert proxies spawn/despawn and that a `CharacterController` is actually
blocked). SceneManagement has no PlayMode test assembly yet, so this needs one more asmdef.
**This is the item to drop if the release gets tight** — the pure-function tests cover the
math, and the end-to-end behaviour gets an in-editor check either way.

## Publish

`fr.jeanf.*` auto-publishes on push to `main`, and **no version bump = silent publish failure
plus a stale registry**. The 1.4.0 bump in `SceneManagement/package.json` is not optional.

## Known limitations (state them in the doc, don't discover them later)

- `MeshCollider` props are not blocked. Warned at bake and in the validator.
- Rotated **non-uniform** scale cannot be represented as a child TRS; the baker warns rather
  than baking something subtly wrong.
- Proxies appear up to `refreshInterval` (0.25 s) after a section streams in — at sprint speed
  a player can briefly clip a prop during that window.
- Props beyond `cullingDistance`, or past `maxProxies`, do not block. By design; the cap is logged.

## Open questions

1. **`maxProxies` / `cullingDistance` defaults** — depends on chair density. Doors use 25 / 25 m,
   seats 30 m uncapped. Starting guess: **128 proxies / 30 m**. Rough count of chairs within
   30 m on the densest floor?
2. **Triggers** — planned as excluded (`includeTriggers = false`). Any prop that needs a baked
   *trigger* proxy rather than a blocker?
3. **Which prefabs get the component in this release** — chairs only, or the full prop set?
   Affects nothing in the code, but it decides what actually gets tested before you ship.

## Menu paths — decided

Convention: `Tools/[PackageName]/[Function]`, no new roots.

The new validator is **`Tools/SceneManagement/Validate Static Colliders`**, and this package's two
existing items are renamed **in the same commit** so it ships with one root, not two:

- `Tools/Scene Management/Scene Loading Tracker` → `Tools/SceneManagement/Scene Loading Tracker`
- `Tools/Scene Management/Volume Data Generator` → `Tools/SceneManagement/Volume Data Generator`

These ride along free on the 1.4.0 bump this feature already needs.

Renames in **other** packages (`Tools/Events/…` → `Tools/EventSystem/…`, `Tools/Tooltip/…` →
`Tools/TooltipSystem/…`, `Tools/Validation/…` → `Tools/propertyDrawer/…`, and FunctionTimer's
`Tools/Function Timer/…` → `Tools/UniversalPlayer/…`) are **out of scope here** — each would force a
version bump of an otherwise untouched package just for a menu string. Batch them after the release.

## Separate from this work

Neither of these is caused by the plan, and either would make the feature look broken:

- Does your real Main scene contain a `SeatDataBridge`? By GUID it appears in exactly **one**
  scene in this repo — `SceneManagement/Samples/Example/Scenes/Main.unity`. If it is missing in
  the project, baked *seats* already don't block or respond, independently of this change.
- Do the chair layers collide with the player's layer in the Physics Layer Collision Matrix?
  If not, proxies spawn and still don't block.
