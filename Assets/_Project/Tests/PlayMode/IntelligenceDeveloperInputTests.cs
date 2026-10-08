#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelligenceDeveloperInputTests : InputTestFixture
    {
        Keyboard keyboard;
        InputActionAsset actions;
        MissionRig rig;
        IntelligenceService service;
        IntelMapView map;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            rig = new MissionRig();
            service = rig.AddIntelligence(IntelligenceSettings.Full());
            var mapHost = rig.World.Track(new GameObject("Map"));
            map = mapHost.AddComponent<IntelMapView>();
            map.Initialize(service, rig.Encounter, rig.Director);
            // Inactive while wiring, so OnEnable subscribes to the real actions, not to the empty references.
            var host = rig.World.Track(new GameObject("IntelDeveloperInput"));
            host.SetActive(false);
            var input = host.AddComponent<IntelligenceDeveloperInput>();
            input.Initialize(service, rig.Director, map, TestControls.Ref(actions, "Developer/ToggleTruthView"),
                TestControls.Ref(actions, "Developer/CycleIntelligencePreset"), TestControls.Ref(actions, "Developer/ToggleIntelMap"));
            host.SetActive(true);
        }

        public override void TearDown()
        {
            rig.Dispose();
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
        public IEnumerator F12_TogglesTruthView_AndOnlyTheDisplay()
        {
            yield return rig.Generate(12345);
            Assert.That(service.TruthView, Is.False);
            yield return Tap(keyboard.f12Key);
            Assert.That(service.TruthView, Is.True);
            yield return Tap(keyboard.f12Key);
            Assert.That(service.TruthView, Is.False);
        }

        [UnityTest]
        public IEnumerator M_TogglesTheMapOverlay()
        {
            yield return rig.Generate(12345);
            Assert.That(map.IsVisible, Is.True);
            yield return Tap(keyboard.mKey);
            Assert.That(map.IsVisible, Is.False);
        }

        [UnityTest]
        public IEnumerator F5_CyclesThePreset_AndRegeneratesTheSameSeed()
        {
            yield return rig.Generate(12345);
            Assert.That(service.IsFogActive, Is.False, "the Full preset");
            var layoutHash = rig.Director.Report.LayoutHash;

            Press(keyboard.f5Key);
            yield return null;
            Release(keyboard.f5Key);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(rig.Director.Settings.seed, Is.EqualTo(12345));
            Assert.That(rig.Director.Settings.intelligence.fogEnabled, Is.True, "the next preset is Blind");
            Assert.That(service.IsFogActive, Is.True);
            Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(layoutHash), "same seed, same level: fog never changes the mission");
        }
    }
}
#endif
