#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class DeveloperOverlayGatingTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        TestWorld world;
        DeveloperOverlay overlay;
        PrototypeHud hud;
        AbilityBarView bar;
        MissionHud missionHud;
        MissionDebugView missionDebug;
        TacticalCursorView cursorView;
        PlayerCommandInput commandInput;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            world = new TestWorld();

            var cameraObject = world.Track(new GameObject("Camera"));
            var viewCamera = cameraObject.AddComponent<Camera>();

            // Inactive while wiring, so OnEnable subscribes to the real actions, not to the empty references.
            var host = world.Track(new GameObject("Overlay"));
            host.SetActive(false);
            overlay = host.AddComponent<DeveloperOverlay>();
            var input = host.AddComponent<DeveloperOverlayInput>();
            input.Initialize(overlay, TestControls.Ref(actions, "Developer/ToggleDebugOverlay"));
            host.SetActive(true);

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize();
            var active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(null, pause);
            commandInput = systems.AddComponent<PlayerCommandInput>();
            commandInput.Initialize(viewCamera, selection, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/ToggleTacticalPause"),
                TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/Cancel"),
                active);
            systems.SetActive(true);

            var views = world.Track(new GameObject("Views"));
            hud = views.AddComponent<PrototypeHud>();
            bar = views.AddComponent<AbilityBarView>();
            missionHud = views.AddComponent<MissionHud>();
            missionDebug = views.AddComponent<MissionDebugView>();
            cursorView = views.AddComponent<TacticalCursorView>();
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
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

        [Test]
        public void EachView_IsDrawing_OnlyWhileTheOverlayIsShown()
        {
            hud.SetOverlay(overlay);
            bar.SetOverlay(overlay);
            missionHud.SetOverlay(overlay);
            missionDebug.SetOverlay(overlay);
            cursorView.SetOverlay(overlay);

            Assert.That(overlay.IsVisible, Is.False, "Hidden by default");
            Assert.That(hud.IsDrawing, Is.False);
            Assert.That(bar.IsDrawing, Is.False);
            Assert.That(missionHud.IsDrawing, Is.False);
            Assert.That(missionDebug.IsDrawing, Is.False);
            Assert.That(cursorView.IsDrawing, Is.False, "the 'Right stick: Camera' text");

            overlay.Toggle();
            Assert.That(hud.IsDrawing, Is.True);
            Assert.That(bar.IsDrawing, Is.True);
            Assert.That(missionHud.IsDrawing, Is.True);
            Assert.That(missionDebug.IsDrawing, Is.True);
            Assert.That(cursorView.IsDrawing, Is.True);

            overlay.Toggle();
            Assert.That(hud.IsDrawing, Is.False);
            Assert.That(missionDebug.IsDrawing, Is.False);
            Assert.That(cursorView.IsDrawing, Is.False);
        }

        [Test]
        public void EachView_IsDrawing_WithNoOverlayAssigned()
        {
            Assert.That(hud.IsDrawing, Is.True);
            Assert.That(bar.IsDrawing, Is.True);
            Assert.That(missionHud.IsDrawing, Is.True);
            Assert.That(missionDebug.IsDrawing, Is.True);
            Assert.That(cursorView.IsDrawing, Is.True);
        }

        [UnityTest]
        public IEnumerator TheDragBox_StaysDrawnWhileTheOverlayIsHidden()
        {
            hud.SetOverlay(overlay);
            hud.SetCommandInput(commandInput);
            yield return null;
            Assert.That(hud.DrawsDragBox, Is.False, "No drag yet");

            Set(mouse.position, new Vector2(100f, 100f));
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, new Vector2(300f, 260f));
            yield return null;

            Assert.That(commandInput.IsDragging, Is.True);
            Assert.That(hud.IsDrawing, Is.False, "The debug text is hidden");
            Assert.That(hud.DrawsDragBox, Is.True, "The player-facing box is not");

            Release(mouse.leftButton);
            yield return null;
            Assert.That(hud.DrawsDragBox, Is.False);
        }

        [UnityTest]
        public IEnumerator TheOverlayKey_TogglesTheOverlay()
        {
            yield return null;
            Assert.That(overlay.IsVisible, Is.False);

            yield return Tap(keyboard.f1Key);
            Assert.That(overlay.IsVisible, Is.True);

            yield return Tap(keyboard.f1Key);
            Assert.That(overlay.IsVisible, Is.False);
        }

        [UnityTest]
        public IEnumerator OtherKeys_DoNotToggleTheOverlay()
        {
            yield return null;
            yield return Tap(keyboard.f2Key);
            yield return Tap(keyboard.f6Key);
            yield return Tap(keyboard.f12Key);
            yield return Tap(keyboard.hKey);

            Assert.That(overlay.IsVisible, Is.False);
        }
    }
}
#endif
