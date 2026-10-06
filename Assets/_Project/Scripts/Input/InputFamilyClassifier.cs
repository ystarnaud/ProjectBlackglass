using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.HID;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;

namespace Blackglass
{
    /// <summary>
    /// Decides which input family a device belongs to. Keyboard and mouse are KeyboardMouse; a gamepad is Xbox,
    /// PlayStation or Nintendo when its layout or USB vendor says so, otherwise GenericGamepad; anything else (joysticks,
    /// touch) is not classified and never changes the active family. Nothing outside Scripts/Input knows device types.
    /// </summary>
    public static class InputFamilyClassifier
    {
        public const int SonyVendorId = 0x054C;
        public const int MicrosoftVendorId = 0x045E;
        public const int NintendoVendorId = 0x057E;

        const int DualSenseProductId = 0x0CE6;
        const int DualSenseEdgeProductId = 0x0DF2;
        // Believed to be the Switch 2 Pro Controller. Not verified on hardware, and Input System 1.20.0 has no Switch 2
        // layout, so such a device only appears as a Gamepad if the platform exposes it as one.
        const int Switch2ProProductId = 0x2069;

        public static bool TryClassify(InputDevice device, out InputFamily family, out ControllerModel model)
        {
            family = InputFamily.KeyboardMouse;
            model = ControllerModel.None;
            if (device is Keyboard || device is Mouse)
                return true;
            if (!(device is Gamepad))
                return false;

            TryReadHidIds(device, out var vendorId, out var productId);

            if (device is XInputController)
            {
                family = InputFamily.Xbox;
                model = ControllerModel.Xbox;
                return true;
            }
            if (device is DualShockGamepad)
            {
                family = InputFamily.PlayStation;
                var isDualSense = device.layout == "DualSenseGamepadHID" || productId == DualSenseProductId
                    || productId == DualSenseEdgeProductId;
                model = isDualSense ? ControllerModel.DualSense : ControllerModel.DualShock;
                return true;
            }
            if (device is SwitchProController)
            {
                family = InputFamily.Nintendo;
                model = IsSwitch2(vendorId, productId) ? ControllerModel.Switch2 : ControllerModel.SwitchPro;
                return true;
            }

            family = FamilyFromVendor(vendorId);
            switch (family)
            {
                case InputFamily.Xbox:
                    model = ControllerModel.Xbox;
                    break;
                case InputFamily.Nintendo:
                    model = IsSwitch2(vendorId, productId) ? ControllerModel.Switch2 : ControllerModel.Unknown;
                    break;
                default:
                    model = ControllerModel.Unknown;
                    break;
            }
            return true;
        }

        public static InputFamily FamilyFromVendor(int vendorId)
        {
            switch (vendorId)
            {
                case SonyVendorId:
                    return InputFamily.PlayStation;
                case MicrosoftVendorId:
                    return InputFamily.Xbox;
                case NintendoVendorId:
                    return InputFamily.Nintendo;
                default:
                    return InputFamily.GenericGamepad;
            }
        }

        public static bool IsSwitch2(int vendorId, int productId) =>
            vendorId == NintendoVendorId && productId == Switch2ProProductId;

        static void TryReadHidIds(InputDevice device, out int vendorId, out int productId)
        {
            vendorId = 0;
            productId = 0;
            var description = device.description;
            if (description.interfaceName != "HID" || string.IsNullOrEmpty(description.capabilities))
                return;
            var hid = HID.HIDDeviceDescriptor.FromJson(description.capabilities);
            vendorId = hid.vendorId;
            productId = hid.productId;
        }
    }
}
