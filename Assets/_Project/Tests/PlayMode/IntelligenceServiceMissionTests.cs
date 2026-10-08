#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The service inside the real pipeline: briefing, hidden hostiles, determinism and regeneration.</summary>
    public class IntelligenceServiceMissionTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator ABlindMission_StartsWithOnlyTheStartRoomAndTheExtractionKnown()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            var layout = rig.Director.Current.Layout;

            Assert.That(service.IsFogActive, Is.True);
            Assert.That(service.StateOfRegion(layout.FriendlyRoom), Is.EqualTo(KnowledgeState.Observed));
            var unknown = Enumerable.Range(0, service.Map.Count).Count(r => service.StateOfRegion(r) == KnowledgeState.Unknown);
            Assert.That(unknown, Is.GreaterThan(service.Map.Count / 2), "most of the map is unknown");
            foreach (var hostile in rig.Encounter.Hostiles)
                Assert.That(service.CanTarget(hostile), Is.False, hostile.name);

            var goals = rig.Director.Runtime.Objectives;
            Assert.That(goals.Single(g => g.Type == ObjectiveType.ReachZone).IsKnown, Is.True, "extraction stays known");
            Assert.That(goals.Single(g => g.Type == ObjectiveType.Interact).IsKnown, Is.False);
            Assert.That(goals.Single(g => g.Type == ObjectiveType.EliminateHostiles).IsKnown, Is.False);
        }

        [UnityTest]
        public IEnumerator AFullKnowledgeMission_HidesNothing()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Full());
            yield return rig.Generate(12345);
            Assert.That(service.IsFogActive, Is.False);
            foreach (var hostile in rig.Encounter.Hostiles)
                Assert.That(service.CanTarget(hostile), Is.True);
            Assert.That(rig.Director.Runtime.Objectives.All(g => g.IsKnown), Is.True);
        }

        [UnityTest]
        public IEnumerator Fog_DoesNotChangeTheMission_ForTheSameSeed()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var layoutHash = rig.Director.Report.LayoutHash;
            var objectiveHash = rig.Director.Report.ObjectiveHash;
            var spawns = rig.Director.Hostiles.Select(h => h.transform.position).ToArray();
            rig.Dispose();

            rig = new MissionRig();
            rig.AddIntelligence(IntelligenceSettings.ObjectivesKnown());   // fog on, no cameras: the same geometry
            yield return rig.Generate(12345);
            Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(layoutHash));
            Assert.That(rig.Director.Report.ObjectiveHash, Is.EqualTo(objectiveHash));
            var fogged = rig.Director.Hostiles.Select(h => h.transform.position).ToArray();
            Assert.That(fogged.Length, Is.EqualTo(spawns.Length));
            for (var i = 0; i < spawns.Length; i++)
                Assert.That(Vector3.Distance(fogged[i], spawns[i]), Is.LessThan(0.01f), $"hostile {i}");
        }

        [UnityTest]
        public IEnumerator Regenerating_StartsFromTheBriefingAgain_AndNothingStaleIsLeft()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            var oldHostile = rig.Encounter.Hostiles[0];
            service.Scan(Vector3.zero, 200f, 100f);   // learn the whole map and every enemy
            Assert.That(service.CanTarget(oldHostile), Is.True);

            yield return rig.Generate(12345);
            yield return null;
            Assert.That(oldHostile == null, Is.True, "the old hostile is destroyed");
            Assert.That(() => service.CanTarget(oldHostile), Throws.Nothing);
            Assert.That(service.CanTarget(oldHostile), Is.False);
            foreach (var hostile in rig.Encounter.Hostiles)
                Assert.That(service.CanTarget(hostile), Is.False, "knowledge from the first mission did not carry over");
            var unknown = Enumerable.Range(0, service.Map.Count).Count(r => service.StateOfRegion(r) == KnowledgeState.Unknown);
            Assert.That(unknown, Is.GreaterThan(service.Map.Count / 2));
        }

        [UnityTest]
        public IEnumerator Regenerating_WhilePaused_IsClean()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            rig.Pause.Pause();
            yield return rig.Generate(31);   // an error log fails the test: no stale subscriptions fire into a dead mission
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(service.HasMission, Is.True);
        }

        [UnityTest]
        public IEnumerator ClearingTheDirector_ClearsTheServiceToo()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            Assert.That(service.HasMission, Is.True);
            rig.Director.Clear();
            Assert.That(service.HasMission, Is.False);
            Assert.That(service.CanTarget(null), Is.True, "no mission: nothing is hidden, and a null unit is no exception");
        }

        // The terminal's device point must clear its own box collider, or every sight ray to it hits the box first.
        [UnityTest]
        public IEnumerator TheCameraTerminal_IsDiscoveredBySight_NextToIt_InARealMission()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            var terminal = rig.Director.Current.CameraTerminal;
            Assert.That(terminal, Is.Not.Null);
            Assert.That(terminal.GetComponent<Collider>(), Is.Not.Null, "Precondition: the real terminal has a collider");

            // A walkable spot about 2 m from the terminal, in the open (not behind its box).
            var found = false;
            var spot = Vector3.zero;
            var ground = new Vector3(terminal.transform.position.x, 0f, terminal.transform.position.z);
            foreach (var direction in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
            {
                if (NavMesh.SamplePosition(ground + direction * 2f, out var hit, 0.5f, NavMesh.AllAreas)
                    && Vector3.Distance(hit.position, ground) > 1.5f
                    && !Physics.Linecast(hit.position + Vector3.up * 1.5f, ground + Vector3.up * 1.3f))
                {
                    spot = hit.position;
                    found = true;
                    break;
                }
            }
            Assert.That(found, Is.True, "Precondition: a clear walkable spot beside the terminal");

            var friendly = rig.Director.Friendlies[0];
            friendly.transform.position = spot + Vector3.up;   // the capsule's pivot is 1 m above the ground, as in the other rigs
            Physics.SyncTransforms();
            service.RunPass();

            Assert.That(service.Model.StateOfDevice(SecurityPlan.TerminalDeviceId), Is.EqualTo(KnowledgeState.Discovered),
                "the squad stands 2 m from the terminal in plain view");
            Assert.That(service.CanInteract(terminal), Is.True);
        }

        [UnityTest]
        public IEnumerator ANewMissionWithCameras_GivesTheServiceItsNetwork()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            var mission = rig.Director.Current;
            var terminal = mission.CameraTerminal;
            Assert.That(mission.Network.Compromised, Is.False);
            terminal.TryBegin(rig.Director.Friendlies[0]);
            terminal.Advance(rig.Director.Friendlies[0], 100f);
            Assert.That(mission.Network.Compromised, Is.True, "using the terminal compromises the network through the service");
        }
    }
}
#endif
