#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class InventoryLifecyclePlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        MissionRig rig;
        SquadInventory inventory;
        ItemDefinition vest;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            var catalogue = Load<ItemCatalogue>(Items + "ItemCatalogue.asset");
            vest = catalogue.Find("item.light-vest");
            inventory = rig.AddInventory(catalogue);
            inventory.Core.EnsureOperative("a");
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;   // never leaks into the next test, even when one stops half-way
            rig.Dispose();
            rig = null;
        }

        // In a scene, something can read SquadInventory.Core before SquadRoster.Awake has built the members.
        [Test]
        public void ReadingCoreBeforeTheRosterIsBuilt_StillSeedsTheStarterItems_ExactlyOnce()
        {
            const string Ops = "Assets/_Project/Data/Operatives/";
            var track = Load<ProgressionTrack>(Ops + "Progression.asset");
            var squad = new[]
            {
                Load<OperativeDefinition>(Ops + "Definitions/Darius.asset"),
                Load<OperativeDefinition>(Ops + "Definitions/Kestrel.asset"),
                Load<OperativeDefinition>(Ops + "Definitions/Sable.asset"),
            };
            var roster = rig.World.Track(new GameObject("LateRoster")).AddComponent<SquadRoster>();
            roster.Initialize(new OperativeDefinition[0], track);   // no members yet
            var host = rig.World.Track(new GameObject("EarlyInventory"));
            host.SetActive(false);   // wired before Awake, as in a scene
            var early = host.AddComponent<SquadInventory>();
            early.Initialize(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"), Load<StarterLoadout>(Items + "StarterLoadout.asset"),
                roster, 8, null);
            host.SetActive(true);
            Assert.That(early.Core.Session.Loadouts.Count, Is.EqualTo(0), "precondition: read before the roster has members");
            var stash = early.Core.Session.Stash.Count;

            roster.Initialize(squad, track);   // the roster builds its members afterwards

            for (var read = 0; read < 3; read++)
            {
                foreach (var member in roster.Members)
                {
                    var loadout = early.Core.Session.Loadout(member.Id);
                    Assert.That(loadout, Is.Not.Null, member.Id + " is seeded once the roster has members");
                    Assert.That(loadout.Bag.CountOf("item.medkit"), Is.EqualTo(2), member.Id + ": granted once, never again");
                    Assert.That(early.EquippedFor(member.Id).Weapon != null, Is.True, member.Id + " deploys with a weapon");
                }
                Assert.That(early.Core.Session.Stash.Count, Is.EqualTo(stash), "the stash is seeded once");
            }
        }

        [UnityTest]
        public IEnumerator Generate_StartsAMissionInstance_AndTheWorkingStateIsACopy()
        {
            inventory.Core.Session.Loadout("a").Bag.Add(vest, 1);

            yield return rig.Generate(12345);

            Assert.That(rig.Director.InstanceId, Is.Not.Null.And.Not.Empty);
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.InMission));
            Assert.That(inventory.Core.ActiveMissionId, Is.EqualTo(rig.Director.InstanceId));
            Assert.That(inventory.Core.Working.Loadout("a").Bag.CountOf("item.light-vest"), Is.EqualTo(1));
            Assert.That(inventory.Core.Working, Is.Not.SameAs(inventory.Core.Session));
        }

        [UnityTest]
        public IEnumerator SameSeedTwice_IsTwoDistinctMissionInstances_AndTheOldWorkingStateIsGone()
        {
            yield return rig.Generate(12345);
            var first = rig.Director.InstanceId;
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            yield return rig.Generate(12345);

            Assert.That(rig.Director.InstanceId, Is.Not.EqualTo(first));
            Assert.That(inventory.Core.ActiveMissionId, Is.EqualTo(rig.Director.InstanceId));
            Assert.That(inventory.Core.Working.Loadout("a").Bag.Count, Is.EqualTo(0), "the abandoned mission's pickup is discarded");
            Assert.That(inventory.Core.Session.Loadout("a").Bag.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Clear_DiscardsTheWorkingState()
        {
            yield return rig.Generate(12345);
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            rig.Director.Clear();

            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(inventory.Core.Session.Loadout("a").Bag.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Success_CommitsOnce_EvenWhenTheEventIsRepeated()
        {
            yield return rig.Generate(12345);
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            inventory.HandleMissionFinished(MissionPhase.Success);
            inventory.HandleMissionFinished(MissionPhase.Success);

            Assert.That(inventory.Core.Session.Loadout("a").Bag.CountOf("item.light-vest"), Is.EqualTo(1));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
        }

        [UnityTest]
        public IEnumerator Failure_ThroughTheRealRuntime_DiscardsThePickups()
        {
            yield return rig.Generate(12345);
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            foreach (var unit in rig.Director.Friendlies)
                unit.GetComponent<Health>().TakeDamage(10000);
            yield return TestWorld.WaitUntil(() => rig.Director.Phase == MissionPhase.Failure, 5f);

            Assert.That(rig.Director.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(inventory.Core.Session.Loadout("a").Bag.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator AFailedGeneration_LeavesNoWorkingState()
        {
            rig.Director.Settings.maxAttempts = 1;
            rig.SetFriendlySlots(new FriendlySlot[0]);   // no slots and no roster: every spawn fails

            LogAssert.ignoreFailingMessages = true;
            yield return rig.Generate(12345);
            LogAssert.ignoreFailingMessages = false;

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Failed));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
        }

        [UnityTest]
        public IEnumerator TheWorkingState_SurvivesAFailedAttemptWithinOneGeneration()
        {
            // BeginMission runs once per generation, before the attempts; AbortMission only after the last attempt fails.
            // A seed whose first attempt fails and a later one succeeds must still be in its mission instance.
            const int lastSeed = 60;
            var found = -1;
            LogAssert.ignoreFailingMessages = true;   // a seed that fails every attempt is not what this test is about
            try
            {
                for (var seed = 1; seed <= lastSeed && found < 0; seed++)
                {
                    yield return rig.Generate(seed);
                    if (rig.Director.State == MissionState.Ready && rig.Director.Report.AttemptsMade > 1)
                        found = seed;
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
            if (found < 0)
                Assert.Ignore($"No seed in 1..{lastSeed} needed a second generation attempt (tried every seed 1..{lastSeed}); nothing to check.");
            Debug.Log($"TheWorkingState_SurvivesAFailedAttemptWithinOneGeneration: seed {found} took {rig.Director.Report.AttemptsMade} attempts");

            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.InMission), $"seed {found}");
            Assert.That(inventory.Core.ActiveMissionId, Is.EqualTo(rig.Director.InstanceId), $"seed {found}");
        }
    }
}
#endif
