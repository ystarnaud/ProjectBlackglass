#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class MissionPresentationPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        // A melee hit: the mission geometry puts walls between any two units, and a blocked ranged shot draws no body impact.
        static ShotRecord SyntheticShot(CommandableUnit shooter, CommandableUnit target, int sequence) =>
            new ShotRecord(shooter.GetInstanceID(), sequence, shooter.GetComponent<Health>(), target.GetComponent<Health>(),
                shooter.transform.position + Vector3.up * 0.5f, target.transform.position + Vector3.up * ShotRecord.BodyHeight, ShotOutcome.Hit, true, null);

        // The shipped Blast ability (its explosion profile has a light and a sound) resolving at the unit's feet.
        static AreaRecord RealBlastAt(CommandableUnit caster)
        {
            var blast = UnityEditor.AssetDatabase.LoadAssetAtPath<AbilityDefinition>("Assets/_Project/Data/Abilities/Blast.asset");
            Assert.That(blast, Is.Not.Null);
            Assert.That(blast.Presentation, Is.Not.Null, "Precondition: Blast has its explosion profile");
            return new AreaRecord(caster.GetInstanceID() + 1, 1, caster.GetComponent<Health>(), blast,
                caster.transform.position + Vector3.down * 0.95f, 3f);
        }

        static void AssertSamePositions(Vector3[] actual, Vector3[] expected, string side)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), side + " count");
            for (var i = 0; i < actual.Length; i++)
                Assert.That(Vector3.Distance(actual[i], expected[i]), Is.LessThan(0.05f), side + " " + i + " spawned at the same place");
        }

        [UnityTest]
        public IEnumerator EveryMissionUnit_GetsAUnitPresentation_BoundToThePresenter()
        {
            rig = new MissionRig();
            var presenter = rig.AddPresentation();
            yield return rig.Generate(7);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            foreach (var unit in rig.Director.Friendlies.Concat(rig.Director.Hostiles))
            {
                Assert.That(unit.TryGetComponent(out UnitPresentation presentation), Is.True, unit.name);
                Assert.That(presentation.Presenter, Is.EqualTo(presenter), unit.name);
            }
        }

        [UnityTest]
        public IEnumerator WithoutAPresenter_NoUnitGetsAUnitPresentation()
        {
            rig = new MissionRig();
            yield return rig.Generate(7);
            foreach (var unit in rig.Director.Friendlies.Concat(rig.Director.Hostiles))
                Assert.That(unit.GetComponent<UnitPresentation>(), Is.Null, unit.name);
        }

        [UnityTest]
        public IEnumerator Regeneration_ReleasesEveryEffect_StopsAudio_AndLeavesNoStaleSubscriptions()
        {
            rig = new MissionRig();
            var presenter = rig.AddPresentation();
            yield return rig.Generate(7);
            var shooter = rig.Director.Friendlies[0];
            var target = rig.Director.Hostiles[0];
            shooter.GetComponent<UnitPresentation>().Deliver(SyntheticShot(shooter, target, 1));
            // The real Blast at the shooter's feet: an explosion that holds a light and plays a sound.
            shooter.GetComponent<UnitPresentation>().Deliver(RealBlastAt(shooter));
            Assert.That(presenter.ActiveEffects, Is.GreaterThan(0), "Precondition: something is playing");
            Assert.That(presenter.Budget.LightsActive, Is.GreaterThan(0), "Precondition: a light is held");
            Assert.That(presenter.Audio.ActiveVoices, Is.GreaterThan(0), "Precondition: a sound is playing");

            yield return rig.Generate(11);

            Assert.That(presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(presenter.Budget.LightsActive, Is.EqualTo(0));
            Assert.That(presenter.Audio.ActiveVoices, Is.EqualTo(0));
            Assert.That(Object.FindObjectsByType<UnitPresentation>(FindObjectsSortMode.None).Length,
                Is.EqualTo(rig.Director.Friendlies.Count + rig.Director.Hostiles.Count), "only the new mission's units remain");
        }

        [UnityTest]
        public IEnumerator RegenerationWhilePaused_ClearsAndResumes_WithoutRestartingOrLeaking()
        {
            rig = new MissionRig();
            var presenter = rig.AddPresentation();
            yield return rig.Generate(7);
            var shooter = rig.Director.Friendlies[0];
            var target = rig.Director.Hostiles[0];
            shooter.GetComponent<UnitPresentation>().Deliver(SyntheticShot(shooter, target, 1));
            shooter.GetComponent<UnitPresentation>().Deliver(RealBlastAt(shooter));
            Assert.That(presenter.ActiveEffects, Is.GreaterThan(0), "Precondition: something is playing");
            Assert.That(presenter.Audio.ActiveVoices, Is.GreaterThan(0), "Precondition: a sound is playing");
            rig.Pause.Pause();
            Assert.That(presenter.Audio.IsPaused, Is.True);

            yield return rig.Generate(11);

            Assert.That(rig.Pause.IsPaused, Is.False, "the director resumes the pause service");
            Assert.That(presenter.Audio.IsPaused, Is.False);
            Assert.That(presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(presenter.Budget.LightsActive, Is.EqualTo(0));
            Assert.That(presenter.Audio.ActiveVoices, Is.EqualTo(0), "the paused sounds were stopped, not resumed");
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator PresenterOff_LeavesTheGeneratedMissionIdentical()
        {
            rig = new MissionRig();
            var presenter = rig.AddPresentation();
            presenter.Enabled = false;
            yield return rig.Generate(5);
            var rooms = rig.Director.Current.Layout.Rooms.Select(r => r.Rect).ToArray();
            var friendlies = rig.Director.Friendlies.Select(u => u.transform.position).ToArray();
            var hostiles = rig.Director.Hostiles.Select(u => u.transform.position).ToArray();
            rig.Dispose();
            yield return null;   // the first mission's objects are destroyed at the end of the frame

            rig = new MissionRig();
            yield return rig.Generate(5);
            Assert.That(rig.Director.Current.Layout.Rooms.Select(r => r.Rect).ToArray(), Is.EqualTo(rooms), "the same rooms, the same rectangles");
            AssertSamePositions(rig.Director.Friendlies.Select(u => u.transform.position).ToArray(), friendlies, "friendly");
            AssertSamePositions(rig.Director.Hostiles.Select(u => u.transform.position).ToArray(), hostiles, "hostile");
        }
    }
}
#endif
