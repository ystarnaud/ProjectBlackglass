using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Gives one order to a group of units. A move is spread over GroupMoveOffsets so units don't pile up; other
    /// orders go to every unit unchanged. Usable by player input, AI and scripts alike.
    /// </summary>
    public static class GroupOrders
    {
        /// <summary>Distance between neighbouring move destinations (NavMesh agents are 0.5 m in radius).</summary>
        public const float DefaultSpacing = 1.5f;

        /// <summary>Issues the order to every non-null unit. Returns how many units accepted it.</summary>
        public static int Issue(IReadOnlyList<CommandableUnit> units, UnitCommand command, IssueMode mode, float spacing = DefaultSpacing)
        {
            if (units == null)
                throw new ArgumentNullException(nameof(units));
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            var accepted = 0;
            if (command is MoveCommand move)
            {
                var offsets = GroupMoveOffsets.Compute(units.Count, spacing);
                for (var i = 0; i < units.Count; i++)
                {
                    var unit = units[i];
                    if (unit == null)
                        continue;
                    // A slot off the NavMesh (past a map edge, inside a wall) falls back to the clicked point itself.
                    var unitAccepted = unit.Issue(new MoveCommand(move.Destination + offsets[i]), mode)
                        || (offsets[i] != Vector3.zero && unit.Issue(move, mode));
                    if (unitAccepted)
                        accepted++;
                }
                return accepted;
            }

            foreach (var unit in units)
            {
                if (unit != null && unit.Issue(command, mode))
                    accepted++;
            }
            return accepted;
        }
    }
}
