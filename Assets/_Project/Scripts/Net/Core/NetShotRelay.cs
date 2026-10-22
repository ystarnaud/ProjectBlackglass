#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// The frame's resolved basic attacks, relayed after the visibility pass (decision 052): each goes to every connection that
    /// receives both units' avatars as of that pass, and then the queue is empty. A shot is judged once, at the first pass after it
    /// was fired (at most one frame later), so a shot from a hostile a connection does not observe then never reaches it. Holds no
    /// networking type. Editor and fixture builds only
    /// (owner ruling D1).
    /// </summary>
    public sealed class NetShotRelay
    {
        public const int MaxPending = 256;

        readonly List<NetShot> pending = new List<NetShot>();

        public int Pending => pending.Count;
        /// <summary>Shots dropped because a frame queued more than MaxPending (diagnostics).</summary>
        public int Dropped { get; private set; }

        public void Add(NetShot shot)
        {
            if (pending.Count >= MaxPending)
            {
                Dropped++;
                return;
            }
            pending.Add(shot);
        }

        public void Flush(IEnumerable<NetConnection> connections, Func<NetConnection, int, bool> replicates, Action<NetConnection, NetShot> send)
        {
            try
            {
                foreach (var shot in pending)
                    foreach (var connection in connections)
                        if (replicates(connection, shot.AttackerId) && replicates(connection, shot.TargetId))
                            send(connection, shot);
            }
            finally
            {
                pending.Clear();
            }
        }

        public void Clear() => pending.Clear();
    }
}
#endif
