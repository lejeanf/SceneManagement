using System;
using System.Collections.Generic;
using jeanf.EventSystem;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// How <see cref="ScenarioManager"/> puts the world back in order when a scenario unload is
    /// requested: objects the scenario teleported go home, and the player is taken out of the
    /// zones that are about to be locked.
    /// </summary>
    [Serializable]
    public class ScenarioResetOptions
    {
        public enum EvacuationMode
        {
            /// <summary>Never move the player - the previous behavior.</summary>
            Disabled = 0,
            /// <summary>Give them <see cref="gracePeriod"/> to walk out, then teleport them.</summary>
            WaitThenForce = 1,
            /// <summary>Teleport immediately, no grace period.</summary>
            ForceImmediately = 2
        }

        [Header("Player evacuation")]
        [Tooltip("What to do when the unload is requested while the player stands in a zone the scenario is about to lock.")]
        public EvacuationMode evacuationMode = EvacuationMode.WaitThenForce;

        [Tooltip("Seconds the player gets to walk out on their own before being teleported out. 0 = teleport right away.")]
        [Min(0f)] public float gracePeriod = 30f;

        [Tooltip("Last stretch of the grace period, where the message switches to the final warning and the countdown gets loud. Kept on screen for as long as the player is still inside.")]
        [Min(0f)] public float warningDuration = 7f;

        [Tooltip("How often the player's zone is re-tested during the grace period.")]
        [Range(0.05f, 1f)] public float pollInterval = 0.2f;

        [Tooltip("Fade to black around the forced teleport. Off when another system already fades (scenario transitions do).")]
        public bool fadeOnForcedTeleport = true;

        [Tooltip("Seconds waited after the forced teleport before the scenes are unloaded, so the move and the zone re-detection have landed.")]
        [Range(0f, 2f)] public float teleportSettleDelay = 0.35f;

        [Tooltip("0 = no limit: the scenes never unload while the player is still inside a zone being locked. Set a positive value only if you want a hard ceiling on the whole teardown (past it the unload proceeds anyway, with an error).")]
        [Min(0f)] public float maxTeardownTimeout = 0f;

        [Header("Guidance")]
        [Tooltip("Publish the exit as a navigation destination while the evacuation runs, so a NavigationTooltip can draw the way out. Needs the tooltip bridge in the scene; harmless without it.")]
        public bool showNavigationPathToExit = true;

        [Header("Teleported objects")]
        [Tooltip("Send objects moved by a SendTeleportTarget during the scenario back to where they were before the first move.")]
        public bool restoreTeleportedObjects = true;

        [Tooltip("Zero out Rigidbody velocities when an object is put back, so a prop does not keep the momentum it had somewhere else.")]
        public bool resetVelocityOnRestore = true;

        [Tooltip("Teleports carrying one of these filters are never journaled (player spawns, elevators, anything that must NOT be undone).")]
        public List<FilterSO> ignoredTeleportFilters = new List<FilterSO>();

        [Header("Broadcasting on (optional)")]
        [Tooltip("Message sent when the player must leave the zone, and again (empty string) once they are clear.")]
        public StringEventChannelSO evacuationMessageChannel;

        [Tooltip("Seconds left before the forced teleport, raised every poll. Negative (-1) once the evacuation is over.")]
        public FloatEventChannelSO evacuationCountdownChannel;

        [Tooltip("Text pushed on the message channel as soon as the evacuation starts.")]
        [TextArea] public string evacuationMessage = "Le scénario se termine, veuillez sortir de la zone.";

        [Tooltip("Text pushed on the message channel for the last seconds (see Warning Duration).")]
        [TextArea] public string evacuationWarningMessage = "Sortez de la zone maintenant : vous allez être déplacé.";
    }
}
