#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>
    /// The deterministic development fixture (hidden information slice, spec section 3.9), compiled only into the Editor and
    /// fixture server build (BLACKGLASS_NET_FIXTURE). In -netFixture mode it is the visibility source: a connection observes
    /// the fixture's hostile only after Observe, and grants are keyed by the server's own connection object, never by an
    /// account string. Real sight and exposure are not consulted, so what the harness sees does not depend on the map.
    /// MoveHostile walks the hostile through points the server sampled and never sends. The adapter checks the caller's fixture identity before Apply.
    /// </summary>
    public sealed class NetFixture : INetIntelSource
    {
        /// <summary>The smallest distance between two walk points, and between a point and the spawn.</summary>
        public const float PointSpacing = 3f;

        readonly NetUnits units;
        readonly int hostileId;
        readonly IReadOnlyList<Vector3> points;
        readonly Action<Vector3> moveHostile;
        readonly HashSet<NetConnection> observers = new HashSet<NetConnection>();
        int nextPoint;

        public NetFixture(NetUnits units, int hostileId, IReadOnlyList<Vector3> points, Action<Vector3> moveHostile)
        {
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.hostileId = hostileId;
            this.points = points ?? new Vector3[0];
            this.moveHostile = moveHostile ?? throw new ArgumentNullException(nameof(moveHostile));
        }

        public int HostileId => hostileId;
        public IReadOnlyList<Vector3> Points => points;
        /// <summary>How many verbs have changed something.</summary>
        public int Step { get; private set; }
        public NetFixtureContainer Container { get; } = new NetFixtureContainer();

        public bool Observes(NetConnection connection, int id) =>
            id == hostileId && connection != null && observers.Contains(connection);

        /// <summary>Carries out one verb for an authorized connection; false, with no step, when it changes nothing.</summary>
        public bool Apply(NetConnection connection, NetFixtureVerb verb)
        {
            if (connection == null)
                return false;
            switch (verb)
            {
                case NetFixtureVerb.Observe:
                    observers.Add(connection);
                    break;
                case NetFixtureVerb.Unobserve:
                    observers.Remove(connection);
                    break;
                case NetFixtureVerb.MoveHostile:
                    if (points.Count == 0)
                        return false;
                    moveHostile(points[nextPoint % points.Count]);
                    nextPoint++;
                    break;
                case NetFixtureVerb.Search:
                    if (!OwnsALivingUnit(connection))
                        return false;
                    Container.Search(connection);
                    break;
                default:
                    return false;
            }
            Step++;
            return true;
        }

        public void Forget(NetConnection connection)
        {
            observers.Remove(connection);
            Container.Forget(connection);
        }

        bool OwnsALivingUnit(NetConnection connection)
        {
            for (var id = 1; id <= units.Count; id++)
            {
                if (connection.Owns(id) && units.TryGet(id, out var unit) && unit.IsAlive)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Up to count walkable points around origin, on rings of 4 m and 7 m, each at least PointSpacing (on the ground plane)
        /// from the origin and from each other.
        /// </summary>
        public static List<Vector3> SamplePoints(Vector3 origin, int count)
        {
            var found = new List<Vector3>();
            foreach (var radius in new[] { 4f, 7f })
            {
                for (var step = 0; step < 8 && found.Count < count; step++)
                {
                    var angle = step * Mathf.PI / 4f;
                    var candidate = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                    if (!NavMesh.SamplePosition(candidate, out var hit, 1.5f, NavMesh.AllAreas))
                        continue;
                    if (Flat(hit.position - origin) < PointSpacing)
                        continue;
                    var clear = true;
                    foreach (var point in found)
                        clear &= Flat(hit.position - point) >= PointSpacing;
                    if (clear)
                        found.Add(hit.position);
                }
            }
            return found;
        }

        static float Flat(Vector3 offset)
        {
            offset.y = 0f;
            return offset.magnitude;
        }
    }
}
#endif
