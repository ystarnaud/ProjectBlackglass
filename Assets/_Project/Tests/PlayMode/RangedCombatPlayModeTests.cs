using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class RangedCombatPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        // Records the phases an attack order passes through, so a test can assert the path, not just the result.
        static IEnumerator RecordPhases(CommandableUnit unit, HashSet<AttackPhase> seen, Func<bool> until, float timeout)
        {
            var deadline = Time.realtimeSinceStartup + timeout;
            while (!until() && Time.realtimeSinceStartup < deadline)
            {
                seen.Add(unit.AttackPhase);
                yield return null;
            }
            // The frame that meets the condition counts too: a hit lands on the very frame the Attack phase starts.
            seen.Add(unit.AttackPhase);
        }

        [UnityTest]
        public IEnumerator RangedTryAttack_BehindAWall_DoesNotHit_AndHitsOnceTheWallIsCleared()
        {
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(3f, 2f, 1f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -3f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 3f));
            yield return new WaitForFixedUpdate();
            var attacker = ranged.GetComponent<UnitAttacker>();

            Assert.That(attacker.IsInRange(dummy), Is.True, "Precondition: 6 m is inside the 8 m range");
            Assert.That(attacker.CanAttack(dummy), Is.False, "The wall blocks sight");
            Assert.That(attacker.TryAttack(dummy), Is.False, "A ranged attack must not land through a wall");
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max));

            // A second ranged unit already standing to the side (teleporting a NavMeshAgent by transform is unreliable).
            var sideAttacker = world.CreateFighter(new Vector3(4f, 0f, -3f), role: CombatRole.Ranged, range: 8f).GetComponent<UnitAttacker>();
            yield return new WaitForFixedUpdate();
            Assert.That(sideAttacker.CanAttack(dummy), Is.True, "From the side the wall is cleared");
            Assert.That(sideAttacker.TryAttack(dummy), Is.True);
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max - sideAttacker.Damage));
        }

        [UnityTest]
        public IEnumerator RangedFighter_AttacksFromItsRange_WithoutClosingIn()
        {
            world.CreateEnvironment();
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -12f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(Vector3.zero);
            var distanceAtFirstHit = -1f;
            ranged.GetComponent<UnitAttacker>().Attacked += _ =>
            {
                if (distanceAtFirstHit < 0f)
                    distanceAtFirstHit = TestWorld.HorizontalDistance(ranged.transform.position, dummy.transform.position);
            };
            var seen = new HashSet<AttackPhase>();

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return RecordPhases(ranged, seen, () => distanceAtFirstHit >= 0f, 8f);

            Assert.That(distanceAtFirstHit, Is.GreaterThanOrEqualTo(6f).And.LessThanOrEqualTo(8.5f), "A ranged unit fires from its range, not from melee");
            Assert.That(seen, Has.Member(AttackPhase.Approach).And.Member(AttackPhase.Attack));
            Assert.That(seen, Has.No.Member(AttackPhase.Reposition), "Nothing blocked the line");
        }

        [UnityTest]
        public IEnumerator RangedFighter_BlindBehindAPillar_RepositionsToTheSide_ThenHits()
        {
            // A 1 m pillar between them. The 2 m ring's north point still looks through the pillar; its north-east
            // point (1.41, -2.09) clears it by 0.3 m and is in range, so that is where the unit should go.
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -3.5f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 4f));
            var seen = new HashSet<AttackPhase>();
            var damageWhileBlind = false;
            yield return new WaitForFixedUpdate();

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return RecordPhases(ranged, seen, () =>
            {
                if (dummy.Current < dummy.Max && !ranged.GetComponent<UnitAttacker>().HasLineOfSight(dummy))
                    damageWhileBlind = true;
                return dummy.Current < dummy.Max;
            }, 8f);

            Assert.That(dummy.Current, Is.LessThan(dummy.Max), "The unit never regained sight and fired");
            Assert.That(damageWhileBlind, Is.False, "No damage may land without sight");
            Assert.That(seen, Has.Member(AttackPhase.Reposition), "It had to reposition first");
            Assert.That(Mathf.Abs(ranged.transform.position.x), Is.GreaterThan(1f), "It stepped to the side of the pillar");
            Assert.That(TestWorld.HorizontalDistance(ranged.transform.position, dummy.transform.position), Is.GreaterThan(4f),
                "It did not walk into the target");
            Assert.That(ranged.AttackPhase, Is.EqualTo(AttackPhase.Attack));
        }

        [UnityTest]
        public IEnumerator RangedFighter_TargetWalledOnThreeSides_FallsBackToApproach_AndHits()
        {
            // A U of 2 m walls open to the north hides the target from every candidate south of it, so the search
            // fails and the unit must walk at the target until the line clears around the side. Health 100 at 25 per
            // hit: the order must end with the target dead, which proves the walk terminates.
            world.CreateEnvironment(
                (new Vector3(0f, 1f, -2f), new Vector3(6f, 2f, 0.5f)),
                (new Vector3(-3f, 1f, 0f), new Vector3(0.5f, 2f, 4.5f)),
                (new Vector3(3f, 1f, 0f), new Vector3(0.5f, 2f, 4.5f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -7f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(Vector3.zero);
            var seen = new HashSet<AttackPhase>();
            yield return new WaitForFixedUpdate();

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return RecordPhases(ranged, seen, () => !dummy.IsAlive, 25f);

            Assert.That(dummy.IsAlive, Is.False, "The ranged unit must eventually get a line and finish the target");
            Assert.That(ranged.CurrentCommand, Is.Null, "The attack order ends with the target");
            Assert.That(seen, Has.Member(AttackPhase.Reposition));
            Assert.That(ranged.transform.position.z, Is.GreaterThan(-5f), "It walked around the wall, it did not stand still");
            // Any logged error during the walk fails the test on its own.
        }

        [UnityTest]
        public IEnumerator MeleeFighter_PursuesATargetThatWalksAway_AndHits()
        {
            world.CreateEnvironment();
            var melee = world.CreateFighter(new Vector3(0f, 0f, -3f));
            var runner = world.CreateFighter(Vector3.zero);
            Assert.That(runner.Issue(new MoveCommand(new Vector3(0f, 0f, 6f))), Is.True);   // an order, so it does not retaliate
            var seen = new HashSet<AttackPhase>();

            Assert.That(melee.Issue(new AttackCommand(HealthOf(runner))), Is.True);
            yield return RecordPhases(melee, seen, () => HealthOf(runner).Current < HealthOf(runner).Max, 10f);

            Assert.That(HealthOf(runner).Current, Is.LessThan(HealthOf(runner).Max), "The pursuer never caught up");
            Assert.That(melee.transform.position.z, Is.GreaterThan(2f), "The pursuer followed the runner north");
            Assert.That(seen, Has.Member(AttackPhase.Approach).And.Member(AttackPhase.Attack));
            Assert.That(seen, Has.No.Member(AttackPhase.Reposition), "Melee never repositions");
        }

        [UnityTest]
        public IEnumerator RangedFighter_TargetEnclosedOnFourSides_EndsTheOrder_AndRunsTheNextOne()
        {
            // 2 m walls on every side: in range, blind, no candidate sees in, and the fallback walk ends at the box
            // still blind. The order must end (unreachable), fire nothing, and let the queued move run.
            world.CreateEnvironment(
                (new Vector3(0f, 1f, -2f), new Vector3(4.5f, 2f, 0.5f)),
                (new Vector3(0f, 1f, 2f), new Vector3(4.5f, 2f, 0.5f)),
                (new Vector3(-2f, 1f, 0f), new Vector3(0.5f, 2f, 4.5f)),
                (new Vector3(2f, 1f, 0f), new Vector3(0.5f, 2f, 4.5f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -6f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(Vector3.zero);
            var away = new Vector3(6f, 0f, -6f);
            yield return new WaitForFixedUpdate();

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            Assert.That(ranged.Issue(new MoveCommand(away), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => ranged.CurrentCommand is MoveCommand, 12f);

            Assert.That(ranged.CurrentCommand, Is.TypeOf<MoveCommand>(), "The attack order must end on its own");
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max), "Nothing may land through the walls");
            yield return TestWorld.WaitUntil(() => ranged.CurrentCommand == null, 8f);
            Assert.That(TestWorld.HorizontalDistance(ranged.transform.position, away), Is.LessThan(0.5f), "The queued move then runs");
            Assert.That(ranged.AttackPhase, Is.EqualTo(AttackPhase.None));
            // Any logged error during the run fails the test on its own.
        }

        [UnityTest]
        public IEnumerator RangedFighter_OneMetreFromACrate_StillFindsASideCandidate()
        {
            // A 2 m crate (eroded footprint +-1.5) right in front of the unit, target off to the north-east. The 2 m
            // ring's north-east point (1.41, -1.09) lands inside the erosion band and must snap onto the mesh edge at
            // x = 1.5, from where the line to (3, 4) clears the crate's east face. A 1 m snap would have rejected it.
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -2.5f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(3f, 0f, 4f));
            var seen = new HashSet<AttackPhase>();
            yield return new WaitForFixedUpdate();
            Assert.That(ranged.GetComponent<UnitAttacker>().CanAttack(dummy), Is.False, "Precondition: the crate blocks the line");

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return RecordPhases(ranged, seen, () => dummy.Current < dummy.Max, 10f);

            Assert.That(dummy.Current, Is.LessThan(dummy.Max), "The unit must find a spot that sees past the crate");
            Assert.That(seen, Has.Member(AttackPhase.Reposition));
            Assert.That(ranged.transform.position.x, Is.GreaterThan(1.2f), "It stepped to the crate's east side");
            Assert.That(ranged.transform.position.z, Is.LessThan(0f), "It did not walk around to the target");
        }
    }
}
