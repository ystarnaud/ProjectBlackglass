using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Blackglass
{
    /// <summary>
    /// Tracks which input family the player is using (keyboard/mouse, Xbox, PlayStation, Nintendo or a generic
    /// gamepad) and makes the input actions follow it: the asset's binding mask selects that family's binding group, and
    /// for a controller the asset is restricted to that one device, so a second connected pad cannot also drive the game.
    /// Switching needs meaningful input (see InputActivityFilter), so stick drift, mouse jitter and held controls never
    /// flip it. The change is applied on the next Update, so the input that woke a family does not also fire an action.
    /// Runs on input events, not scaled time, so it works while tactically paused. Lives on Systems.
    /// </summary>
    [DefaultExecutionOrder(-300)] // before the input components read their actions this frame
    public sealed class ActiveInputDevice : MonoBehaviour
    {
        [SerializeField] InputActionAsset controls;

        readonly InputActivityFilter filter = new InputActivityFilter();
        bool bindingsDirty;

        public InputFamily Family { get; private set; } = InputFamily.KeyboardMouse;

        /// <summary>The more precise controller identity (None for keyboard/mouse). Gameplay never reads it.</summary>
        public ControllerModel Model { get; private set; } = ControllerModel.None;

        /// <summary>The active controller, or null while keyboard/mouse is active.</summary>
        public InputDevice Device { get; private set; }

        /// <summary>Raised with the new family whenever the family or the active controller changes.</summary>
        public event Action<InputFamily> Changed;

        internal void Initialize(InputActionAsset actions) => controls = actions;

        void OnEnable()
        {
            Family = InputFamily.KeyboardMouse;
            Model = ControllerModel.None;
            Device = null;
            filter.Reset();
            ApplyBindings();
            InputSystem.onEvent += OnInputEvent;
            InputSystem.onDeviceChange += OnDeviceChange;
        }

        void OnDisable()
        {
            InputSystem.onEvent -= OnInputEvent;
            InputSystem.onDeviceChange -= OnDeviceChange;
            bindingsDirty = false;
            if (controls == null)
                return;
            controls.bindingMask = null;
            controls.devices = null;
        }

        void Update()
        {
            if (bindingsDirty)
                ApplyBindings();
        }

        void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>())
                return;
            if (!InputFamilyClassifier.TryClassify(device, out var family, out var model))
                return;
            if (!IsMeaningful(eventPtr, device))
                return;
            if (family == Family && (!family.IsController() || device == Device))
                return;
            SwitchTo(family, device, model);
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            var gone = change == InputDeviceChange.Removed || change == InputDeviceChange.Disconnected;
            if (gone && Family.IsController() && device == Device)
                SwitchTo(InputFamily.KeyboardMouse, null, ControllerModel.None);
        }

        void SwitchTo(InputFamily family, InputDevice device, ControllerModel model)
        {
            Family = family;
            Model = model;
            Device = family.IsController() ? device : null;
            bindingsDirty = true;
            Changed?.Invoke(family);
        }

        void ApplyBindings()
        {
            bindingsDirty = false;
            if (controls == null)
                return;
            controls.bindingMask = InputBinding.MaskByGroup(Family.BindingGroup());
            if (Family.IsController() && Device != null)
                controls.devices = new InputDevice[] { Device };
            else
                controls.devices = null;
        }

        // Evaluates every stick first so the filter always sees them, whatever else in the event already qualified.
        // The stick key (deviceId * 4 + stickIndex) identifies one physical stick (left = 0, right = 1) of one device.
        bool IsMeaningful(InputEventPtr eventPtr, InputDevice device)
        {
            var meaningful = false;
            switch (device)
            {
                case Gamepad gamepad:
                    meaningful |= StickEngaged(gamepad.leftStick, device.deviceId * 4, eventPtr);
                    meaningful |= StickEngaged(gamepad.rightStick, device.deviceId * 4 + 1, eventPtr);
                    break;
                case Mouse mouse:
                    meaningful |= mouse.delta.ReadValueFromEvent(eventPtr, out var delta) && filter.IsMouseMotion(delta);
                    meaningful |= mouse.scroll.ReadValueFromEvent(eventPtr, out var scroll) && filter.IsScroll(scroll);
                    break;
            }
            return meaningful || AnyButtonPressed(device, eventPtr);
        }

        bool StickEngaged(StickControl stick, int key, InputEventPtr eventPtr) =>
            stick.ReadValueFromEvent(eventPtr, out var value) && filter.IsStickEngagement(key, value);

        // A button counts on its press edge: the event value is past the press point and the device's current
        // (pre-event) value is not. Stick direction buttons are derived from the stick and are judged as the stick.
        bool AnyButtonPressed(InputDevice device, InputEventPtr eventPtr)
        {
            foreach (var control in device.allControls)
            {
                if (!(control is ButtonControl button) || button.synthetic || button.noisy || button.parent is StickControl)
                    continue;
                if (button.ReadValueFromEvent(eventPtr, out var after) && filter.IsButtonPress(button.ReadValue(), after))
                    return true;
            }
            return false;
        }
    }
}
