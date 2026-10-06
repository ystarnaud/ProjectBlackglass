#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionContentPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        static int ZoneCount() => Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name == "ExtractionZone");

        [UnityTest]
        public IEnumerator AGeneratedMission_HasASolidTerminal_InTheRegistry_ThatIsNotCover_AndNotWalkable()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var mission = rig.Director.Current;
            var terminal = mission.Terminal;

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            Assert.That(terminal, Is.Not.Null);
            Assert.That(terminal.transform.IsChildOf(mission.Geometry), Is.True);
            Assert.That(terminal.GetComponent<Collider>(), Is.Not.Null, "the terminal is solid");
            Assert.That(terminal.GetComponent<CoverSurface>() == null, Is.True, "the terminal offers no cover");
            Assert.That(rig.Interactables.Items, Is.EqualTo(new[] { terminal }));
            Assert.That(NavMesh.SamplePosition(terminal.Position - Vector3.up * 0.6f, out _, 0.3f, NavMesh.AllAreas), Is.False,
                "no NavMesh under the terminal: agents route around it");
            Assert.That(mission.Plan, Is.Not.Null);
            Assert.That(rig.Director.Report.ObjectiveHash, Is.EqualTo(mission.Plan.Hash));
        }

        [UnityTest]
        public IEnumerator TheTerminalAndTheExtractionZone_AreReachableAndAUnitCanStandInRangeOfTheTerminal()
        {
            rig = new MissionRig();
            foreach (var seed in new[] { 12345, 1, 2, 3, 4 })
            {
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}");
                var mover = rig.Director.Friendlies[0].GetComponent<UnitMover>();
                var terminal = rig.Director.Current.Terminal;
                var zone = rig.Director.Current.ExtractionZone;

                Assert.That(mover.CanReach(terminal.Position), Is.True, $"seed {seed}: terminal reachable");
                Assert.That(mover.TrySnap(terminal.Position, out var stand), Is.True);
                Assert.That(TestWorld.HorizontalDistance(stand, terminal.Position), Is.LessThanOrEqualTo(terminal.Range - 0.2f),
                    $"seed {seed}: somewhere to stand within reach");
                Assert.That(mover.CanReach(zone.position), Is.True, $"seed {seed}: extraction reachable");
                Assert.That(NavMesh.SamplePosition(zone.position, out _, 0.5f, NavMesh.AllAreas), Is.True, $"seed {seed}: zone centre is on the mesh");
            }
        }

        [UnityTest]
        public IEnumerator Guards_AreExtraHostilesInTheTerminalRoom_AndTheyAreWiredLikeAnyHostile()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var plan = rig.Director.Current.Plan;
            var layout = rig.Director.Current.Layout;

            Assert.That(rig.Director.Hostiles, Has.Count.EqualTo(layout.HostileSpawns.Count + plan.GuardTiles.Count));
            Assert.That(rig.Encounter.Hostiles, Has.Count.EqualTo(rig.Director.Hostiles.Count));
            Assert.That(rig.Director.Report.Guards, Is.EqualTo(plan.GuardTiles.Count));
            for (var i = 0; i < plan.GuardTiles.Count; i++)
            {
                var guard = rig.Director.Hostiles[layout.HostileSpawns.Count + i];
                var expected = layout.TileCenter(plan.GuardTiles[i]);
                Assert.That(TestWorld.HorizontalDistance(guard.transform.position, expected), Is.LessThan(1.1f));
                Assert.That(guard.GetComponent<EnemyAI>().IsCoverWired, Is.True);
                Assert.That(guard.GetComponent<NavMeshAgent>().isOnNavMesh, Is.True);
            }
            foreach (var friendly in rig.Director.Friendlies)
                Assert.That(friendly.TryGetComponent<UnitInteractor>(out _), Is.True, "every friendly can interact");
            foreach (var hostile in rig.Director.Hostiles)
                Assert.That(hostile.TryGetComponent<UnitInteractor>(out _), Is.False);
        }

        [UnityTest]
        public IEnumerator TheSameSeed_PlacesTheTerminalAtTheSameSpot_AndDifferentSeedsDoNot()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var first = rig.Director.Current.Terminal.Position;
            var firstZone = rig.Director.Current.ExtractionZone.position;
            yield return rig.Generate(12345);
            Assert.That(rig.Director.Current.Terminal.Position, Is.EqualTo(first));
            Assert.That(rig.Director.Current.ExtractionZone.position, Is.EqualTo(firstZone));

            var spots = new System.Collections.Generic.HashSet<Vector3>();
            foreach (var seed in new[] { 1, 2, 3, 4, 5 })
            {
                yield return rig.Generate(seed);
                spots.Add(rig.Director.Current.Terminal.Position);
            }
            Assert.That(spots.Count, Is.GreaterThanOrEqualTo(4));
        }

        [UnityTest]
        public IEnumerator WithoutTheHackObjective_NoTerminalIsBuilt_ButTheZoneStillIs()
        {
            rig = new MissionRig(new MissionSettings { hackTerminal = false });
            yield return rig.Generate(12345);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(rig.Director.Current.Terminal == null, Is.True);
            Assert.That(rig.Interactables.Items, Is.Empty);
            Assert.That(rig.Director.Current.ExtractionZone, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator Regeneration_ReplacesTheTerminalAndTheZone_AndLeavesNothingBehind()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var oldTerminal = rig.Director.Current.Terminal;
            yield return rig.Generate(7);
            yield return null;

            Assert.That(oldTerminal == null, Is.True, "the old terminal is destroyed");
            Assert.That(rig.Interactables.Items, Is.EqualTo(new[] { rig.Director.Current.Terminal }));
            Assert.That(ZoneCount(), Is.EqualTo(1));

            rig.Director.Clear();
            yield return null;
            Assert.That(rig.Interactables.Items, Is.Empty);
            Assert.That(ZoneCount(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator AFailedGeneration_LeavesNoObjectiveContent()
        {
            rig = new MissionRig(new MissionSettings { gridColumns = 2, gridRows = 2, cellSize = 12, minTeamSeparation = 60f, maxAttempts = 3 });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Mission generation failed"));
            yield return rig.Generate(12345);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Failed));
            Assert.That(rig.Director.Current, Is.Null);
            Assert.That(rig.Interactables.Items, Is.Empty);
        }
    }
}
#endif
