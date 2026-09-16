# Scenario reset on unload

When a scenario ends, its scenes are unloaded and the zones it was using get locked. Two things
used to go wrong at that exact moment:

1. the player still standing inside one of those zones was locked in, with no way out;
2. props the scenario had teleported around stayed wherever the scenario had left them.

`ScenarioManager` now runs a short **teardown** before the scenes go away:

```
end scenario request
  1. put back every object the scenario teleported
  2. take the player out of the zones this unload locks
       - wait for them to walk out (grace period)
       - teleport them out when the wait runs out
  3. unload the scenes / drop the zone overrides
  4. onUnloadScenario
```

Nothing here fires while a scenario is running: the whole sequence is tied to the unload request.

---

## 1. Where the player is sent

A scenario never says where the exit is. It only says which zones it uses
(`listOfZonesNeededForThisScenario`); the exits are declared by the world itself, through
`ZoneEvacuationRegistry`.

Two kinds of provider feed that registry:

| Provider | Where it lives | Use it for |
|---|---|---|
| **Door sides** (`DoorAuthoring` + `DoorZoneEvacuationProvider`) | AutomaticDoorSystem | the normal case: the room has a door, so the way out *is* the door |
| **`ZoneExitAnchor`** | SceneManagement | zones with no door, ambiguous doors, or a scenario needing one specific spot |

Resolution order at evacuation time: every provider is asked, and the exit **closest to the
player** wins. A provider that knows where its exit lands (a `ZoneExitAnchor` with its destination
zone set) withdraws when that destination is locked by the same unload - evacuating from one
locked room into the next solves nothing.

### A zone with no exit is a zone that does not lock

This is the important part of the deduction. A scenario's zone list often contains zones that
cannot be locked because nothing closes them (an open corridor, a landing). If no provider
declares an exit for the zone the player is in, the manager logs it and unloads without moving
anybody, instead of inventing a destination.

So you do **not** list "lockable zones" anywhere. You declare exits where exits physically exist,
and the zones that have none are simply left alone.

### Door setup

One transform to place, nothing to wire. On the `DoorAuthoring` of the door that closes the room,
assign **Exit Anchor**: an empty GameObject on the SAFE side of the doorway, facing away from the
door. That is where the player lands. Keep it inside the door's trigger volume so the door opens
for the player it just received - the Scene gizmo turns orange when it falls outside.

Which room that exit frees is **not authored**: a door's `doorId` is the number of the room it
closes (`Zone.zoneNb`), which is how the rest of the project already addresses rooms. The provider
resolves it through `WorldManager.GetZoneByNumber(doorId)`. So door `2005` frees the zone numbered
`2005` (Exam Room 01), and a corridor-to-corridor door like `2050`, whose number matches no zone,
never answers an evacuation query. The inspector prints the room it resolved to right under the
anchor field, and warns when no zone carries that number.

Leaving the anchor empty costs nothing: the door bakes exactly as before.

The anchor is baked into the door entity (`DoorZoneExit`) because doors live in SubScenes and their
authoring components are gone at runtime. Add one **DoorZoneEvacuationProvider** to the
DoorManagement object so those baked exits are visible to SceneManagement.

### ZoneExitAnchor setup

Drop the component on an empty GameObject, list the zones it evacuates, click `Detect zone this
anchor stands in`, and point its blue axis where the player should look. Put it in a scene that
stays loaded while the scenario runs - never in the scenario scene being unloaded.

---

## 2. Waiting, then forcing

On the `ScenarioManager`, under **Reset on unload**:

| Setting | Effect |
|---|---|
| `evacuationMode` | `Disabled` (old behavior) / `WaitThenForce` (default) / `ForceImmediately` |
| `gracePeriod` | seconds the player gets to walk out on their own (default 30) |
| `warningDuration` | last stretch of that period where the message switches to the final warning (default 7) |
| `pollInterval` | how often their zone is re-tested |
| `fadeOnForcedTeleport` | fade to black around the forced teleport |
| `teleportSettleDelay` | pause after the teleport before the scenes unload |
| `maxTeardownTimeout` | **0 by default = no limit**: the scenes never unload under a player still inside. A positive value is an opt-in hard ceiling |
| `showNavigationPathToExit` | publish the exit as a navigation destination (see below) |
| `evacuationMessageChannel` | optional `StringEventChannelSO` - the "leave the room" message, then the warning, then an empty string once clear |
| `evacuationCountdownChannel` | optional `FloatEventChannelSO` - seconds left, `-1` once clear |

The notice is not time-limited: it stays up for as long as the player is inside, and the countdown
only ends with them walking out or being teleported.

`ScenarioManager.PlayerEvacuationCountdown` is the same feedback as a static delegate, for UI that
does not want channel assets.

### Showing the way out

While the evacuation runs, `ScenarioManager` parks a marker on the resolved exit and publishes it
on `ScenarioManager.PlayerEvacuationDestination` (cleared with `null` at the end). The
**ScenarioEvacuationNavigation** component, in the Tooltip package, forwards it to
`NavigationDestinationSender.OnSendDestination`, so the scene's `NavigationTooltip` draws the path
to the door. Drop that component in a persistent scene and the guidance works; without it nothing
breaks, the player simply gets the message and no path.

The bridge lives in the tooltip package because the dependency runs tooltip -> scenemanagement:
the scene loader publishes where the exit is and knows nothing about tooltips.

**Player Evacuation Target** must point at the player's `SendTeleportTarget` (the very same one
`WorldManager` uses for region changes, with *Is Teleport Player* ticked). Without it the forced
teleport logs an error and the unload proceeds.

The player's zone comes from `WorldManager.CurrentPlayerZone` (ECS volume detection); the position
used to pick the nearest exit is the main camera's, unless `playerPositionProbe` is set.

---

## 3. Putting teleported objects back

Every `SendTeleportTarget` now raises `SendTeleportTarget.TeleportRequested` just **before** its
event goes out, so the pose captured is the one the object had before the move.
`ScenarioTeleportJournal` records it while a scenario is running and restores it during teardown.
It also listens to `PlayerEvents.TeleportRequested` (the UniversalPlayer hub event) so teleports
routed through the bridge are covered too; recording the same object twice is a no-op.

- Only the **first** move of a given object is kept: a prop teleported three times goes back to
  where it stood before the first teleport.
- Player teleports are never journaled (the player is the evacuation's business).
- Teleports carrying a filter listed in `ignoredTeleportFilters` are skipped - use it for spawns,
  elevators, anything that must not be undone.
- Objects destroyed with their scene are skipped silently.
- `resetVelocityOnRestore` zeroes a non-kinematic Rigidbody so a restored prop does not keep the
  momentum it had at the far end.

Turn the manager's `isDebug` on to watch the journal fill up in the inspector
(`journaledTeleports`).

---

## Notes for integrators

- The unload is now **asynchronous**. Every existing entry point (`EndScenarioRequest`,
  `KillAllScenariosRequest`, the iPad UI, `EndScenarioOnEnable`) is fire-and-forget, so nothing
  changes for callers - but a scenario transition now waits for the previous scenario's teardown
  before loading the next one.
- A second end request arriving mid-teardown waits for the first instead of starting a second
  evacuation.
- `UpdateScenariosList` is now also published after a kill-all, which it was not before.
