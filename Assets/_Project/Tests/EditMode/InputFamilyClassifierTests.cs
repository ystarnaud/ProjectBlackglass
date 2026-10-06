using NUnit.Framework;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;

namespace Blackglass.Tests
{
    public class InputFamilyClassifierTests : InputTestFixture
    {
        static void AssertClassified(InputDevice device, InputFamily family, ControllerModel model)
        {
            Assert.That(InputFamilyClassifier.TryClassify(device, out var actualFamily, out var actualModel), Is.True);
            Assert.That(actualFamily, Is.EqualTo(family));
            Assert.That(actualModel, Is.EqualTo(model));
        }

        [Test]
        public void KeyboardAndMouse_AreKeyboardMouse()
        {
            AssertClassified(InputSystem.AddDevice<Keyboard>(), InputFamily.KeyboardMouse, ControllerModel.None);
            AssertClassified(InputSystem.AddDevice<Mouse>(), InputFamily.KeyboardMouse, ControllerModel.None);
        }

        [Test]
        public void XInputController_IsXbox() =>
            AssertClassified(InputSystem.AddDevice<XInputController>(), InputFamily.Xbox, ControllerModel.Xbox);

        [Test]
        public void DualSenseHid_IsPlayStation() =>
            AssertClassified(InputSystem.AddDevice<DualSenseGamepadHID>(), InputFamily.PlayStation, ControllerModel.DualSense);

        [Test]
        public void DualShock4Hid_IsPlayStation() =>
            AssertClassified(InputSystem.AddDevice<DualShock4GamepadHID>(), InputFamily.PlayStation, ControllerModel.DualShock);

        [Test]
        public void SwitchProHid_IsNintendo() =>
            AssertClassified(InputSystem.AddDevice<SwitchProControllerHID>(), InputFamily.Nintendo, ControllerModel.SwitchPro);

        [Test]
        public void PlainGamepad_IsGeneric() =>
            AssertClassified(InputSystem.AddDevice<Gamepad>(), InputFamily.GenericGamepad, ControllerModel.Unknown);

        [Test]
        public void NonGamepadDevices_AreNotClassified()
        {
            Assert.That(InputFamilyClassifier.TryClassify(InputSystem.AddDevice<Joystick>(), out _, out _), Is.False);
            Assert.That(InputFamilyClassifier.TryClassify(InputSystem.AddDevice<Touchscreen>(), out _, out _), Is.False);
        }

        [TestCase(0x054C, InputFamily.PlayStation)]
        [TestCase(0x045E, InputFamily.Xbox)]
        [TestCase(0x057E, InputFamily.Nintendo)]
        [TestCase(0x1234, InputFamily.GenericGamepad)]
        [TestCase(0, InputFamily.GenericGamepad)]
        public void FamilyFromVendor_MapsKnownVendors(int vendorId, InputFamily expected) =>
            Assert.That(InputFamilyClassifier.FamilyFromVendor(vendorId), Is.EqualTo(expected));

        [Test]
        public void IsSwitch2_NeedsTheNintendoVendorAndAKnownProduct()
        {
            Assert.That(InputFamilyClassifier.IsSwitch2(0x057E, 0x2069), Is.True);
            Assert.That(InputFamilyClassifier.IsSwitch2(0x057E, 0x2009), Is.False, "0x2009 is the original Switch Pro Controller");
            Assert.That(InputFamilyClassifier.IsSwitch2(0x054C, 0x2069), Is.False, "Right product, wrong vendor");
        }

        [Test]
        public void FamilyExtensions_GiveGroupsAndNames()
        {
            Assert.That(InputFamily.KeyboardMouse.IsController(), Is.False);
            Assert.That(InputFamily.Xbox.IsController(), Is.True);
            Assert.That(InputFamily.GenericGamepad.BindingGroup(), Is.EqualTo("Gamepad"));
            Assert.That(InputFamily.PlayStation.BindingGroup(), Is.EqualTo("PlayStation"));
            Assert.That(InputFamily.KeyboardMouse.DisplayName(), Is.EqualTo("Keyboard/Mouse"));
            Assert.That(InputFamily.GenericGamepad.DisplayName(), Is.EqualTo("Generic Gamepad"));
        }
    }
}
