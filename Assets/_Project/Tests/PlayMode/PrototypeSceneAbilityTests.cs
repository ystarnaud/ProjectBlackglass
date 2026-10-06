#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XInput;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The abilities in the real arena. The squad starts about 28 m from the hostiles, so a test that needs a hostile in
    /// range stands one next to the caster (agent.Warp, its AI off so it neither moves nor fights while the test aims).
    /// Only an Xbox-style simulated pad is used: a simulated DualSense discards delta events (see PrototypeSceneControllerTests).
    /// </summary>
    public class PrototypeSceneAbilityTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        CommandableUnit[] squad;
        EnemyAI[] hostiles;
        TacticalPause pause;
        UnitSelection selection;
        Encounter encounter;
        AbilityTargeting targeting;
        TacticalCursor cursor;
        ActiveInputDevice family;
        Camera viewCamera;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            Time.timeScale = 1f;
            PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            squad = PrototypeSceneTests.FindSquad();
            hostiles = PrototypeSceneTests.FindHostiles();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            encounter = Object.FindFirstObjectByType<Encounter>();
            targeting = Object.FindFirstObjectByType<AbilityTargeting>();
            cursor = Object.FindFirstObjectByType<TacticalCursor>();
            family = Object.FindFirstObjectByType<ActiveInputDevice>();
            viewCamera = Camera.main;
            Assert.That(targeting, Is.Not.Null, "The scene has no AbilityTargeting: run the scene builder");
        }

        static UnitAbilities AbilitiesOf(CommandableUnit unit) => unit.GetComponent<UnitAbilities>();
        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        Vector2 ScreenPointOf(Vector3 world) => viewCamera.WorldToScreenPoint(world);

        bool OnScreen(Vector3 world)
        {
            var p = viewCamera.WorldToScreenPoint(world);
            return p.z > 0f && p.x > 20f && p.x < viewCamera.pixelWidth - 20f && p.y > 20f && p.y < viewCamera.pixelHeight - 20f;
        }

        void Select(int index) => selection.Select(squad[index].GetComponent<SelectableUnit>());

        // Stands a hostile `distance` metres from the caster on open, visible, on-screen ground, with its AI off.
        Health BringHostileNear(EnemyAI hostile, CommandableUnit caster, float distance)
        {
            hostile.enabled = false;
            hostile.GetComponent<CommandableUnit>().Issue(new StopCommand());
            var attacker = caster.GetComponent<UnitAttacker>();
            var ground = caster.transform.position - Vector3.up;
            for (var step = 0; step < 16; step++)
            {
                var angle = step * Mathf.PI * 2f / 16f;
                var candidate = ground + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
                if (!NavMesh.SamplePosition(candidate, out var hit, 0.5f, NavMesh.AllAreas))
                    continue;
                if (!attacker.HasLineOfSightToPoint(hit.position + Vector3.up) || !OnScreen(hit.position + Vector3.up))
                    continue;
                hostile.GetComponent<NavMeshAgent>().Warp(hit.position);
                return HealthOf(hostile);
            }
            Assert.Fail($"No open, visible spot {distance} m from {caster.name}: change the probe distance");
            return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
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
        public IEnumerator Scene_EveryFriendly_HasTheThreeAbilities_AndACooldownOfItsOwn_AndHostilesHaveNone()
        {
            yield return LoadScene();

            foreach (var unit in squad)
            {
                var abilities = AbilitiesOf(unit);
                Assert.That(abilities, Is.Not.Null, $"{unit.name} has no UnitAbilities");
                Assert.That(Enumerable.Range(0, abilities.Count).Select(i => abilities.Definition(i).DisplayName),
                    Is.EqualTo(new[] { "Aimed Shot", "Blast", "Mend" }), unit.name);
            }
            foreach (var hostile in hostiles)
                Assert.That(hostile.GetComponent<UnitAbilities>(), Is.Null, $"{hostile.name} keeps using basic attacks only");

            var first = AbilitiesOf(squad[0]);
            var second = AbilitiesOf(squad[1]);
            Assert.That(first.Definition(2), Is.SameAs(second.Definition(2)), "One shared Mend definition");
            Assert.That(first.TryUse(AbilityCommand.OnUnit(first.Definition(2), HealthOf(squad[0]))), Is.True);
            Assert.That(first.IsReady(2), Is.False);
            Assert.That(second.IsReady(2), Is.True, "The second unit's Mend is not on cooldown");
        }

        [UnityTest]
        public IEnumerator Scene_TheArchetypes_GiveTheThreeFriendliesDifferentRoles()
        {
            yield return LoadScene();
            var melee = squad[0].GetComponent<UnitAttacker>();
            var marksman = squad[1].GetComponent<UnitAttacker>();
            var ranged = squad[2].GetComponent<UnitAttacker>();

            Assert.That(melee.Archetype.DisplayName, Is.EqualTo("Melee"));
            Assert.That(melee.Role, Is.EqualTo(CombatRole.Melee));
            Assert.That((melee.Range, melee.Damage, melee.Cooldown), Is.EqualTo((2f, 25, 1f)));
            Assert.That(ranged.Archetype.DisplayName, Is.EqualTo("Ranged"));
            Assert.That((ranged.Range, ranged.Damage, ranged.Cooldown), Is.EqualTo((8f, 15, 1f)));
            Assert.That(marksman.Archetype.DisplayName, Is.EqualTo("Marksman"));
            Assert.That(marksman.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That((marksman.Range, marksman.Damage, marksman.Cooldown), Is.EqualTo((16f, 40, 2.5f)));
            Assert.That(marksman.NeedsLineOfSight, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_AimedShot_ByKeyboardAndMouse_HitsAHostileInRange()
        {
            yield return LoadScene();
            Select(1);
            var hostile = BringHostileNear(hostiles[0], squad[1], 9f);
            var before = hostile.Current;
            yield return null;

            yield return Tap(keyboard.digit1Key);
            Assert.That(targeting.ArmedAbility.DisplayName, Is.EqualTo("Aimed Shot"));
            yield return LeftClickAt(ScreenPointOf(hostile.transform.position));
            yield return TestWorld.WaitUntil(() => hostile.Current < before, 2f);

            Assert.That(hostile.Current, Is.LessThanOrEqualTo(before - 45));
            Assert.That(AbilitiesOf(squad[1]).IsReady(0), Is.False);
            Assert.That(targeting.IsArmed, Is.False);
        }

        [UnityTest]
        public IEnumerator Scene_Blast_OnTheGround_HitsTheHostileNearThePoint_AndNoFriendly()
        {
            yield return LoadScene();
            Select(0);
            var hostile = BringHostileNear(hostiles[1], squad[0], 8f);
            var before = hostile.Current;
            var attacker = squad[0].GetComponent<UnitAttacker>();
            var spot = hostile.transform.position - Vector3.up;
            Vector3? aim = null;
            foreach (var offset in new[] { new Vector3(1.5f, 0f, 0f), new Vector3(-1.5f, 0f, 0f), new Vector3(0f, 0f, 1.5f), new Vector3(0f, 0f, -1.5f) })
            {
                var candidate = spot + offset;
                if (OnScreen(candidate) && attacker.HasLineOfSightToPoint(candidate + Vector3.up))
                {
                    aim = candidate;
                    break;
                }
            }
            Assert.That(aim.HasValue, Is.True, "No open ground point beside the hostile: change the probe offsets");
            yield return null;

            yield return Tap(keyboard.digit2Key);
            yield return LeftClickAt(ScreenPointOf(aim.Value));
            yield return TestWorld.WaitUntil(() => hostile.Current < before, 2f);

            Assert.That(hostile.Current, Is.LessThanOrEqualTo(before - 35));
            foreach (var friendly in squad)
                Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max), $"{friendly.name} is never hit by its own side's blast");
        }

        [UnityTest]
        public IEnumerator Scene_Mend_HealsAFriendlyClickedWithTheMouse()
        {
            yield return LoadScene();
            Select(0);
            var ally = HealthOf(squad[2]);
            ally.TakeDamage(50);
            Assert.That(OnScreen(squad[2].transform.position), Is.True, "Precondition: the ally is on screen");
            yield return null;

            yield return Tap(keyboard.digit3Key);
            yield return LeftClickAt(ScreenPointOf(squad[2].transform.position));
            yield return TestWorld.WaitUntil(() => ally.Current > ally.Max - 50, 2f);

            Assert.That(ally.Current, Is.EqualTo(ally.Max - 10));
        }

        // Decision 029: an ability out of range is accepted and the caster walks into range and fires, in the real arena
        // (the hostile stands still, its AI off, so only the approach can bring it into range).
        [UnityTest]
        public IEnumerator Scene_AnAbilityOutOfRange_IsAccepted_TheCasterWalksIntoRange_AndFires()
        {
            yield return LoadScene();
            Select(0);
            var hostileAi = hostiles[0];
            hostileAi.enabled = false;
            hostileAi.GetComponent<CommandableUnit>().Issue(new StopCommand());
            var hostile = HealthOf(hostileAi);
            var abilities = AbilitiesOf(squad[0]);
            var aimed = abilities.Definition(0);
            Assert.That(abilities.Check(aimed, hostile, null).Failure, Is.EqualTo(AbilityFailure.OutOfRange), "Precondition: about 28 m away");
            var distanceWhenUsed = -1f;
            abilities.Used += _ => distanceWhenUsed = CoverRules.FlatDistance(squad[0].transform.position, hostile.transform.position);
            yield return null;
            yield return Tap(keyboard.digit1Key);

            Assert.That(targeting.Confirm(PointerTarget.OnHostile(hostile, hostile.transform.position), false), Is.True);

            Assert.That(targeting.IsArmed, Is.False);
            Assert.That(squad[0].CurrentCommand, Is.TypeOf<AbilityCommand>());
            yield return TestWorld.WaitUntil(() => squad[0].CurrentCommand == null, 20f);

            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45).Or.EqualTo(hostile.Max), "the shot may meet cover");
            Assert.That(abilities.UsedCount, Is.EqualTo(1), "it walked into range and fired");
            Assert.That(distanceWhenUsed, Is.LessThanOrEqualTo(aimed.Range));
            Assert.That(abilities.IsReady(0), Is.False);
        }

        [UnityTest]
        public IEnumerator Scene_PausedPlanning_MoveThenMendThenMove_RunsInOrderAfterResume()
        {
            yield return LoadScene();
            Select(1);
            var unit = squad[1];
            var patient = HealthOf(squad[0]);
            patient.TakeDamage(50);
            var start = unit.transform.position;
            var first = start + new Vector3(0f, 0f, 3f);
            var last = first + new Vector3(3f, 0f, 0f);
            yield return null;

            pause.Pause();
            Assert.That(unit.Issue(new MoveCommand(first)), Is.True);
            yield return Tap(keyboard.digit3Key);
            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(ScreenPointOf(squad[0].transform.position));
            Release(keyboard.leftShiftKey);
            yield return null;
            Assert.That(unit.Issue(new MoveCommand(last), IssueMode.Append), Is.True);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(unit.PendingCommands.Select(c => c.GetType()), Is.EqualTo(new[] { typeof(AbilityCommand), typeof(MoveCommand) }));
            Assert.That(patient.Current, Is.EqualTo(patient.Max - 50), "Nothing ran while paused");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.05f));

            var positionWhenUsed = Vector3.zero;
            AbilitiesOf(unit).Used += _ => positionWhenUsed = unit.transform.position;
            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 20f);

            Assert.That(patient.Current, Is.EqualTo(patient.Max - 10), "The Mend ran");
            Assert.That(TestWorld.HorizontalDistance(positionWhenUsed, first), Is.LessThan(1f), "It ran after the first move");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, last), Is.LessThan(1.5f), "and the last move followed");
        }

        [UnityTest]
        public IEnumerator Scene_ThePad_TriggerPlusDpad_ArmsMend_TheCursorSnapsAndConfirmHeals_WithoutStoppingTheUnit()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Tap(pad.selectButton);
            Assert.That(family.Family, Is.EqualTo(InputFamily.Xbox));
            Select(0);
            Assert.That(squad[0].Issue(new MoveCommand(squad[0].transform.position + new Vector3(0f, 0f, 6f))), Is.True);
            var ally = HealthOf(squad[2]);
            ally.TakeDamage(50);
            Assert.That(OnScreen(squad[2].transform.position), Is.True, "Precondition: the ally is on screen");

            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(pad.dpad.down);
            Release(pad.rightTrigger);
            yield return null;
            yield return null;

            Assert.That(targeting.ArmedAbility.DisplayName, Is.EqualTo("Mend"));
            Assert.That(squad[0].StopCount, Is.EqualTo(0), "The held trigger made the D-pad pick, not stop");
            Assert.That(cursor.IsActive, Is.True, "Aiming gives the cursor the stick while the game runs");
            cursor.SetScreenPosition(ScreenPointOf(squad[2].transform.position));
            yield return null;
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));

            yield return Tap(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => ally.Current > ally.Max - 50, 2f);

            Assert.That(ally.Current, Is.EqualTo(ally.Max - 10));
            Assert.That(cursor.Aiming, Is.False);
            Assert.That(cursor.IsActive, Is.False, "The camera has its stick back");
        }

        [UnityTest]
        public IEnumerator Scene_SwitchingFromPadToMouseWhileAiming_KeepsTheAbilityArmed_AndTheMouseThenCasts()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Tap(pad.selectButton);
            Select(0);
            var ally = HealthOf(squad[2]);
            ally.TakeDamage(50);
            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(pad.dpad.down);
            Release(pad.rightTrigger);
            yield return null;
            Assert.That(targeting.IsArmed, Is.True);

            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            yield return null;

            Assert.That(family.Family, Is.EqualTo(InputFamily.KeyboardMouse), "The mouse took the input back");
            Assert.That(targeting.IsArmed, Is.True, "Still armed");
            Assert.That(ally.Current, Is.EqualTo(ally.Max - 50), "The click that woke the mouse did not cast");

            yield return LeftClickAt(ScreenPointOf(squad[2].transform.position));
            yield return TestWorld.WaitUntil(() => ally.Current > ally.Max - 50, 2f);

            Assert.That(ally.Current, Is.EqualTo(ally.Max - 10));
            Assert.That(targeting.IsArmed, Is.False);
            Assert.That(cursor.Aiming, Is.False);
        }

        [UnityTest]
        public IEnumerator Scene_AbilityKills_CountTowardsVictory()
        {
            yield return LoadScene();
            Select(1);
            var target = BringHostileNear(hostiles[0], squad[1], 9f);
            HealthOf(hostiles[1]).TakeDamage(HealthOf(hostiles[1]).Max);
            HealthOf(hostiles[2]).TakeDamage(HealthOf(hostiles[2]).Max);
            var abilities = AbilitiesOf(squad[1]);
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(abilities.Definition(0), target)), Is.True);
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(abilities.Definition(1), target.transform.position - Vector3.up)), Is.True);

            Assert.That(target.IsAlive, Is.False, "45 + 35 against 60 hit points");
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory));
        }
    }
}
#endif
