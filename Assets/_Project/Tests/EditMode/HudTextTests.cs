using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class HudTextTests
    {
        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in created)
            {
                if (item != null)
                    Object.DestroyImmediate(item);
            }
            created.Clear();
        }

        Health Unit(string name)
        {
            var host = new GameObject(name);
            created.Add(host);
            return host.AddComponent<Health>();
        }

        [Test] public void Health_IsCurrentOverMax() => Assert.That(HudText.Health(42, 60), Is.EqualTo("42/60"));

        [TestCase(0f, "0.0")] [TestCase(0.01f, "0.1")] [TestCase(4.21f, "4.3")] [TestCase(9.99f, "10")] [TestCase(24.2f, "25")]
        [TestCase(9.94f, "10")] [TestCase(9.92f, "10")] [TestCase(9.9f, "9.9")] [TestCase(9.89f, "9.9")] [TestCase(10f, "10")]
        public void Seconds_RoundsUp_AndDropsDecimalsFromTen(float value, string expected) =>
            Assert.That(HudText.Seconds(value), Is.EqualTo(expected));

        [TestCase("Darius", "D")] [TestCase("Kestrel Vale", "KV")] [TestCase("", "?")] [TestCase("  sable ", "S")]
        public void Initials(string name, string expected) => Assert.That(HudText.Initials(name), Is.EqualTo(expected));

        [TestCase("HostileUnit_3(Clone)", "HostileUnit_3")] [TestCase("Kestrel", "Kestrel")]
        public void CleanName_DropsTheCloneSuffix(string raw, string expected) => Assert.That(HudText.CleanName(raw), Is.EqualTo(expected));

        [Test]
        public void Step_NamesTheCommand()
        {
            Assert.That(HudText.Step(new MoveCommand(Vector3.zero), null), Is.EqualTo("Move"));
            Assert.That(HudText.Step(new MoveToCoverCommand(new CoverLocation("c", Vector3.zero, Vector3.forward, null)), null), Is.EqualTo("Take cover"));

            var terminal = new GameObject("T");
            created.Add(terminal);
            var interactable = terminal.AddComponent<MissionInteractable>();
            Assert.That(HudText.Step(new InteractCommand(interactable), null), Is.EqualTo("Interact " + interactable.DisplayName));

            var ability = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 12f, true, AbilityCoverRule.Ignored, 10f,
                AbilityEffect.Damage, 35, 3f);
            created.Add(ability);
            Assert.That(HudText.Step(AbilityCommand.AtGround(ability, Vector3.zero), null), Is.EqualTo("Blast"));
            Assert.That(HudText.Step(new StopCommand(), null), Is.EqualTo("Stop"));
            Assert.That(HudText.Step(null, null), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Step_AttackNamesTheTarget_OnlyWhileItIsShown()
        {
            var target = Unit("HostileUnit_1(Clone)");
            var attack = new AttackCommand(target);

            Assert.That(HudText.Step(attack, unit => true), Is.EqualTo("Attack HostileUnit_1"));
            Assert.That(HudText.Step(attack, unit => false), Is.EqualTo("Attack (target lost)"));
            Assert.That(HudText.Step(attack, null), Is.EqualTo("Attack HostileUnit_1"));
        }

        [Test]
        public void Step_AttackOnADestroyedTarget_ReadsTargetLost()
        {
            var target = Unit("HostileUnit_2");
            var attack = new AttackCommand(target);
            Object.DestroyImmediate(target.gameObject);

            Assert.That(HudText.Step(attack, unit => true), Is.EqualTo("Attack (target lost)"));
            Assert.That(HudText.Step(attack, null), Is.EqualTo("Attack (target lost)"));
        }

        [TestCase(CoverStatus.None, "Exposed")] [TestCase(CoverStatus.Reserved, "Moving to cover")]
        public void Cover_WithoutOccupancy(CoverStatus status, string expected) =>
            Assert.That(HudText.Cover(status, CoverHeight.Low, CoverPlacement.Face), Is.EqualTo(expected));

        [Test]
        public void Cover_Occupied_NamesLowCornerAndTall()
        {
            Assert.That(HudText.Cover(CoverStatus.Occupied, CoverHeight.Low, CoverPlacement.Face), Is.EqualTo("Low cover"));
            Assert.That(HudText.Cover(CoverStatus.Occupied, CoverHeight.Low, CoverPlacement.Corner), Is.EqualTo("Corner cover"));
            Assert.That(HudText.Cover(CoverStatus.Occupied, CoverHeight.Tall, CoverPlacement.Corner), Is.EqualTo("Corner cover"));
            Assert.That(HudText.Cover(CoverStatus.Occupied, CoverHeight.Tall, CoverPlacement.Face), Is.EqualTo("Tall cover"));
            Assert.That(HudText.Cover(CoverStatus.Occupied, CoverHeight.Tall, CoverPlacement.Column), Is.EqualTo("Tall cover"));
        }

        [TestCase(true, false, true, "PARKED")] [TestCase(false, true, true, "HOLDING")] [TestCase(false, false, true, "FOLLOWING")] [TestCase(false, false, false, "ATTACHED")]
        public void CompanionTag(bool parked, bool held, bool followOn, string expected) =>
            Assert.That(HudText.CompanionTag(parked, held, followOn), Is.EqualTo(expected));

        // ---- Objective rows (the fog rules)

        EliminateHostilesObjective Eliminate(string title, int total, int down = 0)
        {
            var group = new List<Health>();
            for (var i = 0; i < total; i++)
                group.Add(Unit("H" + i));
            for (var i = 0; i < down; i++)
                group[i].TakeDamage(group[i].Max);
            return new EliminateHostilesObjective("kill", title, group);
        }

        [Test]
        public void ObjectiveRow_KnownActive_IsTheDescription()
        {
            var objective = Eliminate("Clear the room", 3);
            objective.Activate();
            Assert.That(HudText.TryObjectiveRow(objective, false, out var kind, out var text), Is.True);
            Assert.That(kind, Is.EqualTo(HudObjectiveKind.Active));
            Assert.That(text, Is.EqualTo(objective.Describe()));
        }

        [Test]
        public void ObjectiveRow_Completed_FailedAndLocked()
        {
            var done = Eliminate("Done", 1, 1);
            done.Activate();
            done.Evaluate();
            Assert.That(done.State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(HudText.TryObjectiveRow(done, false, out var kind, out var text), Is.True);
            Assert.That(kind, Is.EqualTo(HudObjectiveKind.Completed));
            Assert.That(text, Is.EqualTo("Done"));

            var terminal = new GameObject("T");
            created.Add(terminal);
            var interactable = terminal.AddComponent<MissionInteractable>();
            var failing = new InteractObjective("hack", "Hack", interactable);
            failing.Activate();
            Object.DestroyImmediate(terminal);
            failing.Evaluate();
            Assert.That(failing.State, Is.EqualTo(ObjectiveState.Failed));
            Assert.That(HudText.TryObjectiveRow(failing, false, out kind, out text), Is.True);
            Assert.That(kind, Is.EqualTo(HudObjectiveKind.Failed));

            var locked = Eliminate("Later", 1);
            Assert.That(locked.State, Is.EqualTo(ObjectiveState.Inactive));
            Assert.That(HudText.TryObjectiveRow(locked, false, out kind, out text), Is.True);
            Assert.That(kind, Is.EqualTo(HudObjectiveKind.Locked));
        }

        [Test]
        public void ObjectiveRow_Unknown_ShowsOnlyTheVagueTitle_WhenListed()
        {
            var objective = Eliminate("Eliminate the Kestrel cell in the vault", 4);
            objective.SetKnown(false);
            objective.SetVagueTitle("Neutralise the opposition");
            objective.Activate();

            Assert.That(HudText.TryObjectiveRow(objective, true, out var kind, out var text), Is.True);
            Assert.That(kind, Is.EqualTo(HudObjectiveKind.Unknown));
            Assert.That(text, Is.EqualTo("Neutralise the opposition"));
            Assert.That(text, Does.Not.Contain(objective.Title));
        }

        [Test]
        public void ObjectiveRow_Unknown_IsOmitted_WhenNotListedOrWithoutAVagueTitle()
        {
            var objective = Eliminate("Secret", 2);
            objective.SetKnown(false);
            objective.SetVagueTitle("Something");
            Assert.That(HudText.TryObjectiveRow(objective, false, out _, out var text), Is.False);
            Assert.That(text, Is.Empty);

            var bare = Eliminate("Secret too", 2);
            bare.SetKnown(false);
            Assert.That(HudText.TryObjectiveRow(bare, true, out _, out _), Is.False);
            Assert.That(HudText.TryObjectiveRow(null, true, out _, out _), Is.False);
        }

        [Test]
        public void ObjectiveRow_HiddenCounts_ShowOnlyTheNumberDown()
        {
            var objective = Eliminate("Clear the area", 5, 2);
            objective.ShowCounts = false;
            objective.Activate();

            Assert.That(HudText.TryObjectiveRow(objective, false, out _, out var text), Is.True);
            Assert.That(text, Is.EqualTo("Clear the area (2 down)"));
            Assert.That(text, Does.Not.Contain("/5"));
        }

        // ---- Extraction

        ReachZoneObjective Zone(Vector3 center)
        {
            var squad = new List<Health> { Unit("S") };
            return new ReachZoneObjective("ex", "Extract", center, 3f, 1, squad, false);
        }

        [Test]
        public void ExtractionOf_MapsTheObjectiveAndPhase()
        {
            Assert.That(HudText.ExtractionOf(null, MissionPhase.Active, 0), Is.EqualTo(HudExtractionState.Hidden));

            var zone = Zone(new Vector3(50f, 0f, 50f));
            Assert.That(HudText.ExtractionOf(zone, MissionPhase.Active, 0), Is.EqualTo(HudExtractionState.Locked));

            zone.SetKnown(false);
            Assert.That(HudText.ExtractionOf(zone, MissionPhase.Active, 0), Is.EqualTo(HudExtractionState.Unknown));
            zone.SetKnown(true);

            zone.Activate();
            Assert.That(HudText.ExtractionOf(zone, MissionPhase.ExtractionOpen, 0), Is.EqualTo(HudExtractionState.Available));
            Assert.That(HudText.ExtractionOf(zone, MissionPhase.ExtractionOpen, 1), Is.EqualTo(HudExtractionState.Active));
            Assert.That(HudText.ExtractionOf(zone, MissionPhase.Success, 1), Is.EqualTo(HudExtractionState.Extracted));
        }

        [Test]
        public void ExtractionOf_CompletedIsExtracted()
        {
            var zone = Zone(Vector3.zero);   // the squad member stands at the origin, inside the zone
            zone.Activate();
            zone.Evaluate();
            Assert.That(zone.State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(HudText.ExtractionOf(zone, MissionPhase.Active, 0), Is.EqualTo(HudExtractionState.Extracted));
        }

        [Test]
        public void ExtractionOf_AFailedObjective_IsHidden()
        {
            var terminal = new GameObject("T");
            created.Add(terminal);
            var failing = new InteractObjective("hack", "Hack", terminal.AddComponent<MissionInteractable>());
            failing.Activate();
            Object.DestroyImmediate(terminal);
            failing.Evaluate();
            Assert.That(failing.State, Is.EqualTo(ObjectiveState.Failed), "Precondition");

            Assert.That(HudText.ExtractionOf(failing, MissionPhase.ExtractionOpen, 1), Is.EqualTo(HudExtractionState.Hidden));
        }

        [Test]
        public void ExtractionLabel_PerState()
        {
            Assert.That(HudText.ExtractionLabel(HudExtractionState.Unknown, 0, 2), Is.EqualTo("EXTRACTION UNKNOWN"));
            Assert.That(HudText.ExtractionLabel(HudExtractionState.Locked, 0, 2), Is.EqualTo("EXTRACTION LOCKED"));
            Assert.That(HudText.ExtractionLabel(HudExtractionState.Available, 0, 2), Is.EqualTo("EXTRACTION AVAILABLE"));
            Assert.That(HudText.ExtractionLabel(HudExtractionState.Active, 1, 2), Is.EqualTo("EXTRACTION ACTIVE 1/2 IN ZONE"));
            Assert.That(HudText.ExtractionLabel(HudExtractionState.Extracted, 2, 2), Is.EqualTo("EXTRACTED"));
            Assert.That(HudText.ExtractionLabel(HudExtractionState.Hidden, 0, 2), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Pause_NamesTheResumePrompt() =>
            Assert.That(HudText.Pause("Space"), Is.EqualTo("TACTICAL PAUSE - Space to resume"));

        [Test]
        public void Snapshot_StringsAreEmptyNotNull_BeforeTheFirstClear()
        {
            var snapshot = new HudSnapshot();
            foreach (var text in new[] { snapshot.PhaseText, snapshot.BannerText, snapshot.ResumePrompt, snapshot.ControlledName,
                         snapshot.ControlledRole, snapshot.ControlledCover, snapshot.QueueOwner, snapshot.CasterName, snapshot.ArmedLine })
                Assert.That(text, Is.Not.Null.And.Empty);
        }

        [Test]
        public void Snapshot_ClearEmptiesEverything()
        {
            var snapshot = new HudSnapshot();
            snapshot.Squad.Add(default);
            snapshot.Prompts.Add(new HudPromptEntry { Label = "x", Prompt = "y" });
            snapshot.HasMission = true;
            snapshot.BannerText = "b";
            snapshot.Target.Visible = true;
            snapshot.QueueHidden = 3;
            snapshot.Extraction = HudExtractionState.Active;

            snapshot.Clear();

            Assert.That(snapshot.Squad, Is.Empty);
            Assert.That(snapshot.Prompts, Is.Empty);
            Assert.That(snapshot.HasMission, Is.False);
            Assert.That(snapshot.BannerText, Is.Null.Or.Empty);
            Assert.That(snapshot.Target.Visible, Is.False);
            Assert.That(snapshot.QueueHidden, Is.Zero);
            Assert.That(snapshot.Extraction, Is.EqualTo(HudExtractionState.Hidden));
        }
    }
}
