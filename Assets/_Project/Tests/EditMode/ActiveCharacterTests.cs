using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ActiveCharacterTests
    {
        GameObject systems;
        GameObject unitHost;
        TacticalPause pause;
        ActiveCharacter active;
        CommandableUnit unit;
        readonly List<GameObject> squadHosts = new List<GameObject>();
        UnitSelection selection;
        SelectableUnit[] squad;

        [SetUp]
        public void SetUp()
        {
            Time.timeScale = 1f;
            systems = new GameObject("Systems");
            pause = systems.AddComponent<TacticalPause>();
            active = systems.AddComponent<ActiveCharacter>();
            unitHost = new GameObject("Primary");
            unit = unitHost.AddComponent<CommandableUnit>();
            active.Initialize(unit, pause);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(systems);
            Object.DestroyImmediate(unitHost);
            foreach (var host in squadHosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            squadHosts.Clear();
            Time.timeScale = 1f;
        }

        [Test]
        public void StartsInFreeMode()
        {
            Assert.That(active.IsTakeoverOn, Is.False);
            Assert.That(active.IsDriving, Is.False);
        }

        [Test]
        public void FollowStartsOn()
        {
            Assert.That(active.IsFollowOn, Is.True);
        }

        [Test]
        public void ToggleFollow_TurnsItOffAndOn()
        {
            active.ToggleFollow();
            Assert.That(active.IsFollowOn, Is.False);
            active.ToggleFollow();
            Assert.That(active.IsFollowOn, Is.True);
        }

        [Test]
        public void SetFollow_SetsTheFlag_AndIsIdempotent()
        {
            active.SetFollow(false);
            active.SetFollow(false);
            Assert.That(active.IsFollowOn, Is.False);
            active.SetFollow(true);
            active.SetFollow(true);
            Assert.That(active.IsFollowOn, Is.True);
        }

        [Test]
        public void FollowFlag_IsIndependentOfTakeover()
        {
            active.SetFollow(false);
            active.ToggleTakeover();
            Assert.That(active.IsFollowOn, Is.False);
            active.SetFollow(true);
            Assert.That(active.IsTakeoverOn, Is.True, "Follow must not touch takeover");
        }

        [Test]
        public void Cycle_LeavesTheFollowFlagAlone()
        {
            CreateSquad();
            active.SetFollow(false);
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.IsFollowOn, Is.False);
            active.SetFollow(true);
            Assert.That(active.Cycle(-1), Is.True);
            Assert.That(active.IsFollowOn, Is.True);
        }

        [Test]
        public void ExposesItsUnit()
        {
            Assert.That(active.Unit, Is.SameAs(unit));
            Assert.That(active.HasUnit, Is.True);
        }

        [Test]
        public void ToggleTakeover_TurnsItOnAndOff()
        {
            active.ToggleTakeover();
            Assert.That(active.IsTakeoverOn, Is.True);
            active.ToggleTakeover();
            Assert.That(active.IsTakeoverOn, Is.False);
        }

        [Test]
        public void SetTakeover_SetsTheMode()
        {
            active.SetTakeover(true);
            active.SetTakeover(true);
            Assert.That(active.IsTakeoverOn, Is.True);
            active.SetTakeover(false);
            Assert.That(active.IsTakeoverOn, Is.False);
        }

        [Test]
        public void IsDriving_WithTakeoverOn_Unpaused_AndAnActiveUnit()
        {
            active.SetTakeover(true);
            Assert.That(active.IsDriving, Is.True);
        }

        [Test]
        public void IsDriving_FalseWhilePaused_AndBackOnResume()
        {
            active.SetTakeover(true);
            pause.Pause();
            Assert.That(active.IsPaused, Is.True);
            Assert.That(active.IsDriving, Is.False);
            pause.Resume();
            Assert.That(active.IsDriving, Is.True);
        }

        [Test]
        public void ToggleTakeover_WhilePaused_TakesEffectOnResume()
        {
            pause.Pause();
            active.ToggleTakeover();
            Assert.That(active.IsTakeoverOn, Is.True);
            Assert.That(active.IsDriving, Is.False);
            pause.Resume();
            Assert.That(active.IsDriving, Is.True);
        }

        [Test]
        public void HasUnit_FalseWithoutAUnit()
        {
            active.Initialize(null, pause);
            active.SetTakeover(true);
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
        }

        [Test]
        public void HasUnit_FalseWhenTheUnitIsDisabled()
        {
            active.SetTakeover(true);
            unit.enabled = false;
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
        }

        [Test]
        public void HasUnit_FalseWhenTheUnitIsInactive()
        {
            active.SetTakeover(true);
            unitHost.SetActive(false);
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
        }

        [Test]
        public void IsPaused_FalseWithoutAPauseService()
        {
            active.Initialize(unit, null);
            active.SetTakeover(true);
            Assert.That(active.IsPaused, Is.False);
            Assert.That(active.IsDriving, Is.True);
        }

        // --- Cycling. Three friendlies A, B, C in roster order; A is active.

        void CreateSquad()
        {
            squad = new[] { CreateFriendly("A"), CreateFriendly("B"), CreateFriendly("C") };
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(squad);
            active.Initialize(squad[0].Unit, pause, selection);
        }

        SelectableUnit CreateFriendly(string name)
        {
            var host = new GameObject(name);
            squadHosts.Add(host);
            return host.AddComponent<SelectableUnit>();
        }

        // A dead unit that stays active, so the test checks Health and not just activeInHierarchy.
        static void KillWithoutDeactivating(GameObject host)
        {
            var health = host.AddComponent<Health>();
            var serialized = new SerializedObject(health);
            serialized.FindProperty("disableOnDeath").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            health.TakeDamage(health.Max);
            Assert.That(health.IsAlive, Is.False);
            Assert.That(host.activeInHierarchy, Is.True);
        }

        [Test]
        public void Cycle_Forward_VisitsEachFriendly_AndWraps()
        {
            CreateSquad();
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void Cycle_Backward_VisitsEachFriendly_AndWraps()
        {
            CreateSquad();
            Assert.That(active.Cycle(-1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
            Assert.That(active.Cycle(-1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
            Assert.That(active.Cycle(-1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void Cycle_SkipsAnInactiveFriendly()
        {
            CreateSquad();
            squad[1].gameObject.SetActive(false);
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_SkipsAFriendlyWithItsSelectableUnitDisabled()
        {
            CreateSquad();
            squad[1].enabled = false;
            active.Cycle(1);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_SkipsAFriendlyWithItsCommandableUnitDisabled()
        {
            CreateSquad();
            squad[1].Unit.enabled = false;
            active.Cycle(1);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_SkipsADeadFriendly()
        {
            CreateSquad();
            KillWithoutDeactivating(squad[1].gameObject);
            active.Cycle(1);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_AFriendlyWithLivingHealth_IsEligible()
        {
            CreateSquad();
            squad[1].gameObject.AddComponent<Health>();
            active.Cycle(1);
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
        }

        [Test]
        public void Cycle_OnlyOneEligible_KeepsItAndReturnsFalse()
        {
            CreateSquad();
            squad[1].gameObject.SetActive(false);
            squad[2].gameObject.SetActive(false);
            Assert.That(active.Cycle(1), Is.False);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            Assert.That(active.Cycle(-1), Is.False);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void Cycle_NothingEligible_ReturnsFalse_AndKeepsTheUnit()
        {
            CreateSquad();
            foreach (var friendly in squad)
                friendly.gameObject.SetActive(false);
            Assert.That(active.Cycle(1), Is.False);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void Cycle_WithoutASelection_ReturnsFalse()
        {
            Assert.That(active.Cycle(1), Is.False);
            Assert.That(active.Unit, Is.SameAs(unit));
        }

        [Test]
        public void Cycle_EmptyRoster_ReturnsFalse()
        {
            selection = systems.AddComponent<UnitSelection>();
            active.Initialize(unit, pause, selection);
            Assert.That(active.Cycle(1), Is.False);
            Assert.That(active.Unit, Is.SameAs(unit));
        }

        [Test]
        public void Cycle_SkipsNullAndDestroyedRosterEntries()
        {
            var a = CreateFriendly("A");
            var b = CreateFriendly("B");
            var c = CreateFriendly("C");
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(a, null, b, c);
            active.Initialize(a.Unit, pause, selection);
            Object.DestroyImmediate(b.gameObject);

            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(c.Unit));
        }

        [Test]
        public void Cycle_ActiveUnitNotInTheRoster_StartsFromTheEnds()
        {
            CreateSquad();
            active.SetUnit(unit);
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            active.SetUnit(unit);
            Assert.That(active.Cycle(-1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_FromAnIneligibleActiveUnit_MovesOn()
        {
            CreateSquad();
            squad[0].gameObject.SetActive(false);
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
        }

        [Test]
        public void SetUnit_ChangesTheUnit_AndNullClearsIt()
        {
            CreateSquad();
            active.SetUnit(squad[2].Unit);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
            active.SetUnit(null);
            Assert.That(active.Unit, Is.Null);
            Assert.That(active.HasUnit, Is.False);
        }

        [Test]
        public void IsEligible_NullIsFalse()
        {
            Assert.That(ActiveCharacter.IsEligible((SelectableUnit)null), Is.False);
        }

        // --- Hand-over when the active unit stops being eligible.

        // TryGetComponent, not GetComponent with ??: in the Editor a missing component is a placeholder, not a C# null.
        static void Kill(GameObject host)
        {
            if (!host.TryGetComponent<Health>(out var health))
                health = host.AddComponent<Health>();
            health.TakeDamage(health.Max);
        }

        [Test]
        public void RefreshEligibility_WhileEligible_ChangesNothing()
        {
            CreateSquad();
            active.SetTakeover(true);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            Assert.That(active.IsTakeoverOn, Is.True);
        }

        [Test]
        public void RefreshEligibility_WhenTheActiveUnitDies_MovesToTheNextFriendly()
        {
            CreateSquad();
            Kill(squad[0].gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
            Assert.That(active.HasUnit, Is.True);
        }

        [Test]
        public void RefreshEligibility_DeadButStillActiveUnit_IsReplaced()
        {
            CreateSquad();
            KillWithoutDeactivating(squad[0].gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
        }

        [Test]
        public void RefreshEligibility_WrapsToTheFirstFriendly()
        {
            CreateSquad();
            active.SetUnit(squad[2].Unit);
            Kill(squad[2].gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void RefreshEligibility_SkipsDeadFriendlies()
        {
            CreateSquad();
            Kill(squad[0].gameObject);
            Kill(squad[1].gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void RefreshEligibility_WithNobodyEligible_ClearsTheUnit_AndKeepsTakeover()
        {
            CreateSquad();
            active.SetTakeover(true);
            foreach (var friendly in squad)
                Kill(friendly.gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.Null);
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
            Assert.That(active.IsTakeoverOn, Is.True, "Takeover mode is the player's choice; death does not change it");
            active.RefreshEligibility();   // idempotent with no unit
            Assert.That(active.Unit, Is.Null);
        }

        [Test]
        public void RefreshEligibility_DeactivatedOrDisabledUnit_IsReplaced()
        {
            CreateSquad();
            squad[0].gameObject.SetActive(false);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));

            squad[1].Unit.enabled = false;
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void RefreshEligibility_WithoutASelection_ClearsADeadUnit()
        {
            Kill(unitHost);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.Null);
        }

        [Test]
        public void IsEligible_CommandableUnit_Cases()
        {
            Assert.That(ActiveCharacter.IsEligible((CommandableUnit)null), Is.False);
            Assert.That(ActiveCharacter.IsEligible(unit), Is.True, "A living unit without Health or SelectableUnit is eligible");
            unit.enabled = false;
            Assert.That(ActiveCharacter.IsEligible(unit), Is.False);
            unit.enabled = true;
            unitHost.SetActive(false);
            Assert.That(ActiveCharacter.IsEligible(unit), Is.False);
            unitHost.SetActive(true);
            var selectable = unitHost.AddComponent<SelectableUnit>();
            selectable.enabled = false;
            Assert.That(ActiveCharacter.IsEligible(unit), Is.False, "A disabled SelectableUnit makes the unit ineligible");
            selectable.enabled = true;
            KillWithoutDeactivating(unitHost);
            Assert.That(ActiveCharacter.IsEligible(unit), Is.False);
        }

        [Test]
        public void IsEligible_SelectableUnit_AgreesWithTheCommandableUnitOverload()
        {
            CreateSquad();
            Assert.That(ActiveCharacter.IsEligible(squad[0]), Is.EqualTo(ActiveCharacter.IsEligible(squad[0].Unit)));
            Kill(squad[0].gameObject);
            Assert.That(ActiveCharacter.IsEligible(squad[0]), Is.False);
            Assert.That(ActiveCharacter.IsEligible(squad[0].Unit), Is.False);
        }
    }
}
