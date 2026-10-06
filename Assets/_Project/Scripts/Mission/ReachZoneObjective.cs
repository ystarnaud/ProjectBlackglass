using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A location objective: complete once at least `requiredUnits` living squad members stand within `radius` (flat
    /// distance) of the centre while it is Active. Extraction is one of these, not required and opened by the runtime.
    /// </summary>
    public sealed class ReachZoneObjective : MissionObjective
    {
        readonly IReadOnlyList<Health> squad;

        public ReachZoneObjective(string id, string title, Vector3 center, float radius, int requiredUnits,
            IReadOnlyList<Health> squad, bool isRequired)
            : base(id, ObjectiveType.ReachZone, title, isRequired)
        {
            this.squad = squad ?? throw new ArgumentNullException(nameof(squad));
            if (radius <= 0f)
                throw new ArgumentOutOfRangeException(nameof(radius), radius, "The zone needs a positive radius.");
            if (requiredUnits < 1)
                throw new ArgumentOutOfRangeException(nameof(requiredUnits), requiredUnits, "At least one unit must be required.");
            Center = center;
            Radius = radius;
            RequiredUnits = requiredUnits;
        }

        public Vector3 Center { get; }
        public float Radius { get; }
        public int RequiredUnits { get; }

        public override bool HasTarget => true;
        public override Vector3 TargetPosition => Center;

        /// <summary>Living squad members inside the zone right now.</summary>
        public int Inside
        {
            get
            {
                var inside = 0;
                foreach (var unit in squad)
                {
                    if (unit != null && unit.IsAlive && CoverRules.FlatDistance(unit.transform.position, Center) <= Radius)
                        inside++;
                }
                return inside;
            }
        }

        public override string Describe() => Title;

        protected override void OnEvaluate()
        {
            if (Inside >= RequiredUnits)
                Complete();
        }
    }
}
