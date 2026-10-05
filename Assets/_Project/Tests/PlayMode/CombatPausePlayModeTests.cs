using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatPausePlayModeTests
    {
        TestWorld world;
        GameObject environment;
        TacticalPause pause;
        Encounter encounter;
        CommandableUnit friendly;
        EnemyAI hostile;
        Health friendlyHealth;
        Health hostileHealth;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            environment = world.CreateEnvironment();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            encounter = world.CreateEncounter();
            friendly = world.CreateFighter(new Vector3(0f, 0f, -6f), cooldown: 0.4f);
            hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter, maxHealth: 300, cooldown: 0.4f);
            friendlyHealth = friendly.GetComponent<Health>();
            hostileHealth = hostile.GetComponent<Health>();
            encounter.Initialize(new[] { friendlyHealth }, new[] { hostileHealth });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        IEnumerator StartTheFight()
        {
            yield return TestWorld.WaitUntil(() => friendlyHealth.Current < friendlyHealth.Max && hostileHealth.Current < hostileHealth.Max, 8f);
            Assert.That(friendlyHealth.Current, Is.LessThan(friendlyHealth.Max), "Precondition: the hostile hit the friendly");
            Assert.That(hostileHealth.Current, Is.LessThan(hostileHealth.Max), "Precondition: the friendly retaliated");
        }

        [UnityTest]
        public IEnumerator Paused_NothingInTheFightChanges_AndItResumesAfterwards()
        {
            yield return StartTheFight();

            pause.Pause();
            var friendlyHp = friendlyHealth.Current;
            var hostileHp = hostileHealth.Current;
            var state = hostile.State;
            var friendlyCooldown = friendly.GetComponent<UnitAttacker>().CooldownRemaining;
            var hostileCooldown = hostile.GetComponent<UnitAttacker>().CooldownRemaining;
            var friendlyAt = friendly.transform.position;
            var hostileAt = hostile.transform.position;
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(friendlyHealth.Current, Is.EqualTo(friendlyHp), "Friendly health changed while paused");
            Assert.That(hostileHealth.Current, Is.EqualTo(hostileHp), "Hostile health changed while paused");
            Assert.That(hostile.State, Is.EqualTo(state), "Enemy AI advanced while paused");
            Assert.That(friendly.GetComponent<UnitAttacker>().CooldownRemaining, Is.EqualTo(friendlyCooldown), "A cooldown progressed while paused");
            Assert.That(hostile.GetComponent<UnitAttacker>().CooldownRemaining, Is.EqualTo(hostileCooldown), "A cooldown progressed while paused");
            Assert.That(Vector3.Distance(friendly.transform.position, friendlyAt), Is.LessThan(0.01f), "The friendly moved while paused");
            Assert.That(Vector3.Distance(hostile.transform.position, hostileAt), Is.LessThan(0.01f), "The hostile moved while paused");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => friendlyHealth.Current < friendlyHp || hostileHealth.Current < hostileHp, 3f);
            Assert.That(friendlyHealth.Current < friendlyHp || hostileHealth.Current < hostileHp, Is.True, "The fight did not resume");
        }

        [UnityTest]
        public IEnumerator RepeatedPauseAndResume_DuringTheFight_LogsNoErrors_AndEndsConsistent()
        {
            yield return StartTheFight();

            for (var i = 0; i < 10; i++)
            {
                pause.Pause();
                yield return new WaitForSecondsRealtime(0.1f);
                pause.Resume();
                yield return new WaitForSeconds(0.15f);
            }

            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
            foreach (var unit in new[] { friendly, hostile.GetComponent<CommandableUnit>() })
            {
                var health = unit.GetComponent<Health>();
                if (health.IsAlive)
                    Assert.That(unit.gameObject.activeSelf, Is.True, $"{unit.name} is alive but inactive");
                else
                    Assert.That(unit.CurrentCommand, Is.Null, $"{unit.name} is dead but still has orders");
            }
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing).Or.EqualTo(EncounterOutcome.Defeat));
        }

        [UnityTest]
        public IEnumerator OrdersIssuedWhilePaused_RunAfterResume_AndTheHostileKeepsFighting()
        {
            yield return StartTheFight();
            pause.Pause();
            var retreat = new MoveCommand(new Vector3(0f, 0f, -14f));
            Assert.That(friendly.Issue(retreat), Is.True, "Orders must be accepted while paused");
            var friendlyAt = friendly.transform.position;
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(Vector3.Distance(friendly.transform.position, friendlyAt), Is.LessThan(0.01f));

            pause.Resume();
            yield return new WaitForSeconds(1.5f);

            Assert.That(TestWorld.HorizontalDistance(friendly.transform.position, friendlyAt), Is.GreaterThan(2f), "The queued retreat did not run");
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase).Or.EqualTo(EnemyState.Attack), "The hostile must keep after the friendly");
        }

        [UnityTest]
        public IEnumerator Paused_RepositioningUnit_DoesNotAdvance()
        {
            // The fixture's duel and flat ground would interfere (a second NavMesh without the pillar would also let
            // the unit path straight through it), so clear them and build the pillar scenario from
            // RangedCombatPlayModeTests: the unit is blind and walks to a side spot.
            Object.Destroy(environment);
            Object.Destroy(friendly.gameObject);
            Object.Destroy(hostile.gameObject);
            yield return null;

            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -3.5f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 4f));
            yield return new WaitForFixedUpdate();
            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return TestWorld.WaitUntil(() => ranged.AttackPhase == AttackPhase.Reposition
                && TestWorld.HorizontalDistance(ranged.transform.position, new Vector3(0f, 0f, -3.5f)) > 0.2f, 3f);
            Assert.That(ranged.AttackPhase, Is.EqualTo(AttackPhase.Reposition), "Precondition: it is walking to a firing position");

            pause.Pause();
            var frozenAt = ranged.transform.position;
            var health = dummy.Current;
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(ranged.transform.position, Is.EqualTo(frozenAt), "Repositioning must not advance while paused");
            Assert.That(ranged.AttackPhase, Is.EqualTo(AttackPhase.Reposition));
            Assert.That(dummy.Current, Is.EqualTo(health));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => dummy.Current < health, 8f);
            Assert.That(dummy.Current, Is.LessThan(health), "After resume the unit finishes repositioning and fires");
        }

        [UnityTest]
        public IEnumerator Paused_ReservationIsUnchanged_AndTheWalkResumesAfterwards()
        {
            yield return StartTheFight();
            var box = world.CreateObstacle(new Vector3(-6f, 0.45f, -5f), new Vector3(2f, 0.9f, 0.5f));
            var point = world.CreateCoverPoint(new Vector3(-6f, 0f, -6f), Vector3.forward, box.GetComponent<Collider>());
            Assert.That(friendly.Issue(new MoveToCoverCommand(point)), Is.True);
            Assert.That(friendly.Cover.Status, Is.EqualTo(CoverStatus.Reserved));

            pause.Pause();
            var at = friendly.transform.position;
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(friendly.Cover.Status, Is.EqualTo(CoverStatus.Reserved), "Pause neither releases nor completes a reservation");
            Assert.That(point.Claimant, Is.SameAs(friendly.Cover));
            Assert.That(Vector3.Distance(friendly.transform.position, at), Is.LessThan(0.01f));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => friendly.Cover.Status == CoverStatus.Occupied, 6f);
            Assert.That(friendly.Cover.Status, Is.EqualTo(CoverStatus.Occupied), "The walk resumed and arrived");
        }
    }
}
