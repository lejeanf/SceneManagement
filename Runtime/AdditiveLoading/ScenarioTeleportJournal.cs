using System;
using System.Collections.Generic;
using jeanf.EventSystem;
using jeanf.universalplayer;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Notes where objects stood before a scenario moved them, so the unload can put them back.
    /// <para>
    /// Feeds on <see cref="SendTeleportTarget.TeleportRequested"/>, which fires before the object
    /// is moved - the pose captured is therefore the one the object had when the scenario started
    /// playing with it. Only the FIRST move of a given object is kept: a prop teleported three
    /// times goes back to where it was before the first teleport, not before the last.
    /// </para>
    /// <para>
    /// Player teleports are never journaled (the player is handled by the evacuation step), and
    /// neither are teleports carrying a filter listed in
    /// <see cref="ScenarioResetOptions.ignoredTeleportFilters"/>.
    /// </para>
    /// </summary>
    public sealed class ScenarioTeleportJournal
    {
        public readonly struct Entry
        {
            public readonly Transform Target;
            public readonly Transform Parent;
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly string ScenarioId;
            public readonly string SourceName;

            public Entry(Transform target, string scenarioId, string sourceName)
            {
                Target = target;
                Parent = target.parent;
                Position = target.position;
                Rotation = target.rotation;
                ScenarioId = scenarioId;
                SourceName = sourceName;
            }
        }

        private readonly Func<string> _currentScenarioId;
        private readonly Func<ScenarioResetOptions> _options;
        private readonly List<Entry> _entries = new List<Entry>(32);
        // Keyed on the transform itself rather than an instance id: Unity objects hash by
        // reference, and the id accessors are deprecated in this Unity version.
        private readonly HashSet<Transform> _known = new HashSet<Transform>();
        private bool _isSubscribed;

        public IReadOnlyList<Entry> Entries => _entries;

        public ScenarioTeleportJournal(Func<string> currentScenarioId, Func<ScenarioResetOptions> options)
        {
            _currentScenarioId = currentScenarioId;
            _options = options;
        }

        public void Enable()
        {
            if (_isSubscribed) return;
            // Both hooks, on purpose: SendTeleportTarget always fires its own, while the hub event
            // only carries teleports that go through the PlayerEventBridge. Recording the same
            // object twice is a no-op (the first capture wins), so covering both is free.
            SendTeleportTarget.TeleportRequested += OnTeleportRequested;
            PlayerEvents.TeleportRequested += OnTeleportRequested;
            _isSubscribed = true;
        }

        public void Disable()
        {
            if (!_isSubscribed) return;
            SendTeleportTarget.TeleportRequested -= OnTeleportRequested;
            PlayerEvents.TeleportRequested -= OnTeleportRequested;
            _isSubscribed = false;
        }

        private void OnTeleportRequested(TeleportInformation information)
        {
            if (information == null || information.objectIsPlayer) return;

            var options = _options?.Invoke();
            if (options == null || !options.restoreTeleportedObjects) return;

            var subject = information.objectToTeleport;
            if (subject == null) return;

            // No scenario running: nothing would ever restore this entry.
            var scenarioId = _currentScenarioId?.Invoke();
            if (string.IsNullOrEmpty(scenarioId)) return;

            if (information.isUsingFilter && information.filter != null &&
                options.ignoredTeleportFilters != null &&
                options.ignoredTeleportFilters.Contains(information.filter)) return;

            if (!_known.Add(subject)) return; // already journaled: keep the ORIGINAL pose

            _entries.Add(new Entry(subject, scenarioId,
                information.targetDestination != null ? information.targetDestination.name : "<unknown>"));
        }

        /// <summary>
        /// Puts back every object journaled for one of <paramref name="scenarioIds"/>, most recent
        /// first, and drops those entries. Objects destroyed since (they lived in a scene already
        /// unloaded) are skipped silently.
        /// </summary>
        public int Restore(ICollection<string> scenarioIds, bool resetVelocity, bool isDebug)
        {
            if (_entries.Count == 0) return 0;

            var restored = 0;

            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (scenarioIds != null && scenarioIds.Count > 0 && !scenarioIds.Contains(entry.ScenarioId)) continue;

                _entries.RemoveAt(i);

                _known.Remove(entry.Target);
                if (entry.Target == null) continue; // destroyed with its scene

                if (entry.Parent != null && entry.Target.parent != entry.Parent)
                    entry.Target.SetParent(entry.Parent, false);

                TeleportUtility.PlaceAt(entry.Target.gameObject, entry.Position, entry.Rotation, resetVelocity);
                ObjectZoneTrackingBridge.MarkDirty(entry.Target);
                restored++;

                if (isDebug)
                    Debug.Log($"[ScenarioManager] restored '{entry.Target.name}' to {entry.Position} " +
                              $"(was teleported to '{entry.SourceName}').", entry.Target);
            }

            return restored;
        }

        /// <summary>Drops everything without moving anything (scene reset, world re-init).</summary>
        public void Clear()
        {
            _entries.Clear();
            _known.Clear();
        }
    }
}
