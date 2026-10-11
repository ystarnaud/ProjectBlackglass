using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Reuses presentation instances: one idle stack per source prefab and an explicit list of the active ones, so active
    /// budgets can be enforced by the caller (an idle-pool size says nothing about what is playing). Instances live under a
    /// container owned by the presenter, never under a mission.
    /// </summary>
    internal sealed class EffectPool
    {
        readonly Transform container;
        readonly Dictionary<GameObject, Stack<PooledEffect>> idle = new Dictionary<GameObject, Stack<PooledEffect>>();
        readonly List<PooledEffect> active = new List<PooledEffect>();

        public EffectPool(Transform container) => this.container = container;

        public IReadOnlyList<PooledEffect> Active => active;
        public int ActiveCount => active.Count;

        public int IdleCount
        {
            get
            {
                var n = 0;
                foreach (var stack in idle.Values)
                    n += stack.Count;
                return n;
            }
        }

        /// <summary>An idle instance of the prefab (never a destroyed one), or a new one. The caller calls Begin and Track.</summary>
        public PooledEffect Rent(GameObject prefab)
        {
            if (idle.TryGetValue(prefab, out var stack))
            {
                while (stack.Count > 0)
                {
                    var candidate = stack.Pop();
                    if (candidate != null)
                    {
                        candidate.InPool = false;
                        return candidate;
                    }
                }
            }
            return PooledEffect.Create(prefab, container);
        }

        public void Track(PooledEffect effect) => active.Add(effect);

        public void Return(PooledEffect effect)
        {
            // Prune by reference first: a destroyed effect is still a valid list entry and must not linger in Active.
            for (var i = 0; i < active.Count; i++)
            {
                if (!ReferenceEquals(active[i], effect))
                    continue;
                active[i] = active[active.Count - 1];
                active.RemoveAt(active.Count - 1);
                break;
            }
            if (effect == null || effect.InPool)   // destroyed, or already returned (a second Return is harmless)
                return;
            effect.ResetForPool(container);
            if (!idle.TryGetValue(effect.Prefab, out var stack))
                idle[effect.Prefab] = stack = new Stack<PooledEffect>();
            stack.Push(effect);
        }

        /// <summary>Releases (through the callback) every active effect whose scaled-time deadline has passed, and forgets destroyed ones.</summary>
        public void Sweep(System.Action<PooledEffect> release)
        {
            for (var i = active.Count - 1; i >= 0; i--)
            {
                if (i >= active.Count)
                    continue;
                var effect = active[i];
                if (effect == null)
                    active.RemoveAt(i);
                else if (effect.Expired)
                    release(effect);
            }
        }

        /// <summary>Releases (through the callback) every active effect, and forgets destroyed ones.</summary>
        public void ReleaseAll(System.Action<PooledEffect> release)
        {
            for (var i = active.Count - 1; i >= 0; i--)
            {
                if (i >= active.Count)
                    continue;
                var effect = active[i];
                if (effect == null)
                    active.RemoveAt(i);
                else
                    release(effect);
            }
        }

        public int CountOf(EffectCategory category)
        {
            var n = 0;
            foreach (var effect in active)
                if (effect != null && effect.Category == category)
                    n++;
            return n;
        }
    }
}
