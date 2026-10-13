using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What the net-test client decoded, kept so the harness can judge what reached it (hidden information slice, spec section
    /// 3.11): the last-known records (one per unit), the searched contents, the fixture step, and bounded logs of decoded
    /// messages and avatar sightings with their times and positions. The logs keep the first Limit entries and say when more
    /// arrived. Holds no networking type.
    /// </summary>
    public sealed class NetClientRecord
    {
        public const int Limit = 4096;

        public readonly struct Decoded
        {
            public Decoded(double time, string type, bool hasPosition, Vector3 position)
            {
                Time = time;
                Type = type;
                HasPosition = hasPosition;
                Position = position;
            }

            public double Time { get; }
            public string Type { get; }
            public bool HasPosition { get; }
            public Vector3 Position { get; }
        }

        public readonly struct Sighting
        {
            public Sighting(double time, int unitId, bool hostile, Vector3 position)
            {
                Time = time;
                UnitId = unitId;
                Hostile = hostile;
                Position = position;
            }

            public double Time { get; }
            public int UnitId { get; }
            public bool Hostile { get; }
            public Vector3 Position { get; }
        }

        readonly Dictionary<int, Vector3> lastKnown = new Dictionary<int, Vector3>();
        readonly List<Decoded> decoded = new List<Decoded>();
        readonly List<Sighting> sightings = new List<Sighting>();
        readonly HashSet<int> sighted = new HashSet<int>();
        IReadOnlyList<NetItem> contents;

        public IReadOnlyDictionary<int, Vector3> LastKnown => lastKnown;
        public IReadOnlyList<Decoded> DecodedMessages => decoded;
        public IReadOnlyList<Sighting> Sightings => sightings;
        /// <summary>Null until a container message arrives.</summary>
        public IReadOnlyList<NetItem> Contents => contents;
        public int KnowledgeMessages { get; private set; }
        public int ContainerMessages { get; private set; }
        public int FixtureStep { get; private set; }
        /// <summary>More than Limit messages or sightings arrived; the logs hold only the first Limit.</summary>
        public bool Truncated { get; private set; }

        public void Clear()
        {
            lastKnown.Clear();
            decoded.Clear();
            sightings.Clear();
            sighted.Clear();
            contents = null;
            KnowledgeMessages = 0;
            ContainerMessages = 0;
            FixtureStep = 0;
            Truncated = false;
        }

        public void Note(double time, string type) => Add(new Decoded(time, type, false, Vector3.zero));

        public void Knowledge(double time, int unitId, NetKnowledge knowledge, Vector3 position)
        {
            KnowledgeMessages++;
            var lastKnownRecord = knowledge == NetKnowledge.LastKnown;
            Add(new Decoded(time, "NetKnowledgeMessage", lastKnownRecord, position));
            if (lastKnownRecord)
                lastKnown[unitId] = position;
            else
                lastKnown.Remove(unitId);
        }

        public void Container(double time, IReadOnlyList<NetItem> items)
        {
            ContainerMessages++;
            Add(new Decoded(time, "NetContainerMessage", false, Vector3.zero));
            var copy = new NetItem[items != null ? items.Count : 0];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = items[i];
            contents = new System.Collections.ObjectModel.ReadOnlyCollection<NetItem>(copy);
        }

        public void Step(double time, int step)
        {
            Add(new Decoded(time, "NetFixtureStepMessage", false, Vector3.zero));
            if (step > FixtureStep)
                FixtureStep = step;
        }

        /// <summary>Records the first sighting of an avatar instance; true when this instance had not been seen.</summary>
        public bool Sight(double time, int instanceKey, int unitId, bool hostile, Vector3 position)
        {
            if (!sighted.Add(instanceKey))
                return false;
            if (sightings.Count >= Limit)
                Truncated = true;
            else
                sightings.Add(new Sighting(time, unitId, hostile, position));
            return true;
        }

        void Add(Decoded entry)
        {
            if (decoded.Count >= Limit)
                Truncated = true;
            else
                decoded.Add(entry);
        }
    }
}
