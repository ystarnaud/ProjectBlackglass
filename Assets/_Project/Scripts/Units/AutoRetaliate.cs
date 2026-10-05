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
            unit.Issue(new AttackCommand(attackerHealth));
        }
    }
}
