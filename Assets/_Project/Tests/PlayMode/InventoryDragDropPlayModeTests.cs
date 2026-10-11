#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// Mouse drag and drop in the inventory panel, driven with the same pointer events the event system sends: a drop does
    /// exactly what the matching button does, through the same requests.
    /// </summary>
    public class InventoryDragDropPlayModeTests : InputTestFixture
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        InputActionAsset actions;
        MissionRig rig;
        SquadRoster roster;
        SquadInventory inventory;
        InventoryModal modal;
        GameObject eventSystem;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        public override void Setup()
        {
            base.Setup();
            InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            eventSystem = new GameObject("EventSystem", typeof(EventSystem));
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }.Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"), Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
            var host = rig.World.Track(new GameObject("Modal"));
            host.SetActive(false);
            modal = host.AddComponent<InventoryModal>();
            modal.Initialize(inventory, roster, rig.Director, null, actions, TestControls.Ref(actions, "Inventory/Toggle"), null, openOnStart: false);
            host.SetActive(true);
        }

        public override void TearDown()
        {
            modal.Close();
            rig.Dispose();
            Object.DestroyImmediate(eventSystem);
            TestControls.Reset(actions);
            base.TearDown();
        }

        string Id => roster.Members[0].Id;
        OperativeLoadout Loadout => inventory.Core.Active.Loadout(Id);

        Transform Panel => modal.Canvas.transform.Find("Panel");

        GameObject RowWith(string prefix, string text)
        {
            for (var i = 0; i < 14; i++)
            {
                var row = Panel.Find(prefix + i);
                if (row != null && row.gameObject.activeSelf && row.GetComponentInChildren<Text>().text.Contains(text))
                    return row.gameObject;
            }
            Assert.Fail($"no {prefix} row containing '{text}'");
            return null;
        }

        static void Drag(GameObject source, GameObject dropOn)
        {
            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = new Vector2(300f, 300f) };
            ExecuteEvents.Execute(source, data, ExecuteEvents.beginDragHandler);
            ExecuteEvents.Execute(source, data, ExecuteEvents.dragHandler);
            if (dropOn != null)
                ExecuteEvents.Execute(dropOn, data, ExecuteEvents.dropHandler);
            ExecuteEvents.Execute(source, data, ExecuteEvents.endDragHandler);
        }

        GameObject Zone(string name) => Panel.Find(name).gameObject;

        IEnumerator OpenLoadout()
        {
            modal.Open();
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator StashRow_DroppedOnTheBag_MovesItToTheBag()
        {
            yield return OpenLoadout();
            var boots = RowWith("StashRow", "Servo");
            var bagBefore = Loadout.Bag.Count;
            var stashBefore = inventory.Core.Session.Stash.Count;

            Drag(boots, Zone("BagZone"));

            Assert.That(Loadout.Bag.Count, Is.EqualTo(bagBefore + 1));
            Assert.That(inventory.Core.Session.Stash.Count, Is.EqualTo(stashBefore - 1));
        }

        [UnityTest]
        public IEnumerator StashRow_DroppedOnAnEquipmentSlot_MovesAndEquipsIt()
        {
            yield return OpenLoadout();
            var boots = RowWith("StashRow", "Servo");

            Drag(boots, Panel.Find("Slot2").gameObject);

            Assert.That(Loadout.Equipment.InstanceIdIn(ItemSlot.Utility), Is.Not.Empty);
            Assert.That(inventory.EquippedFor(Id).Utility.DisplayName, Does.Contain("Servo"));
        }

        [UnityTest]
        public IEnumerator BagRow_DroppedOnTheStash_MovesItBack_AndOnADroppedRowToo()
        {
            yield return OpenLoadout();
            var medkit = RowWith("BagRow", "Medkit");
            var stashMedkits = inventory.Core.Session.Stash.CountOf("item.medkit");
            var carried = Loadout.Bag.CountOf("item.medkit");

            Drag(medkit, Panel.Find("StashRow0").gameObject);   // a drop on a row of the list counts as a drop on the list

            Assert.That(Loadout.Bag.CountOf("item.medkit"), Is.EqualTo(0));
            Assert.That(inventory.Core.Session.Stash.CountOf("item.medkit"), Is.EqualTo(stashMedkits + carried));
        }

        [UnityTest]
        public IEnumerator EquippedSlot_DroppedOnTheBag_Unequips_ButNotOnTheStash()
        {
            yield return OpenLoadout();
            Assert.That(Loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Not.Empty, "precondition: the operative starts armed");

            Drag(Panel.Find("Slot0").gameObject, Zone("StashZone"));
            Assert.That(Loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Not.Empty, "a drop on the stash does nothing");
            StringAssert.Contains("bag", modal.Message);

            Drag(Panel.Find("Slot0").gameObject, Zone("BagZone"));
            Assert.That(Loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Null.Or.Empty);
        }

        [UnityTest]
        public IEnumerator ADropOutsideEveryZone_ChangesNothing_AndTheDragLabelGoesAway()
        {
            yield return OpenLoadout();
            var boots = RowWith("StashRow", "Servo");
            var stashBefore = inventory.Core.Session.Stash.Count;

            Drag(boots, null);

            Assert.That(inventory.Core.Session.Stash.Count, Is.EqualTo(stashBefore));
            Assert.That(modal.Canvas.transform.Find("DragProxy").gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator ADragOfAnEmptySlot_CarriesNothing_AndAFilledOneDoes()
        {
            yield return OpenLoadout();
            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = new Vector2(300f, 300f) };
            var slot = Panel.Find("Slot0").gameObject;

            ExecuteEvents.Execute(slot, data, ExecuteEvents.beginDragHandler);
            Assert.That(modal.IsDragging, Is.True, "the equipped weapon can be picked up");
            ExecuteEvents.Execute(slot, data, ExecuteEvents.endDragHandler);
            Assert.That(modal.IsDragging, Is.False);

            Drag(slot, Zone("BagZone"));   // unequip it: the slot is now empty
            yield return null;
            ExecuteEvents.Execute(slot, data, ExecuteEvents.beginDragHandler);

            Assert.That(modal.IsDragging, Is.False, "an empty slot carries nothing");
            Assert.That(modal.Canvas.transform.Find("DragProxy").gameObject.activeSelf, Is.False);
            ExecuteEvents.Execute(slot, data, ExecuteEvents.endDragHandler);
        }

        [UnityTest]
        public IEnumerator ADropBackOnTheSameList_SelectsTheRow_LikeAClick()
        {
            yield return OpenLoadout();
            var medkit = RowWith("BagRow", "Medkit");
            var expected = Loadout.Bag.Entries.First(e => e.DefinitionId == "item.medkit").InstanceId;

            Drag(medkit, Zone("BagZone"));

            Assert.That(modal.Selection.Kind, Is.EqualTo(InventoryListKind.Bag));
            Assert.That(modal.Selection.InstanceId, Is.EqualTo(expected));
        }

        IEnumerator StartASwap(System.Action<CommandableUnit> afterwards)
        {
            yield return rig.Generate(12345);
            var unit = rig.Director.Friendlies[0];
            unit.GetComponent<Health>().SetMax(100000);   // the walk of hostiles past the squad is not under test
            var id = unit.GetComponent<UnitIdentity>().OperativeId;
            inventory.Core.Working.Loadout(id).Bag.Add(inventory.Catalogue.Find("item.marksman-rifle"), 1);
            modal.Open();
            modal.InspectOperative(id);
            yield return null;
            yield return null;
            Drag(RowWith("BagRow", "Marksman"), Panel.Find("Slot0").gameObject);
            afterwards(unit);
        }

        [UnityTest]
        public IEnumerator ASwap_ReportsHowItEnded_WhenItFinishes()
        {
            CommandableUnit swapper = null;
            yield return StartASwap(unit => swapper = unit);
            StringAssert.Contains("Swapping", modal.Message);

            yield return TestWorld.WaitUntil(() => swapper.CurrentCommand == null, UnitItems.SwapSeconds * 4f);
            yield return null;
            yield return null;

            Assert.That(modal.Message, Is.EqualTo("Gear swapped."));
        }

        [UnityTest]
        public IEnumerator ASwap_ReportsItWasCancelled_WhenANewOrderReplacesIt()
        {
            CommandableUnit swapper = null;
            yield return StartASwap(unit => swapper = unit);

            swapper.Issue(new MoveCommand(swapper.transform.position + new Vector3(3f, 0f, 0f)));
            yield return null;
            yield return null;

            Assert.That(modal.Message, Is.EqualTo("Swap cancelled."));
        }

        [UnityTest]
        public IEnumerator InAMission_ABagWeaponDroppedOnASlot_StartsTheTimedSwap_AndStashDropsAreGone()
        {
            yield return rig.Generate(12345);
            var unit = rig.Director.Friendlies[0];
            var id = unit.GetComponent<UnitIdentity>().OperativeId;
            inventory.Core.Working.Loadout(id).Bag.Add(inventory.Catalogue.Find("item.marksman-rifle"), 1);
            modal.Open();
            modal.InspectOperative(id);
            yield return null;
            yield return null;

            Drag(RowWith("BagRow", "Marksman"), Panel.Find("Slot0").gameObject);

            Assert.That(unit.CurrentCommand, Is.InstanceOf<EquipItemCommand>(), "the same timed order the button sends");
            Assert.That(Zone("StashZone").activeSelf, Is.False, "the stash is not shown in a mission");
        }
    }
}
#endif
