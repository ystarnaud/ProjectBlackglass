using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Server;
using FishNet.Object;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass
{
    /// <summary>
    /// The server's avatar objects (networking experiment, decisions 046 and 047): one networked object per simulation unit,
    /// spawned with no owner when the server starts. Before spawning, each avatar is told whom it may be sent to (`visible`,
    /// asked by NetVisibilityCondition). Each frame the avatar copies its unit's transform and pushes health and orders into
    /// NetUnitState; a hostile's orders, queue and steering are never pushed, because they would reveal what it does and whom
    /// it targets. The simulation units themselves are never networked.
    /// </summary>
    internal sealed class NetAvatars
    {
        struct Entry
        {
            public CommandableUnit Unit;
            public Health Health;
            public Transform Avatar;
            public NetUnitState State;
            public bool Hostile;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly List<NetUnitState> states = new List<NetUnitState>();
        readonly Dictionary<int, NetworkObject> objects = new Dictionary<int, NetworkObject>();

        internal int Count => entries.Count;
        internal IReadOnlyList<NetUnitState> States => states;

        internal void SpawnAll(ServerManager server, GameObject prefab, NetUnits units, Func<NetworkConnection, int, bool> visible)
        {
            Forget();
            for (var id = 1; id <= units.Count; id++)
            {
                if (!units.TryGet(id, out var unit))
                    continue;
                var avatar = Object.Instantiate(prefab, unit.transform.position, unit.transform.rotation);
                avatar.name = "Avatar_" + id;
                var state = avatar.GetComponent<NetUnitState>();
                var unitId = id;   // the loop variable is shared by every iteration's closure
                state.VisibleTo = connection => visible(connection, unitId);
                server.Spawn(avatar);
                var hostile = units.IsHostile(id);
                state.Assign(id, hostile);
                var entry = new Entry { Unit = unit, Health = unit.GetComponent<Health>(), Avatar = avatar.transform, State = state, Hostile = hostile };
                entries.Add(entry);
                states.Add(state);
                objects[id] = avatar.GetComponent<NetworkObject>();
                Push(entry);
            }
        }

        internal NetworkObject ObjectOf(int unitId) => objects.TryGetValue(unitId, out var found) && found != null ? found : null;

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
            objects.Clear();
        }

        static void Push(Entry entry)
        {
            if (entry.Unit == null || entry.Avatar == null)
                return;
            entry.Avatar.SetPositionAndRotation(entry.Unit.transform.position, entry.Unit.transform.rotation);
            var health = entry.Health;
            var current = health != null ? health.Current : 0;
            var maximum = health != null ? health.Max : 0;
            var alive = health != null && health.IsAlive;
            if (entry.Hostile)
                entry.State.Push(current, maximum, alive, NetOrderKind.None, 0, false);
            else
                entry.State.Push(current, maximum, alive, NetOrder.KindOf(entry.Unit.CurrentCommand), entry.Unit.PendingCommands.Count,
                    entry.Unit.MoveIntent != Vector3.zero);
        }
    }
}
