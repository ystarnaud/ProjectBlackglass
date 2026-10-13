using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What one connection knows about one hostile. The wire carries this byte in NetKnowledgeMessage.</summary>
    public enum NetKnowledge : byte
    {
        Unknown = 0,
        /// <summary>Live: the connection receives the hostile's avatar.</summary>
        Observed = 1,
        /// <summary>Hidden again: the connection keeps only a frozen record of where it last saw the hostile.</summary>
        LastKnown = 2,
    }

    /// <summary>Whether a connection observes a hostile right now: the squad's intelligence, or the development fixture.</summary>
    public interface INetIntelSource
    {
        /// <summary>Asked only about registered, living hostiles.</summary>
        bool Observes(NetConnection connection, int hostileId);
    }

    /// <summary>
    /// One transition for one connection and one hostile. Position is the hostile's position for Observed and the frozen record
    /// for LastKnown.
    /// </summary>
    public readonly struct NetKnowledgeChange
    {
        public NetKnowledgeChange(NetConnection connection, int unitId, NetKnowledge knowledge, Vector3 position)
        {
            Connection = connection;
            UnitId = unitId;
            Knowledge = knowledge;
            Position = position;
        }

        public NetConnection Connection { get; }
        public int UnitId { get; }
        public NetKnowledge Knowledge { get; }
        public Vector3 Position { get; }
    }

    /// <summary>
    /// The server's per-connection view of the hostiles (hidden information slice, decision 047). Each frame it asks one source
    /// whether each welcomed connection observes each living hostile and records a transition only when the answer changes:
    /// Unknown or LastKnown to Observed, and Observed to LastKnown at the last position recorded while observed. A last-known
    /// record never moves while the hostile is hidden. A hostile that dies keeps whatever each connection knew at that moment,
    /// so a death is witnessed only by a connection that was observing. Friendlies replicate only to their owner. Holds no
    /// networking type: the adapter turns the transitions into observer rebuilds and messages.
    /// </summary>
    public sealed class NetVisibility : INetSight
    {
        struct Entry
        {
            public NetKnowledge Knowledge;
            public Vector3 Position;
        }

        readonly NetUnits units;
        readonly INetIntelSource source;
        readonly Action<string> log;
        bool reported;
        readonly Dictionary<NetConnection, Dictionary<int, Entry>> views = new Dictionary<NetConnection, Dictionary<int, Entry>>();

        public NetVisibility(NetUnits units, INetIntelSource source, Action<string> log = null)
        {
            this.log = log;
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public int ConnectionCount => views.Count;

        /// <summary>A connection whose hello was accepted. It starts knowing nothing.</summary>
        public void Add(NetConnection connection)
        {
            if (connection != null && !views.ContainsKey(connection))
                views.Add(connection, new Dictionary<int, Entry>());
        }

        public void Remove(NetConnection connection)
        {
            if (connection != null)
                views.Remove(connection);
        }

        public NetKnowledge KnowledgeOf(NetConnection connection, int unitId) =>
            connection != null && views.TryGetValue(connection, out var view) && view.TryGetValue(unitId, out var entry)
                ? entry.Knowledge
                : NetKnowledge.Unknown;

        public bool TryLastKnown(NetConnection connection, int unitId, out Vector3 position)
        {
            position = default;
            if (connection == null || !views.TryGetValue(connection, out var view) || !view.TryGetValue(unitId, out var entry)
                || entry.Knowledge != NetKnowledge.LastKnown)
                return false;
            position = entry.Position;
            return true;
        }

        /// <summary>For the gate: friendlies always (they are not hidden information); a hostile only while observed.</summary>
        public bool CanSee(NetConnection connection, int unitId) =>
            !units.IsHostile(unitId) || KnowledgeOf(connection, unitId) == NetKnowledge.Observed;

        /// <summary>For the observer condition: whether this connection may receive the unit's live avatar.</summary>
        public bool Replicates(NetConnection connection, int unitId)
        {
            if (connection == null || !views.ContainsKey(connection) || unitId < 1 || unitId > units.Count)
                return false;
            return units.IsHostile(unitId)
                ? KnowledgeOf(connection, unitId) == NetKnowledge.Observed
                : connection.Owns(unitId);
        }

        // Fails closed: a source that throws counts as "not observed" (logged once), so a hostile is never revealed by an error.
        bool Observes(NetConnection connection, int hostileId)
        {
            try
            {
                return source.Observes(connection, hostileId);
            }
            catch (Exception exception)
            {
                if (!reported)
                {
                    reported = true;
                    log?.Invoke("visibility fails closed: the intel source threw " + exception.GetType().Name + ": " + exception.Message + "; hostiles it cannot answer for are hidden");
                }
                return false;
            }
        }

        /// <summary>Re-evaluates every connection and living hostile and appends one change per transition.</summary>
        public void Update(List<NetKnowledgeChange> changes)
        {
            foreach (var pair in views)
            {
                var connection = pair.Key;
                var view = pair.Value;
                for (var id = 1; id <= units.Count; id++)
                {
                    if (!units.IsHostile(id) || !units.TryGet(id, out var unit) || !unit.IsAlive)
                        continue;
                    view.TryGetValue(id, out var entry);
                    var position = unit.transform.position;
                    if (Observes(connection, id))
                    {
                        if (entry.Knowledge != NetKnowledge.Observed)
                            changes.Add(new NetKnowledgeChange(connection, id, NetKnowledge.Observed, position));
                        entry.Knowledge = NetKnowledge.Observed;
                        entry.Position = position;
                    }
                    else if (entry.Knowledge == NetKnowledge.Observed)
                    {
                        entry.Knowledge = NetKnowledge.LastKnown;
                        changes.Add(new NetKnowledgeChange(connection, id, NetKnowledge.LastKnown, entry.Position));
                    }
                    view[id] = entry;
                }
            }
        }
    }
}
