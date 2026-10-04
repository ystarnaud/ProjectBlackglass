using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The single entry point for gameplay commands. Holds the current order and executes it
    /// each simulation frame using UnitMover and UnitAttacker. A new command replaces the old one.
    /// </summary>
    [RequireComponent(typeof(UnitMover), typeof(UnitAttacker))]
    public sealed class CommandableUnit : MonoBehaviour
    {
        const float ChaseRepathDistance = 0.5f;

        UnitMover mover;
        UnitAttacker attacker;
        bool chasing;
        Vector3 lastChaseTarget;

        public UnitCommand CurrentCommand { get; private set; }

        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        /// <summary>
        /// Gives the unit a new order. Returns false if the order cannot be carried out
        /// (no walkable point within 2 m of the destination, dead or inactive target); the current order then continues.
        /// Re-issuing an attack on the current target keeps the unit moving instead of restarting its chase.
        /// </summary>
        public bool Issue(UnitCommand command)
        {
            switch (command)
            {
                case null:
                    throw new ArgumentNullException(nameof(command));

                case MoveCommand move:
                    if (!Mover.MoveTo(move.Destination))
                        return false;
                    chasing = false;
                    CurrentCommand = move;
                    return true;

                case AttackCommand attack:
                    if (attack.Target == null || !IsAttackable(attack.Target))
                        return false;
                    if (CurrentCommand is AttackCommand current && current.Target == attack.Target)
                    {
                        CurrentCommand = attack;
                        return true;
                    }
                    Mover.Stop();
                    chasing = false;
                    CurrentCommand = attack;
                    return true;

                default:
                    throw new ArgumentException($"Unsupported command type {command.GetType().Name}.", nameof(command));
            }
        }

        void Update()
        {
            // Orders only advance while simulation time advances (tactical pause sets timeScale to 0).
            if (Time.deltaTime <= 0f)
                return;

            switch (CurrentCommand)
            {
                case MoveCommand _:
                    if (Mover.HasArrived)
                        CurrentCommand = null;
                    break;
                case AttackCommand attack:
                    UpdateAttack(attack.Target);
                    break;
            }
        }

        void UpdateAttack(Health target)
        {
            if (target == null || !IsAttackable(target))
            {
                FinishAttack();
                return;
            }

            if (!Attacker.IsInRange(target))
            {
                var targetPosition = target.transform.position;
                if (!chasing || (targetPosition - lastChaseTarget).sqrMagnitude > ChaseRepathDistance * ChaseRepathDistance)
                {
                    if (!Mover.MoveTo(targetPosition))
                    {
                        FinishAttack();
                        return;
                    }
                    chasing = true;
                    lastChaseTarget = targetPosition;
                }
                return;
            }

            if (chasing)
            {
                Mover.Stop();
                chasing = false;
            }
            FaceTowards(target.transform.position);
            Attacker.TryAttack(target);
            if (!target.IsAlive)
                FinishAttack();
        }

        // A target that is dead, or deactivated while still alive, can no longer be attacked.
        static bool IsAttackable(Health target) => target.IsAlive && target.gameObject.activeInHierarchy;

        void FinishAttack()
        {
            Mover.Stop();
            chasing = false;
            CurrentCommand = null;
        }

        void FaceTowards(Vector3 point)
        {
            var direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}
