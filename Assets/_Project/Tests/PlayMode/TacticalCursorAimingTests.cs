#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class TacticalCursorAimingTests : InputTestFixture
    {
        // The friendly and the hostile are 2.2 m apart; the ground between them is 1.1 m from each (inside the 1.2 m snap).
        static readonly Vector3 FriendlyGround = new Vector3(-4f, 0f, -6f);
        static readonly Vector3 OtherFriendlyGround = new Vector3(-4f, 0f, -10f);
        static readonly Vector3 HostileGround = new Vector3(-1.8f, 0f, -6f);
        static readonly Vector3 BetweenThem = new Vector3(-2.9f, 0f, -6f);
        static readonly Vector3 OpenGround = new Vector3(4f, 0f, 2f);

        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit friendly;
        SelectableUnit otherFriendly;
        Health hostile;
        TacticalPause pause;
        ActiveCharacter active;
        TacticalCursor cursor;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            world = new TestWorld();
            world.CreateEnvironment();
            friendly = world.CreateFriendlyFighter(FriendlyGround);
            otherFriendly = world.CreateFriendlyFighter(OtherFriendlyGround);
            hostile = world.CreateDummy(HostileGround);
            var encounter = world.CreateEncounter();
            encounter.Initialize(new[] { friendly.GetComponent<Health>(), otherFriendly.GetComponent<Health>() }, new[] { hostile });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(friendly, otherFriendly);
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(friendly.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, encounter, null, null,
                TestControls.Ref(actions, "Commands/CursorMove"),
                TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"),
                TestControls.Ref(actions, "Commands/PreviousTarget"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Aiming_MakesTheCursorOwnTheStick_WhileTheGameRuns_WithOrWithoutTheTriggerHeld()
        {
            yield return null;
            Assert.That(cursor.IsActive, Is.False, "Running, nothing armed: the camera owns the stick");

            cursor.Aiming = true;
            yield return null;
            Assert.That(cursor.IsActive, Is.True);

            Press(pad.rightTrigger);
            yield return null;
            Assert.That(cursor.IsActive, Is.True, "The held menu trigger does not hand the stick back while aiming");
            Release(pad.rightTrigger);

            cursor.Aiming = false;
            yield return null;
            Assert.That(cursor.IsActive, Is.False);
        }

        [UnityTest]
        public IEnumerator SnapTo_DecidesWhichSideTheCursorSnapsTo()
        {
            pause.Pause();
            cursor.SetScreenPosition(ScreenPointOf(BetweenThem));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly), "Default snapping prefers a friendly");

            cursor.SnapTo = PointerTargetKind.Hostile;
            cursor.Refresh();
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(hostile));

            cursor.SnapTo = PointerTargetKind.Friendly;
            cursor.Refresh();
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly));

            cursor.SnapTo = PointerTargetKind.Ground;
            cursor.Refresh();
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground), "An area is aimed freely: no snapping");
            Assert.That(CoverRules.FlatDistance(cursor.Target.Point, BetweenThem), Is.LessThan(0.2f));
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapTo_AHostile_WithNobodyNear_IsPlainGround_AndSnapToNoneIsTheOldBehaviour()
        {
            pause.Pause();
            cursor.SnapTo = PointerTargetKind.Hostile;
            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));

            cursor.SnapTo = PointerTargetKind.None;
            cursor.SetScreenPosition(ScreenPointOf(FriendlyGround));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapTo_Hostile_DoesNotRelabelAFriendlyUnderTheCursor()
        {
            pause.Pause();
            cursor.SnapTo = PointerTargetKind.Hostile;

            cursor.SetScreenPosition(ScreenPointOf(friendly.transform.position));

            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly),
                "A cursor right on a friendly is a friendly: the ability will say 'wrong side'");
            yield return null;
        }

        [UnityTest]
        public IEnumerator DpadCycling_WithSnapToFriendly_WalksTheFriendlies_AndSetsNoSoftTarget()
        {
            pause.Pause();
            cursor.SnapTo = PointerTargetKind.Friendly;
            cursor.SetScreenPosition(ScreenPointOf(friendly.transform.position));
            yield return null;
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly), "Precondition");

            yield return Tap(pad.dpad.right);

            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(cursor.Target.Friendly, Is.SameAs(otherFriendly));
            Assert.That(cursor.SoftTarget, Is.Null, "Attack must never pick a friendly");

            yield return Tap(pad.dpad.left);
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly));
        }

        [UnityTest]
        public IEnumerator DpadCycling_WithSnapToHostile_StillSetsTheSoftTarget()
        {
            pause.Pause();
            cursor.SnapTo = PointerTargetKind.Hostile;
            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            yield return null;

            yield return Tap(pad.dpad.right);

            Assert.That(cursor.SoftTarget, Is.SameAs(hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(hostile));
        }
    }
}
#endif
