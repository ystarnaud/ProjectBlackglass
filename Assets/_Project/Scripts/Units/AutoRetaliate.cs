using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Fights back: a unit hit while it has no orders attacks its attacker, through the normal order path. Any order
    /// wins (a Move away is a retreat) and so do held movement keys. A unit holding cover it was ordered into fights
    /// back only at an attacker it can attack from where it stands (a melee unit behind a wall does not charge a
    /// shooter); cover it merely stopped on changes nothing. Shared by friendlies and hostiles; it decides what to
    /// attack and nothing about how.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(Health))]
    public sealed class AutoRetaliate : MonoBehaviour
    {
        CommandableUnit unit;
        Health health;
        UnitAttacker attacker;
        // The last order this component issued; identity is all CompanionAI needs, so it is never cleared.
        UnitCommand lastRetaliation;

        void Awake()
        {
            unit = GetComponent<CommandableUnit>();
            health = GetComponent<Health>();
            attacker = GetComponent<UnitAttacker>();   // CommandableUnit requires it
        }

        void OnEnable() => health.AttackedBy += OnAttackedBy;

        void OnDisable() => health.AttackedBy -= OnAttackedBy;

        void OnAttackedBy(Health attackerHealth)
        {
            if (!unit.IsAlive || unit.CurrentCommand != null || unit.MoveIntent != Vector3.zero)
                return;
            if (attackerHealth == null || !attackerHealth.IsAlive || !attackerHealth.gameObject.activeInHierarchy)
                return;
            // Decided here, once: a retaliation that starts from cover runs like any attack order afterwards.
            if (unit.Cover.OccupiedByOrder && !attacker.CanAttack(attackerHealth))
                return;
            var command = new AttackCommand(attackerHealth);
            if (unit.Issue(command))
                lastRetaliation = command;
        }

        /// <summary>
        /// True only for the exact command object this component issued as a retaliation. Lets CompanionAI tell a
        /// fight-back from an explicit order, since both look like "an order that is not mine".
        /// </summary>
        public bool IsRetaliating(UnitCommand command) => command != null && command == lastRetaliation;
    }
}
