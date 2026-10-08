#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class OperativeDeveloperInputTests : InputTestFixture
    {
        Keyboard keyboard;
        InputActionAsset actions;
        MissionRig rig;
        OperativeKit kit;
        SquadRoster roster;
        OperativePanelView panel;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            rig = new MissionRig();
            kit = OperativeKit.Build();
            roster = rig.AddRoster(kit.Definitions, kit.Track);
            // Inactive while wiring, so OnEnable subscribes to the real actions, not to the empty references.
            var host = rig.World.Track(new GameObject("OperativeDeveloper"));
            host.SetActive(false);
            panel = host.AddComponent<OperativePanelView>();
            panel.Initialize(roster, rig.Active, rig.Director);
            var input = host.AddComponent<OperativeDeveloperInput>();
            input.Initialize(roster, rig.Active, panel, TestControls.Ref(actions, "Developer/ToggleOperativePanel"),
                TestControls.Ref(actions, "Developer/AddExperience"), TestControls.Ref(actions, "Developer/ResetProgression"));
            host.SetActive(true);
        }

        public override void TearDown()
        {
            rig.Dispose();
            kit.Dispose();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(UnityEngine.InputSystem.Controls.KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator F10_AddsTheTracksDebugXp_ToTheControlledOperativeOnly()
        {
            yield return rig.Generate(31);
            Assert.That(rig.Active.Unit, Is.SameAs(rig.Director.Friendlies[0]));

            yield return Tap(keyboard.f10Key);

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 50, 0, 0 }));

            rig.Active.SetUnit(rig.Director.Friendlies[2]);
            yield return Tap(keyboard.f10Key);
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 50, 0, 50 }));
        }

        [UnityTest]
        public IEnumerator F11_ResetsTheControlledOperativesProgression_AndNoOneElses()
        {
            yield return rig.Generate(31);
            roster.AwardExperience("test-alpha", 250);
            roster.AwardExperience("test-bravo", 250);
            roster.TryPickChoice("test-alpha", "combat");

            yield return Tap(keyboard.f11Key);

            Assert.That(roster.Find("test-alpha").State.Experience, Is.EqualTo(0));
            Assert.That(roster.Find("test-alpha").State.ChoiceIds, Is.Empty);
            Assert.That(roster.Find("test-bravo").State.Experience, Is.EqualTo(250));
            Assert.That(rig.Director.Friendlies[0].GetComponent<Health>().Max, Is.EqualTo(130), "the running unit follows the reset");
        }

        [UnityTest]
        public IEnumerator F9_TogglesThePanel_HiddenByDefault()
        {
            yield return rig.Generate(31);
            Assert.That(panel.IsVisible, Is.False);

            yield return Tap(keyboard.f9Key);
            Assert.That(panel.IsVisible, Is.True);
            yield return null;   // OnGUI may run; it must not throw
            yield return Tap(keyboard.f9Key);
            Assert.That(panel.IsVisible, Is.False);
        }

        [UnityTest]
        public IEnumerator WithNoControlledUnit_TheKeysDoNothing_AndDoNotThrow()
        {
            yield return rig.Generate(31);
            rig.Active.SetUnit(null);

            yield return Tap(keyboard.f10Key);
            yield return Tap(keyboard.f11Key);

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 0, 0, 0 }));
        }
    }
}
#endif
