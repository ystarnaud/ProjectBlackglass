using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The player's selected units, plus the roster of units the player controls. Holds state only: input decides
    /// what to select, and orders reach the selected units through GroupOrders.
    /// </summary>
    public sealed class UnitSelection : MonoBehaviour
    {
        [SerializeField] List<SelectableUnit> roster = new List<SelectableUnit>();

        readonly List<SelectableUnit> selected = new List<SelectableUnit>();

        /// <summary>Every unit the player controls. Box selection tests against this list.</summary>
        public IReadOnlyList<SelectableUnit> Roster => roster;

        /// <summary>Selected units, in the order they were selected.</summary>
        public IReadOnlyList<SelectableUnit> Selected => selected;

        /// <summary>Raised after the selection actually changes.</summary>
        public event Action Changed;

        internal void Initialize(params SelectableUnit[] rosterUnits)
        {
            roster.Clear();
            roster.AddRange(rosterUnits);
        }

        public void AddToRoster(SelectableUnit unit)
        {
            if (unit == null)
                throw new ArgumentNullException(nameof(unit));
            if (!roster.Contains(unit))
                roster.Add(unit);
        }

        /// <summary>Selects only this unit.</summary>
        public void Select(SelectableUnit unit)
        {
            if (unit == null)
                throw new ArgumentNullException(nameof(unit));
            SetSelection(new[] { unit });
        }

        /// <summary>Removes the unit if it is selected, otherwise adds it.</summary>
        public void Toggle(SelectableUnit unit)
        {
            if (unit == null)
                throw new ArgumentNullException(nameof(unit));
            if (selected.Remove(unit))
                Release(unit);
            else if (CanSelect(unit))
                Claim(unit);
            else
                return;
            Changed?.Invoke();
        }

        /// <summary>Adds units to the selection, keeping the ones already selected.</summary>
        public void Add(IEnumerable<SelectableUnit> units)
        {
            if (units == null)
                throw new ArgumentNullException(nameof(units));
            var changed = false;
            foreach (var unit in units)
            {
                if (!CanSelect(unit) || selected.Contains(unit))
                    continue;
                Claim(unit);
                changed = true;
            }
            if (changed)
                Changed?.Invoke();
        }

        /// <summary>Replaces the selection with these units, in this order.</summary>
        public void SetSelection(IEnumerable<SelectableUnit> units)
        {
            if (units == null)
                throw new ArgumentNullException(nameof(units));
            var wanted = new List<SelectableUnit>();
            foreach (var unit in units)
            {
                if (CanSelect(unit) && !wanted.Contains(unit))
                    wanted.Add(unit);
            }
            if (IsSelectedExactly(wanted))
                return;

            var previous = new List<SelectableUnit>(selected);
            selected.Clear();
            foreach (var unit in previous)
            {
                if (!wanted.Contains(unit))
                    Release(unit);
            }
            foreach (var unit in wanted)
            {
                if (previous.Contains(unit))
                    selected.Add(unit);
                else
                    Claim(unit);
            }
            Changed?.Invoke();
        }

        public void Clear() => SetSelection(Array.Empty<SelectableUnit>());

        static bool CanSelect(SelectableUnit unit) => unit != null && unit.enabled && unit.gameObject.activeInHierarchy;

        bool IsSelectedExactly(List<SelectableUnit> units)
        {
            if (units.Count != selected.Count)
                return false;
            for (var i = 0; i < units.Count; i++)
            {
                if (units[i] != selected[i])
                    return false;
            }
            return true;
        }

        // Invariant kept by Claim, Release and OnUnitDisabled: a unit is subscribed to, and has IsSelected true,
        // exactly while it is in `selected`.

        // Adds a unit to the end of the selection.
        void Claim(SelectableUnit unit)
        {
            selected.Add(unit);
            unit.Disabled += OnUnitDisabled;
            unit.IsSelected = true;
        }

        // Finishes removing a unit that is no longer in the selected list.
        void Release(SelectableUnit unit)
        {
            Unsubscribe(unit);
            if (unit != null)
                unit.IsSelected = false;
        }

        void Unsubscribe(SelectableUnit unit) => unit.Disabled -= OnUnitDisabled;

        void OnUnitDisabled(SelectableUnit unit)
        {
            if (!selected.Remove(unit))
                return;
            Release(unit);
            Changed?.Invoke();
        }

        void OnDestroy()
        {
            foreach (var unit in selected)
                Unsubscribe(unit);
        }
    }
}
