using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Marks a unit the player can select. Its selected state is owned by UnitSelection; enemies and AI units are
    /// CommandableUnits without this component.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit))]
    public sealed class SelectableUnit : MonoBehaviour
    {
        CommandableUnit unit;
        bool isSelected;

        public CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();

        /// <summary>Set only by UnitSelection.</summary>
        public bool IsSelected
        {
            get => isSelected;
            internal set
            {
                if (isSelected == value)
                    return;
                isSelected = value;
                SelectionChanged?.Invoke(value);
            }
        }

        /// <summary>Raised with the new state, only when it actually changes.</summary>
        public event Action<bool> SelectionChanged;

        /// <summary>Lets UnitSelection drop units that are disabled or destroyed.</summary>
        internal event Action<SelectableUnit> Disabled;

        void OnDisable() => Disabled?.Invoke(this);
    }
}
