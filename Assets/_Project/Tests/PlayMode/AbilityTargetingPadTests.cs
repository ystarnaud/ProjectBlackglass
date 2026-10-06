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
    public class AbilityTargetingPadTests : InputTestFixture
    {
        Gamepad pad;
        InputActionAsset actions;
        AbilityRig rig;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            rig = AbilityRig.Build(actions, true);
        }

        public override void TearDown()
        {
            rig.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        // Holds the right trigger, taps a D-pad direction, releases the trigger: the ability menu gesture.
        IEnumerator PickWithTheMenu(ButtonControl direction)
        {
            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(direction);
            Release(pad.rightTrigger);
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator TriggerPlusDpadUp_ArmsAimedShot_AndTheCursorOwnsTheStickEvenWhileTheGameRuns()
        {
            yield return null;
            Assert.That(rig.Cursor.IsActive, Is.False, "Precondition: running, the camera owns the stick");

            yield return PickWithTheMenu(pad.dpad.up);

            Assert.That(rig.Targeting.IsArmed, Is.True);
            Assert.That(rig.Targeting.ArmedAbility, Is.SameAs(rig.Aimed));
            Assert.That(rig.Cursor.Aiming, Is.True);
            Assert.That(rig.Cursor.IsActive, Is.True, "Aiming hands the stick to the cursor");
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.Hostile));
        }

        [UnityTest]
        public IEnumerator EachDpadDirection_PicksItsOwnSlot()
        {
            yield return null;

            yield return PickWithTheMenu(pad.dpad.right);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(1));
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.Ground), "A blast is aimed freely");

            yield return PickWithTheMenu(pad.dpad.down);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(2));
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.Friendly), "A heal snaps to friendlies");

            yield return PickWithTheMenu(pad.dpad.left);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(2), "Slot 4 is empty: the armed slot does not change");
        }

        [UnityTest]
        public IEnumerator DpadDown_WithTheTriggerHeld_PicksMend_AndDoesNotStopTheCaster_ButStopWorksAfterwards()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            Assert.That(rig.Caster.Unit.Issue(new MoveCommand(new Vector3(-6f, 0f, -10f))), Is.True);

            yield return PickWithTheMenu(pad.dpad.down);

            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(2));
            Assert.That(rig.Caster.Unit.StopCount, Is.EqualTo(0), "The held trigger turns the D-pad into ability slots only");
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            yield return Tap(pad.dpad.down);
            Assert.That(rig.Caster.Unit.StopCount, Is.EqualTo(1), "Released: the D-pad stops the unit again");
        }

        [UnityTest]
        public IEnumerator Confirm_OnAHostile_UsesAimedShot_ThenTheCursorIsAnOrdinaryCursorAgain()
        {
            yield return null;
            yield return PickWithTheMenu(pad.dpad.up);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;

            yield return Tap(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);

            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 45));
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Cursor.Aiming, Is.False);
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.None));
            Assert.That(rig.Cursor.IsActive, Is.False, "Running again with nothing armed: the camera has its stick back");
        }

        [UnityTest]
        public IEnumerator Blast_OnAPad_UsesTheExactCursorPoint()
        {
            yield return null;
            yield return PickWithTheMenu(pad.dpad.right);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(AbilityRig.BlastGround));
            yield return null;
            Assert.That(rig.Targeting.Preview.IsValid, Is.True);

            yield return Tap(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);

            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 35));
        }

        [UnityTest]
        public IEnumerator Mend_OnAPad_SnapsToTheAlly_AndHealsThem()
        {
            yield return null;
            rig.AllyHealth.TakeDamage(60);
            yield return PickWithTheMenu(pad.dpad.down);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Ally.transform.position + new Vector3(0.6f, -1f, 0f)));
            yield return null;
            Assert.That(rig.Cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly), "Snapped to the ally");

            yield return Tap(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => rig.AllyHealth.Current > rig.AllyHealth.Max - 60, 2f);

            Assert.That(rig.AllyHealth.Current, Is.EqualTo(rig.AllyHealth.Max - 20));
        }

        [UnityTest]
        public IEnumerator Cancel_DisarmsFirst_AndOnlyTheNextCancelClearsTheSelection()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            yield return PickWithTheMenu(pad.dpad.up);

            yield return Tap(pad.buttonEast);
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Cursor.Aiming, Is.False);
            Assert.That(rig.Selection.Selected, Has.Count.EqualTo(1));

            yield return Tap(pad.buttonEast);
            Assert.That(rig.Selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Paused_TheQueueModifierQueuesTheAbilityBehindTheCastersOrders()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            rig.Pause.Pause();
            Assert.That(rig.Caster.Unit.Issue(new MoveCommand(new Vector3(-6f, 0f, -8f))), Is.True);
            yield return PickWithTheMenu(pad.dpad.up);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;

            Press(pad.leftTrigger);
            yield return null;
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;

            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(rig.Caster.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(rig.Caster.Unit.PendingCommands[0], Is.TypeOf<AbilityCommand>());
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max), "Paused: nothing ran");
        }

        [UnityTest]
        public IEnumerator Paused_ThePadPlansTheAbility_AndItRunsAfterResume()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            rig.Pause.Pause();
            yield return PickWithTheMenu(pad.dpad.up);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;

            yield return Tap(pad.buttonSouth);
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<AbilityCommand>());
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max));

            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 45));
        }

        [UnityTest]
        public IEnumerator DpadCycling_WhileMendIsArmed_WalksTheFriendlies_AndNeverSetsTheSoftTarget()
        {
            yield return null;
            yield return PickWithTheMenu(pad.dpad.down);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Caster.transform.position));
            yield return null;
            Assert.That(rig.Cursor.Target.Friendly, Is.SameAs(rig.Caster), "Precondition");

            yield return Tap(pad.dpad.right);

            Assert.That(rig.Cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(rig.Cursor.Target.Friendly, Is.SameAs(rig.Ally));
            Assert.That(rig.Cursor.SoftTarget, Is.Null, "Cycling friendlies never sets the soft target Attack uses");
            Assert.That(rig.Targeting.Preview.Target, Is.SameAs(rig.AllyHealth));
        }

        [UnityTest]
        public IEnumerator Attack_WhileAnAbilityIsArmed_DisarmsIt_ThenAttacksTheBestHostile()
        {
            yield return null;
            yield return PickWithTheMenu(pad.dpad.up);
            Assert.That(rig.Targeting.IsArmed, Is.True, "Precondition");
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;

            yield return Tap(pad.buttonWest);

            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Cursor.Aiming, Is.False);
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.None));
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<AttackCommand>(), "The plain attack still goes out");
            Assert.That(((AttackCommand)rig.Caster.Unit.CurrentCommand).Target, Is.SameAs(rig.Hostile));
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max), "No ability was used");
        }

        [UnityTest]
        public IEnumerator ChangingTheCaster_WhileArmed_DisarmsAndResetsTheCursor()
        {
            yield return null;
            yield return PickWithTheMenu(pad.dpad.up);
            Assert.That(rig.Targeting.IsArmed, Is.True, "Precondition");

            rig.Selection.Select(rig.Ally);
            yield return null;
            yield return null;

            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Cursor.Aiming, Is.False);
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.None));
        }

        [UnityTest]
        public IEnumerator TheCasterDying_WhileArmed_DisarmsAndResetsTheCursor()
        {
            yield return null;
            yield return PickWithTheMenu(pad.dpad.up);
            Assert.That(rig.Targeting.IsArmed, Is.True, "Precondition");

            rig.CasterHealth.TakeDamage(rig.CasterHealth.Max);
            yield return null;
            yield return null;

            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Cursor.Aiming, Is.False);
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.None));
        }
    }
}
#endif
