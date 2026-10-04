using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Fights back: a unit hit while it has no orders attacks its attacker, through the normal order path. Any order
    /// wins (a Move away is a retreat) and so do held movement keys. Shared by friendlies and hostiles; it decides
    /// what to attack and nothing about how.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(Health))]
    public sealed class AutoRetaliate : MonoBehaviour
    {
        CommandableUnit unit;
        Health health;

        void Awake()
        {
            unit = GetComponent<CommandableUnit>();
            health = GetComponent<Health>();
        }

        void OnEnable() => health.AttackedBy += OnAttackedBy;

        void OnDisable() => health.AttackedBy -= OnAttackedBy;

        void OnAttackedBy(Health attacker)
        {
            if (!unit.IsAlive || unit.CurrentCommand != null || unit.MoveIntent != Vector3.zero)
                return;
            if (attacker == null || !attacker.IsAlive || !attacker.gameObject.activeInHierarchy)
                return;
            unit.Issue(new AttackCommand(attacker));
        }
    }
}
