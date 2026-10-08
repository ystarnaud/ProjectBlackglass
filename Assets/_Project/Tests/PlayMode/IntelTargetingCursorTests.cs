#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelTargetingCursorTests : InputTestFixture
    {
        InputActionAsset actions;
        IntelRig rig;
        Camera viewCamera;
        TacticalCursor cursor;
        Health shown;
        Health hidden;

        public override void Setup()
        {
            base.Setup();
            InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
        }

        public override void TearDown()
        {
            rig?.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        // The corridor is open: the hostile in line is observed, the one off the corridor's axis is not.
        void Build()
        {
            rig = new IntelRig(corridor: true);
            shown = rig.AddHostile(IntelRig.InLineGround);
            hidden = rig.AddHostile(IntelRig.OffAxisGround);
            rig.Begin(IntelRig.Fog());

            var cameraObject = rig.World.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = rig.World.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(rig.Friendly);
            var active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(rig.Friendly.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, rig.Encounter, null, null,
                TestControls.Ref(actions, "Commands/CursorMove"), TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"), TestControls.Ref(actions, "Commands/PreviousTarget"));
            cursor.SetIntelligence(rig.Service);
            systems.SetActive(true);
            pause.Pause();   // the cursor owns the right stick while paused
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        [UnityTest]
        public IEnumerator Cycling_SkipsTheUnobservedHostile_AndFindsNothingWhenNoneIsObserved()
        {
            Build();
            yield return null;
            cursor.CycleTarget(1);
            Assert.That(cursor.SoftTarget, Is.SameAs(shown));
            cursor.CycleTarget(1);
            Assert.That(cursor.SoftTarget, Is.SameAs(shown), "the only observed hostile again: the hidden one is never visited");
            cursor.CycleTarget(-1);
            Assert.That(cursor.SoftTarget, Is.SameAs(shown));

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);   // the corridor is out of line: nobody is observed
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(cursor.SoftTarget, Is.Null, "a soft target that stops being observed stops being one");
            cursor.CycleTarget(1);
            Assert.That(cursor.SoftTarget, Is.Null);
        }

        [UnityTest]
        public IEnumerator PickAttackTarget_IgnoresUnobservedHostiles()
        {
            Build();
            yield return null;
            var origin = rig.Friendly.transform.position;
            Assert.That(cursor.PickAttackTarget(origin, Vector3.right), Is.SameAs(shown));

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(cursor.PickAttackTarget(rig.Friendly.transform.position, Vector3.right), Is.Null,
                "no observed hostile: Attack has nothing to do, even though two living hostiles exist");
        }

        [UnityTest]
        public IEnumerator TheSnap_NeverLandsOnAnUnobservedHostile()
        {
            Build();
            yield return null;
            cursor.SetScreenPosition(ScreenPointOf(hidden.transform.position));
            Assert.That(cursor.Target.Kind, Is.Not.EqualTo(PointerTargetKind.Hostile));
            cursor.SetScreenPosition(ScreenPointOf(shown.transform.position));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(shown));
        }

        [UnityTest]
        public IEnumerator WhileAimingAtHostiles_TheSnapStillIgnoresTheUnobserved()
        {
            Build();
            yield return null;
            cursor.Aiming = true;
            cursor.SnapTo = PointerTargetKind.Hostile;
            // Ground next to the hidden hostile: within the 1.2 m snap radius, which would snap if it were allowed.
            cursor.SetScreenPosition(ScreenPointOf(hidden.transform.position + new Vector3(0.5f, -1f, 0f)));
            Assert.That(cursor.Target.Kind, Is.Not.EqualTo(PointerTargetKind.Hostile));
        }

        [UnityTest]
        public IEnumerator FogOff_LeavesTheCursorAsBefore()
        {
            rig = new IntelRig(corridor: false);
            var behindWall = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());
            var cameraObject = rig.World.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();
            var systems = rig.World.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(rig.Friendly);
            var active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(rig.Friendly.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, rig.Encounter, null, null,
                TestControls.Ref(actions, "Commands/CursorMove"), TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"), TestControls.Ref(actions, "Commands/PreviousTarget"));
            cursor.SetIntelligence(rig.Service);
            systems.SetActive(true);
            yield return null;
            cursor.CycleTarget(1);
            Assert.That(cursor.SoftTarget, Is.SameAs(behindWall));
        }
    }
}
#endif
