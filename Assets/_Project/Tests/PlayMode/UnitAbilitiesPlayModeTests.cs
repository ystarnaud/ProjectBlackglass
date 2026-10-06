using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitAbilitiesPlayModeTests
    {
        TestWorld world;
        Encounter encounter;
        AbilityDefinition aimed;
        AbilityDefinition blast;
        AbilityDefinition mend;
        CommandableUnit caster;
        Health casterHealth;
        UnitAbilities abilities;
        CommandableUnit allyUnit;
        Health ally;
        Health hostile;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // A 3 m tall wall west of the middle: the line from the caster (0, -6) to (-4, 2) or (-3, 0) crosses it.
            world.CreateEnvironment((new Vector3(-3f, 1.5f, -2f), new Vector3(4f, 3f, 0.5f)));
            encounter = world.CreateEncounter();
            aimed = world.CreateAimedShot();
            blast = world.CreateBlast();
            mend = world.CreateMend();
            caster = world.CreateFighter(new Vector3(0f, 0f, -6f));
            casterHealth = caster.GetComponent<Health>();
            abilities = world.AddAbilities(caster, encounter, aimed, blast, mend);
            allyUnit = world.CreateFighter(new Vector3(4f, 0f, -6f));
            ally = allyUnit.GetComponent<Health>();
            hostile = world.CreateDummy(new Vector3(3f, 0f, 2f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator Check_EverythingInOrder_IsValid_WithDistanceAndSightReported()
        {
            yield return null;

            var check = abilities.Check(aimed, hostile, null);

            Assert.That(check.Failure, Is.EqualTo(AbilityFailure.None));
            Assert.That(check.IsValid, Is.True);
            Assert.That(check.Distance, Is.EqualTo(Mathf.Sqrt(9f + 64f)).Within(0.01f));
            Assert.That(check.SightClear, Is.True);
            Assert.That(check.TargetInCover, Is.False);
            Assert.That(check.HitChance, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator Check_ADeadCaster_CannotUseAnything()
        {
            yield return null;
            casterHealth.TakeDamage(casterHealth.Max);

            Assert.That(abilities.Check(aimed, hostile, null).Failure, Is.EqualTo(AbilityFailure.CasterDead));
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.CasterDead));
        }

        [UnityTest]
        public IEnumerator Check_AnAbilityTheUnitDoesNotHave_IsUnknown()
        {
            yield return null;
            var foreign = world.CreateAimedShot();

            Assert.That(abilities.Check(foreign, hostile, null).Failure, Is.EqualTo(AbilityFailure.UnknownAbility));
            Assert.That(abilities.Check(null, hostile, null).Failure, Is.EqualTo(AbilityFailure.UnknownAbility));
        }

        [UnityTest]
        public IEnumerator Check_NoTarget_DeadTarget_AndWrongSide_AreRejected()
        {
            yield return null;
            Assert.That(abilities.Check(aimed, null, null).Failure, Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(abilities.Check(aimed, ally, null).Failure, Is.EqualTo(AbilityFailure.WrongSide), "An attack on a friendly");
            Assert.That(abilities.Check(mend, hostile, null).Failure, Is.EqualTo(AbilityFailure.WrongSide), "A heal on a hostile");

            hostile.TakeDamage(hostile.Max);
            Assert.That(abilities.Check(aimed, hostile, null).Failure, Is.EqualTo(AbilityFailure.TargetDead));
        }

        [UnityTest]
        public IEnumerator Check_AUnitDamageAbilityStoredAsFriendly_StillRefusesAnAlly()
        {
            yield return null;
            // A hand-edited asset: the stored side says Friendly although the ability deals damage.
            var field = typeof(AbilityDefinition).GetField("targetSide", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field.SetValue(aimed, AbilityTargetSide.Friendly);
            Assert.That(field.GetValue(aimed), Is.EqualTo(AbilityTargetSide.Friendly), "Precondition: the stored side is the unsafe one");

            Assert.That(abilities.Check(aimed, ally, null).Failure, Is.EqualTo(AbilityFailure.WrongSide), "No friendly fire from a hand-edited asset");
            Assert.That(abilities.Check(aimed, hostile, null).Failure, Is.Not.EqualTo(AbilityFailure.WrongSide));
        }

        [UnityTest]
        public IEnumerator Check_ARangeAtTheEdgeIsInRange_AndAHairBeyondIsNot()
        {
            yield return null;
            var edge = world.CreateDummy(new Vector3(0f, 0f, 8f));       // exactly 14 m from the caster at z = -6
            var beyond = world.CreateDummy(new Vector3(6f, 0f, 8.01f));  // farther than 14 m, off the wall's line
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, edge, beyond });

            Assert.That(abilities.Check(aimed, edge, null).Failure, Is.EqualTo(AbilityFailure.None));
            Assert.That(abilities.Check(aimed, beyond, null).Failure, Is.EqualTo(AbilityFailure.OutOfRange));
        }

        [UnityTest]
        public IEnumerator Check_AWallBlocksAnAbilityThatNeedsSight_ButNotOneThatDoesNot()
        {
            yield return null;
            var walledHostile = world.CreateDummy(new Vector3(-4f, 0f, 2f));
            var walledAllyUnit = world.CreateFighter(new Vector3(-3f, 0f, 0f));
            var walledAlly = walledAllyUnit.GetComponent<Health>();
            encounter.Initialize(new[] { casterHealth, ally, walledAlly }, new[] { hostile, walledHostile });
            yield return null;

            var blocked = abilities.Check(aimed, walledHostile, null);
            Assert.That(blocked.Failure, Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(blocked.SightClear, Is.False);
            Assert.That(abilities.Check(mend, walledAlly, null).Failure, Is.EqualTo(AbilityFailure.None), "Mend ignores walls");
            Assert.That(abilities.Check(blast, null, new Vector3(-3f, 0f, 0f)).Failure, Is.EqualTo(AbilityFailure.NoLineOfSight),
                "A blast needs a clear line to its point");
            Assert.That(abilities.Check(blast, null, new Vector3(3f, 0f, 2f)).Failure, Is.EqualTo(AbilityFailure.None));
        }

        [UnityTest]
        public IEnumerator Check_AGroundAbilityWithoutAPoint_HasNoPosition()
        {
            yield return null;
            Assert.That(abilities.Check(blast, null, null).Failure, Is.EqualTo(AbilityFailure.NoPosition));
        }

        [UnityTest]
        public IEnumerator Check_TheStaticScope_SkipsRangeSightAndCooldown_ButNotWhoOrWhat()
        {
            yield return null;
            var far = world.CreateDummy(new Vector3(15f, 0f, 15f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });

            Assert.That(abilities.Check(aimed, far, null, AbilityCheckScope.Full).Failure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.Check(aimed, far, null, AbilityCheckScope.Static).Failure, Is.EqualTo(AbilityFailure.None));
            Assert.That(abilities.Check(aimed, ally, null, AbilityCheckScope.Static).Failure, Is.EqualTo(AbilityFailure.WrongSide));
        }

        [UnityTest]
        public IEnumerator TryUse_SingleTargetDamage_AppliesTheAmount_AndStartsTheCooldown()
        {
            yield return null;
            var used = 0;
            abilities.Used += _ => used++;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45));
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(6f).Within(0.1f));
            Assert.That(abilities.IsReady(0), Is.False);
            Assert.That(abilities.IsReady(1), Is.True, "Other slots are unaffected");
            Assert.That(used, Is.EqualTo(1));
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TryUse_AFailure_RecordsTheReason_AndStartsNoCooldown()
        {
            yield return null;
            var reported = AbilityFailure.None;
            abilities.Failed += (_, failure) => reported = failure;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, ally)), Is.False);

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(abilities.LastFailedAbility, Is.SameAs(aimed));
            Assert.That(reported, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(0f));
            Assert.That(ally.Current, Is.EqualTo(ally.Max));
        }

        [UnityTest]
        public IEnumerator TryUse_WhileOnCooldown_IsRefused()
        {
            yield return null;
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OnCooldown));
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45), "No second hit");
        }

        [UnityTest]
        public IEnumerator Cooldowns_ArePerUnit_EvenWhenTwoUnitsShareOneDefinition()
        {
            yield return null;
            var secondUnit = world.CreateFighter(new Vector3(2f, 0f, -6f));
            var second = world.AddAbilities(secondUnit, encounter, aimed);
            encounter.Initialize(new[] { casterHealth, ally, secondUnit.GetComponent<Health>() }, new[] { hostile });

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            Assert.That(abilities.IsReady(0), Is.False);
            Assert.That(second.IsReady(0), Is.True, "The second unit's cooldown is its own");
            Assert.That(second.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 90));
        }

        [UnityTest]
        public IEnumerator Cooldown_FreezesWhilePaused_AndRunsAgainAfterResume()
        {
            yield return null;
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            pause.Pause();
            var frozen = abilities.CooldownRemaining(0);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(frozen).Within(0.001f), "The cooldown must not run while paused");

            pause.Resume();
            yield return new WaitForSeconds(0.5f);
            Assert.That(abilities.CooldownRemaining(0), Is.LessThan(frozen - 0.3f), "The cooldown runs again after the pause");
        }

        [UnityTest]
        public IEnumerator Mend_HealsAnAlly_ClampedAtMax()
        {
            yield return null;
            ally.TakeDamage(60);
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.True);
            Assert.That(ally.Current, Is.EqualTo(ally.Max - 20), "60 missing, 40 restored");

            var secondUnit = world.CreateFighter(new Vector3(2f, 0f, -6f));
            var second = world.AddAbilities(secondUnit, encounter, mend);
            encounter.Initialize(new[] { casterHealth, ally, secondUnit.GetComponent<Health>() }, new[] { hostile });
            Assert.That(second.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.True);

            Assert.That(ally.Current, Is.EqualTo(ally.Max), "Only the 20 that were missing");
        }

        [UnityTest]
        public IEnumerator Mend_OnTheCasterItself_IsAllowed()
        {
            yield return null;
            casterHealth.TakeDamage(50);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, casterHealth)), Is.True);

            Assert.That(casterHealth.Current, Is.EqualTo(casterHealth.Max - 10));
        }

        [UnityTest]
        public IEnumerator Mend_OnADeadAlly_IsRefused()
        {
            yield return null;
            ally.TakeDamage(ally.Max);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.TargetDead));
            Assert.That(ally.IsAlive, Is.False);
        }

        [UnityTest]
        public IEnumerator Blast_HitsOnlyHostilesInsideTheRadius_EdgeIncluded_NeverFriendliesOrTheCaster()
        {
            yield return null;
            var center = new Vector3(0f, 0f, 4f);
            var inside = world.CreateDummy(center);
            var onTheEdge = world.CreateDummy(new Vector3(3f, 0f, 4f));
            var outside = world.CreateDummy(new Vector3(3.1f, 0f, 4f));
            var friendlyInside = world.CreateFighter(new Vector3(1f, 0f, 4f));
            var friendlyHealth = friendlyInside.GetComponent<Health>();
            encounter.Initialize(new[] { casterHealth, ally, friendlyHealth }, new[] { hostile, inside, onTheEdge, outside });
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, center)), Is.True);

            Assert.That(inside.Current, Is.EqualTo(inside.Max - 35));
            Assert.That(onTheEdge.Current, Is.EqualTo(onTheEdge.Max - 35), "A unit exactly on the radius is hit");
            Assert.That(outside.Current, Is.EqualTo(outside.Max));
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max), "3.6 m from the blast point");
            Assert.That(friendlyHealth.Current, Is.EqualTo(friendlyHealth.Max), "No friendly fire");
            Assert.That(ally.Current, Is.EqualTo(ally.Max));
            Assert.That(casterHealth.Current, Is.EqualTo(casterHealth.Max));
            Assert.That(abilities.CooldownRemaining(1), Is.EqualTo(10f).Within(0.1f));
        }

        [UnityTest]
        public IEnumerator Blast_CentredOnTheCaster_NeverHurtsTheCaster_OrItsSide()
        {
            yield return null;
            var near = world.CreateDummy(new Vector3(2f, 0f, -6f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, near });
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(0f, 0f, -6f))), Is.True);

            Assert.That(near.Current, Is.EqualTo(near.Max - 35));
            Assert.That(casterHealth.Current, Is.EqualTo(casterHealth.Max));
            Assert.That(ally.Current, Is.EqualTo(ally.Max));
        }

        [UnityTest]
        public IEnumerator Blast_WithNobodyInTheRadius_StillSucceeds_AndStartsTheCooldown()
        {
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(-8f, 0f, -12f))), Is.True);

            Assert.That(abilities.UsedCount, Is.EqualTo(1));
            Assert.That(abilities.IsReady(1), Is.False);
        }

        [UnityTest]
        public IEnumerator Blast_IgnoresDeadAndDeactivatedHostiles()
        {
            yield return null;
            var center = new Vector3(0f, 0f, 4f);
            var alive = world.CreateDummy(center);
            var dead = world.CreateDummy(new Vector3(1f, 0f, 4f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, alive, dead });
            dead.TakeDamage(dead.Max);
            yield return null;

            Assert.That(() => abilities.TryUse(AbilityCommand.AtGround(blast, center)), Throws.Nothing);

            Assert.That(alive.Current, Is.EqualTo(alive.Max - 35));
            Assert.That(dead.IsAlive, Is.False);
            var area = new List<Health>();
            abilities.CollectArea(blast, center, area);
            Assert.That(area, Is.EqualTo(new[] { alive }));
        }

        [UnityTest]
        public IEnumerator Blast_OutOfRange_AndBehindAWall_AreRefused_WithoutACooldown()
        {
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(0f, 0f, 7f))), Is.False, "13 m, range 12");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(-3f, 0f, 0f))), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(abilities.CooldownRemaining(1), Is.EqualTo(0f));
        }

        [UnityTest]
        public IEnumerator AUnitWithNoEncounter_FailsClosed_WithoutAnException()
        {
            yield return null;
            var loner = world.CreateFighter(new Vector3(-6f, 0f, -6f));
            var lonerHealth = loner.GetComponent<Health>();
            var lonerAbilities = world.AddAbilities(loner, null, aimed, blast, mend);

            Assert.That(lonerAbilities.Check(aimed, hostile, null).Failure, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(lonerAbilities.Check(mend, ally, null).Failure, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(lonerAbilities.Check(mend, lonerHealth, null).Failure, Is.EqualTo(AbilityFailure.None), "It can still mend itself");
            Assert.That(() => lonerAbilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(0f, 0f, 4f))), Throws.Nothing);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
        }

        [UnityTest]
        public IEnumerator CanQueue_ChecksOnlyWhoAndWhat_AndCanStartNow_ChecksEverything()
        {
            yield return null;
            var far = world.CreateDummy(new Vector3(15f, 0f, 15f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });

            Assert.That(abilities.CanQueue(AbilityCommand.OnUnit(aimed, far)), Is.True);
            Assert.That(abilities.CanStartNow(AbilityCommand.OnUnit(aimed, far)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.CanQueue(AbilityCommand.OnUnit(aimed, ally)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.WrongSide));
        }

        [UnityTest]
        public IEnumerator TheCasterFacesItsAim_WhenItUsesAnAbility()
        {
            yield return null;
            caster.transform.rotation = Quaternion.LookRotation(Vector3.back);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            var toTarget = hostile.transform.position - caster.transform.position;
            toTarget.y = 0f;
            Assert.That(Vector3.Angle(caster.transform.forward, toTarget), Is.LessThan(1f));
        }
    }
}
