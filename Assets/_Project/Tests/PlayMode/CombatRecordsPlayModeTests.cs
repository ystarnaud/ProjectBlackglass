using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace Blackglass.Tests
{
    public class CombatRecordsPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static UnitAttacker AttackerOf(Component c) => c.GetComponent<UnitAttacker>();
        static Health HealthOf(Component c) => c.GetComponent<Health>();

        [UnityTest]
        public IEnumerator Fired_IsRaisedOncePerDischargedShot_AfterAttacked_WithTheShotsFacts()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 12f, cooldown: 0.2f);
            var target = world.CreateDummy(new Vector3(0f, 0f, 3f));
            yield return null;
            var attacker = AttackerOf(shooter);
            var order = new List<string>();
            var records = new List<ShotRecord>();
            attacker.Attacked += _ => order.Add("Attacked");
            attacker.Missed += _ => order.Add("Missed");
            attacker.Fired += r => { order.Add("Fired"); records.Add(r); };

            Assert.That(attacker.TryAttack(target), Is.True);

            Assert.That(order, Is.EqualTo(new[] { "Attacked", "Fired" }), "Fired comes after Attacked (decision 037 exposure is applied inside Attacked)");
            var record = records[0];
            Assert.That(record.Outcome, Is.EqualTo(ShotOutcome.Hit));
            Assert.That(record.Source, Is.EqualTo(HealthOf(shooter)));
            Assert.That(record.Target, Is.EqualTo(target));
            Assert.That(record.IsMelee, Is.False);
            Assert.That(record.Ability, Is.Null);
            Assert.That(record.Sequence, Is.EqualTo(1));
            Assert.That(record.AimPoint, Is.EqualTo(target.transform.position + Vector3.up * ShotRecord.BodyHeight).Using(Vector3EqualityComparer.Instance));
            Assert.That(record.Origin, Is.EqualTo(shooter.transform.position + Vector3.up * LineOfSight.EyeHeight).Using(Vector3EqualityComparer.Instance));

            yield return new WaitForSeconds(0.3f);
            attacker.TryAttack(target);
            Assert.That(records[1].Sequence, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Fired_ForAKillingBlow_StillCarriesTheTargetsPositionSnapshot()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, -5f), damage: 500, role: CombatRole.Ranged, range: 12f);
            var target = world.CreateDummy(new Vector3(1f, 0f, 3f));
            yield return null;
            var records = new List<ShotRecord>();
            AttackerOf(shooter).Fired += r => records.Add(r);

            AttackerOf(shooter).TryAttack(target);

            Assert.That(target.IsAlive, Is.False);
            Assert.That(records.Count, Is.EqualTo(1));
            Assert.That(records[0].AimPoint.x, Is.EqualTo(1f).Within(1e-4f));
        }

        [UnityTest]
        public IEnumerator Fired_IsNotRaisedForARejectedAttack()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 5f);
            var far = world.CreateDummy(new Vector3(0f, 0f, 20f));
            yield return null;
            var fired = 0;
            AttackerOf(shooter).Fired += _ => fired++;

            Assert.That(AttackerOf(shooter).TryAttack(far), Is.False, "out of range");
            Assert.That(AttackerOf(shooter).TryAttack(null), Is.False);
            Assert.That(fired, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Fired_IsNotRaisedDuringCooldown()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 12f, cooldown: 5f);
            var target = world.CreateDummy(new Vector3(0f, 0f, 3f));
            yield return null;
            var fired = 0;
            AttackerOf(shooter).Fired += _ => fired++;
            AttackerOf(shooter).TryAttack(target);
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.False);
            Assert.That(fired, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Fired_ForAMelee_IsMarkedMelee()
        {
            var shooter = world.CreateFighter(new Vector3(0f, 0f, 0f));
            var target = world.CreateDummy(new Vector3(0f, 0f, 1.2f));
            yield return null;
            var records = new List<ShotRecord>();
            AttackerOf(shooter).Fired += r => records.Add(r);
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(records[0].IsMelee, Is.True);
        }

        [UnityTest]
        public IEnumerator Fired_OnACoverMiss_IsRaisedAfterMissed_AndNoDamageIsDealt()
        {
            world.Dispose();
            world = new TestWorld();
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            var registry = world.CreateRegistry(point);
            var defender = world.CreateFighter(new Vector3(0f, 0f, -3f), role: CombatRole.Ranged, range: 8f, registry: registry);
            Assert.That(defender.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => defender.GetComponent<UnitCover>().Status == CoverStatus.Occupied, 10f);
            yield return new WaitForFixedUpdate();
            var shooter = world.CreateFighter(new Vector3(0f, 0f, 5f), damage: 10, role: CombatRole.Ranged, range: 12f);
            shooter.GetComponent<AutoRetaliate>().enabled = false;
            AttackerOf(shooter).HitRoll = () => 0.99f;
            yield return new WaitForFixedUpdate();
            var order = new List<string>();
            var records = new List<ShotRecord>();
            AttackerOf(shooter).Attacked += _ => order.Add("Attacked");
            AttackerOf(shooter).Missed += _ => order.Add("Missed");
            AttackerOf(shooter).Fired += r => { order.Add("Fired"); records.Add(r); };

            Assert.That(AttackerOf(shooter).TryAttack(HealthOf(defender)), Is.True);

            Assert.That(order, Is.EqualTo(new[] { "Missed", "Fired" }));
            Assert.That(records[0].Outcome, Is.EqualTo(ShotOutcome.CoverMiss));
            Assert.That(HealthOf(defender).Current, Is.EqualTo(HealthOf(defender).Max));
        }

        // ---- abilities ----

        [UnityTest]
        public IEnumerator AimedShot_RaisesShotResolvedOnce_WithTheAbility_AndARefusedUseRaisesNothing()
        {
            var caster = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 12f);
            var encounter = world.CreateEncounter();
            var target = world.CreateDummy(new Vector3(0f, 0f, 3f));
            encounter.Initialize(new[] { HealthOf(caster) }, new[] { target });
            var aimed = world.CreateAimedShot();
            var abilities = world.AddAbilities(caster, encounter, aimed);
            yield return null;
            var records = new List<ShotRecord>();
            abilities.ShotResolved += records.Add;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, target)), Is.True);
            Assert.That(records.Count, Is.EqualTo(1));
            Assert.That(records[0].Ability, Is.EqualTo(aimed));
            Assert.That(records[0].Outcome, Is.EqualTo(ShotOutcome.Hit));
            Assert.That(records[0].Source, Is.EqualTo(HealthOf(caster)));

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, target)), Is.False, "on cooldown");
            Assert.That(records.Count, Is.EqualTo(1), "a refused use presents nothing");
        }

        [UnityTest]
        public IEnumerator Blast_RaisesAreaResolvedOnce_WithTheGameplayCentreAndRadius_EvenWithNoVictims()
        {
            var caster = world.CreateFighter(new Vector3(0f, 0f, -5f), role: CombatRole.Ranged, range: 12f);
            var encounter = world.CreateEncounter();
            var victim = world.CreateDummy(new Vector3(0.5f, 0f, 3f));
            encounter.Initialize(new[] { HealthOf(caster) }, new[] { victim });
            var blast = world.CreateBlast();
            var abilities = world.AddAbilities(caster, encounter, blast);
            yield return null;
            var areas = new List<AreaRecord>();
            abilities.AreaResolved += areas.Add;
            var shots = 0;
            abilities.ShotResolved += _ => shots++;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(0f, 0f, 3f))), Is.True);

            Assert.That(areas.Count, Is.EqualTo(1));
            Assert.That(areas[0].Center, Is.EqualTo(new Vector3(0f, 0f, 3f)).Using(Vector3EqualityComparer.Instance));
            Assert.That(areas[0].Radius, Is.EqualTo(3f));
            Assert.That(areas[0].Ability, Is.EqualTo(blast));
            Assert.That(shots, Is.EqualTo(0), "an area is not a shot");
            Assert.That(victim.Current, Is.LessThan(victim.Max), "the blast still hit (gameplay unchanged)");
        }

        [UnityTest]
        public IEnumerator Mend_RaisesNoShotOrAreaRecord()
        {
            var caster = world.CreateFighter(new Vector3(0f, 0f, 0f));
            var encounter = world.CreateEncounter();
            encounter.Initialize(new[] { HealthOf(caster) }, new Health[0]);
            var mend = world.CreateMend();
            var abilities = world.AddAbilities(caster, encounter, mend);
            HealthOf(caster).TakeDamage(30);
            yield return null;
            var raised = 0;
            abilities.ShotResolved += _ => raised++;
            abilities.AreaResolved += _ => raised++;
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, HealthOf(caster))), Is.True);
            Assert.That(raised, Is.EqualTo(0));
        }
    }
}
