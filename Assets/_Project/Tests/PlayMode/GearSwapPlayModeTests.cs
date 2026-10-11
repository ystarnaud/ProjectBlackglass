#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The timed gear swap in a mission: an order that takes UnitItems.SwapSeconds, cancelled by any new order.</summary>
    public class GearSwapPlayModeTests
    {
        const string Op = "op-1";
        TestWorld world;
        InventoryKit kit;
        CommandableUnit unit;
        UnitItems items;
        string rifleId;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            kit = new InventoryKit(world, 8, Op);
            kit.Inventory.Core.Session.Loadout(Op).Bag.Add(kit.Rifle, 1);
            kit.BeginMission();
            unit = world.CreateFighter(Vector3.zero);
            items = kit.Equip(unit, Op);
            rifleId = kit.Bag(Op).Entries[0].InstanceId;
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        bool RifleEquipped => kit.Inventory.Core.Working.Loadout(Op).Equipment.Contains(rifleId);

        [UnityTest]
        public IEnumerator TheSwap_TakesItsTime_ThenEquips()
        {
            yield return null;

            Assert.That(unit.Issue(new EquipItemCommand(rifleId)), Is.True);
            Assert.That(unit.CurrentCommand, Is.InstanceOf<EquipItemCommand>());
            yield return new WaitForSeconds(UnitItems.SwapSeconds * 0.4f);

            Assert.That(RifleEquipped, Is.False, "not yet: the swap has not finished");
            Assert.That(unit.CurrentCommand, Is.InstanceOf<EquipItemCommand>());
            Assert.That(unit.SwapProgress, Is.InRange(0.2f, 0.8f));

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, UnitItems.SwapSeconds * 3f);

            Assert.That(RifleEquipped, Is.True);
            Assert.That(unit.SwapProgress, Is.EqualTo(0f));
        }

        [UnityTest]
        public IEnumerator ANewOrderDuringTheSwap_CancelsIt_AndKeepsTheOldGear()
        {
            yield return null;
            unit.Issue(new EquipItemCommand(rifleId));
            yield return new WaitForSeconds(UnitItems.SwapSeconds * 0.3f);

            Assert.That(unit.Issue(new MoveCommand(new Vector3(4f, 0f, 0f))), Is.True);
            yield return new WaitForSeconds(UnitItems.SwapSeconds * 1.5f);

            Assert.That(RifleEquipped, Is.False, "the swap never finished");
        }

        [UnityTest]
        public IEnumerator ThePause_FreezesTheSwap()
        {
            var pause = Object.FindFirstObjectByType<TacticalPause>();
            yield return null;
            unit.Issue(new EquipItemCommand(rifleId));
            yield return new WaitForSeconds(UnitItems.SwapSeconds * 0.3f);
            pause.Toggle();
            var frozenAt = unit.SwapProgress;

            yield return new WaitForSecondsRealtime(UnitItems.SwapSeconds * 1.5f);

            Assert.That(unit.SwapProgress, Is.EqualTo(frozenAt).Within(0.001f));
            Assert.That(RifleEquipped, Is.False);
            pause.Toggle();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, UnitItems.SwapSeconds * 3f);
            Assert.That(RifleEquipped, Is.True, "it finishes once the pause ends");
        }

        [UnityTest]
        public IEnumerator ItemsThatCannotBeSwappedIn_AreRefused_WithAReason()
        {
            kit.Bag(Op).Add(kit.Medkit, 1);
            var medkitId = kit.Bag(Op).Entries.First(e => e.DefinitionId == "medkit").InstanceId;
            yield return null;

            Assert.That(unit.Issue(new EquipItemCommand(medkitId)), Is.False);
            Assert.That(items.LastEquipFailure, Is.EqualTo(ItemUseFailure.NotEquippable));
            Assert.That(unit.Issue(new EquipItemCommand("gone")), Is.False);
            Assert.That(items.LastEquipFailure, Is.EqualTo(ItemUseFailure.NotOwned));

            kit.Inventory.Core.EquipInMission(Op, rifleId);
            Assert.That(unit.Issue(new EquipItemCommand(rifleId)), Is.False);
            Assert.That(items.LastEquipFailure, Is.EqualTo(ItemUseFailure.AlreadyEquipped));
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ADeathDuringTheSwap_EndsIt_WithoutEquipping()
        {
            yield return null;
            unit.Issue(new EquipItemCommand(rifleId));
            yield return new WaitForSeconds(UnitItems.SwapSeconds * 0.3f);

            unit.GetComponent<Health>().TakeDamage(100000);
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(RifleEquipped, Is.False);
        }

        [UnityTest]
        public IEnumerator SteeringDuringTheSwap_CancelsIt()
        {
            yield return null;
            unit.Issue(new EquipItemCommand(rifleId));
            yield return new WaitForSeconds(UnitItems.SwapSeconds * 0.3f);

            unit.SetMoveIntent(Vector3.right);
            yield return null;
            yield return null;
            unit.SetMoveIntent(Vector3.zero);
            yield return new WaitForSeconds(UnitItems.SwapSeconds * 1.5f);

            Assert.That(RifleEquipped, Is.False, "taking the controls cancels the swap");
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ASwapRefusedWithNoItemsComponent_IsRefusedNotThrown()
        {
            var bare = world.CreateFighter(new Vector3(5f, 0f, 0f));   // no UnitItems
            yield return null;

            Assert.That(bare.Issue(new EquipItemCommand(rifleId)), Is.False);
        }

        [UnityTest]
        public IEnumerator ASwapQueuedBehindAnOrder_RunsWhenItsTurnComes()
        {
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(3f, 0f, 0f)));
            Assert.That(unit.Issue(new EquipItemCommand(rifleId), IssueMode.Append), Is.True);

            yield return TestWorld.WaitUntil(() => RifleEquipped, 15f);

            Assert.That(RifleEquipped, Is.True);
        }
    }

    /// <summary>The swap through the panel's request in a full generated mission: the unit's stats and weapon follow the new gear.</summary>
    public class GearSwapMissionPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        MissionRig rig;
        SquadInventory inventory;
        ItemCatalogue catalogue;
        SquadRoster roster;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }.Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            catalogue = Load<ItemCatalogue>(Items + "ItemCatalogue.asset");
            inventory = rig.AddInventory(catalogue, Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator SwappingToTheMarksmanRifle_TakesTheSwapTime_ThenChangesRangeDamageAndThePanelTruth()
        {
            yield return rig.Generate(12345);
            var unit = rig.Director.Friendlies[0];
            var identity = unit.GetComponent<UnitIdentity>();
            var id = identity.OperativeId;
            var marksman = catalogue.Find("item.marksman-rifle");
            var bag = inventory.Core.Working.Loadout(id).Bag;
            bag.Add(marksman, 1);
            var entry = bag.Entries.First(e => e.DefinitionId == marksman.Id).InstanceId;
            var before = identity.Effective;
            var requests = new InventoryRequests(inventory, rig.Director);

            var result = requests.Equip(id, entry);

            Assert.That(result.Ok, Is.True, result.Message);
            Assert.That(identity.Effective.AttackRange, Is.EqualTo(before.AttackRange), "the old weapon until the swap finishes");
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, UnitItems.SwapSeconds * 4f);

            Assert.That(inventory.Core.Working.Loadout(id).Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(entry));
            Assert.That(inventory.Core.Session.Loadout(id).Equipment.Contains(entry), Is.False, "the session waits for a success");
            Assert.That(identity.Effective.AttackRange, Is.EqualTo(marksman.Weapon.Range));
            Assert.That(identity.Effective.AttackDamage, Is.GreaterThan(before.AttackDamage), "the heavier weapon hits harder (the operative's own bonuses still apply)");
            Assert.That(unit.GetComponent<UnitAttacker>().HasWeapon, Is.True);
        }

        [UnityTest]
        public IEnumerator InAMission_OnlyTheSwapIsOpen_UnequipAndTheStashStayLocked()
        {
            yield return rig.Generate(12345);
            var id = rig.Director.Friendlies[0].GetComponent<UnitIdentity>().OperativeId;
            var bag = inventory.Core.Working.Loadout(id).Bag;
            bag.Add(catalogue.Find("item.marksman-rifle"), 1);
            var sources = new InventoryViewSources { inventory = inventory, roster = roster, director = rig.Director };
            var view = new InventoryView();
            var marksmanId = bag.Entries.First(e => e.DefinitionId == "item.marksman-rifle").InstanceId;

            InventoryViewBuilder.Build(sources, id, new InventorySelection(InventoryListKind.Bag, marksmanId), view);

            Assert.That(view.Equip.Enabled, Is.True, "a spare weapon in the bag can be swapped in");
            Assert.That(view.ToStash.Enabled, Is.False);
            var equippedId = inventory.Core.Working.Loadout(id).Equipment.InstanceIdIn(ItemSlot.Weapon);
            InventoryViewBuilder.Build(sources, id, new InventorySelection(InventoryListKind.Bag, equippedId), view);
            Assert.That(view.Unequip.Enabled, Is.False);
            Assert.That(view.Unequip.Reason, Is.EqualTo(InventorySession.LockedReason));
        }
    }
}
#endif
