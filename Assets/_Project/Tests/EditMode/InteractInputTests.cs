using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class InteractInputTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        MissionInteractable Terminal(bool available = true)
        {
            var host = new GameObject("Terminal");
            hosts.Add(host);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.SetAvailable(available);
            return terminal;
        }

        Health Hostile()
        {
            var host = new GameObject("Hostile");
            hosts.Add(host);
            return host.AddComponent<Health>();
        }

        [Test]
        public void PointerTarget_OnInteractable_CarriesTheTerminalAndThePoint()
        {
            var terminal = Terminal();
            var target = PointerTarget.OnInteractable(terminal, new Vector3(1f, 2f, 3f));

            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Interactable));
            Assert.That(target.Interactable, Is.SameAs(terminal));
            Assert.That(target.Point, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(target.Friendly, Is.Null);
            Assert.That(target.Hostile, Is.Null);
            Assert.That(target.Cover, Is.Null);
        }

        [Test]
        public void CommandResolver_ChoosesInteract_ForAnAvailableTerminal()
        {
            var terminal = Terminal();
            var command = CommandResolver.Resolve(null, Vector3.zero, null, terminal);

            Assert.That(command, Is.TypeOf<InteractCommand>());
            Assert.That(((InteractCommand)command).Target, Is.SameAs(terminal));
        }

        [Test]
        public void CommandResolver_PrefersAnAttackOnALivingHostile_OverATerminal()
        {
            var command = CommandResolver.Resolve(Hostile(), Vector3.zero, null, Terminal());
            Assert.That(command, Is.TypeOf<AttackCommand>());
        }

        [Test]
        public void CommandResolver_IgnoresAnUnavailableTerminal_AndFallsBackToMove()
        {
            var command = CommandResolver.Resolve(null, new Vector3(4f, 0f, 4f), null, Terminal(available: false));
            Assert.That(command, Is.TypeOf<MoveCommand>());
        }

        [Test]
        public void InteractAction_ExistsInTheCommandsMap_WithAKeyboardBindingAndNoPadBinding()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            var action = asset.FindAction("Commands/Interact", throwIfNotFound: true);

            var keyboard = action.bindings.Where(b => !b.isComposite && b.groups == "KeyboardMouse").Select(b => b.path).ToArray();
            Assert.That(keyboard, Is.EqualTo(new[] { "<Keyboard>/r" }));
            Assert.That(action.bindings.Any(b => b.path.StartsWith("<Gamepad>")), Is.False,
                "the pad interacts through context Confirm, not its own button");
        }

        [Test]
        public void InteractAction_UsesAKeyThatNoOtherActionUses()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            var users = asset.bindings.Where(b => b.path == "<Keyboard>/r").Select(b => b.action).Distinct().ToArray();
            Assert.That(users, Is.EqualTo(new[] { "Interact" }));
        }
    }
}
