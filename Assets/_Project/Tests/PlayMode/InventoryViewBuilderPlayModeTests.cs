#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class InventoryViewBuilderPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        MissionRig rig;
        SquadRoster roster;
        SquadInventory inventory;
        InventoryRequests requests;
        InventoryViewSources sources;
        readonly InventoryView view = new InventoryView();

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }.Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"), Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
            rig.AddLoot(Load<LootTable>(Items + "LootTable.asset"));
            requests = new InventoryRequests(inventory, rig.Director);
            sources = new InventoryViewSources { inventory = inventory, roster = roster, director = rig.Director };
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        string Id(int index) => roster.Members[index].Id;

        [Test]
        public void Loadout_ShowsTheStash_TheBag_AndTheEquipmentOfTheInspectedOperative()
        {
            InventoryViewBuilder.Build(sources, Id(1), default, view);

            Assert.That(view.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(view.InspectedId, Is.EqualTo(Id(1)));
            Assert.That(view.InspectedName, Is.EqualTo("Kestrel"));
            Assert.That(view.Tabs, Has.Count.EqualTo(3));
            Assert.That(view.Slots[0].ItemLabel, Does.Contain("Marksman"));
            Assert.That(view.Stash.Count, Is.GreaterThan(0));
            Assert.That(view.Bag.Any(r => r.IsEquipped && r.Label.Contains("Marksman")), Is.True);
        }

        [Test]
        public void AnUnknownInspectedId_FallsBackToTheFirstOperative()
        {
            InventoryViewBuilder.Build(sources, "nobody", default, view);
            Assert.That(view.InspectedId, Is.EqualTo(Id(0)));
        }

        [Test]
        public void Loadout_Equip_IsOfferedForAnUnequippedWeapon_AndAppliesToTheInspectedOperativeOnly()
        {
            var blade = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.combat-blade");
            Assert.That(requests.FromStash(Id(2), blade.InstanceId).Succeeded, Is.True);
            var bladeId = inventory.Core.Session.Loadout(Id(2)).Bag.Entries.First(e => e.DefinitionId == "item.combat-blade").InstanceId;

            InventoryViewBuilder.Build(sources, Id(2), new InventorySelection(InventoryListKind.Bag, bladeId), view);
            Assert.That(view.Equip.Visible && view.Equip.Enabled, Is.True);
            Assert.That(view.DetailTitle, Does.Contain("Combat Blade"));
            Assert.That(view.DetailBody, Does.Contain("Damage"));

            var result = requests.Equip(Id(2), bladeId);
            Assert.That(result.Ok, Is.True, result.Message);
            Assert.That(inventory.EquippedFor(Id(2)).Weapon.Id, Is.EqualTo("item.combat-blade"));
            Assert.That(inventory.EquippedFor(Id(0)).Weapon.Id, Is.EqualTo("item.service-rifle"), "another operative is unchanged");
        }

        [Test]
        public void Loadout_MoveToBag_ShowsWhyWhenTheBagIsFull()
        {
            var bag = inventory.Core.Session.Loadout(Id(0)).Bag;
            while (bag.FreeEntries > 0)
                bag.Add(inventory.Catalogue.Find("item.light-vest"), 1);
            var stashEntry = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.servo-boots");

            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Stash, stashEntry.InstanceId), view);

            Assert.That(view.ToBag.Visible, Is.True);
            Assert.That(view.ToBag.Enabled, Is.False);
            Assert.That(view.ToBag.Reason, Does.Contain("full"));
        }

        [Test]
        public void Loadout_AnEquippableStashItem_OffersEquip_AndTheRequestMovesAndEquipsIt()
        {
            var boots = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.servo-boots");
            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Stash, boots.InstanceId), view);

            Assert.That(view.Equip.Visible, Is.True);
            Assert.That(view.Equip.Enabled, Is.True);
            Assert.That(view.ToBag.Enabled, Is.True, "moving without equipping stays available");

            var result = requests.EquipFromStash(Id(0), boots.InstanceId);

            Assert.That(result.Ok, Is.True, result.Message);
            var loadout = inventory.Core.Session.Loadout(Id(0));
            Assert.That(loadout.Bag.Find(boots.InstanceId), Is.Not.Null);
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Utility), Is.EqualTo(boots.InstanceId));
            Assert.That(inventory.Core.Session.Stash.Find(boots.InstanceId), Is.Null);
        }

        [Test]
        public void Loadout_AStashConsumable_OffersNoEquip()
        {
            var medkit = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.medkit");

            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Stash, medkit.InstanceId), view);

            Assert.That(view.Equip.Visible, Is.False);
        }

        [Test]
        public void Loadout_EquipFromStash_WithAFullBag_ShowsWhy_AndChangesNothing()
        {
            var bag = inventory.Core.Session.Loadout(Id(0)).Bag;
            while (bag.FreeEntries > 0)
                bag.Add(inventory.Catalogue.Find("item.light-vest"), 1);
            var boots = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.servo-boots");

            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Stash, boots.InstanceId), view);
            var result = requests.EquipFromStash(Id(0), boots.InstanceId);

            Assert.That(view.Equip.Visible, Is.True);
            Assert.That(view.Equip.Enabled, Is.False);
            Assert.That(view.Equip.Reason, Does.Contain("full"));
            Assert.That(result.Ok, Is.False);
            Assert.That(inventory.Core.Session.Stash.Find(boots.InstanceId), Is.Not.Null, "still in the stash");
        }

        [UnityTest]
        public IEnumerator InMission_EquipmentIsLocked_WithTheReason_AndUseIsOffered()
        {
            yield return rig.Generate(12345);

            var medkit = inventory.Core.Working.Loadout(Id(0)).Bag.Entries.First(e => e.DefinitionId == "item.medkit");
            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Bag, medkit.InstanceId), view);

            Assert.That(view.Mode, Is.EqualTo(InventoryMode.InMission));
            Assert.That(view.Equip.Visible, Is.False, "a consumable does not equip");
            Assert.That(view.Use.Visible, Is.True);
            Assert.That(view.Use.Enabled, Is.False, "full health");
            Assert.That(view.Use.Reason, Does.Contain("full health"));

            var weapon = inventory.Core.Working.Loadout(Id(0)).Bag.Entries.First(e => e.DefinitionId == "item.service-rifle");
            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Bag, weapon.InstanceId), view);
            Assert.That(view.Unequip.Visible && !view.Unequip.Enabled, Is.True, "an equipped weapon shows Unequip, locked");
            Assert.That(view.Unequip.Reason, Does.Contain("during a mission"));
        }

        [UnityTest]
        public IEnumerator Use_AppliesToTheInspectedOperative_NotTheControlledOne()
        {
            yield return rig.Generate(12345);
            var inspected = rig.Director.Friendlies[1];   // Kestrel; Darius is the controlled character
            var controlled = rig.Director.Friendlies[0];
            Assert.That(rig.Active.Unit, Is.EqualTo(controlled));
            inspected.GetComponent<Health>().TakeDamage(40);
            var medkit = inventory.Core.Working.Loadout(Id(1)).Bag.Entries.First(e => e.DefinitionId == "item.medkit");

            var result = requests.Use(Id(1), medkit.InstanceId, queue: false);
            yield return null;
            yield return null;

            Assert.That(result.Ok, Is.True, result.Message);
            var health = inspected.GetComponent<Health>();
            Assert.That(health.Current, Is.EqualTo(health.Max), "the inspected operative was healed");
            Assert.That(inventory.Core.Working.Loadout(Id(1)).Bag.CountOf("item.medkit"), Is.EqualTo(1));
            Assert.That(inventory.Core.Working.Loadout(Id(0)).Bag.CountOf("item.medkit"), Is.EqualTo(2), "the controlled operative's medkits are untouched");
        }

        [UnityTest]
        public IEnumerator Use_AtFullHealth_IsRefusedWithAMessage_AndConsumesNothing()
        {
            yield return rig.Generate(12345);
            var medkit = inventory.Core.Working.Loadout(Id(0)).Bag.Entries.First(e => e.DefinitionId == "item.medkit");

            var result = requests.Use(Id(0), medkit.InstanceId, queue: false);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("full health"));
            Assert.That(inventory.Core.Working.Loadout(Id(0)).Bag.CountOf("item.medkit"), Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Container_ContentsAreListedOnlyOnceSearched()
        {
            yield return rig.Generate(12345);
            var container = rig.Director.Current.LootContainers[0];
            sources.container = container;

            InventoryViewBuilder.Build(sources, Id(0), default, view);
            Assert.That(view.HasContainer, Is.False, "unsearched: no contents, not even a count");
            Assert.That(view.Container, Is.Empty);

            container.InitializeWith(container.Contents, searched: true);
            InventoryViewBuilder.Build(sources, Id(0), default, view);
            Assert.That(view.HasContainer, Is.True);
            Assert.That(view.Container.Count, Is.EqualTo(container.Contents.Count));
            Assert.That(view.TakeAll.Visible, Is.True);
        }

        [UnityTest]
        public IEnumerator Take_And_TakeAll_IssueOrdersForTheInspectedOperative()
        {
            yield return rig.Generate(12345);
            var container = rig.Director.Current.LootContainers[0];
            container.InitializeWith(container.Contents, searched: true);
            var unit = rig.Director.Friendlies[2];
            UnityEngine.AI.NavMesh.SamplePosition(container.Position, out var stand, 2f, UnityEngine.AI.NavMesh.AllAreas);
            unit.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(stand.position);
            yield return null;
            var before = container.Contents.Entries.Sum(e => e.Quantity);

            var result = requests.TakeAll(Id(2), container, queue: false);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(result.Ok, Is.True, result.Message);
            var after = container.Contents.Entries.Sum(e => e.Quantity);
            Assert.That(after, Is.LessThan(before));
            Assert.That(rig.Director.Friendlies[0].GetComponent<UnitItems>().LastCollect.Taken, Is.EqualTo(0), "nobody else took anything");
        }

        static void AssertNoActions(InventoryView v)
        {
            Assert.That(v.Equip.Visible, Is.False, "Equip");
            Assert.That(v.Unequip.Visible, Is.False, "Unequip");
            Assert.That(v.ToStash.Visible, Is.False, "ToStash");
            Assert.That(v.ToBag.Visible, Is.False, "ToBag");
            Assert.That(v.Use.Visible, Is.False, "Use");
            Assert.That(v.QueueUse.Visible, Is.False, "QueueUse");
            Assert.That(v.Take.Visible, Is.False, "Take");
            Assert.That(v.QueueTake.Visible, Is.False, "QueueTake");
        }

        ItemEntry Medkit(int operative) => inventory.Core.Working.Loadout(Id(operative)).Bag.Entries.First(e => e.DefinitionId == "item.medkit");

        LootContainer SearchedContainer()
        {
            var container = rig.Director.Current.LootContainers[0];
            container.InitializeWith(container.Contents, searched: true);
            return container;
        }

        [Test]
        public void AStaleSelection_InAnotherOperativesBag_ResolvesToNothing_WithNoActions()
        {
            var darius = inventory.Core.Session.Loadout(Id(0)).Bag.Entries.First();
            Assert.That(inventory.Core.Session.Loadout(Id(1)).Bag.Find(darius.InstanceId), Is.Null, "the entry belongs to Darius only");

            InventoryViewBuilder.Build(sources, Id(1), new InventorySelection(InventoryListKind.Bag, darius.InstanceId), view);

            Assert.That(view.Selected.IsNone, Is.True);
            Assert.That(view.DetailTitle, Is.Empty);
            AssertNoActions(view);

            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Bag, darius.InstanceId), view);
            Assert.That(view.Selected.IsNone, Is.False, "the same selection is live for its owner");
        }

        [Test]
        public void AStaleSelection_OfAnEntryMovedToTheStash_ResolvesToNothing_WithNoActions()
        {
            var blade = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.combat-blade");
            Assert.That(requests.FromStash(Id(2), blade.InstanceId).Succeeded, Is.True);
            var bladeId = inventory.Core.Session.Loadout(Id(2)).Bag.Entries.First(e => e.DefinitionId == "item.combat-blade").InstanceId;
            var selection = new InventorySelection(InventoryListKind.Bag, bladeId);
            InventoryViewBuilder.Build(sources, Id(2), selection, view);
            Assert.That(view.Selected.IsNone, Is.False);

            Assert.That(requests.ToStash(Id(2), bladeId).Succeeded, Is.True);
            InventoryViewBuilder.Build(sources, Id(2), selection, view);

            Assert.That(view.Selected.IsNone, Is.True);
            AssertNoActions(view);
        }

        [UnityTest]
        public IEnumerator AContainerInAnUnknownRegion_IsNotListed_EvenWhenSearched_UntilTheRegionIsKnown()
        {
            sources.intelligence = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            var squad = rig.Director.Friendlies[0].transform.position;
            var container = rig.Director.Current.LootContainers
                .OrderByDescending(c => TestWorld.HorizontalDistance(c.Position, squad)).First();
            container.InitializeWith(container.Contents, searched: true);
            sources.container = container;
            Assert.That(LootKnowledge.CanSeeLocation(sources.intelligence, container), Is.False, "precondition: the region is fogged");

            InventoryViewBuilder.Build(sources, Id(0), default, view);
            Assert.That(view.HasContainer, Is.False);
            Assert.That(view.Container, Is.Empty);
            Assert.That(view.TakeAll.Visible, Is.False);

            sources.intelligence.Model.RevealArea(container.Position, 3f);
            yield return null;
            InventoryViewBuilder.Build(sources, Id(0), default, view);

            Assert.That(view.HasContainer, Is.True);
            Assert.That(view.Container.Count, Is.EqualTo(container.Contents.Count));
        }

        [UnityTest]
        public IEnumerator UseNow_NeedsAHurtUnit_WhileQueuedUse_IsOfferedAtFullHealth()
        {
            yield return rig.Generate(12345);
            var selection = new InventorySelection(InventoryListKind.Bag, Medkit(0).InstanceId);

            InventoryViewBuilder.Build(sources, Id(0), selection, view);
            Assert.That(view.Use.Enabled, Is.False, "full health");
            Assert.That(view.Use.Reason, Does.Contain("full health"));
            Assert.That(view.QueueUse.Visible && view.QueueUse.Enabled, Is.True, "queued use is judged when it runs, not now");

            rig.Director.Friendlies[0].GetComponent<Health>().TakeDamage(40);
            InventoryViewBuilder.Build(sources, Id(0), selection, view);
            Assert.That(view.Use.Enabled, Is.True);
            Assert.That(view.QueueUse.Enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator ADeadInspectedUnit_CannotUseOrTake_ShowsDown_AndRequestsFailWithAMessage()
        {
            yield return rig.Generate(12345);
            var container = SearchedContainer();
            sources.container = container;
            var medkitId = Medkit(1).InstanceId;
            var entryId = container.Contents.Entries[0].InstanceId;
            rig.Director.Friendlies[1].GetComponent<Health>().TakeDamage(9999);
            Assert.That(rig.Director.Friendlies[1].IsAlive, Is.False);

            InventoryViewBuilder.Build(sources, Id(1), new InventorySelection(InventoryListKind.Bag, medkitId), view);
            Assert.That(view.Tabs[1].IsDown, Is.True);
            Assert.That(view.Tabs[0].IsDown, Is.False);
            Assert.That(view.Use.Visible && !view.Use.Enabled, Is.True);
            Assert.That(view.Use.Reason, Does.Contain("is down"));
            Assert.That(view.QueueUse.Enabled, Is.False);

            InventoryViewBuilder.Build(sources, Id(1), new InventorySelection(InventoryListKind.Container, entryId), view);
            Assert.That(view.Take.Visible && !view.Take.Enabled, Is.True);
            Assert.That(view.Take.Reason, Does.Contain("is down"));
            Assert.That(view.TakeAll.Enabled, Is.False);
            Assert.That(view.TakeAll.Reason, Does.Contain("is down"));

            var use = requests.Use(Id(1), medkitId, queue: false);
            Assert.That(use.Ok, Is.False);
            Assert.That(use.Message, Does.Contain("down"));
            var take = requests.Take(Id(1), container, entryId, queue: false);
            Assert.That(take.Ok, Is.False);
            Assert.That(take.Message, Does.Contain("down"));
        }

        [UnityTest]
        public IEnumerator AnOperativeWithNoUnitOnTheMission_IsNotOnTheMission_NotDown()
        {
            yield return rig.Generate(12345);
            var container = SearchedContainer();
            sources.container = container;
            sources.director = null;

            InventoryViewBuilder.Build(sources, Id(1), new InventorySelection(InventoryListKind.Bag, Medkit(1).InstanceId), view);
            Assert.That(view.Use.Reason, Does.Contain("Kestrel is not on the mission."));
            Assert.That(view.Use.Reason, Does.Not.Contain("down"));
            Assert.That(view.TakeAll.Reason, Is.EqualTo("Kestrel is not on the mission."));
        }

        [Test]
        public void ToStash_InTheLoadout_IsBlockedForAnEquippedItem_WithAnUnequipHint()
        {
            InventoryViewBuilder.Build(sources, Id(0), default, view);
            var equippedId = view.Slots[0].InstanceId;
            Assert.That(equippedId, Is.Not.Empty);

            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Slot, equippedId), view);

            Assert.That(view.ToStash.Visible && !view.ToStash.Enabled, Is.True);
            Assert.That(view.ToStash.Reason, Does.Contain("Unequip"));
            Assert.That(view.Unequip.Visible && view.Unequip.Enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator ToStash_InAMission_IsBlockedWithTheLockedReason()
        {
            yield return rig.Generate(12345);

            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Bag, Medkit(0).InstanceId), view);
            Assert.That(view.ToStash.Visible && !view.ToStash.Enabled, Is.True);
            Assert.That(view.ToStash.Reason, Is.EqualTo(InventorySession.LockedReason));

            var equippedId = inventory.Core.Working.Loadout(Id(0)).Equipment.InstanceIdIn(ItemSlot.Weapon);
            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Bag, equippedId), view);
            Assert.That(view.ToStash.Reason, Is.EqualTo(InventorySession.LockedReason));
        }

        [UnityTest]
        public IEnumerator Use_QueueTrue_AppendsBehindTheCurrentOrder_WhileQueueFalseReplacesIt()
        {
            yield return rig.Generate(12345);
            var unit = rig.Director.Friendlies[1];
            unit.GetComponent<Health>().TakeDamage(40);
            var medkitId = Medkit(1).InstanceId;
            var container = SearchedContainer();
            UnityEngine.AI.NavMesh.SamplePosition(container.Position, out var far, 2f, UnityEngine.AI.NavMesh.AllAreas);
            var move = new MoveCommand(far.position);
            Assert.That(unit.Issue(move), Is.True);

            var queued = requests.Use(Id(1), medkitId, queue: true);

            Assert.That(queued.Ok, Is.True, queued.Message);
            Assert.That(unit.CurrentCommand, Is.SameAs(move), "queueing leaves the current order running");
            Assert.That(unit.PendingCommands.OfType<UseItemCommand>().Count(), Is.EqualTo(1));

            var now = requests.Use(Id(1), medkitId, queue: false);

            Assert.That(now.Ok, Is.True, now.Message);
            Assert.That(unit.CurrentCommand, Is.Not.SameAs(move), "queue:false replaces the current order");
            Assert.That(unit.PendingCommands, Is.Empty, "and drops the pending ones");
        }

        [UnityTest]
        public IEnumerator Take_QueueTrue_AppendsBehindTheCurrentOrder()
        {
            yield return rig.Generate(12345);
            var unit = rig.Director.Friendlies[1];
            var container = SearchedContainer();
            var entryId = container.Contents.Entries[0].InstanceId;
            UnityEngine.AI.NavMesh.SamplePosition(container.Position, out var far, 2f, UnityEngine.AI.NavMesh.AllAreas);
            var move = new MoveCommand(far.position);
            Assert.That(unit.Issue(move), Is.True);

            var queued = requests.Take(Id(1), container, entryId, queue: true);

            Assert.That(queued.Ok, Is.True, queued.Message);
            Assert.That(unit.CurrentCommand, Is.SameAs(move));
            Assert.That(unit.PendingCommands.OfType<CollectCommand>().Count(), Is.EqualTo(1));

            var now = requests.Take(Id(1), container, entryId, queue: false);

            Assert.That(now.Ok, Is.True, now.Message);
            Assert.That(unit.CurrentCommand, Is.InstanceOf<CollectCommand>(), "queue:false replaces the move");
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Deploy_WithNoDirector_SaysSo_ApartFromAGenerationInProgress()
        {
            var result = new InventoryRequests(inventory, null).DeployNew();

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("No mission director"));
        }

        [Test]
        public void Deploy_IsRefusedWhileGenerating()
        {
            rig.Director.Generate(12345);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("already being generated"));

            var result = requests.DeployNew();

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("generat"));
        }
    }
}
#endif
