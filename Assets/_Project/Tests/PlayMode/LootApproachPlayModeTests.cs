#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Owner report: a collect order sent a unit the long way round a baffle wall to a crate it could reach straight on. The
    /// walk went to the crate's centre, which is Not Walkable, so the destination snapped to whichever walkable point was
    /// nearest the centre, not the one the unit could reach soonest.
    /// </summary>
    public class LootApproachPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        const int Seeds = 24;
        MissionRig rig;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"));
            rig.AddLoot(Load<LootTable>(Items + "LootTable.asset"));
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        static float PathLength(Vector3 from, Vector3 to, NavMeshPath scratch)
        {
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, scratch) || scratch.status != NavMeshPathStatus.PathComplete)
                return float.PositiveInfinity;
            var length = 0f;
            for (var i = 1; i < scratch.corners.Length; i++)
                length += Vector3.Distance(scratch.corners[i - 1], scratch.corners[i]);
            return length;
        }

        [UnityTest]
        public IEnumerator AUnitOrderedToSearchACrate_ArrivesAndSearchesIt_OnGeneratedMissions()
        {
            var searched = 0;
            for (var seed = 1; seed <= 6; seed++)
            {
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}");
                var crate = rig.Director.Current.LootContainers[0];
                var unit = rig.Director.Friendlies[0];
                var health = unit.GetComponent<Health>();   // hostiles patrol the route: the walk, not the fight, is under test
                health.SetMax(100000);
                health.Heal(100000);

                Assert.That(unit.Issue(new InteractCommand(crate.Interactable)), Is.True, $"seed {seed}: the order was accepted");
                yield return TestWorld.WaitUntil(() => crate.IsSearched, 90f);

                Assert.That(crate.IsSearched, Is.True, $"seed {seed}: the unit reached the crate and finished the search (order now: {(unit.CurrentCommand == null ? "none" : unit.CurrentCommand.GetType().Name)}, flat distance {CoverRules.FlatDistance(unit.transform.position, crate.Position):0.0} m)");
                searched++;
            }
            Assert.That(searched, Is.EqualTo(6));
        }

        [UnityTest]
        public IEnumerator AUnitOrderedToUseTheTerminal_AlsoArrivesAndFinishes_OnGeneratedMissions()
        {
            for (var seed = 1; seed <= 4; seed++)
            {
                yield return rig.Generate(seed);
                var terminal = rig.Director.Current.Terminal;
                var unit = rig.Director.Friendlies[0];
                var health = unit.GetComponent<Health>();   // hostiles patrol the route: the walk, not the fight, is under test
                health.SetMax(100000);
                health.Heal(100000);

                Assert.That(unit.Issue(new InteractCommand(terminal)), Is.True, $"seed {seed}: the order was accepted");
                yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 120f);

                Assert.That(terminal.IsCompleted, Is.True, $"seed {seed}: the unit reached the terminal and finished the interaction");
            }
        }

        [UnityTest]
        public IEnumerator TheWalkToAContainer_IsNeverMuchLongerThanTheBestWayToItsSide()
        {
            var path = new NavMeshPath();
            var worst = new List<string>();
            var checkedCrates = 0;
            for (var seed = 1; seed <= Seeds; seed++)
            {
                yield return rig.Generate(seed);
                if (rig.Director.State != MissionState.Ready)
                    continue;
                foreach (var container in rig.Director.Current.LootContainers)
                {
                    var unit = rig.Director.Friendlies[0];
                    var from = unit.transform.position;
                    // Where the order walks: the mover's chosen stand point, else the crate's centre snapped onto the NavMesh.
                    var mover = unit.GetComponent<UnitMover>();
                    var found = mover.TryApproachPoint(container.Position, container.Interactable.Range, out var destination)
                        || mover.TrySnap(container.Position, out destination);
                    var chosen = found ? PathLength(from, destination, path) : float.PositiveInfinity;
                    checkedCrates++;
                    var best = float.PositiveInfinity;
                    for (var step = 0; step < 8; step++)
                    {
                        var angle = step * Mathf.PI / 4f;
                        var side = container.Position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 1.2f;
                        if (NavMesh.SamplePosition(side, out var hit, 0.4f, NavMesh.AllAreas))
                            best = Mathf.Min(best, PathLength(from, hit.position, path));
                    }
                    if (chosen > best + 3f)
                        worst.Add($"seed {seed} crate at {container.Position}: order path {chosen:0.0} m, best side {best:0.0} m");
                }
            }
            Assert.That(checkedCrates, Is.GreaterThan(Seeds), "the sweep looked at crates");
            Assert.That(worst, Is.Empty, string.Join("\n", worst));
        }
    }
}
#endif
