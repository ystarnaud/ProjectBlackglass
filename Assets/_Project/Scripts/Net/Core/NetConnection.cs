using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// What the server knows about one connected client: its account, the units it owns, the last sequence it got
    /// through, and the most recent command ids (so a replayed request is recognised). Owned by whoever manages the
    /// connection; the gate reads and updates the freshness state.
    /// </summary>
    public sealed class NetConnection
    {
        /// <summary>How many recent command ids a connection remembers.</summary>
        public const int CommandIdWindow = 256;

        readonly HashSet<int> owned;
        readonly Queue<uint> recent = new Queue<uint>();
        readonly HashSet<uint> recentSet = new HashSet<uint>();
        bool hasSequence;
        uint lastSequence;

        public NetConnection(string account, IEnumerable<int> ownedUnitIds, bool drivesFixture = false)
        {
            Account = account;
            DrivesFixture = drivesFixture;
            owned = new HashSet<int>(ownedUnitIds ?? new int[0]);
        }

        public string Account { get; }

        /// <summary>Admitted with a `+fixture` token: may send fixture verbs when the server runs -netFixture.</summary>
        public bool DrivesFixture { get; }

        public bool Owns(int unitId) => owned.Contains(unitId);

        internal bool IsFresh(uint sequence) => !hasSequence || sequence > lastSequence;

        internal bool HasSeen(uint commandId) => recentSet.Contains(commandId);

        internal void Record(uint sequence, uint commandId)
        {
            hasSequence = true;
            lastSequence = sequence;
            recent.Enqueue(commandId);
            recentSet.Add(commandId);
            if (recent.Count > CommandIdWindow)
                recentSet.Remove(recent.Dequeue());
        }
    }
}
