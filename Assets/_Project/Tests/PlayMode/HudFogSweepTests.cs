#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The fog leak sweep (spec section 7) on the shipped ProceduralMission scene under the Blind preset: at every step of
    /// start, a hostile observed, the hostile lost (last known), a Recon Scan and the scan run out, nothing the HUD shows
    /// (every Text under its canvas, hidden ones included, and every world mark) names, counts, locates or gives the
    /// health of a hostile the player does not observe, with the developer truth view off and on. Each check is made
    /// while the tactical pause holds the simulation still, so the knowledge the test compares against is the knowledge
    /// the HUD's last frame was built from.
    /// </summary>
    public class HudFogSweepTests : InputTestFixture
    {
        const float SamePoint = 0.01f;

        InputActionAsset actions;
        HudSceneRig rig;

        public override void Setup()
        {
            base.Setup();
            InputSystem.AddDevice<Keyboard>();
            InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = new HudSceneRig();
        }

        public override void TearDown()
        {
            HudSceneRig.TearDown();
            TestControls.Reset(actions);
            base.TearDown();
        }

        List<Health> LivingHostiles() =>
            rig.Director.Hostiles.Where(h => h != null).Select(h => h.GetComponent<Health>()).Where(h => h != null && h.IsAlive).ToList();

        /// <summary>
        /// The scene's mission on the known seed, hostiles held still (AI off) and each given a distinct health value, so
        /// a hostile's "current/max" can only appear on screen if that hostile's health is shown.
        /// </summary>
        IEnumerator Load(IntelligenceSettings intelligence = null)
        {
            yield return rig.LoadMission(HudSceneRig.Seed, intelligence);
            Assert.That(rig.Service.IsFogActive, Is.True, "the scene ships with fog");
            HoldHostiles();
            yield return null;
        }

        void HoldHostiles()
        {
            var hostiles = rig.Director.Hostiles;
            for (var i = 0; i < hostiles.Count; i++)
            {
                hostiles[i].GetComponent<EnemyAI>().enabled = false;
                hostiles[i].GetComponent<Health>().TakeDamage(i + 1);
            }
        }

        // Pauses, checks the HUD with the truth view off and on, and leaves the pause on.
        IEnumerator Sweep(string step)
        {
            if (!rig.Pause.IsPaused)
                rig.Pause.Pause();
            rig.Service.TruthView = false;
            yield return null;
            yield return null;
            AssertNothingLeaks(step + " (player view)");
            rig.Service.TruthView = true;
            yield return null;
            AssertNothingLeaks(step + " (truth view on)");
            rig.Service.TruthView = false;
            yield return null;
        }

        void AssertNothingLeaks(string step)
        {
            var service = rig.Service;
            var snapshot = rig.Hud.Snapshot;
            var texts = rig.AllTexts();
            var hostiles = LivingHostiles();
            var observed = hostiles.Where(service.CanTarget).ToList();
            var hidden = hostiles.Where(h => !service.CanTarget(h)).ToList();
            var discovered = hidden.Where(h => service.StateOfEnemy(h) == KnowledgeState.Discovered && service.TryLastKnown(h, out _)).ToList();

            foreach (var hostile in hidden)
            {
                var name = new Regex(@"\b" + Regex.Escape(hostile.name) + @"\b");
                var health = new Regex(@"(?<!\d)" + Regex.Escape(HudText.Health(hostile.Current, hostile.Max)) + @"(?!\d)");
                foreach (var text in texts)
                {
                    Assert.That(name.IsMatch(text), Is.False, $"{step}: the unobserved {hostile.name} is named in \"{text}\"");
                    Assert.That(health.IsMatch(text), Is.False, $"{step}: the unobserved {hostile.name}'s health is shown in \"{text}\"");
                }
                foreach (var mark in snapshot.Marks)
                {
                    if (TestWorld.HorizontalDistance(mark.World, hostile.transform.position) >= SamePoint)
                        continue;
                    // Only its last-known mark may sit where it stands (it has not moved since it was lost).
                    Assert.That(mark.Kind, Is.EqualTo(HudMarkKind.LastKnown), $"{step}: a {mark.Kind} mark sits on the unobserved {hostile.name}");
                    Assert.That(discovered, Has.Member(hostile), $"{step}: a last-known mark on {hostile.name}, which is not Discovered");
                }
            }

            Assert.That(rig.MarksOf(HudMarkKind.Hostile), Is.EqualTo(observed.Count), $"{step}: one hostile mark per observed hostile");
            Assert.That(rig.DrawnMarksOf(HudMarkKind.Hostile), Is.LessThanOrEqualTo(observed.Count), $"{step}: drawn hostile marks");
            foreach (var mark in snapshot.Marks.Where(m => m.Kind == HudMarkKind.Hostile))
                Assert.That(observed.Any(h => TestWorld.HorizontalDistance(mark.World, h.transform.position) < SamePoint), Is.True,
                    $"{step}: a hostile mark at {mark.World} on no observed hostile");

            Assert.That(rig.MarksOf(HudMarkKind.LastKnown), Is.EqualTo(discovered.Count), $"{step}: one last-known mark per Discovered hostile");
            Assert.That(rig.DrawnMarksOf(HudMarkKind.LastKnown), Is.LessThanOrEqualTo(discovered.Count), $"{step}: drawn last-known marks");
            foreach (var mark in snapshot.Marks.Where(m => m.Kind == HudMarkKind.LastKnown))
            {
                var matches = discovered.Any(h => service.TryLastKnown(h, out var point) && Vector3.Distance(point, mark.World) < SamePoint);
                Assert.That(matches, Is.True, $"{step}: the last-known mark at {mark.World} is not a Discovered hostile's last-known point");
            }

            if (snapshot.Target.Visible)
                Assert.That(service.CanTarget(snapshot.Target.Unit), Is.True, $"{step}: the target panel shows an unobserved hostile");
            if (rig.Hud.Target.Root.gameObject.activeSelf)
                Assert.That(snapshot.Target.Visible, Is.True, $"{step}: the target panel is up without a target");

            foreach (var objective in rig.Director.Runtime.Objectives.Where(o => !o.IsKnown))
            {
                foreach (var text in texts)
                    Assert.That(text.Contains(objective.Title), Is.False, $"{step}: the unknown objective {objective.Id} is named in \"{text}\"");
            }
            Assert.That(rig.ShownObjectiveRows(), Is.EqualTo(rig.ExpectedObjectiveRows()), $"{step}: the objective rows");
        }

        // A hostile no friendly can see, that a scan of `radius` around it reaches alone.
        Health LoneHiddenHostile(float radius)
        {
            var hostiles = LivingHostiles();
            var lone = hostiles.FirstOrDefault(h => !rig.Service.CanTarget(h)
                && hostiles.All(other => other == h || TestWorld.HorizontalDistance(other.transform.position, h.transform.position) > radius + 0.5f));
            Assert.That(lone, Is.Not.Null, "precondition: an unobserved hostile standing apart");
            return lone;
        }

        [UnityTest]
        public IEnumerator NoUnobservedHostile_Leaks_ThroughObservedLostAndScanned()
        {
            yield return Load();
            Assert.That(rig.Service.ListsUnknownObjectives, Is.True, "the Blind preset lists unknown objectives vaguely");
            Assert.That(LivingHostiles().Any(rig.Service.CanTarget), Is.False, "precondition: nothing is observed at start");

            // Start, with the controlled operative ordered to attack a hostile it has not observed: no target panel.
            rig.Pause.Pause();
            var controlled = rig.Active.Unit;
            var unseen = LivingHostiles()[0];
            Assert.That(controlled.Issue(new AttackCommand(unseen)), Is.True);
            Assert.That(controlled.AttackTarget, Is.SameAs(unseen), "precondition: the controlled unit's attack target is unobserved");
            yield return Sweep("start, attacking an unobserved hostile");
            Assert.That(rig.Hud.Target.Root.gameObject.activeSelf, Is.False, "no target panel for an unobserved attack target");
            Assert.That(rig.Hud.Snapshot.Queue.Count, Is.GreaterThan(0), "precondition: the queue shows the order");
            Assert.That(rig.Hud.Snapshot.Queue.Any(q => q.Text.Contains(unseen.name)), Is.False);
            controlled.Issue(new StopCommand());

            Assert.That(rig.Hud.Snapshot.Objectives.Any(o => o.Kind == HudObjectiveKind.Unknown), Is.True,
                "precondition: an unknown objective is listed by its vague title");

            // Observed: a short scan pulse on one hostile standing apart.
            var hostile = LoneHiddenHostile(0.5f);
            rig.Service.Scan(hostile.transform.position, 0.5f, 0.5f);
            Assert.That(rig.Service.CanTarget(hostile), Is.True, "precondition: the pulse observes it");
            yield return Sweep("observed");
            Assert.That(rig.MarksOf(HudMarkKind.Hostile), Is.GreaterThanOrEqualTo(1));

            // Targeting the observed hostile shows the panel, and only for it.
            Assert.That(controlled.Issue(new AttackCommand(hostile)), Is.True);
            yield return Sweep("observed and attacked");
            Assert.That(rig.Hud.Snapshot.Target.Unit, Is.SameAs(hostile), "an observed attack target fills the panel");
            controlled.Issue(new StopCommand());

            // Lost: the pulse runs out and no friendly sees it; it becomes Discovered with a last-known point.
            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => rig.Service.StateOfEnemy(hostile) == KnowledgeState.Discovered, 5f);
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Discovered), "precondition: the hostile was lost");
            Assert.That(controlled.Issue(new AttackCommand(hostile)), Is.True, "an order on a lost hostile");
            yield return Sweep("lost (last known), attacked");
            Assert.That(rig.MarksOf(HudMarkKind.LastKnown), Is.GreaterThanOrEqualTo(1));
            Assert.That(rig.Hud.Target.Root.gameObject.activeSelf, Is.False, "no target panel for a lost attack target");
            controlled.Issue(new StopCommand());

            // Recon Scan: the squad's scanner is ordered to scan the nearest unobserved hostile's ground. The hostiles stand
            // 30 m or more from the squad on this seed, beyond the scan's reach, so the operative walks into range first
            // (decision 029), seeing whatever its walk shows it, then casts.
            var caster = rig.Director.Friendlies.Select(f => f.GetComponent<UnitAbilities>())
                .First(a => a != null && Enumerable.Range(0, a.Count).Any(i => a.Definition(i).Effect == AbilityEffect.Reveal));
            var scan = Enumerable.Range(0, caster.Count).Select(caster.Definition).First(d => d.Effect == AbilityEffect.Reveal);
            var scanTarget = LivingHostiles().Where(h => !rig.Service.CanTarget(h))
                .OrderBy(h => Vector3.Distance(h.transform.position, caster.transform.position)).First();
            Assert.That(NavMesh.SamplePosition(scanTarget.transform.position, out var ground, 2f, NavMesh.AllAreas), Is.True);
            var aim = ground.position;
            var used = caster.UsedCount;
            rig.Pause.Resume();
            yield return null;
            Assert.That(caster.GetComponent<CommandableUnit>().Issue(AbilityCommand.AtGround(scan, aim)), Is.True, caster.LastFailure.ToString());
            yield return TestWorld.WaitUntil(() => caster.UsedCount > used, 30f);
            rig.Pause.Pause();
            Assert.That(caster.UsedCount, Is.GreaterThan(used), "precondition: the scan was cast");
            var scanned = LivingHostiles().Where(h => TestWorld.HorizontalDistance(h.transform.position, aim) < scan.Radius - 0.5f).ToList();
            Assert.That(scanned, Is.Not.Empty, "precondition: the scan circle holds a hostile");
            foreach (var seen in scanned)
                Assert.That(rig.Service.CanTarget(seen), Is.True, $"precondition: the scan observes {seen.name}");
            yield return Sweep("recon scan");

            // The scan runs out: what only the scan showed is lost again (last known); what the squad sees stays observed.
            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => rig.Service.Pulses.Count == 0, 15f);
            Assert.That(rig.Service.Pulses.Count, Is.Zero, "precondition: the scan ran out");
            yield return Sweep("scan expired");
            var lost = scanned.Where(h => !rig.Service.CanTarget(h)).ToList();
            foreach (var seen in lost)
                Assert.That(rig.Service.StateOfEnemy(seen), Is.EqualTo(KnowledgeState.Discovered), $"{seen.name} was lost after the scan");
            Assert.That(rig.MarksOf(HudMarkKind.LastKnown), Is.GreaterThanOrEqualTo(lost.Count), "the lost hostiles left last-known marks");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator UnknownObjectives_AreNeverNamed_WhetherTheyAreListedOrNot()
        {
            var settings = IntelligenceSettings.Blind();
            settings.showUnknownObjectives = false;
            yield return Load(settings);
            Assert.That(rig.Service.ListsUnknownObjectives, Is.False);
            Assert.That(rig.Director.Runtime.Objectives.Any(o => !o.IsKnown), Is.True, "precondition: an unknown objective");
            yield return Sweep("unknown objectives not listed");
            Assert.That(rig.Hud.Snapshot.Objectives.Any(o => o.Kind == HudObjectiveKind.Unknown), Is.False, "nothing vague is listed");
            foreach (var objective in rig.Director.Runtime.Objectives.Where(o => !o.IsKnown && !string.IsNullOrEmpty(o.VagueTitle)))
                Assert.That(rig.AllTexts().Any(t => t.Contains(objective.VagueTitle)), Is.False, $"{objective.Id}'s vague title is not listed");

            rig.Pause.Resume();
            rig.Director.Settings.intelligence = IntelligenceSettings.Blind();
            Assert.That(rig.Director.RegenerateSame(), Is.True);
            yield return rig.WaitForMission();
            HoldHostiles();
            yield return null;
            Assert.That(rig.Service.ListsUnknownObjectives, Is.True);
            yield return Sweep("unknown objectives listed");
            Assert.That(rig.Hud.Snapshot.Objectives.Any(o => o.Kind == HudObjectiveKind.Unknown), Is.True, "the vague titles are listed");
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
