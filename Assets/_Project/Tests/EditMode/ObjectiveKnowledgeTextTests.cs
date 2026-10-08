using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ObjectiveKnowledgeTextTests
    {
        readonly List<GameObject> created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                Object.DestroyImmediate(go);
            created.Clear();
        }

        Health Unit()
        {
            var go = new GameObject("Hostile");
            created.Add(go);
            var health = go.AddComponent<Health>();
            health.Initialize(10);
            return health;
        }

        sealed class Stub : MissionObjective
        {
            public Stub(string id, ObjectiveType type, string title) : base(id, type, title, true) { }
            public override string Describe() => Title;
        }

        // MissionRuntime refuses a required extraction and a null squad: this one is optional and the squad is empty.
        sealed class ExtractionStub : MissionObjective
        {
            public ExtractionStub() : base("extract", ObjectiveType.ReachZone, "Extraction", false) { }
            public override string Describe() => Title;
        }

        static MissionRuntime Runtime(params MissionObjective[] goals) =>
            new MissionRuntime(goals, new ExtractionStub(), new List<Health>());

        [Test]
        public void VagueTitle_DefaultsToNull_AndCanBeSet()
        {
            var goal = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            Assert.That(goal.VagueTitle, Is.Null);
            goal.SetVagueTitle("Locate the data terminal");
            Assert.That(goal.VagueTitle, Is.EqualTo("Locate the data terminal"));
        }

        [Test]
        public void Panel_HidesAnUnknownObjective_ByDefault()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetVagueTitle("Locate the data terminal");
            hack.SetKnown(false);
            var text = MissionHudText.Panel(Runtime(hack));
            Assert.That(text, Does.Not.Contain("Access data terminal").And.Not.Contain("Locate the data terminal"));
        }

        [Test]
        public void Panel_WithListUnknown_ShowsTheVagueTitle_AndNeverTheRealOne()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetVagueTitle("Locate the data terminal");
            hack.SetKnown(false);
            var text = MissionHudText.Panel(Runtime(hack), listUnknown: true);
            Assert.That(text, Does.Contain("[?] Locate the data terminal"));
            Assert.That(text, Does.Not.Contain("Access data terminal"));
        }

        [Test]
        public void Panel_WithListUnknown_SkipsAnUnknownObjectiveWithoutAVagueTitle()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetKnown(false);
            Assert.That(MissionHudText.Panel(Runtime(hack), listUnknown: true), Does.Not.Contain("Access data terminal"));
        }

        [Test]
        public void Panel_ShowsAKnownObjectiveNormally_WhateverTheFlag()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetVagueTitle("Locate the data terminal");
            var text = MissionHudText.Panel(Runtime(hack), listUnknown: true);
            Assert.That(text, Does.Contain("Access data terminal"));
            Assert.That(text, Does.Not.Contain("Locate the data terminal"));
        }

        [Test]
        public void MarkerLabel_IsEmptyForAnUnknownObjective()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetKnown(false);
            Assert.That(MissionHudText.MarkerLabel(hack), Is.Empty);
        }

        [Test]
        public void Eliminate_ShowsLivingOverTotal_ByDefault()
        {
            var group = new List<Health> { Unit(), Unit(), Unit() };
            var objective = new EliminateHostilesObjective("e", "Eliminate security team", group);
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team (3/3)"));
        }

        [Test]
        public void Eliminate_WithCountsHidden_ShowsOnlyWhatTheSquadHasDone()
        {
            var group = new List<Health> { Unit(), Unit(), Unit() };
            var objective = new EliminateHostilesObjective("e", "Eliminate security team", group) { ShowCounts = false };
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team"), "no count leaks how many there are");
            group[0].TakeDamage(100);
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team (1 down)"));
            group[1].TakeDamage(100);
            group[2].TakeDamage(100);
            objective.Activate();
            objective.Evaluate();
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team"), "completed: the title alone, as before");
        }
    }
}
