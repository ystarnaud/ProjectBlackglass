using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// The attack-chase mitigation (hidden information slice, spec section 3.5). An accepted attack would otherwise keep following
    /// its target after the issuing client lost sight of it, so the friendly's movement would reveal where the hidden hostile
    /// goes. The guard remembers each attack the gate accepted (one per unit) and ends it, through CommandableUnit.DropCurrent,
    /// as soon as its connection no longer observes the target. Attacks issued by AI, companion assist, retaliation or abilities
    /// never pass the gate and are never touched.
    /// </summary>
    public sealed class NetAttackGuard
    {
        struct Record
        {
            public NetConnection Connection;
            public int UnitId;
            public int TargetId;
            public UnitCommand Command;
        }

        readonly NetUnits units;
        readonly NetVisibility visibility;
        readonly List<Record> records = new List<Record>();

        public NetAttackGuard(NetUnits units, NetVisibility visibility)
        {
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        }

        public int Count => records.Count;

        public void Accepted(NetConnection connection, int unitId, int targetId, UnitCommand command)
        {
            if (connection == null || command == null)
                return;
            records.RemoveAll(r => r.UnitId == unitId);
            records.Add(new Record { Connection = connection, UnitId = unitId, TargetId = targetId, Command = command });
        }

        /// <summary>
        /// A connection left. Its tokens are released, so the same account can reconnect at once; an attack still chasing a hostile
        /// it can no longer see would then be watchable. End those attacks (no-op for one that is no longer current), then forget them.
        /// </summary>
        public void Forget(NetConnection connection)
        {
            for (var i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                if (record.Connection != connection)
                    continue;
                if (units.TryGet(record.UnitId, out var unit))
                    unit.DropCurrent(record.Command);
                records.RemoveAt(i);
            }
        }

        /// <summary>Call after NetVisibility.Update each frame. Returns how many attacks it ended.</summary>
        public int Tick()
        {
            var ended = 0;
            for (var i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                if (!units.TryGet(record.UnitId, out var unit) || !ReferenceEquals(unit.CurrentCommand, record.Command))
                {
                    records.RemoveAt(i);
                    continue;
                }
                if (visibility.KnowledgeOf(record.Connection, record.TargetId) == NetKnowledge.Observed)
                    continue;
                unit.DropCurrent(record.Command);
                records.RemoveAt(i);
                ended++;
            }
            return ended;
        }
    }
}
