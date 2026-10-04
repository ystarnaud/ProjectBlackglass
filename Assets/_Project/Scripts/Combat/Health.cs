using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Hit points for anything that can be attacked. Dies once, at zero.</summary>
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
        /// <summary>Raised exactly once, when hit points reach zero.</summary>
        public event Action Died;

        public void TakeDamage(int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Damage cannot be negative.");
            if (hasDied || amount == 0)
                return;

            damageTaken = (int)Math.Min((long)max, (long)damageTaken + amount);
            Damaged?.Invoke(amount);

            if (Current > 0)
                return;
            hasDied = true;
            Died?.Invoke();
            if (disableOnDeath)
                gameObject.SetActive(false);
        }
    }
}
