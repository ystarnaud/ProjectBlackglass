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
    public class LootFogPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        MissionRig rig;
        IntelligenceService intelligence;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"));
            rig.AddLoot(Load<LootTable>(Items + "LootTable.asset"));
            intelligence = rig.AddIntelligence(IntelligenceSettings.Blind());
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        LootContainer FarContainer()
        {
            var squad = rig.Director.Friendlies[0].transform.position;
            return rig.Director.Current.LootContainers
                .OrderByDescending(c => TestWorld.HorizontalDistance(c.Position, squad)).First();
        }

        [UnityTest]
        public IEnumerator AnUnknownContainer_IsNotInteractable_NotSnappable_AndNotNearestAvailable()
        {
            yield return rig.Generate(12345);
            yield return null;
            var container = FarContainer();

            Assert.That(intelligence.CanInteract(container.Interactable), Is.False, "its region is unknown");
            Assert.That(LootKnowledge.CanSeeLocation(intelligence, container), Is.False);
            var nearest = rig.Interactables.NearestAvailable(container.Position, 2f, item => Knowledge.CanInteract(intelligence, item));
            Assert.That(nearest == container.Interactable, Is.False, "the cursor snap and proximity filters skip it");
            Assert.That(LootKnowledge.CanSeeContents(container), Is.False);
        }

        [UnityTest]
        public IEnumerator ADiscoveredContainer_IsKnownByLocation_ButItsContentsStayHiddenUntilSearched()
        {
            yield return rig.Generate(12345);
            var container = FarContainer();

            intelligence.Model.RevealArea(container.Position, 3f);   // the region becomes known
            yield return null;

            Assert.That(LootKnowledge.CanSeeLocation(intelligence, container), Is.True);
            Assert.That(LootKnowledge.CanSeeContents(container), Is.False, "knowing where it is does not reveal what is in it");
            Assert.That(container.Contents.Count, Is.GreaterThan(0), "the generator did create contents");
        }

        [UnityTest]
        public IEnumerator ReconScan_DiscoversTheLocation_NeverTheContents()
        {
            yield return rig.Generate(12345);
            var container = FarContainer();

            intelligence.Scan(container.Position, 6f, 5f);
            yield return null;

            Assert.That(LootKnowledge.CanSeeLocation(intelligence, container), Is.True);
            Assert.That(LootKnowledge.CanSeeContents(container), Is.False);
        }

        [UnityTest]
        public IEnumerator SearchingIt_MakesTheContentsKnown()
        {
            yield return rig.Generate(12345);
            var container = FarContainer();
            var unit = rig.Director.Friendlies[0];
            intelligence.Model.RevealArea(container.Position, 3f);
            Assert.That(NavMesh.SamplePosition(container.Position, out var stand, 2f, NavMesh.AllAreas), Is.True);
            unit.GetComponent<NavMeshAgent>().Warp(stand.position);
            yield return null;

            unit.Issue(new InteractCommand(container.Interactable));
            yield return TestWorld.WaitUntil(() => container.IsSearched, 6f);

            Assert.That(LootKnowledge.CanSeeContents(container), Is.True);
        }
    }
}
#endif
