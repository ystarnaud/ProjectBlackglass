using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitAbilityPlayModeTests
    {
        TestWorld world;
        TacticalPause pause;
        Encounter encounter;
        AbilityDefinition aimed;
        AbilityDefinition blast;
        AbilityDefinition mend;
        CommandableUnit caster;
        Health casterHealth;
        UnitAbilities abilities;
        Health ally;
        Health hostile;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment((new Vector3(-3f, 1.5f, -2f), new Vector3(4f, 3f, 0.5f)));
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            encounter = world.CreateEncounter();
            aimed = world.CreateAimedShot();
            blast = world.CreateBlast();
            mend = world.CreateMend();
            caster = world.CreateFighter(new Vector3(0f, 0f, -6f));
            casterHealth = caster.GetComponent<Health>();
            abilities = world.AddAbilities(caster, encounter, aimed, blast, mend);
            ally = world.CreateFighter(new Vector3(4f, 0f, -6f)).GetComponent<Health>();
            hostile = world.CreateDummy(new Vector3(3f, 0f, 2f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        IEnumerator WaitUntilIdle() => TestWorld.WaitUntil(() => caster.CurrentCommand == null, 15f);

        [UnityTest]
        public IEnumerator AnAbilityOnAnIdleUnit_RunsOnTheNextFrame_AndTheUnitIsIdleAgain()
        {
            yield return null;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            Assert.That(caster.CurrentCommand, Is.TypeOf<AbilityCommand>(), "Accepted and waiting for the next running frame");
            yield return WaitUntilIdle();

            Assert.That(caster.CurrentCommand, Is.Null);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45));
            Assert.That(abilities.IsReady(0), Is.False);
        }

        [UnityTest]
        public IEnumerator AnAbilityIssuedWhilePaused_IsAccepted_ButNothingRunsUntilTheGameResumes()
        {
            yield return null;
            pause.Pause();

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(hostile.Current, Is.EqualTo(hostile.Max), "Nothing may run while paused");
            Assert.That(caster.CurrentCommand, Is.TypeOf<AbilityCommand>());
            Assert.That(abilities.IsReady(0), Is.True, "No cooldown started");

            pause.Resume();
            yield return WaitUntilIdle();
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45));
        }

        [UnityTest]
        public IEnumerator AnAbilityThatCannotStartNow_IsRejectedWithItsReason_AndTheOrdersAreUnchanged()
        {
            yield return null;
            var far = world.CreateDummy(new Vector3(15f, 0f, 15f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });
            var move = new MoveCommand(new Vector3(-6f, 0f, -6f));
            Assert.That(caster.Issue(move), Is.True);

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far), IssueMode.Replace), Is.False);

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(caster.CurrentCommand, Is.SameAs(move));
            Assert.That(caster.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator AnAbilityOnACooldown_IsRejectedAtIssue()
        {
            yield return null;
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            yield return WaitUntilIdle();

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OnCooldown));
        }

        [UnityTest]
        public IEnumerator AUnitWithoutAbilities_RejectsAnAbilityOrder()
        {
            yield return null;
            var plain = world.CreateFighter(new Vector3(-6f, 0f, -6f));

            Assert.That(plain.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(plain.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ADeadCaster_TakesNoAbilityOrders()
        {
            yield return null;
            casterHealth.TakeDamage(casterHealth.Max);

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
        }

        [UnityTest]
        public IEnumerator AnAbilityQueuedBehindAMove_IsAcceptedEvenOutOfRangeNow_AndFailsCleanlyWhenItRuns_ThenTheNextOrderStillRuns()
        {
            yield return null;
            var far = world.CreateDummy(new Vector3(15f, 0f, 15f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });
            var first = new Vector3(-6f, 0f, -6f);
            var last = new Vector3(6f, 0f, -10f);

            Assert.That(caster.Issue(new MoveCommand(first)), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far), IssueMode.Append), Is.True, "Only the static checks apply behind other orders");
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);
            yield return WaitUntilIdle();

            Assert.That(far.Current, Is.EqualTo(far.Max), "It was out of range when it ran");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.IsReady(0), Is.True, "A failure costs no cooldown");
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f), "The next order still ran");
        }

        [UnityTest]
        public IEnumerator AQueuedAbility_WhoseTargetDiesBeforeItRuns_FailsAndTheNextOrderStillRuns()
        {
            yield return null;
            var last = new Vector3(6f, 0f, -10f);
            Assert.That(caster.Issue(new MoveCommand(new Vector3(-6f, 0f, -6f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);

            hostile.TakeDamage(hostile.Max);
            yield return WaitUntilIdle();

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.TargetDead));
            Assert.That(abilities.UsedCount, Is.EqualTo(0));
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator AQueuedAbility_WhoseTargetWalksOutOfRange_FailsWithOutOfRange()
        {
            yield return null;
            var last = new Vector3(6f, 0f, -10f);
            Assert.That(caster.Issue(new MoveCommand(new Vector3(3f, 0f, -6f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);

            hostile.transform.position = new Vector3(15f, 1f, 15f);
            yield return WaitUntilIdle();

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator AQueuedAbility_WhoseLineGetsBlocked_FailsWithNoLineOfSight()
        {
            yield return null;
            var last = new Vector3(6f, 0f, -10f);
            Assert.That(caster.Issue(new MoveCommand(new Vector3(3f, 0f, -6f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);

            world.CreateObstacle(new Vector3(3f, 1.5f, -2f), new Vector3(4f, 3f, 0.5f));
            yield return WaitUntilIdle();

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator AQueuedAbility_RunsFromWhereTheUnitEndsUp_NotWhereItWasQueued()
        {
            yield return null;
            // From the start (0, -6) the far target is 16 m away (range 14); from the move's end (4, -8) it is 12.2 m away.
            var far = world.CreateDummy(new Vector3(16f, 0f, -6f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });
            Assert.That(abilities.Check(aimed, far, null).Failure, Is.EqualTo(AbilityFailure.OutOfRange), "Precondition");

            Assert.That(caster.Issue(new MoveCommand(new Vector3(4f, 0f, -8f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far), IssueMode.Append), Is.True);
            yield return WaitUntilIdle();

            Assert.That(far.Current, Is.EqualTo(far.Max - 45));
        }

        [UnityTest]
        public IEnumerator AnAbilityIssuedWhileDirectControlSteers_IsNotDroppedBySteering()
        {
            yield return null;
            caster.SetMoveIntent(Vector3.right);
            yield return new WaitForSeconds(0.2f);
            var before = caster.transform.position;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            yield return new WaitForSeconds(0.3f);

            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45), "The ability ran although a move key is held");
            Assert.That(caster.CurrentCommand, Is.Null);
            Assert.That(caster.transform.position.x, Is.GreaterThan(before.x), "And steering carried on");
            Assert.That(caster.MoveIntent, Is.Not.EqualTo(Vector3.zero));
        }

        [UnityTest]
        public IEnumerator Replace_DropsAPendingAbility_AndStopClearsAQueuedOne()
        {
            yield return null;
            pause.Pause();
            Assert.That(caster.Issue(new MoveCommand(new Vector3(-6f, 0f, -6f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.PendingCommands, Has.Count.EqualTo(1));

            Assert.That(caster.Issue(new MoveCommand(new Vector3(-6f, 0f, -8f)), IssueMode.Replace), Is.True);
            Assert.That(caster.PendingCommands, Is.Empty, "Replace drops the pending ability");

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.Issue(new StopCommand()), Is.True);
            Assert.That(caster.CurrentCommand, Is.Null);
            Assert.That(caster.PendingCommands, Is.Empty);

            pause.Resume();
            yield return new WaitForSeconds(0.3f);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max), "A stopped unit uses nothing");
        }

        [UnityTest]
        public IEnumerator TwoQueuedAbilities_RunInOrder_InTheSameFrame_ThenTheQueueIsEmpty()
        {
            yield return null;
            ally.TakeDamage(60);
            pause.Pause();
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(mend, ally), IssueMode.Append), Is.True);

            pause.Resume();
            yield return WaitUntilIdle();

            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45));
            Assert.That(ally.Current, Is.EqualTo(ally.Max - 20));
            Assert.That(abilities.UsedCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator AnAbilityOrder_ParksACompanion_AndTheParkSticks()
        {
            yield return null;
            var leader = world.CreateFighter(new Vector3(6f, 0f, -10f));
            var leaderHealth = leader.GetComponent<Health>();
            var active = world.CreateActiveCharacter(leader, pause);
            var companionAi = world.CreateCompanion(new Vector3(6f, 0f, -6f), active, encounter);
            var companion = companionAi.GetComponent<CommandableUnit>();
            var companionAbilities = world.AddAbilities(companion, encounter, mend);
            // No hostiles: the companion must not start an assist attack of its own while the test watches its orders.
            encounter.Initialize(new[] { casterHealth, ally, leaderHealth, companion.GetComponent<Health>() }, new Health[0]);
            leaderHealth.TakeDamage(50);
            yield return new WaitForSeconds(0.3f);
            Assert.That(companionAi.IsParked, Is.False, "Precondition: attached, within follow distance");
            var standing = companion.transform.position;

            Assert.That(companion.Issue(AbilityCommand.OnUnit(mend, leaderHealth)), Is.True);
            yield return new WaitForSeconds(0.5f);

            Assert.That(companionAbilities.UsedCount, Is.EqualTo(1));
            Assert.That(leaderHealth.Current, Is.EqualTo(leaderHealth.Max - 10));
            Assert.That(companionAi.IsParked, Is.True, "An ability order is an explicit order: it parks the companion");
            Assert.That(companion.CurrentCommand, Is.Null);
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, standing), Is.LessThan(0.3f), "It did not wander off to follow");
        }
    }
}
