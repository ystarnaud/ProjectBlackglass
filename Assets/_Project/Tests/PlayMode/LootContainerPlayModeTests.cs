#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class LootContainerPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        MissionRig rig;
        SquadInventory inventory;
        LootTable table;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"));
            table = Load<LootTable>(Items + "LootTable.asset");
            rig.AddLoot(table);
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator Containers_AreBuilt_Registered_AndUnsearched_WithThePlannedContents()
        {
            yield return rig.Generate(12345);
            var mission = rig.Director.Current;

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            Assert.That(mission.LootContainers.Count, Is.EqualTo(mission.Loot.Placed));
            Assert.That(mission.LootContainers.Count, Is.GreaterThan(0));
            Assert.That(rig.Director.Report.LootPlaced, Is.EqualTo(mission.Loot.Placed));
            for (var i = 0; i < mission.LootContainers.Count; i++)
            {
                var container = mission.LootContainers[i];
                Assert.That(container.IsSearched, Is.False);
                Assert.That(rig.Interactables.Items.Contains(container.Interactable), Is.True, "found like any interactable");
                Assert.That(container.transform.IsChildOf(mission.Geometry), Is.True);
                var planned = mission.Loot.Containers[i].Items.Sum(item => item.Quantity);
                Assert.That(container.Contents.Entries.Sum(e => e.Quantity), Is.EqualTo(planned));
            }
        }

        [UnityTest]
        public IEnumerator SameSeed_RepeatsPlacementAndContents_ButNotInstanceIds()
        {
            yield return rig.Generate(12345);
            var first = rig.Director.Current.LootContainers
                .Select(c => (pos: c.Position, items: c.Contents.Entries.OrderBy(e => e.DefinitionId).Select(e => (e.DefinitionId, e.Quantity)).ToArray(),
                              ids: c.Contents.Entries.Select(e => e.InstanceId).ToArray())).ToArray();
            var hash = rig.Director.Report.LootHash;
            var layoutHash = rig.Director.Report.LayoutHash;

            yield return rig.Generate(12345);
            var second = rig.Director.Current.LootContainers
                .Select(c => (pos: c.Position, items: c.Contents.Entries.OrderBy(e => e.DefinitionId).Select(e => (e.DefinitionId, e.Quantity)).ToArray(),
                              ids: c.Contents.Entries.Select(e => e.InstanceId).ToArray())).ToArray();

            Assert.That(first.Length, Is.GreaterThan(0), "the seed places containers, so the comparison below means something");
            Assert.That(rig.Director.Report.LootHash, Is.EqualTo(hash));
            Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(layoutHash));
            Assert.That(second.Length, Is.EqualTo(first.Length));
            for (var i = 0; i < first.Length; i++)
            {
                Assert.That(second[i].pos, Is.EqualTo(first[i].pos));
                Assert.That(second[i].items, Is.EqualTo(first[i].items));
                Assert.That(second[i].ids.Intersect(first[i].ids), Is.Empty, "new ownership ids every deployment");
            }
        }

        [UnityTest]
        public IEnumerator TheLayoutAndObjectives_AreTheSame_WithOrWithoutLoot()
        {
            yield return rig.Generate(12345);
            var withLoot = (rig.Director.Report.LayoutHash, rig.Director.Report.ObjectiveHash);

            rig.AddLoot(null);
            yield return rig.Generate(12345);

            Assert.That((rig.Director.Report.LayoutHash, rig.Director.Report.ObjectiveHash), Is.EqualTo(withLoot));
            Assert.That(rig.Director.Current.LootContainers, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Containers_AreReachable_AndNeverBlockTheGoals()
        {
            foreach (var seed in new[] { 12345, 1, 2, 3, 4 })
            {
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}");
                var start = rig.Director.Friendlies[0].transform.position - Vector3.up;
                Assert.That(rig.Director.Current.LootContainers.Count, Is.GreaterThan(0), $"seed {seed} places containers");
                foreach (var container in rig.Director.Current.LootContainers)
                {
                    var path = new NavMeshPath();
                    Assert.That(NavMesh.SamplePosition(container.Position, out var near, 2f, NavMesh.AllAreas), Is.True, $"seed {seed}");
                    Assert.That(NavMesh.CalculatePath(start, near.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete, Is.True, $"seed {seed}");
                    Assert.That(TestWorld.HorizontalDistance(near.position, container.Position), Is.LessThanOrEqualTo(MissionContent.InteractionRange - 0.2f));
                }
            }
        }

        [UnityTest]
        public IEnumerator Search_TakesTime_ThenOpens_AndLaterOpensAreInstant_AndTheContainerStaysAvailable()
        {
            yield return rig.Generate(12345);
            var container = rig.Director.Current.LootContainers[0];
            var unit = rig.Director.Friendlies[0];
            var opened = new System.Collections.Generic.List<CommandableUnit>();
            container.OpenRequested += u => opened.Add(u);
            Assert.That(NavMesh.SamplePosition(container.Position, out var stand, 2f, NavMesh.AllAreas), Is.True);
            unit.GetComponent<NavMeshAgent>().Warp(stand.position);
            yield return null;

            Assert.That(unit.Issue(new InteractCommand(container.Interactable)), Is.True);
            yield return null;
            Assert.That(container.IsSearched, Is.False, "searching takes time");
            yield return TestWorld.WaitUntil(() => container.IsSearched, 6f);

            Assert.That(container.IsSearched, Is.True);
            Assert.That(opened, Is.EqualTo(new[] { unit }), "the searching unit is told");
            Assert.That(container.Interactable.IsAvailable, Is.True, "still interactable");
            Assert.That(container.Interactable.DisplayName, Is.EqualTo(LootContainer.OpenLabel));

            Assert.That(unit.Issue(new InteractCommand(container.Interactable)), Is.True);
            yield return TestWorld.WaitUntil(() => opened.Count == 2, 3f);
            Assert.That(opened.Count, Is.EqualTo(2), "an opened container opens at once");
            Assert.That(unit.CurrentCommand, Is.Null.Or.Not.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator Regeneration_RemovesTheOldContainers_AndTheRegistryHoldsOnlyTheNewOnes()
        {
            yield return rig.Generate(12345);
            var old = rig.Director.Current.LootContainers.ToArray();

            yield return rig.Generate(777);
            yield return null;

            foreach (var container in old)
                Assert.That(container == null, Is.True, "destroyed with the mission");
            var current = rig.Director.Current.LootContainers.Select(c => c.Interactable).ToArray();
            foreach (var item in rig.Interactables.Items)
                if (item != null && item.DisplayName.Contains("container"))
                    Assert.That(current.Contains(item), Is.True);
        }

        [UnityTest]
        public IEnumerator InvalidTable_IsReported_AndTheMissionStillGenerates()
        {
            var bad = ScriptableObject.CreateInstance<LootTable>();   // no entries
            rig.AddLoot(bad);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("loot table"));
            yield return rig.Generate(12345);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(rig.Director.Current.LootContainers, Is.Empty);
            Object.DestroyImmediate(bad);
        }

        [Test]
        public void ReInitialising_ASearchedContainerAsUnsearched_ResetsItCompletely()
        {
            var world = new TestWorld();
            try
            {
                var kit = new InventoryKit(world, 8, "a");
                var container = kit.CreateContainer(Vector3.zero, searched: true, (kit.Medkit, 1));
                Assert.That(container.IsSearched, Is.True);
                Assert.That(container.Interactable.DisplayName, Is.EqualTo(LootContainer.OpenLabel));

                container.InitializeWith(container.Contents, searched: false);

                Assert.That(container.IsSearched, Is.False);
                Assert.That(container.Interactable.DisplayName, Is.EqualTo(LootContainer.SearchLabel));
                Assert.That(container.Interactable.Duration, Is.EqualTo(LootContainer.SearchSeconds));
                Assert.That(container.Interactable.IsAvailable, Is.True);
                Assert.That(LootKnowledge.CanSeeContents(container), Is.False);
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void KnowledgeHelper_SeesContentsOnlyOnceSearched()
        {
            var world = new TestWorld();
            try
            {
                var kit = new InventoryKit(world, 8, "a");
                var container = kit.CreateContainer(Vector3.zero, searched: false, (kit.Medkit, 1));
                Assert.That(LootKnowledge.CanSeeContents(container), Is.False);
                container.InitializeWith(container.Contents, searched: true);
                Assert.That(LootKnowledge.CanSeeContents(container), Is.True);
                Assert.That(LootKnowledge.CanSeeContents(null), Is.False);
                Assert.That(LootKnowledge.CanSeeLocation(null, container), Is.True, "no intelligence service means everything is known");
            }
            finally
            {
                world.Dispose();
            }
        }
    }
}
#endif
