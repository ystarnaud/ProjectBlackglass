using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// Complete when every unit of its own group is dead. The group is a list of Health the mission hands it, not the
    /// Encounter's hostile side, so a mission may hold hostiles that are not this objective's concern. A destroyed or null
    /// member counts as dead, and an empty group is already done.
    /// </summary>
    public sealed class EliminateHostilesObjective : MissionObjective
    {
        readonly IReadOnlyList<Health> group;

        public EliminateHostilesObjective(string id, string title, IReadOnlyList<Health> group, bool isRequired = true)
            : base(id, ObjectiveType.EliminateHostiles, title, isRequired)
        {
            this.group = group ?? throw new ArgumentNullException(nameof(group));
        }

        public IReadOnlyList<Health> Group => group;

        public int Living
        {
            get
            {
                var living = 0;
                foreach (var unit in group)
                {
                    if (unit != null && unit.IsAlive)
                        living++;
                }
                return living;
            }
        }

        /// <summary>False hides the living/total count: the player may not know how many hostiles there are (decision 037).</summary>
        public bool ShowCounts { get; set; } = true;

        public override string Describe()
        {
            if (State == ObjectiveState.Completed)
                return Title;
            if (ShowCounts)
                return $"{Title} ({Living}/{group.Count})";
            var down = group.Count - Living;
            return down > 0 ? $"{Title} ({down} down)" : Title;
        }

        protected override void OnEvaluate()
        {
            if (Living == 0)
                Complete();
        }
    }
}
