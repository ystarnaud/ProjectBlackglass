using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// The net ids of one mission attempt's units (audit F2). Ids start at 1 and are never reused, so a stale id can never
    /// name a different unit. Create one per attempt; sides are fixed at registration. A destroyed unit resolves to nothing.
    /// </summary>
    public sealed class NetUnits
    {
        readonly List<CommandableUnit> units = new List<CommandableUnit>();
        readonly List<bool> hostile = new List<bool>();

        public int Count => units.Count;

        /// <summary>Registers the unit and returns its id.</summary>
        public int Register(CommandableUnit unit, bool isHostile)
        {
            units.Add(unit);
            hostile.Add(isHostile);
            return units.Count;
        }

        public bool TryGet(int id, out CommandableUnit unit)
        {
            unit = null;
            if (id < 1 || id > units.Count)
                return false;
            var found = units[id - 1];
            if (found == null)
                return false;
            unit = found;
            return true;
        }

        /// <summary>True for a registered hostile; false for a friendly and for an unknown id.</summary>
        public bool IsHostile(int id) => id >= 1 && id <= units.Count && hostile[id - 1];

        /// <summary>The unit's id, or 0 when it is not registered.</summary>
        public int IdOf(CommandableUnit unit)
        {
            if (unit == null)
                return 0;
            for (var i = 0; i < units.Count; i++)
            {
                if (ReferenceEquals(units[i], unit))
                    return i + 1;
            }
            return 0;
        }

        public static NetUnits FromMission(MissionDirector director) => FromMission(director.Friendlies, director.Hostiles);

        /// <summary>Friendlies first, so their ids are 1..n; then the hostiles.</summary>
        public static NetUnits FromMission(IReadOnlyList<CommandableUnit> friendlies, IReadOnlyList<CommandableUnit> hostiles)
        {
            var result = new NetUnits();
            foreach (var unit in friendlies)
                result.Register(unit, false);
            foreach (var unit in hostiles)
                result.Register(unit, true);
            return result;
        }
    }
}
