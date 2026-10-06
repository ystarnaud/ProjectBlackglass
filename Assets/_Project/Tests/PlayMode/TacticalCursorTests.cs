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
    public class TacticalCursorTests : InputTestFixture
    {
        static readonly Vector3 FriendlyGround = new Vector3(-4f, 0f, -6f);
        static readonly Vector3 NearHostileGround = new Vector3(-6f, 0f, -6f);
        static readonly Vector3 FarHostileGround = new Vector3(6f, 0f, 6f);
        static readonly Vector3 CoverGroundPoint = new Vector3(-6f, 0f, 2f);
        static readonly Vector3 OpenGround = new Vector3(2f, 0f, 2f);

        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit friendly;
        Health nearHostile;
        Health farHostile;
        CoverLocation cover;
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
            friendly = world.CreateFriendly(FriendlyGround);
            nearHostile = world.CreateDummy(NearHostileGround);
            farHostile = world.CreateDummy(FarHostileGround);
            var wall = world.CreateObstacle(CoverGroundPoint + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            cover = world.CreateCoverPoint(CoverGroundPoint, Vector3.forward, wall.GetComponent<Collider>());
            var registry = world.CreateRegistry(cover);
            var encounter = world.CreateEncounter();
            // Far first in the list, so the distance ordering (not list order) is what the tests see.
            encounter.Initialize(new Health[0], new[] { farHostile, nearHostile });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(friendly);
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(friendly.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, encounter, registry, null,
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
        public IEnumerator Stick_MovesTheCursor_OnUnscaledTime_WhilePaused()
        {
            pause.Pause();
            cursor.SetScreenPosition(new Vector2(50f, 200f));
            yield return null;
            var start = cursor.ScreenPosition;

            Set(pad.rightStick, new Vector2(1f, 0f));
            yield return new WaitForSecondsRealtime(0.2f);
            Set(pad.rightStick, Vector2.zero);
            yield return null;

            Assert.That(cursor.ScreenPosition.x - start.x, Is.GreaterThan(60f));
            Assert.That(cursor.ScreenPosition.y, Is.EqualTo(start.y).Within(1f));
            Assert.That(pause.IsPaused, Is.True);
        }

        [UnityTest]
        public IEnumerator StickDrift_DoesNotMoveTheCursor()
        {
            cursor.SetScreenPosition(new Vector2(200f, 200f));
            yield return null;
            var start = cursor.ScreenPosition;
            Set(pad.rightStick, new Vector2(0.12f, 0.1f));
            yield return new WaitForSecondsRealtime(0.3f);
            Set(pad.rightStick, Vector2.zero);

            Assert.That(cursor.ScreenPosition, Is.EqualTo(start));
        }

        [UnityTest]
        public IEnumerator Cursor_StaysOnTheScreen()
        {
            cursor.SetScreenPosition(new Vector2(viewCamera.pixelWidth * 0.5f, viewCamera.pixelHeight * 0.5f));
            Set(pad.rightStick, new Vector2(1f, 1f));
            yield return new WaitForSecondsRealtime(1.5f);
            Set(pad.rightStick, Vector2.zero);
            yield return null;

            Assert.That(cursor.ScreenPosition.x, Is.InRange(0f, viewCamera.pixelWidth));
            Assert.That(cursor.ScreenPosition.y, Is.InRange(0f, viewCamera.pixelHeight));
        }

        [UnityTest]
        public IEnumerator IsActive_FollowsTheRightStickRole()
        {
            yield return null;
            Assert.That(cursor.IsActive, Is.True, "Tactical mode: the cursor owns the right stick");

            active.SetTakeover(true);
            yield return null;
            Assert.That(cursor.IsActive, Is.False, "Driving: the camera owns it");
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.None), "An inactive cursor points at nothing");

            Press(pad.rightTrigger);
            yield return null;
            Assert.That(cursor.IsActive, Is.True, "RT hands the stick to the cursor while driving");
            Release(pad.rightTrigger);
            yield return null;
            Assert.That(cursor.IsActive, Is.False);

            pause.Pause();
            yield return null;
            Assert.That(cursor.IsActive, Is.True, "Paused is tactical mode");
        }

        [UnityTest]
        public IEnumerator Resolves_Friendly_Hostile_AndGround()
        {
            cursor.SetScreenPosition(ScreenPointOf(friendly.transform.position));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly));

            cursor.SetScreenPosition(ScreenPointOf(farHostile.transform.position));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(farHostile));

            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapsToCoverWithin075Metres_ButNotBeyond()
        {
            cursor.SetScreenPosition(ScreenPointOf(CoverGroundPoint + new Vector3(0.4f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Cover));
            Assert.That(cursor.Target.Cover, Is.SameAs(cover));

            cursor.SetScreenPosition(ScreenPointOf(CoverGroundPoint + new Vector3(0.7f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Cover));

            cursor.SetScreenPosition(ScreenPointOf(CoverGroundPoint + new Vector3(1.4f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapsToUnitsWithin12Metres_ButNotBeyond()
        {
            cursor.SetScreenPosition(ScreenPointOf(FriendlyGround + new Vector3(0.8f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly));

            cursor.SetScreenPosition(ScreenPointOf(FriendlyGround + new Vector3(2.5f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));

            cursor.SetScreenPosition(ScreenPointOf(FarHostileGround + new Vector3(0.8f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(farHostile));

            cursor.SetScreenPosition(ScreenPointOf(FarHostileGround + new Vector3(3.5f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            yield return null;
        }

        [UnityTest]
        public IEnumerator FriendlyBeatsHostile_WhenBothAreInSnapRange()
        {
            // Midway between the friendly (x = -4) and the near hostile (x = -6): one metre from each.
            cursor.SetScreenPosition(ScreenPointOf(new Vector3(-5f, 0f, -6f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            yield return null;
        }

        [UnityTest]
        public IEnumerator NextTarget_CyclesLivingHostilesByDistance_AndSnapsTheCursorOnto()
        {
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(nearHostile), "Nearest to the active character first");
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(nearHostile));

            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile));
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(nearHostile), "Wraps");
            yield return Tap(pad.dpad.left);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile), "Previous goes back");
        }

        [UnityTest]
        public IEnumerator TargetCycling_SkipsADeadHostile()
        {
            nearHostile.TakeDamage(1000);
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile));
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile), "The only living hostile");
        }

        [UnityTest]
        public IEnumerator TargetCycling_WithNoLivingHostiles_DoesNothing()
        {
            nearHostile.TakeDamage(1000);
            farHostile.TakeDamage(1000);
            yield return Tap(pad.dpad.right);
            yield return Tap(pad.dpad.left);

            Assert.That(cursor.SoftTarget, Is.Null);
        }

        [UnityTest]
        public IEnumerator SoftTarget_ClearsWhenItDiesOrIsDestroyed()
        {
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(nearHostile));
            nearHostile.TakeDamage(1000);
            Assert.That(cursor.SoftTarget, Is.Null, "Dead");

            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile));
            Object.DestroyImmediate(farHostile.gameObject);
            Assert.That(cursor.SoftTarget, Is.Null, "Destroyed");
        }

        [UnityTest]
        public IEnumerator PickAttackTarget_PrefersTheCursorHostile_ThenTheCycledSoftTarget_ThenTheBest()
        {
            var origin = friendly.transform.position;
            cursor.SetScreenPosition(ScreenPointOf(farHostile.transform.position));
            Assert.That(cursor.PickAttackTarget(origin, Vector3.forward), Is.SameAs(farHostile), "The cursor's hostile");

            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            yield return null;
            Assert.That(cursor.SoftTarget, Is.Null, "Hovering a hostile does not choose it");
            Assert.That(cursor.PickAttackTarget(origin, Vector3.forward), Is.SameAs(nearHostile),
                "Having hovered the far hostile and moved off, Attack falls back to the best hostile, not the hovered one");

            yield return Tap(pad.dpad.right);
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile), "Chosen explicitly with NextTarget");
            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            yield return null;
            Assert.That(cursor.PickAttackTarget(origin, Vector3.forward), Is.SameAs(farHostile), "The cycled soft target stays");

            farHostile.TakeDamage(1000);
            Assert.That(cursor.PickAttackTarget(origin, Vector3.forward), Is.SameAs(nearHostile), "Falls back to the best living hostile");

            nearHostile.TakeDamage(1000);
            Assert.That(cursor.PickAttackTarget(origin, Vector3.forward), Is.Null, "Nothing left to attack");
        }

        [UnityTest]
        public IEnumerator PickAttackTarget_BestByFacingFallback_IsNotRemembered()
        {
            // Equidistant from both hostiles (about 8.5 m), so only the facing decides.
            var origin = Vector3.zero;
            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            yield return null;

            Assert.That(cursor.PickAttackTarget(origin, new Vector3(-1f, 0f, -1f)), Is.SameAs(nearHostile));
            Assert.That(cursor.SoftTarget, Is.Null, "The fallback is not stored as the soft target");
            Assert.That(cursor.PickAttackTarget(origin, new Vector3(1f, 0f, 1f)), Is.SameAs(farHostile),
                "Turning toward the other hostile picks it: nothing locked on to the first");
        }

        [UnityTest]
        public IEnumerator HoveringAHostile_DoesNotSetTheSoftTarget()
        {
            cursor.SetScreenPosition(ScreenPointOf(nearHostile.transform.position));
            yield return null;
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.SoftTarget, Is.Null);
        }

        [UnityTest]
        public IEnumerator Cycling_KeepsItsChoice_AfterTheCursorSnapsOntoIt()
        {
            yield return Tap(pad.dpad.right);
            yield return Tap(pad.dpad.right);
            yield return null;
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(farHostile));
        }
    }
}
#endif
