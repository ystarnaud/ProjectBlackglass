using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class InputModalGateTests
    {
        InputActionAsset asset;
        InputAction fire, move, navigate, toggle;

        [SetUp]
        public void SetUp()
        {
            asset = ScriptableObject.CreateInstance<InputActionAsset>();
            var game = asset.AddActionMap("Game");
            fire = game.AddAction("Fire", InputActionType.Button, "<Keyboard>/space");
            move = game.AddAction("Move", InputActionType.Value, "<Keyboard>/w");
            var ui = asset.AddActionMap("UI");
            navigate = ui.AddAction("Navigate", InputActionType.Value, "<Keyboard>/upArrow");
            var inventory = asset.AddActionMap("Inventory");
            toggle = inventory.AddAction("Toggle", InputActionType.Button, "<Keyboard>/i");
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(asset);

        [Test]
        public void Open_DisablesGameplayActions_AndEnablesTheModalMaps()
        {
            fire.Enable();
            move.Enable();
            var gate = new InputModalGate(asset, "UI", "Inventory");

            gate.Open();

            Assert.That(gate.IsOpen, Is.True);
            Assert.That(fire.enabled, Is.False);
            Assert.That(move.enabled, Is.False);
            Assert.That(navigate.enabled, Is.True);
            Assert.That(toggle.enabled, Is.True);
        }

        [Test]
        public void Close_RestoresEachActionsPreviousState_NotJustEnabled()
        {
            fire.Enable();                 // was on
            toggle.Enable();               // was on
            var gate = new InputModalGate(asset, "UI", "Inventory");
            gate.Open();

            gate.Close();

            Assert.That(gate.IsOpen, Is.False);
            Assert.That(fire.enabled, Is.True);
            Assert.That(move.enabled, Is.False, "it was off before and is off again");
            Assert.That(navigate.enabled, Is.False, "the UI map is off outside the modal");
            Assert.That(toggle.enabled, Is.True);
        }

        [Test]
        public void Enforce_DisablesAnActionAnotherComponentSwitchedOnWhileOpen_AndRestoresItOnClose()
        {
            var gate = new InputModalGate(asset, "UI", "Inventory");
            gate.Open();

            move.Enable();                 // a component enabled itself during the modal
            gate.Enforce();
            Assert.That(move.enabled, Is.False, "no gameplay input leaks through");

            gate.Close();
            Assert.That(move.enabled, Is.True, "it wanted to be on");
        }

        [Test]
        public void Enforce_ReEnablesAModalActionSomeoneSwitchedOff()
        {
            var gate = new InputModalGate(asset, "UI", "Inventory");
            gate.Open();

            navigate.Disable();
            gate.Enforce();

            Assert.That(navigate.enabled, Is.True);
        }

        [Test]
        public void OpenTwice_AndCloseWithoutOpen_AreHarmless()
        {
            fire.Enable();
            var gate = new InputModalGate(asset, "UI", "Inventory");

            gate.Close();
            Assert.That(fire.enabled, Is.True);

            gate.Open();
            gate.Open();
            gate.Close();
            Assert.That(fire.enabled, Is.True, "the second Open did not record the disabled state as the original");
        }
    }
}
