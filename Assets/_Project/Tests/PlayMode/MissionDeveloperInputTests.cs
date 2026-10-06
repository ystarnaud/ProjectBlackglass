#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class MissionDeveloperInputTests : InputTestFixture
    {
        Keyboard keyboard;
        InputActionAsset actions;
        MissionRig rig;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            rig = new MissionRig();
            // Inactive while wiring, so OnEnable subscribes to the real actions, not to the empty references.
            var host = rig.World.Track(new GameObject("DeveloperInput"));
            host.SetActive(false);
            var input = host.AddComponent<MissionDeveloperInput>();
            input.Initialize(rig.Director, TestControls.Ref(actions, "Developer/RegenerateSame"), TestControls.Ref(actions, "Developer/RegenerateNew"));
            host.SetActive(true);
        }

        public override void TearDown()
        {
            rig.Dispose();
            TestControls.Reset(actions);
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator F6_RegeneratesTheSameSeed()
        {
            yield return rig.Generate(31);
            var hash = rig.Director.Report.LayoutHash;
            var oldRoot = rig.Director.Current.Root;

            Press(keyboard.f6Key);
            yield return null;
            Release(keyboard.f6Key);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Generating));
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(rig.Director.Settings.seed, Is.EqualTo(31));
            Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(hash));
            Assert.That(oldRoot == null, Is.True);
        }

        [UnityTest]
        public IEnumerator F7_GeneratesANewSeed_AndShowsItInTheSettings()
        {
            yield return rig.Generate(31);

            Press(keyboard.f7Key);
            yield return null;
            Release(keyboard.f7Key);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(rig.Director.Settings.seed, Is.Not.EqualTo(31));
            Assert.That(rig.Director.Report.Seed, Is.EqualTo(rig.Director.Settings.seed));
        }
    }
}
#endif
