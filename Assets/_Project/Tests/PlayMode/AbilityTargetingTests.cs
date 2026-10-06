#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AbilityTargetingTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        AbilityRig rig;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = AbilityRig.Build(actions, false);
        }

        public override void TearDown()
        {
            rig.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(ButtonControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        IEnumerator LeftClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DigitOne_ArmsTheFirstAbility_PressingItAgainDisarms_AndAnotherDigitSwitches()
        {
            yield return null;
            Assert.That(rig.Targeting.IsArmed, Is.False);

            yield return Tap(keyboard.digit1Key);
            Assert.That(rig.Targeting.IsArmed, Is.True);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(0));
            Assert.That(rig.Targeting.ArmedAbility, Is.SameAs(rig.Aimed));
            Assert.That(rig.Targeting.Caster, Is.SameAs(rig.Caster.Unit), "No selection: the active character casts");

            yield return Tap(keyboard.digit2Key);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(1));
            Assert.That(rig.Targeting.ArmedAbility, Is.SameAs(rig.Blast));

            yield return Tap(keyboard.digit2Key);
            Assert.That(rig.Targeting.IsArmed, Is.False, "The same key again backs out");
        }

        [UnityTest]
        public IEnumerator AnEmptySlot_ArmsNothing()
        {
            yield return null;

            yield return Tap(keyboard.digit4Key);

            Assert.That(rig.Targeting.IsArmed, Is.False);
        }

        [UnityTest]
        public IEnumerator ClickingAHostileInRange_UsesAimedShot_AndDisarms_WithoutSelectingAnything()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);

            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 45));
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Selection.Selected, Is.Empty, "The click picked a target, it did not select or order");
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator AUnitAbility_ClickedOnTheGround_ReportsNoTarget_AndStaysArmed()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(new Vector3(8f, 0f, -2f)));

            Assert.That(rig.Abilities.LastFailure, Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(rig.Targeting.IsArmed, Is.True);
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max));
        }

        [UnityTest]
        public IEnumerator AnAttack_ClickedOnAFriendly_IsRefusedAsTheWrongSide()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.Ally.transform.position));

            Assert.That(rig.Abilities.LastFailure, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(rig.AllyHealth.Current, Is.EqualTo(rig.AllyHealth.Max));
            Assert.That(rig.Targeting.IsArmed, Is.True);
            Assert.That(rig.Selection.Selected, Is.Empty, "While armed a click on a friendly does not select it");
        }

        // Decision 029 (the mouse path): out of range is accepted and the caster walks into range, as a click on a hostile
        // makes a unit walk to attack it.
        [UnityTest]
        public IEnumerator AHostileOutOfRange_IsAccepted_TheCasterWalksIntoRange_AndFires()
        {
            yield return null;
            rig.Pause.Pause();   // the click lands while paused, so the order can be seen before anything moves
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.FarHostile.transform.position));

            Assert.That(rig.Targeting.IsArmed, Is.False, "Accepted: disarmed");
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<AbilityCommand>());
            Assert.That(rig.Abilities.LastFailure, Is.EqualTo(AbilityFailure.None), "Not refused");
            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => rig.FarHostile.Current < rig.FarHostile.Max, 5f);

            Assert.That(rig.FarHostile.Current, Is.EqualTo(rig.FarHostile.Max - 45));
            Assert.That(TestWorld.HorizontalDistance(rig.Caster.transform.position, AbilityRig.CasterGround), Is.GreaterThan(1f), "It walked");
        }

        // Ruling R10 (the mouse path): while the caster is being steered, a far hostile is previewed with its reason and the
        // click is refused with it; the ability stays armed. Released, the same target previews as an approach.
        [UnityTest]
        public IEnumerator WhileSteering_AHostileOutOfRange_IsPreviewedAndRefusedWithItsReason_AndStaysArmed()
        {
            yield return null;
            rig.Caster.Unit.SetMoveIntent(Vector3.left);
            yield return Tap(keyboard.digit1Key);
            Set(mouse.position, rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.Failure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(rig.Targeting.Preview.WillApproach, Is.False, "Steering: it would be refused, not walked to");

            yield return LeftClickAt(rig.ScreenPointOf(rig.FarHostile.transform.position));

            Assert.That(rig.Abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(rig.Targeting.IsArmed, Is.True, "Still armed: try another target or let go of the key");
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.Null);

            rig.Caster.Unit.SetMoveIntent(Vector3.zero);
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.WillApproach, Is.True, "Not steering: confirming would walk into range");
            Assert.That(rig.FarHostile.Current, Is.EqualTo(rig.FarHostile.Max));
        }

        [UnityTest]
        public IEnumerator Blast_ClickedOnTheGround_HitsOnlyTheHostilesNearThePoint()
        {
            yield return null;
            yield return Tap(keyboard.digit2Key);

            yield return LeftClickAt(rig.ScreenPointOf(AbilityRig.BlastGround));
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);

            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 35), "2 m from the blast point");
            Assert.That(rig.FarHostile.Current, Is.EqualTo(rig.FarHostile.Max));
            Assert.That(rig.AllyHealth.Current, Is.EqualTo(rig.AllyHealth.Max));
            Assert.That(rig.CasterHealth.Current, Is.EqualTo(rig.CasterHealth.Max));
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Abilities.IsReady(1), Is.False);
        }

        [UnityTest]
        public IEnumerator Mend_ClickedOnAnAlly_HealsThem()
        {
            yield return null;
            rig.AllyHealth.TakeDamage(60);
            yield return Tap(keyboard.digit3Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.Ally.transform.position));
            yield return TestWorld.WaitUntil(() => rig.AllyHealth.Current > rig.AllyHealth.Max - 60, 2f);

            Assert.That(rig.AllyHealth.Current, Is.EqualTo(rig.AllyHealth.Max - 20));
        }

        [UnityTest]
        public IEnumerator Escape_DisarmsFirst_AndOnlyTheNextEscapeClearsTheSelection()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            yield return Tap(keyboard.digit1Key);

            yield return Tap(keyboard.escapeKey);
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Selection.Selected, Has.Count.EqualTo(1), "The first Escape only backed out of the ability");

            yield return Tap(keyboard.escapeKey);
            Assert.That(rig.Selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ChangingTheSelection_DisarmsTheAbility()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            yield return Tap(keyboard.digit1Key);
            Assert.That(rig.Targeting.IsArmed, Is.True);

            rig.Selection.Select(rig.Ally);
            yield return null;

            Assert.That(rig.Targeting.IsArmed, Is.False, "A different caster: nothing stays armed");
            Assert.That(rig.Targeting.Caster, Is.SameAs(rig.Ally.Unit));
        }

        [UnityTest]
        public IEnumerator TheCasterDying_DisarmsTheAbility()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            rig.CasterHealth.TakeDamage(rig.CasterHealth.Max);
            yield return null;
            yield return null;

            Assert.That(rig.Targeting.IsArmed, Is.False);
            yield return Tap(keyboard.digit1Key);
            Assert.That(rig.Targeting.IsArmed, Is.False, "The ally who inherits the role has no abilities");
        }

        [UnityTest]
        public IEnumerator Paused_AClickQueuesTheAbility_ButNothingRunsUntilTheGameResumes()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            rig.Pause.Pause();
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<AbilityCommand>());
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max), "Nothing runs while paused");
            Assert.That(rig.Abilities.IsReady(0), Is.True);

            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 45));
        }

        [UnityTest]
        public IEnumerator ShiftClick_QueuesTheAbilityBehindTheCastersOtherOrders()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            rig.Pause.Pause();
            Assert.That(rig.Caster.Unit.Issue(new MoveCommand(new Vector3(-6f, 0f, -8f))), Is.True);
            yield return Tap(keyboard.digit1Key);

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(rig.ScreenPointOf(rig.Hostile.transform.position));
            Release(keyboard.leftShiftKey);
            yield return null;

            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(rig.Caster.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(rig.Caster.Unit.PendingCommands[0], Is.TypeOf<AbilityCommand>());
        }

        [UnityTest]
        public IEnumerator ThePreview_FollowsTheMouse_AndSaysWhy()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            Set(mouse.position, rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.IsValid, Is.True);
            Assert.That(rig.Targeting.Preview.Target, Is.SameAs(rig.Hostile));
            Assert.That(rig.Targeting.Preview.Check.Distance, Is.EqualTo(Mathf.Sqrt(9f + 64f)).Within(0.05f));

            Set(mouse.position, rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.IsValid, Is.False);
            Assert.That(rig.Targeting.Preview.Failure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(rig.Targeting.Preview.WillApproach, Is.True, "Not usable from here, but confirming walks into range");

            Set(mouse.position, rig.ScreenPointOf(new Vector3(8f, 0f, -2f)));
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.HasAim, Is.False);
            Assert.That(rig.Targeting.Preview.Failure, Is.EqualTo(AbilityFailure.NoTarget));
        }

        [UnityTest]
        public IEnumerator ABlastPreview_ListsTheHostilesItWouldHit()
        {
            yield return null;
            yield return Tap(keyboard.digit2Key);

            Set(mouse.position, rig.ScreenPointOf(AbilityRig.BlastGround));
            yield return null;
            yield return null;

            Assert.That(rig.Targeting.Preview.IsValid, Is.True);
            Assert.That(rig.Targeting.AreaHits, Is.EqualTo(new[] { rig.Hostile }));
        }

        [UnityTest]
        public IEnumerator Evaluate_ATargetBehindAWall_HasNoLineOfSight()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            var preview = rig.Targeting.Evaluate(rig.Aimed, PointerTarget.OnHostile(rig.WalledHostile, rig.WalledHostile.transform.position), false);

            Assert.That(preview.Failure, Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(preview.Check.SightClear, Is.False);
            Assert.That(preview.WillApproach, Is.True, "Confirming walks to a firing position");
        }

        [UnityTest]
        public IEnumerator HoldingTheQueueKey_WhileTheCasterHasOrders_PreviewsOnlyTheStaticChecks()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            Assert.That(rig.Caster.Unit.Issue(new MoveCommand(new Vector3(-6f, 0f, -8f))), Is.True);
            yield return Tap(keyboard.digit1Key);
            Set(mouse.position, rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.Failure, Is.EqualTo(AbilityFailure.OutOfRange), "Replacing the order: judged from here");

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return null;

            Assert.That(rig.Targeting.Preview.Queued, Is.True);
            Assert.That(rig.Targeting.Preview.IsValid, Is.True, "Queued: range is judged when it runs");
            Release(keyboard.leftShiftKey);
            yield return null;
        }
    }
}
#endif
