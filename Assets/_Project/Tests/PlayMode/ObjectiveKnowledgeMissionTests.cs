#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ObjectiveKnowledgeMissionTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator EveryGeneratedObjective_HasAVagueTitle_ThatNamesNoLocationAndNoCount()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            foreach (var goal in rig.Director.Runtime.Objectives)
            {
                Assert.That(goal.VagueTitle, Is.Not.Null.And.Not.Empty, goal.Id);
                Assert.That(goal.VagueTitle, Does.Not.Match(@"\d"), $"{goal.Id}: no numbers");
            }
        }

        [UnityTest]
        public IEnumerator ABlindMission_ListsTheUnknownObjectivesVaguely_UntilTheyAreLearned()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            var runtime = rig.Director.Runtime;

            var hidden = MissionHudText.Panel(runtime, service.ListsUnknownObjectives);
            Assert.That(service.ListsUnknownObjectives, Is.True);
            Assert.That(hidden, Does.Contain("Locate the data terminal"));
            Assert.That(hidden, Does.Not.Contain("Access data terminal"), "the real objective is not named yet");
            Assert.That(hidden, Does.Contain("Extraction"), "extraction was known from the start");
            Assert.That(hidden, Does.Not.Match(@"\(\d+/\d+\)"), "no hostile count");

            service.Scan(rig.Director.Current.Terminal.Position, 1f, 5f);   // a pulse puts the terminal's region in sight
            var learned = MissionHudText.Panel(runtime, service.ListsUnknownObjectives);
            Assert.That(learned, Does.Contain("Access data terminal"));
            Assert.That(learned, Does.Not.Contain("Locate the data terminal"));
        }

        [UnityTest]
        public IEnumerator TheLayoutKnownPreset_KnowsEveryRoom_ButNoPlacedObjective_UntilItsRegionIsSeen()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.LayoutKnown());
            yield return rig.Generate(12345);
            var runtime = rig.Director.Runtime;

            for (var region = 0; region < service.Map.Count; region++)
                Assert.That(service.StateOfRegion(region), Is.Not.EqualTo(KnowledgeState.Unknown), $"region {region} is on the map");
            var terminal = runtime.Objectives.Single(g => g.Type == ObjectiveType.Interact);
            var extraction = runtime.Objectives.Single(g => g.Type == ObjectiveType.ReachZone);
            Assert.That(terminal.IsKnown, Is.False, "a mapped region does not teach the objective placed in it");
            Assert.That(extraction.IsKnown, Is.False, "objectives = None");
            Assert.That(MissionHudText.Panel(runtime, service.ListsUnknownObjectives), Does.Not.Contain(terminal.Title));

            service.Scan(rig.Director.Current.Terminal.Position, 1f, 5f);
            Assert.That(terminal.IsKnown, Is.True, "seeing its region teaches it");
            Assert.That(MissionHudText.Panel(runtime, service.ListsUnknownObjectives), Does.Contain(terminal.Title));
        }

        [UnityTest]
        public IEnumerator MissionCompletion_DoesNotDependOnKnowledge()
        {
            rig = new MissionRig();
            rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            var runtime = rig.Director.Runtime;
            Assert.That(runtime.Objectives.Count(g => !g.IsKnown), Is.GreaterThan(0), "Precondition: something is unknown");
            foreach (var hostile in rig.Director.Hostiles)
            {
                hostile.GetComponent<EnemyAI>().enabled = false;
                hostile.GetComponent<Health>().TakeDamage(1000);
            }
            yield return null;
            var eliminate = runtime.Objectives.Single(g => g.Type == ObjectiveType.EliminateHostiles);
            Assert.That(eliminate.State, Is.EqualTo(ObjectiveState.Completed), "an unknown objective still completes");
        }
    }
}
#endif
