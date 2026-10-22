using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>Which own operatives the player has selected, by net id. Pure; the first selected is the primary (follow, steer).</summary>
    public sealed class NetSelection
    {
        readonly List<int> selected = new List<int>();

        public IReadOnlyList<int> Selected => selected;
        public int Count => selected.Count;
        public int Primary => selected.Count > 0 ? selected[0] : 0;

        public bool Contains(int id) => selected.Contains(id);

        public void Only(int id)
        {
            selected.Clear();
            selected.Add(id);
        }

        public void Toggle(int id)
        {
            if (!selected.Remove(id))
                selected.Add(id);
        }

        public void All(IEnumerable<int> ids)
        {
            selected.Clear();
            selected.AddRange(ids);
            selected.Sort();
        }

        /// <summary>One operative: the next living id after the current primary, wrapping; nothing when none lives.</summary>
        public void Next(IReadOnlyList<int> living)
        {
            if (living.Count == 0)
            {
                selected.Clear();
                return;
            }
            var ordered = new List<int>(living);
            ordered.Sort();
            var at = ordered.IndexOf(Primary);
            Only(ordered[(at + 1) % ordered.Count]);
        }

        /// <summary>Drops every id the predicate rejects (dead, unknown or no longer owned).</summary>
        public void Retain(Func<int, bool> keep) => selected.RemoveAll(id => !keep(id));
    }
}
