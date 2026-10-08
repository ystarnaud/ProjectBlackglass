using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Hit points for anything that can be attacked or healed. Dies once, at zero, and by default deactivates then.</summary>
    public sealed class Health : MonoBehaviour
    {
        [SerializeField, Min(1)] int max = 100;
        [SerializeField] bool disableOnDeath = true;

        int damageTaken;
        bool hasDied;

        public int Max => max;
        public int Current => Mathf.Max(0, max - damageTaken);
        public bool IsAlive => !hasDied;

        /// <summary>Raised with the damage amount whenever a living target takes damage.</summary>
        public event Action<int> Damaged;
        /// <summary>
        /// Raised with the attacker, after Damaged, whenever a living target takes damage from a known attacker.
        /// Also raised for the killing blow (before Died); IsAlive is already false by then, so handlers that must not
        /// act on a corpse check it.
        /// </summary>
        public event Action<Health> AttackedBy;
        /// <summary>Raised exactly once, when hit points reach zero.</summary>
        public event Action Died;
        /// <summary>Raised with the hit points actually restored whenever a living target is healed.</summary>
        public event Action<int> Healed;

        internal void Initialize(int maximum)
        {
            if (maximum < 1)
                throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "Maximum health must be at least 1.");
            max = maximum;
            damageTaken = 0;
            hasDied = false;
        }

        /// <summary>
        /// Changes the maximum without touching the damage already taken, so a stronger unit gains the difference as health
        /// and a weaker one loses it. A living unit never drops below 1 hit point this way, and a dead one stays dead with
        /// 0 hit points.
        /// </summary>
        internal void SetMax(int maximum)
        {
            if (maximum < 1)
                throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "Maximum health must be at least 1.");
            max = maximum;
            if (hasDied)
                damageTaken = max;
            else if (damageTaken >= max)
                damageTaken = max - 1;
        }

        /// <summary>Applies damage. The attacker, when given, is reported through AttackedBy so the target can respond.</summary>
        public void TakeDamage(int amount, Health attacker = null)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Damage cannot be negative.");
            if (hasDied || amount == 0)
                return;

            damageTaken = (int)Math.Min((long)max, (long)damageTaken + amount);
            // Death is decided before any handler runs, so IsAlive is already false inside Damaged and AttackedBy for
            // the killing blow. A handler that re-enters TakeDamage then returns at the hasDied guard above.
            var dies = damageTaken >= max;
            if (dies)
                hasDied = true;

            Damaged?.Invoke(amount);
            if (attacker != null)
                AttackedBy?.Invoke(attacker);

            if (!dies)
                return;
            Died?.Invoke();
            if (disableOnDeath)
                gameObject.SetActive(false);
        }

        /// <summary>
        /// Restores up to `amount` hit points, never above Max. Returns how many were restored (0 for the dead, at full
        /// health or for 0). The dead stay dead.
        /// </summary>
        public int Heal(int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Healing cannot be negative.");
            if (hasDied || amount == 0 || damageTaken == 0)
                return 0;

            var restored = Math.Min(amount, damageTaken);
            damageTaken -= restored;
            Healed?.Invoke(restored);
            return restored;
        }
    }
}
