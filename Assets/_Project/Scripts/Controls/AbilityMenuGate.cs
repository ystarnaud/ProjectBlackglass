using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Holding the ability-menu action (a pad's right trigger) turns the D-pad into ability slots: the ability chords
    /// (trigger + a D-pad direction) are bindings, but the plain D-pad actions (Stop, Follow, previous/next target)
    /// would fire on the same press, and Input System shortcut consumption is off by default. So while the menu action
    /// is held this disables the plain actions it was given, and gives back exactly the ones it took when the menu is
    /// released, cancelled (a family switch, an unplugged pad) or no longer pressed at the next Update. Contains no
    /// gameplay: it knows actions, not buttons.
    /// </summary>
    public sealed class AbilityMenuGate : MonoBehaviour
    {
        [SerializeField] InputActionReference menuAction;
        [SerializeField] InputActionReference[] suppressedWhileHeld = System.Array.Empty<InputActionReference>();

        readonly List<InputAction> taken = new List<InputAction>();

        /// <summary>True while the plain actions are switched off because the menu is held.</summary>
        internal bool IsSuppressing { get; private set; }

        internal void Initialize(InputActionReference menu, params InputActionReference[] suppressed)
        {
            menuAction = menu;
            suppressedWhileHeld = suppressed;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, menuAction);
            if (menuAction != null)
            {
                menuAction.action.started += OnMenuChanged;
                menuAction.action.canceled += OnMenuChanged;
            }
        }

        void OnDisable()
        {
            if (menuAction != null)
            {
                menuAction.action.started -= OnMenuChanged;
                menuAction.action.canceled -= OnMenuChanged;
            }
            Suppress(false);
            InputActionUtility.SetEnabled(false, menuAction);
        }

        void Update() => Reconcile();

        void OnMenuChanged(InputAction.CallbackContext context) => Reconcile();

        void Reconcile() => Suppress(InputActionUtility.IsPressed(menuAction));

        void Suppress(bool on)
        {
            if (on == IsSuppressing)
                return;
            IsSuppressing = on;
            if (on)
            {
                foreach (var reference in suppressedWhileHeld)
                {
                    if (reference == null || reference.action == null || !reference.action.enabled)
                        continue;
                    reference.action.Disable();
                    taken.Add(reference.action);
                }
                return;
            }
            foreach (var action in taken)
                action.Enable();
            taken.Clear();
        }
    }
}
