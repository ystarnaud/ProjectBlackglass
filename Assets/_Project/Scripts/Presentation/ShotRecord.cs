using UnityEngine;

namespace Blackglass
{
    public enum ShotOutcome
    {
        Hit,
        /// <summary>The target's cover turned the shot away.</summary>
        CoverMiss,
    }

    /// <summary>
    /// What a discharged unit-targeted shot (a basic attack or Aimed Shot) tells the presentation layer, captured at the
    /// moment gameplay resolved it. Positions are snapshots: presentation never reads the target again. Read-only; it
    /// decides nothing.
    /// </summary>
    public readonly struct ShotRecord
    {
        /// <summary>Height above the target's pivot of the point a shot is drawn at (chest).</summary>
        public const float BodyHeight = 0.3f;

        public ShotRecord(int emitterId, int sequence, Health source, Health target, Vector3 origin, Vector3 aimPoint,
            ShotOutcome outcome, bool isMelee, AbilityDefinition ability)
        {
            EmitterId = emitterId;
            Sequence = sequence;
            Source = source;
            Target = target;
            Origin = origin;
            AimPoint = aimPoint;
            Outcome = outcome;
            IsMelee = isMelee;
            Ability = ability;
        }

        /// <summary>Identifies the emitting component; with Sequence it makes a record unique.</summary>
        public int EmitterId { get; }
        public int Sequence { get; }
        public Health Source { get; }
        public Health Target { get; }
        /// <summary>The shooter's eye at the time of the shot (a fallback when no muzzle is known).</summary>
        public Vector3 Origin { get; }
        public Vector3 AimPoint { get; }
        public ShotOutcome Outcome { get; }
        public bool IsMelee { get; }
        /// <summary>The ability that fired it, or null for a basic attack.</summary>
        public AbilityDefinition Ability { get; }
    }
}
