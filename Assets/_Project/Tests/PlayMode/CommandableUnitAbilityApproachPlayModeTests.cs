using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Decision 029: an ability order whose only problem is range or line of sight is accepted and the unit walks into
    /// position (the attack's approach and reposition steps), then casts the moment the ability is usable.
    /// </summary>
    public class CommandableUnitAbilityApproachPlayModeTests
    {
        // A 3 m wall from x -5 to -1 at z -2 (as in CommandableUnitAbilityPlayModeTests).
        static readonly (Vector3, Vector3) Wall = (new Vector3(-3f, 1.5f, -2f), new Vector3(4f, 3f, 0.5f));
        static readonly Vector3 CasterGround = new Vector3(0f, 0f, -6f);

        TestWorld world;
        TacticalPause pause;
        Encounter encounter;
        AbilityDefinition aimed;
        AbilityDefinition blast;
        AbilityDefinition mend;
        CommandableUnit caster;
        Health casterHealth;
        UnitAbilities abilities;
        Health hostile;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        // The arena (the wall plus any extra baked obstacles), a caster with the three abilities at (0, -6) and a hostile
        // in range and in sight at (3, 2).
        void Build(params (Vector3, Vector3)[] extra)
        {
            var obstacles = new (Vector3, Vector3)[extra.Length + 1];
            obstacles[0] = Wall;
            extra.CopyTo(obstacles, 1);
            world.CreateEnvironment(obstacles);
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            encounter = world.CreateEncounter();
            aimed = world.CreateAimedShot();
            blast = world.CreateBlast();
            mend = world.CreateMend();
            caster = world.CreateFighter(CasterGround);
            casterHealth = caster.GetComponent<Health>();
            abilities = world.AddAbilities(caster, encounter, aimed, blast, mend);
            hostile = world.CreateDummy(new Vector3(3f, 0f, 2f));
            Sides(new Health[0], new Health[0]);
        }

        void Sides(Health[] friendlies, Health[] hostiles)
        {
            var friends = new Health[friendlies.Length + 1];
            friends[0] = casterHealth;
            friendlies.CopyTo(friends, 1);
            var foes = new Health[hostiles.Length + 1];
            foes[0] = hostile;
            hostiles.CopyTo(foes, 1);
            encounter.Initialize(friends, foes);
        }

        Health FarHostile(Vector3 ground)
        {
            var far = world.CreateDummy(ground);
            Sides(new Health[0], new[] { far });
            return far;
        }

        IEnumerator WaitUntilIdle(float seconds = 15f) => TestWorld.WaitUntil(() => caster.CurrentCommand == null, seconds);

        [UnityTest]
        public IEnumerator AimedShot_OutOfRange_IsAccepted_WalksUntilInRange_ThenFires_AndTheCooldownStarts()
        {
            Build();
            var far = FarHostile(new Vector3(0f, 0f, 14f));   // 20 m away, range 14, a clear line along x = 0
            yield return null;
            Assert.That(abilities.Check(aimed, far, null).Failure, Is.EqualTo(AbilityFailure.OutOfRange), "Precondition");

            var distanceWhenUsed = -1f;
            abilities.Used += _ => distanceWhenUsed = CoverRules.FlatDistance(caster.transform.position, far.transform.position);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far)), Is.True, "Out of range only: accepted");
            Assert.That(caster.CurrentCommand, Is.TypeOf<AbilityCommand>());
            yield return new WaitForSeconds(0.3f);
            Assert.That(caster.AbilityPhase, Is.EqualTo(AttackPhase.Approach), "It walks toward the target");
            Assert.That(caster.AttackPhase, Is.EqualTo(AttackPhase.None), "An ability order is not an attack");
            yield return WaitUntilIdle();

            Assert.That(far.Current, Is.EqualTo(far.Max - 45));
            Assert.That(abilities.IsReady(0), Is.False, "The cooldown started");
            Assert.That(distanceWhenUsed, Is.LessThanOrEqualTo(14f).And.GreaterThan(13f), "It stopped at the edge of range");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.None), "Nothing was reported as a failure");
            var standing = caster.transform.position;
            yield return new WaitForSeconds(0.3f);
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, standing), Is.LessThan(0.05f), "And it stays there");
        }

        [UnityTest]
        public IEnumerator AimedShot_InRangeBehindAWall_RepositionsToAFiringPosition_ThenFires()
        {
            Build();
            var walled = FarHostile(new Vector3(-3f, 0f, 2f));   // 8.5 m away, behind the wall
            yield return null;
            Assert.That(abilities.Check(aimed, walled, null).Failure, Is.EqualTo(AbilityFailure.NoLineOfSight), "Precondition");

            var attacker = caster.GetComponent<UnitAttacker>();
            var sightWhenUsed = false;
            var positionWhenUsed = Vector3.zero;
            abilities.Used += _ =>
            {
                sightWhenUsed = attacker.HasLineOfSight(walled);
                positionWhenUsed = caster.transform.position;
            };
            var everRepositioned = false;
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, walled)), Is.True, "Out of sight only: accepted");
            yield return TestWorld.WaitUntil(() =>
            {
                everRepositioned |= caster.AbilityPhase == AttackPhase.Reposition;
                return caster.CurrentCommand == null;
            }, 15f);

            Assert.That(everRepositioned, Is.True);
            Assert.That(walled.Current, Is.EqualTo(walled.Max - 45));
            Assert.That(sightWhenUsed, Is.True, "Cast from a spot with a clear line");
            Assert.That(TestWorld.HorizontalDistance(positionWhenUsed, CasterGround + Vector3.up), Is.GreaterThan(1f), "It moved");
        }

        [UnityTest]
        public IEnumerator Blast_OnAGroundPointOutOfRange_WalksIntoRange_ThenFires()
        {
            Build();
            var point = new Vector3(0f, 0f, 10f);   // 16 m away, range 12
            var victim = FarHostile(new Vector3(0f, 0f, 11f));
            yield return null;
            Assert.That(abilities.Check(blast, null, point).Failure, Is.EqualTo(AbilityFailure.OutOfRange), "Precondition");

            Assert.That(caster.Issue(AbilityCommand.AtGround(blast, point)), Is.True);
            yield return WaitUntilIdle();

            Assert.That(victim.Current, Is.EqualTo(victim.Max - 35));
            Assert.That(abilities.IsReady(1), Is.False);
            Assert.That(CoverRules.FlatDistance(caster.transform.position, point), Is.LessThanOrEqualTo(12f));
        }

        [UnityTest]
        public IEnumerator Blast_OnAGroundPointOutOfSight_RepositionsThenFires()
        {
            Build();
            var point = new Vector3(-3f, 0f, 1f);   // 7.6 m away, the wall hides it
            var victim = FarHostile(new Vector3(-3f, 0f, 2f));
            yield return null;
            Assert.That(abilities.Check(blast, null, point).Failure, Is.EqualTo(AbilityFailure.NoLineOfSight), "Precondition");

            Assert.That(caster.Issue(AbilityCommand.AtGround(blast, point)), Is.True);
            yield return WaitUntilIdle();

            Assert.That(victim.Current, Is.EqualTo(victim.Max - 35));
            Assert.That(abilities.IsReady(1), Is.False);
        }

        [UnityTest]
        public IEnumerator Mend_OnAFriendlyOutOfRange_WalksIntoRange_AndHeals()
        {
            Build();
            var ally = world.CreateFighter(new Vector3(10f, 0f, 6f)).GetComponent<Health>();   // 15.6 m away, range 8
            Sides(new[] { ally }, new Health[0]);
            ally.TakeDamage(60);
            yield return null;
            Assert.That(abilities.Check(mend, ally, null).Failure, Is.EqualTo(AbilityFailure.OutOfRange), "Precondition");

            Assert.That(caster.Issue(AbilityCommand.OnUnit(mend, ally)), Is.True);
            yield return WaitUntilIdle();

            Assert.That(ally.Current, Is.EqualTo(ally.Max - 20));
            Assert.That(CoverRules.FlatDistance(caster.transform.position, ally.transform.position), Is.LessThanOrEqualTo(8f));
        }

        [UnityTest]
        public IEnumerator OnCooldown_ADeadTarget_OrTheWrongSide_AreRefusedAtOnce_AndNothingMoves()
        {
            Build();
            var far = FarHostile(new Vector3(0f, 0f, 14f));
            var farAlly = world.CreateFighter(new Vector3(10f, 0f, 6f)).GetComponent<Health>();
            var dead = world.CreateDummy(new Vector3(-6f, 0f, 14f));
            Sides(new[] { farAlly }, new[] { far, dead });
            dead.TakeDamage(dead.Max);
            yield return null;
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            yield return WaitUntilIdle();
            Assert.That(abilities.IsReady(0), Is.False, "Precondition: Aimed Shot is cooling down");
            var start = caster.transform.position;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far)), Is.False, "Far and cooling down: never walked to");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OnCooldown));
            Assert.That(caster.Issue(AbilityCommand.OnUnit(mend, dead)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.TargetDead));
            Assert.That(caster.Issue(AbilityCommand.OnUnit(mend, far)), Is.False, "Mend on a far hostile");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(caster.CurrentCommand, Is.Null);
            yield return new WaitForSeconds(0.5f);

            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, start), Is.LessThan(0.05f), "Nothing moved");
            Assert.That(farAlly.Current, Is.EqualTo(farAlly.Max));
        }

        [UnityTest]
        public IEnumerator TheTargetDyingDuringTheApproach_EndsTheOrder_WithoutACooldown_AndTheQueueContinues()
        {
            Build();
            var far = FarHostile(new Vector3(0f, 0f, 14f));
            var last = new Vector3(6f, 0f, -10f);
            yield return null;
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far)), Is.True);
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => caster.transform.position.z > -4f, 3f);
            Assert.That(caster.AbilityPhase, Is.EqualTo(AttackPhase.Approach), "Precondition: walking toward it");

            far.TakeDamage(far.Max);
            yield return WaitUntilIdle();

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.TargetDead), "The order ends with its reason on the unit");
            Assert.That(abilities.UsedCount, Is.EqualTo(0));
            Assert.That(abilities.IsReady(0), Is.True, "No cooldown spent");
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f), "The next order ran");
        }

        [UnityTest]
        public IEnumerator AnUnreachableTarget_EndsTheOrder_WithoutACooldown_AndTheQueueContinues()
        {
            // A closed 3 m box of walls around (12, 12): no path leads in and no line leads in from outside.
            Build((new Vector3(12f, 1.5f, 13.5f), new Vector3(3.5f, 3f, 0.5f)),
                (new Vector3(12f, 1.5f, 10.5f), new Vector3(3.5f, 3f, 0.5f)),
                (new Vector3(13.5f, 1.5f, 12f), new Vector3(0.5f, 3f, 3.5f)),
                (new Vector3(10.5f, 1.5f, 12f), new Vector3(0.5f, 3f, 3.5f)));
            var boxed = FarHostile(new Vector3(12f, 0f, 12f));
            var last = new Vector3(-6f, 0f, -6f);
            yield return null;
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, boxed)), Is.True);
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => !(caster.CurrentCommand is AbilityCommand), 20f);

            Assert.That(caster.CurrentCommand, Is.Not.TypeOf<AbilityCommand>(), "The order gave up");
            Assert.That(boxed.Current, Is.EqualTo(boxed.Max));
            Assert.That(abilities.IsReady(0), Is.True, "No cooldown spent");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange).Or.EqualTo(AbilityFailure.NoLineOfSight),
                "It says why it could not cast");
            yield return WaitUntilIdle();
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f), "The next order ran");
        }

        [UnityTest]
        public IEnumerator AWalkThatMakesNoProgressForThreeSeconds_EndsTheOrder_AndTheQueueContinues()
        {
            Build();
            var far = FarHostile(new Vector3(0f, 0f, 14f));
            yield return null;
            caster.GetComponent<NavMeshAgent>().speed = 0f;   // it has a path but never gets anywhere
            var next = new MoveCommand(new Vector3(-6f, 0f, -6f));
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far)), Is.True);
            Assert.That(caster.Issue(next, IssueMode.Append), Is.True);

            yield return new WaitForSeconds(2.5f);
            Assert.That(caster.CurrentCommand, Is.TypeOf<AbilityCommand>(), "Not given up before 3 s");
            yield return new WaitForSeconds(1.5f);

            Assert.That(caster.CurrentCommand, Is.SameAs(next), "Given up after 3 s without progress; the queue moved on");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.IsReady(0), Is.True);
            Assert.That(far.Current, Is.EqualTo(far.Max));
        }

        [UnityTest]
        public IEnumerator AnOutOfRangeAbility_IssuedWhilePaused_IsAccepted_AndNothingMovesUntilTheGameResumes()
        {
            Build();
            var far = FarHostile(new Vector3(0f, 0f, 14f));
            yield return null;
            pause.Pause();
            var start = caster.transform.position;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far)), Is.True, "Accepted while paused, for planning");
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(caster.CurrentCommand, Is.TypeOf<AbilityCommand>());
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, start), Is.LessThan(0.01f), "Frozen while paused");
            Assert.That(far.Current, Is.EqualTo(far.Max));

            pause.Resume();
            yield return WaitUntilIdle();
            Assert.That(far.Current, Is.EqualTo(far.Max - 45));
        }

        [UnityTest]
        public IEnumerator DirectControl_OrANewOrder_CancelsTheApproach()
        {
            Build();
            var far = FarHostile(new Vector3(0f, 0f, 14f));
            yield return null;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far)), Is.True);
            yield return TestWorld.WaitUntil(() => caster.transform.position.z > -5f, 3f);
            caster.SetMoveIntent(Vector3.right);
            yield return null;
            yield return null;
            Assert.That(caster.CurrentCommand, Is.Null, "A held move key drops the approach like any other order");
            caster.SetMoveIntent(Vector3.zero);

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far)), Is.True);
            yield return TestWorld.WaitUntil(() => caster.AbilityPhase == AttackPhase.Approach, 2f);
            var move = new MoveCommand(new Vector3(6f, 0f, -10f));
            Assert.That(caster.Issue(move), Is.True);
            Assert.That(caster.CurrentCommand, Is.SameAs(move), "A new order replaces it");
            yield return new WaitForSeconds(1f);

            Assert.That(far.Current, Is.EqualTo(far.Max));
            Assert.That(abilities.IsReady(0), Is.True);
        }
    }
}
