using System.Collections.Generic;
using FishNet.Managing.Server;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The server's avatar objects (networking experiment, spec section 3.4): one networked object per simulation unit, spawned
    /// when the server starts. Each frame the avatar copies its unit's transform (the NetworkTransform replicates it) and
    /// pushes the unit's health and orders into NetUnitState. The simulation units themselves are never networked.
    /// </summary>
    internal sealed class NetAvatars
    {
        struct Entry
        {
            public CommandableUnit Unit;
            public Health Health;
            public Transform Avatar;
            public NetUnitState State;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly List<NetUnitState> states = new List<NetUnitState>();

        internal int Count => entries.Count;
        internal IReadOnlyList<NetUnitState> States => states;

        internal void SpawnAll(ServerManager server, GameObject prefab, NetUnits units)
        {
            Forget();
            for (var id = 1; id <= units.Count; id++)
            {
                if (!units.TryGet(id, out var unit))
                    continue;
                var avatar = Object.Instantiate(prefab, unit.transform.position, unit.transform.rotation);
                avatar.name = "Avatar_" + id;
                var state = avatar.GetComponent<NetUnitState>();
                server.Spawn(avatar);
                state.Assign(id, units.IsHostile(id));
                var entry = new Entry { Unit = unit, Health = unit.GetComponent<Health>(), Avatar = avatar.transform, State = state };
                entries.Add(entry);
                states.Add(state);
                Push(entry);
            }
        }

        internal void Sync()
        {
            foreach (var entry in entries)
                Push(entry);
        }

        internal void Despawn(ServerManager server)
        {
            foreach (var entry in entries)
            {
                if (entry.Avatar != null)
                    server.Despawn(entry.Avatar.gameObject);
            }
            Forget();
        }

        internal void Forget()
        {
            entries.Clear();
            states.Clear();
        }

        static void Push(Entry entry)
        {
            if (entry.Unit == null || entry.Avatar == null)
                return;
            entry.Avatar.SetPositionAndRotation(entry.Unit.transform.position, entry.Unit.transform.rotation);
            var health = entry.Health;
            entry.State.Push(health != null ? health.Current : 0, health != null ? health.Max : 0, health != null && health.IsAlive,
                NetOrder.KindOf(entry.Unit.CurrentCommand), entry.Unit.PendingCommands.Count, entry.Unit.MoveIntent != Vector3.zero);
        }
    }
}
