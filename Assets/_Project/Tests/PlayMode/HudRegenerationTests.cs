#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// The HUD across mission regeneration (F6 same seed, F7 new seed), a regeneration in the middle of a fight, a squad
    /// member dying, a fog reset, and the populated HUD's layout on the live scene canvas (spec section 10): the HUD keeps
    /// no per-mission reference between frames, so nothing stale is shown and nothing throws.
    /// </summary>
    public class HudRegenerationTests : InputTestFixture
    {
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

        IEnumerator Regenerate(bool sameSeed)
        {
            Assert.That(sameSeed ? rig.Director.RegenerateSame() : rig.Director.GenerateNew(), Is.True);
            yield return rig.WaitForMission();
            yield return null;
            yield return null;   // the old mission's objects are destroyed at the end of the regeneration frame
        }

        // Everything the HUD shows belongs to the current mission.
        void AssertBoundToTheCurrentMission(string step)
        {
            var friendlies = rig.Director.Friendlies;
            var snapshot = rig.Hud.Snapshot;
            Assert.That(rig.Hud.enabled && rig.Hud.isActiveAndEnabled, Is.True, $"{step}: the HUD is still running");
            Assert.That(snapshot.Squad.Count, Is.EqualTo(friendlies.Count), $"{step}: one card per squad member");
            foreach (var card in snapshot.Squad)
            {
                Assert.That(card.Unit != null, Is.True, $"{step}: a card refers to a destroyed unit");
                Assert.That(friendlies, Has.Member(card.Unit), $"{step}: a card refers to {card.Unit.name}, not of this mission");
            }
            Assert.That(rig.VisibleCards(), Is.EqualTo(friendlies.Count), $"{step}: visible cards");
            for (var i = 0; i < friendlies.Count; i++)
                Assert.That(rig.Hud.Squad.CardAt(i).Card.Unit, Is.SameAs(friendlies[i]), $"{step}: card {i}");
            // Every pooled mark beyond the snapshot's is hidden and blank; none drawn is from another mission.
            var marks = rig.Hud.Marks;
            Assert.That(marks.VisibleCount, Is.LessThanOrEqualTo(snapshot.Marks.Count), $"{step}: no more marks drawn than the snapshot holds");
            for (var i = snapshot.Marks.Count; i < marks.PoolCount; i++)
            {
                Assert.That(marks.View(i).Root.gameObject.activeSelf, Is.False, $"{step}: pooled mark {i} is hidden");
                Assert.That(marks.View(i).Label.text, Is.Empty, $"{step}: pooled mark {i} keeps no label");
            }
            Assert.That(rig.ShownObjectiveRows(), Is.EqualTo(rig.ExpectedObjectiveRows()), $"{step}: objective rows from the new runtime");
            Assert.That(rig.Hud.Status.ExtractionLabel.text, Is.EqualTo(rig.ExpectedExtractionLabel()), $"{step}: extraction");
            if (rig.Active.HasUnit)
            {
                Assert.That(friendlies, Has.Member(rig.Active.Unit));
                Assert.That(rig.Hud.Operative.NameLabel.text, Is.EqualTo(rig.Active.Unit.GetComponent<UnitIdentity>().DisplayName),
                    $"{step}: the controlled panel names the new controlled operative");
            }
            if (snapshot.QueueUnit != null)
                Assert.That(friendlies, Has.Member(snapshot.QueueUnit), $"{step}: the queue subject");
            if (snapshot.Target.Visible)
                Assert.That(rig.Director.Hostiles.Select(h => h.GetComponent<Health>()), Has.Member(snapshot.Target.Unit), $"{step}: the target");
        }

        [UnityTest]
        public IEnumerator RegenerateSame_ThenGenerateNew_LeavesNoStaleCardLabelOrRow()
        {
            yield return rig.LoadMission(HudSceneRig.Seed);
            AssertBoundToTheCurrentMission("first mission");
            var oldUnits = rig.Director.Friendlies.ToArray();
            var pool = rig.Hud.Marks.PoolCount;

            yield return Regenerate(sameSeed: true);
            Assert.That(oldUnits.All(u => u == null), Is.True, "precondition: the old squad is destroyed");
            AssertBoundToTheCurrentMission("same seed (F6)");
            Assert.That(rig.Hud.Marks.PoolCount, Is.LessThanOrEqualTo(Mathf.Max(pool, rig.Hud.Snapshot.Marks.Count)),
                "the mark pool is reused, not grown, by a regeneration");

            oldUnits = rig.Director.Friendlies.ToArray();
            pool = rig.Hud.Marks.PoolCount;
            yield return Regenerate(sameSeed: false);
            Assert.That(oldUnits.All(u => u == null), Is.True, "precondition: the old squad is destroyed");
            AssertBoundToTheCurrentMission("new seed (F7)");
            Assert.That(rig.Hud.Marks.PoolCount, Is.LessThanOrEqualTo(Mathf.Max(pool, rig.Hud.Snapshot.Marks.Count)));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator Regenerating_MidFight_LeavesNoStaleTargetQueueOrCard()
        {
            yield return rig.LoadMission(HudSceneRig.Seed, IntelligenceSettings.Full());
            var hostile = rig.Director.Hostiles[0].GetComponent<Health>();
            foreach (var friendly in rig.Director.Friendlies)
                Assert.That(friendly.Issue(new AttackCommand(hostile)), Is.True);
            bool Fighting() => rig.Director.Friendlies.Concat(rig.Director.Hostiles)
                .Select(u => u.GetComponent<Health>()).Any(h => h.Current < h.Max);
            yield return TestWorld.WaitUntil(Fighting, 30f);
            Assert.That(Fighting(), Is.True, "precondition: the fight started (someone took damage)");
            Assert.That(rig.Hud.Snapshot.QueueUnit, Is.Not.Null, "precondition: the HUD shows an order");

            yield return Regenerate(sameSeed: true);
            AssertBoundToTheCurrentMission("regenerated mid-fight");
            Assert.That(rig.Hud.Snapshot.Queue.Count, Is.Zero, "the new squad has no orders");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator FromFullKnowledgeToBlind_NoUnknownObjectiveTitle_SurvivesAnywhereInTheHud()
        {
            // Full knowledge: every objective is known, so its rows and world marks carry the real titles.
            yield return rig.LoadMission(HudSceneRig.Seed, IntelligenceSettings.Full());
            var titles = rig.Director.Runtime.Objectives.Where(o => o != rig.Director.Runtime.Extraction).Select(o => o.Title).ToList();
            Assert.That(rig.MarksOf(HudMarkKind.Objective), Is.GreaterThan(0), "precondition: objective marks with labels");
            Assert.That(titles.Any(t => rig.AllTexts().Any(text => text.Contains(t))), Is.True, "precondition: a real title is on screen");

            rig.Director.Settings.intelligence = IntelligenceSettings.Blind();
            yield return Regenerate(sameSeed: true);
            var unknown = rig.Director.Runtime.Objectives.Where(o => !o.IsKnown).ToList();
            Assert.That(unknown, Is.Not.Empty, "precondition: the blind mission hides objectives");
            var texts = rig.AllTexts();
            foreach (var objective in unknown)
                Assert.That(texts.Any(text => text.Contains(objective.Title)), Is.False,
                    $"the unknown {objective.Id}'s title \"{objective.Title}\" survives in the HUD (hidden or pooled texts included)");
            AssertBoundToTheCurrentMission("blind after full");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator TheControlledOperativeDying_ShowsDown_AndControlAndThePanelPassOn()
        {
            yield return rig.LoadMission(HudSceneRig.Seed);
            var dying = rig.Active.Unit;
            var dyingName = dying.GetComponent<UnitIdentity>().DisplayName;
            Assert.That(rig.Hud.Operative.NameLabel.text, Is.EqualTo(dyingName), "precondition");

            dying.GetComponent<Health>().TakeDamage(100000);
            yield return null;
            yield return null;

            var card = rig.CardOf(dying);
            Assert.That(card.Card.IsDown, Is.True);
            Assert.That(card.DownLabel.gameObject.activeSelf, Is.True, "the card says DOWN");
            Assert.That(card.DownLabel.text, Is.EqualTo("DOWN"));
            Assert.That(card.ControlLabel.gameObject.activeSelf, Is.False, "a down operative is not in control");
            var next = rig.Active.Unit;
            Assert.That(next, Is.Not.Null.And.Not.SameAs(dying), "control passed on");
            Assert.That(next.IsAlive, Is.True);
            Assert.That(rig.CardOf(next).ControlLabel.gameObject.activeSelf, Is.True);
            Assert.That(rig.Hud.Operative.NameLabel.text, Is.EqualTo(next.GetComponent<UnitIdentity>().DisplayName), "the controlled panel follows");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator AFogReset_LeavesNoHostileOrLastKnownMarkFromTheOldMission()
        {
            yield return rig.LoadMission(HudSceneRig.Seed);
            foreach (var unit in rig.Director.Hostiles)
                unit.GetComponent<EnemyAI>().enabled = false;
            var hostile = rig.Director.Hostiles[0].GetComponent<Health>();
            rig.Service.Scan(hostile.transform.position, 0.5f, 0.3f);
            yield return TestWorld.WaitUntil(() => rig.Service.StateOfEnemy(hostile) == KnowledgeState.Discovered, 5f);
            yield return null;
            Assert.That(rig.MarksOf(HudMarkKind.LastKnown), Is.GreaterThanOrEqualTo(1), "precondition: a last-known mark");

            // F6's path: the knowledge is cleared and a new mission begins, in the same frame.
            rig.Service.Clear();
            yield return Regenerate(sameSeed: true);
            var observed = rig.Director.Hostiles.Count(h => rig.Service.CanTarget(h.GetComponent<Health>()));
            Assert.That(rig.MarksOf(HudMarkKind.LastKnown), Is.Zero, "no last-known mark survives the reset");
            Assert.That(rig.DrawnMarksOf(HudMarkKind.LastKnown), Is.Zero);
            Assert.That(rig.MarksOf(HudMarkKind.Hostile), Is.EqualTo(observed), "hostile marks only for what the new mission observes");
            Assert.That(rig.DrawnMarksOf(HudMarkKind.Hostile), Is.LessThanOrEqualTo(observed));
            AssertBoundToTheCurrentMission("after the fog reset");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator ThePopulatedHud_OnTheLiveCanvas_StaysInsideAndDoesNotOverlap()
        {
            yield return rig.LoadMission(HudSceneRig.Seed);
            rig.Pause.Pause();   // the pause banner up as well
            yield return null;
            yield return null;
            Assert.That(rig.VisibleCards(), Is.GreaterThan(0), "precondition: squad cards");
            Assert.That(rig.Hud.Status.PauseBanner.gameObject.activeInHierarchy, Is.True, "precondition: the pause banner");
            Assert.That(rig.Hud.Objectives.Root.gameObject.activeInHierarchy, Is.True, "precondition: the objectives");

            // The live canvas, at the test runner's own screen size: everything inside it.
            var root = rig.Hud.Root;
            var status = rig.Hud.Status;
            HudLayout.Rebuild(root);
            HudLayout.AssertInside(root, rig.Hud.Objectives.Root, status.PauseBanner, status.RightBlock, rig.Hud.Squad.Root,
                rig.Hud.Operative.Root, rig.Hud.Prompts.Root, rig.Hud.Target.Root);

            // The same live, populated panels at the supported aspect ratios: the overlay canvas is sized by the screen, so
            // it is switched to world space for the check, sized as the CanvasScaler would size it, and switched back.
            var canvas = root.GetComponent<Canvas>();
            var scaler = root.GetComponent<CanvasScaler>();
            try
            {
                scaler.enabled = false;
                canvas.renderMode = RenderMode.WorldSpace;
                root.localScale = Vector3.one;
                root.position = Vector3.zero;
                foreach (var (width, height) in new[] { (1920, 1080), (1920, 1200), (2560, 1080) })
                {
                    root.sizeDelta = HudLayout.CanvasUnits(width, height);
                    HudLayout.Rebuild(root);
                    var label = $" at {width}x{height}";
                    Assert.That(rig.VisibleCards(), Is.GreaterThan(0), "still populated" + label);
                    HudLayout.AssertInside(root, rig.Hud.Objectives.Root, status.PauseBanner, status.RightBlock, rig.Hud.Squad.Root,
                        rig.Hud.Operative.Root, rig.Hud.Prompts.Root, rig.Hud.Target.Root);
                    HudLayout.AssertNoOverlap(rig.Hud.Objectives.Root, status.PauseBanner, status.ResultBanner, status.RightBlock,
                        rig.Hud.Squad.Footprint, rig.Hud.Operative.Root, rig.Hud.Prompts.Background, rig.Hud.Target.Root);
                    HudLayout.AssertNoOverlap(rig.Hud.Objectives.Root, status.PauseBanner, status.ResultBanner, status.RightBlock,
                        rig.Hud.Squad.Root, rig.Hud.Operative.Root, rig.Hud.Prompts.Root, rig.Hud.Target.Root);
                }
            }
            finally
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                scaler.enabled = true;
            }
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
