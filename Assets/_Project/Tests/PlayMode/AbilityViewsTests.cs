#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AbilityViewsTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        AbilityRig rig;
        AbilityBarView bar;
        AbilityTargetingView view;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = AbilityRig.Build(actions, false);
            var views = rig.World.Track(new GameObject("Views"));
            bar = views.AddComponent<AbilityBarView>();
            bar.Initialize(rig.Targeting, null, actions, null);
            view = views.AddComponent<AbilityTargetingView>();
            view.Initialize(rig.Targeting, null);
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

        [UnityTest]
        public IEnumerator TheBar_ListsEverySlot_AndMarksTheArmedOne()
        {
            yield return null;
            yield return null;

            var idle = bar.BuildLines().ToArray();
            Assert.That(idle[0], Is.EqualTo($"Abilities: {rig.Caster.name}"));
            Assert.That(idle[1], Is.EqualTo("  [1] Aimed Shot  1  ready"));
            Assert.That(idle[2], Is.EqualTo("  [2] Blast  2  ready"));
            Assert.That(idle[3], Is.EqualTo("  [3] Mend  3  ready"));
            Assert.That(idle, Has.Length.EqualTo(4), "Nothing armed: no preview line");

            yield return Tap(keyboard.digit2Key);
            var armed = bar.BuildLines().ToArray();
            Assert.That(armed[2], Does.StartWith("> [2] Blast"));
            Assert.That(armed[armed.Length - 1], Does.StartWith("Blast @ ("), "The pointer (at 0,0) is on the ground, so a ground ability already has an aim");

            yield return Tap(keyboard.digit1Key);
            var unitArmed = bar.BuildLines().ToArray();
            Assert.That(unitArmed[1], Does.StartWith("> [1] Aimed Shot"));
            Assert.That(unitArmed[unitArmed.Length - 1], Is.EqualTo("Aimed Shot: choose a target"), "Bare ground is no target for a unit ability");
        }

        [UnityTest]
        public IEnumerator TheBar_ShowsTheCooldown_AndTheQueuedAndRunningOrders()
        {
            yield return null;
            rig.Pause.Pause();
            Assert.That(rig.Caster.Unit.Issue(AbilityCommand.OnUnit(rig.Aimed, rig.Hostile)), Is.True);
            Assert.That(rig.Caster.Unit.Issue(AbilityCommand.AtGround(rig.Blast, AbilityRig.BlastGround), IssueMode.Append), Is.True);
            yield return null;

            var lines = bar.BuildLines().ToArray();
            Assert.That(lines, Has.Member($"Casting: Aimed Shot -> {rig.Hostile.name}"));
            Assert.That(lines, Has.Member("Queued: Blast @ (5.0, 2.0)"));

            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => rig.Caster.Unit.CurrentCommand == null, 3f);
            Assert.That(bar.BuildLines().ToArray()[1], Does.Contain("CD "));
        }

        [UnityTest]
        public IEnumerator ARefusedClick_ShowsItsReasonOnTheBar()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);
            Set(mouse.position, rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(bar.BuildLines().Last(), Does.Contain("out of range"), "The preview already says why");

            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;

            Assert.That(bar.BuildLines().ToArray(), Has.Member("Aimed Shot: out of range"));
        }

        [UnityTest]
        public IEnumerator TheRangeCircle_IsDrawnAtTheAbilitysRangeAroundTheCaster_OnlyWhileArmed()
        {
            yield return null;
            yield return null;
            Assert.That(view.IsShowingRange, Is.False);

            yield return Tap(keyboard.digit1Key);
            yield return null;

            Assert.That(view.IsShowingRange, Is.True);
            var casterAt = rig.Caster.transform.position;
            for (var i = 0; i < view.RangeCircle.positionCount; i += 8)
                Assert.That(CoverRules.FlatDistance(view.RangeCircle.GetPosition(i), casterAt), Is.EqualTo(14f).Within(0.05f));

            yield return Tap(keyboard.escapeKey);
            yield return null;
            Assert.That(view.IsShowingRange, Is.False);
            Assert.That(view.IsShowingAim, Is.False);
        }

        [UnityTest]
        public IEnumerator TheAimRing_IsGreenWhenValid_AndRedWhenNot()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            Set(mouse.position, rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(view.IsShowingAim, Is.True);
            Assert.That(view.AimColor, Is.EqualTo(AbilityTargetingView.ValidColor));

            Set(mouse.position, rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(view.AimColor, Is.EqualTo(AbilityTargetingView.InvalidColor));
        }

        [UnityTest]
        public IEnumerator ABlastRing_HasTheBlastRadius_AroundTheAimPoint()
        {
            yield return null;
            yield return Tap(keyboard.digit2Key);

            Set(mouse.position, rig.ScreenPointOf(AbilityRig.BlastGround));
            yield return null;
            yield return null;

            Assert.That(view.IsShowingAim, Is.True);
            var aim = rig.Targeting.Preview.Point;
            Assert.That(CoverRules.FlatDistance(view.AimCircle.GetPosition(0), aim), Is.EqualTo(3f).Within(0.05f));
            Assert.That(CoverRules.FlatDistance(view.AimCircle.GetPosition(12), aim), Is.EqualTo(3f).Within(0.05f));
        }

        [UnityTest]
        public IEnumerator TheViews_HaveNoColliders_SoTheyNeverBlockAClick()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);
            yield return null;

            Assert.That(view.GetComponentsInChildren<Collider>(true), Is.Empty);
        }

        [UnityTest]
        public IEnumerator TheQueueView_DrawsAnAbilityOrder_AndAMarkerForAGroundAim()
        {
            yield return null;
            var queueView = rig.Caster.Unit.gameObject.AddComponent<CommandQueueView>();
            rig.Pause.Pause();
            yield return null;
            Assert.That(queueView.LinePointCount, Is.EqualTo(0));

            Assert.That(rig.Caster.Unit.Issue(AbilityCommand.OnUnit(rig.Aimed, rig.Hostile)), Is.True);
            yield return null;
            Assert.That(queueView.LinePointCount, Is.EqualTo(2), "The unit and the target");
            Assert.That(queueView.ActiveMarkerCount, Is.EqualTo(0));

            Assert.That(rig.Caster.Unit.Issue(AbilityCommand.AtGround(rig.Blast, AbilityRig.BlastGround), IssueMode.Append), Is.True);
            yield return null;
            Assert.That(queueView.LinePointCount, Is.EqualTo(3));
            Assert.That(queueView.ActiveMarkerCount, Is.EqualTo(1), "A ground aim gets a disc");
        }
    }
}
#endif
