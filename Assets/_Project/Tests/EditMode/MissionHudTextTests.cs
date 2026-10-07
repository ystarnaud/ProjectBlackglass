using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionHudTextTests
    {
        sealed class Fake : MissionObjective
        {
            public Fake(string id, string title, bool required) : base(id, ObjectiveType.Interact, title, required) { }
            public bool CompleteNow;
            public bool FailNow;
            public override bool HasTarget => true;
            public override Vector3 TargetPosition => Vector3.zero;
            public override string Describe() => Title;
            protected override void OnEvaluate()
            {
                if (FailNow)
                    Fail();
                else if (CompleteNow)
                    Complete();
            }
        }

        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        MissionRuntime Runtime(out Fake kill, out Fake hack, out Fake extraction)
        {
            var unit = new GameObject("Unit");
            hosts.Add(unit);
            var squad = new[] { unit.AddComponent<Health>() };
            kill = new Fake("kill", "Eliminate security team", true);
            hack = new Fake("hack", "Access data terminal", true);
            extraction = new Fake("extract", "Extraction", false);
            var runtime = new MissionRuntime(new MissionObjective[] { kill, hack }, extraction, squad);
            runtime.Start();
            return runtime;
        }

        [Test]
        public void Panel_ShowsEachObjectiveWithItsStatus_AndExtractionLocked()
        {
            var runtime = Runtime(out var kill, out _, out _);
            kill.CompleteNow = true;
            runtime.Tick();

            var text = MissionHudText.Panel(runtime);

            Assert.That(text, Does.StartWith("MISSION"));
            Assert.That(text, Does.Contain("[x] Eliminate security team"));
            Assert.That(text, Does.Contain("[ ] Access data terminal"));
            Assert.That(text, Does.Contain("[LOCKED] Extraction"));
        }

        [Test]
        public void Panel_ShowsExtractionOpenAsAnOrdinaryOpenLine_ThenDoneOnSuccess()
        {
            var runtime = Runtime(out var kill, out var hack, out var extraction);
            kill.CompleteNow = true;
            hack.CompleteNow = true;
            runtime.Tick();
            Assert.That(MissionHudText.Panel(runtime), Does.Contain("[ ] Extraction"));
            Assert.That(MissionHudText.Panel(runtime), Does.Contain(MissionHudText.PhaseLabel(MissionPhase.ExtractionOpen)));

            extraction.CompleteNow = true;
            runtime.Tick();
            Assert.That(MissionHudText.Panel(runtime), Does.Contain("[x] Extraction"));
        }

        [Test]
        public void Panel_MarksAFailedObjective_AndSkipsUnknownOnes()
        {
            var runtime = Runtime(out _, out var hack, out _);
            hack.FailNow = true;
            runtime.Tick();
            Assert.That(MissionHudText.Panel(runtime), Does.Contain("[FAILED] Access data terminal"));

            var second = Runtime(out _, out var secret, out _);
            secret.SetKnown(false);
            Assert.That(MissionHudText.Panel(second), Does.Not.Contain("Access data terminal"));
        }

        [Test]
        public void Banner_NamesTheResult_AndIsEmptyOtherwise()
        {
            Assert.That(MissionHudText.Banner(MissionPhase.Success), Does.Contain("SUCCESS"));
            Assert.That(MissionHudText.Banner(MissionPhase.Failure), Does.Contain("FAILED"));
            Assert.That(MissionHudText.Banner(MissionPhase.Active), Is.Empty);
            Assert.That(MissionHudText.Banner(MissionPhase.ExtractionOpen), Is.Empty);
            Assert.That(MissionHudText.Banner(MissionPhase.Inactive), Is.Empty);
        }

        [Test]
        public void MarkerLabel_ShowsLockedExtraction_AndNothingForACompletedOrUnknownObjective()
        {
            var runtime = Runtime(out var kill, out var hack, out var extraction);
            Assert.That(MissionHudText.MarkerLabel(extraction), Is.EqualTo("Extraction (locked)"));
            Assert.That(MissionHudText.MarkerLabel(hack), Is.EqualTo("Access data terminal"));
            hack.CompleteNow = true;
            runtime.Tick();
            Assert.That(MissionHudText.MarkerLabel(hack), Is.Empty);
            kill.SetKnown(false);
            Assert.That(MissionHudText.MarkerLabel(kill), Is.Empty);
        }

        [Test]
        public void PrototypeHud_KeepsItsKillAllBanner_OnlyWhenNoMissionDirectorIsWired()
        {
            Assert.That(PrototypeHud.ShowsEncounterOutcome(hasMissionDirector: false), Is.True);
            Assert.That(PrototypeHud.ShowsEncounterOutcome(hasMissionDirector: true), Is.False);
        }

        [Test]
        public void MarkerColours_DistinguishEveryTerminalAndZoneState()
        {
            var host = new GameObject("Terminal");
            hosts.Add(host);
            var terminal = host.AddComponent<MissionInteractable>();
            var unit = new GameObject("Unit");
            hosts.Add(unit);
            unit.AddComponent<Health>();
            unit.AddComponent<UnitInteractor>();
            var worker = unit.AddComponent<CommandableUnit>();
            terminal.Initialize(1.8f, 2f);

            var idle = ObjectiveMarker.TerminalColour(terminal);
            terminal.SetAvailable(true);
            var available = ObjectiveMarker.TerminalColour(terminal);
            terminal.TryBegin(worker);
            var working = ObjectiveMarker.TerminalColour(terminal);
            terminal.Advance(worker, 5f);
            var done = ObjectiveMarker.TerminalColour(terminal);

            Assert.That(new[] { idle, available, working, done }, Is.Unique);
            Assert.That(ObjectiveMarker.ZoneColour(ObjectiveState.Inactive), Is.Not.EqualTo(ObjectiveMarker.ZoneColour(ObjectiveState.Active)));
            Assert.That(ObjectiveMarker.ZoneColour(ObjectiveState.Active), Is.Not.EqualTo(ObjectiveMarker.ZoneColour(ObjectiveState.Completed)));
        }
    }
}
