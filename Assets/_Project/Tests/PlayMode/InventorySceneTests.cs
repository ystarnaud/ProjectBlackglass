#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class InventorySceneTests : InputTestFixture
    {
        MissionDirector director;
        SquadInventory inventory;
        InventoryModal modal;

        // The scene's components (the open panel included) share the project's InputActionAsset: destroy them so their actions
        // are given back and disabled, and none of it leaks into later fixtures.

        public override void TearDown()
        {
            Time.timeScale = 1f;
            if (SceneManager.GetActiveScene().name == "ProceduralMission")
                PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(TestControls.Load());
            base.TearDown();
        }

        IEnumerator Load()
        {
            var loading = SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
            while (!loading.isDone)
                yield return null;
            yield return null;
            director = Object.FindFirstObjectByType<MissionDirector>();
            inventory = Object.FindFirstObjectByType<SquadInventory>();
            modal = Object.FindFirstObjectByType<InventoryModal>();
        }

        [UnityTest]
        public IEnumerator TheScene_OpensOnTheLoadout_WithoutGeneratingAMission()
        {
            yield return Load();
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(inventory != null && modal != null, Is.True, "the scene has the inventory and the panel");
            Assert.That(director.State, Is.EqualTo(MissionState.Idle), "no mission until Deploy");
            Assert.That(modal.IsOpen, Is.True);
            Assert.That(inventory.StartInLoadout, Is.True);
        }

        [UnityTest]
        public IEnumerator Deploy_GeneratesAMission_WithEquippedUnits_ItemsAndContainers()
        {
            yield return Load();
            yield return null;

            modal.Invoke(InventoryCommand.DeployNew);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 30f);

            Assert.That(director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", director.Report.Failures));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.InMission));
            foreach (var unit in director.Friendlies)
            {
                Assert.That(unit.TryGetComponent<UnitItems>(out _), Is.True, unit.name);
                Assert.That(unit.GetComponent<UnitIdentity>().Inventory == inventory, Is.True);
                Assert.That(unit.GetComponent<UnitAttacker>().HasWeapon, Is.True, "starter weapons are equipped");
            }
            Assert.That(director.Report.LootRequested, Is.GreaterThan(0));
            Assert.That(modal.IsOpen, Is.False);

            // The loot path end to end in the shipped scene (loot table enabled): a scene whose loot places nothing fails here.
            Assert.That(director.Report.LootPlaced, Is.GreaterThan(0), "the shipped scene places loot");
            Assert.That(director.Current.LootContainers.Count, Is.EqualTo(director.Report.LootPlaced));
            var registry = Object.FindFirstObjectByType<InteractableRegistry>();
            Assert.That(registry, Is.Not.Null, "the scene has an interactable registry");
            foreach (var container in director.Current.LootContainers)
                Assert.That(registry.Items.Contains(container.Interactable), Is.True, container.name + " is registered as an interactable");
        }

        // The interactables a mission owns: its terminal, its camera terminal (each only when present) and its loot containers.
        static List<MissionInteractable> OwnedInteractables(GeneratedMission mission)
        {
            var owned = new List<MissionInteractable>();
            if (mission.Terminal != null)
                owned.Add(mission.Terminal);
            if (mission.CameraTerminal != null)
                owned.Add(mission.CameraTerminal);
            foreach (var container in mission.LootContainers)
                owned.Add(container.Interactable);
            return owned;
        }

        [UnityTest]
        public IEnumerator Regeneration_KeepsTheStarterItemsOnce()
        {
            yield return Load();
            yield return null;
            var medkits = inventory.Core.Session.Stash.CountOf("item.medkit");

            modal.Invoke(InventoryCommand.DeployNew);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready, 30f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), "the first deployment");
            modal.Invoke(InventoryCommand.Close);
            var oldContainers = director.Current.LootContainers.ToList();
            Assert.That(oldContainers, Is.Not.Empty, "precondition: the first mission has loot");
            Assert.That(director.RegenerateSame(), Is.True);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready, 30f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), "the regeneration");
            yield return null;   // deferred destroys of the old mission have run

            Assert.That(inventory.Core.Session.Stash.CountOf("item.medkit"), Is.EqualTo(medkits));

            // The registry holds exactly the new mission's interactables: the old containers are gone.
            var registry = Object.FindFirstObjectByType<InteractableRegistry>();
            Assert.That(oldContainers.All(c => c == null), Is.True, "the old containers were destroyed");
            var expected = OwnedInteractables(director.Current);
            Assert.That(registry.Items.Count, Is.EqualTo(expected.Count));
            Assert.That(registry.Items, Is.EquivalentTo(expected));
        }
    }
}
#endif
