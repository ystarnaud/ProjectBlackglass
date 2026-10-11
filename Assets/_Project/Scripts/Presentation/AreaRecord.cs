using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A ground-targeted damage ability has been applied: where, how large (the gameplay radius it actually used) and
    /// by whom. Raised even when nobody was hit. Read-only; it decides nothing.
    /// </summary>
    public readonly struct AreaRecord
    {
        public AreaRecord(int emitterId, int sequence, Health caster, AbilityDefinition ability, Vector3 center, float radius)
        {
            EmitterId = emitterId;
            Sequence = sequence;
            Caster = caster;
            Ability = ability;
            Center = center;
            Radius = radius;
        }

        public int EmitterId { get; }
        public int Sequence { get; }
        public Health Caster { get; }
        public AbilityDefinition Ability { get; }
        public Vector3 Center { get; }
        public float Radius { get; }
    }
}
