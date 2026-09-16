using System.Collections.Generic;
using UnityEngine;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// A place to send the player when the zone they stand in is about to be locked by a
    /// scenario unload. Positions are world space and are resolved at the moment of the
    /// evacuation, never cached across frames.
    /// </summary>
    public readonly struct ZoneExit
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        /// <summary>Zone this exit lands in, empty when the author did not declare it.</summary>
        public readonly string DestinationZoneId;
        /// <summary>Human-readable origin of the exit (door name, anchor name) - logs only.</summary>
        public readonly string SourceName;

        public ZoneExit(Vector3 position, Quaternion rotation, string destinationZoneId, string sourceName)
        {
            Position = position;
            Rotation = rotation;
            DestinationZoneId = destinationZoneId ?? string.Empty;
            SourceName = sourceName ?? string.Empty;
        }
    }

    /// <summary>
    /// Anything able to say "here is the way out of zone X". Implemented by
    /// <see cref="ZoneExitAnchor"/> and, when the door package is installed, by its
    /// DoorZoneEvacuationProvider.
    /// <para>
    /// The dependency runs this way round on purpose: AutomaticDoorSystem already references
    /// jeanf.scenemanagement, so SceneManagement cannot reference it back. Providers push
    /// themselves into <see cref="ZoneEvacuationRegistry"/> instead, which also keeps the door
    /// package optional - a project with no doors just registers anchors.
    /// </para>
    /// </summary>
    public interface IZoneEvacuationProvider
    {
        /// <param name="zoneId">Zone the player must leave.</param>
        /// <param name="from">Where the player stands - used to pick the nearest way out.</param>
        /// <param name="blockedZoneIds">
        /// Every zone being locked by this unload. An exit landing in one of them is no exit;
        /// providers must skip those. Null means "nothing else is blocked".
        /// </param>
        bool TryGetExit(string zoneId, Vector3 from, IReadOnlyCollection<string> blockedZoneIds, out ZoneExit exit);
    }

    /// <summary>
    /// Where <see cref="ScenarioManager"/> asks for a way out of a zone it is about to lock.
    /// A zone with no registered exit is treated as "not lockable" (typically a zone without a
    /// door): the manager logs it and unloads without moving anybody.
    /// </summary>
    public static class ZoneEvacuationRegistry
    {
        private static readonly List<IZoneEvacuationProvider> Providers = new List<IZoneEvacuationProvider>(8);

        public static int ProviderCount => Providers.Count;

        public static void Register(IZoneEvacuationProvider provider)
        {
            if (provider == null || Providers.Contains(provider)) return;
            Providers.Add(provider);
        }

        public static void Unregister(IZoneEvacuationProvider provider)
        {
            if (provider == null) return;
            Providers.Remove(provider);
        }

        /// <summary>Nearest exit out of <paramref name="zoneId"/> across every provider.</summary>
        public static bool TryResolveExit(string zoneId, Vector3 from,
            IReadOnlyCollection<string> blockedZoneIds, out ZoneExit exit)
        {
            exit = default;
            if (string.IsNullOrEmpty(zoneId) || Providers.Count == 0) return false;

            var found = false;
            var bestDistanceSq = float.MaxValue;

            for (var i = 0; i < Providers.Count; i++)
            {
                var provider = Providers[i];
                if (provider == null) continue;
                if (!provider.TryGetExit(zoneId, from, blockedZoneIds, out var candidate)) continue;

                var distanceSq = (candidate.Position - from).sqrMagnitude;
                if (found && distanceSq >= bestDistanceSq) continue;

                bestDistanceSq = distanceSq;
                exit = candidate;
                found = true;
            }

            return found;
        }

        /// <summary>Does this zone have a way out at all - i.e. can it be locked safely?</summary>
        public static bool HasExitFor(string zoneId, IReadOnlyCollection<string> blockedZoneIds)
            => TryResolveExit(zoneId, Vector3.zero, blockedZoneIds, out _);

        /// <summary>
        /// Membership test providers use on the blocked set. Kept here so every provider agrees on
        /// the convention (null or empty = nothing else is blocked) and so a HashSet keeps its O(1)
        /// lookup instead of falling back to a linear scan.
        /// </summary>
        public static bool IsBlocked(IReadOnlyCollection<string> blockedZoneIds, string zoneId)
        {
            if (blockedZoneIds == null || blockedZoneIds.Count == 0 || string.IsNullOrEmpty(zoneId)) return false;
            if (blockedZoneIds is ICollection<string> collection) return collection.Contains(zoneId);

            foreach (var blocked in blockedZoneIds)
                if (blocked == zoneId) return true;
            return false;
        }
    }
}
