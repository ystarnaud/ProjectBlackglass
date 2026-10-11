#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The HUD's Bag chip: shown only when an inventory panel exists, labelled with its key, and a click opens the panel.</summary>
    public class HudBagChipTests : InputTestFixture
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        InputActionAsset actions;
        MissionRig rig;
        InventoryModal modal;
        PointerBlocker blocker;
        GameObject hudObject;
        TacticalHud hud;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        public override void Setup()
        {
            base.Setup();
            InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }.Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            var roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            var inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"), Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
            var host = rig.World.Track(new GameObject("Modal"));
            host.SetActive(false);
            modal = host.AddComponent<InventoryModal>();
            modal.Initialize(inventory, roster, rig.Director, null, actions, TestControls.Ref(actions, "Inventory/Toggle"), null, openOnStart: false);
            host.SetActive(true);
            blocker = rig.World.Track(new GameObject("PointerBlocker")).AddComponent<PointerBlocker>();
        }

        public override void TearDown()
        {
            if (modal != null)
                modal.Close();
            if (hudObject != null)
                Object.DestroyImmediate(hudObject);
            foreach (var eventSystem in Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
                Object.DestroyImmediate(eventSystem.gameObject);
            rig.Dispose();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator StartHud(InventoryModal withModal)
        {
            hudObject = new GameObject("hud");
            hudObject.SetActive(false);
            hud = hudObject.AddComponent<TacticalHud>();
            hud.Initialize(new HudSources { director = rig.Director, controls = actions, inventoryModal = withModal }, blocker);
            hudObject.SetActive(true);
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheChip_IsShownWithItsKey_AndAClickOpensThePanel()
        {
            yield return StartHud(modal);

            Assert.That(hud.Status.BagChip.gameObject.activeInHierarchy, Is.True);
            StringAssert.StartsWith("BAG", hud.Status.BagLabel.text);
            StringAssert.Contains("I", hud.Status.BagLabel.text, "the keyboard key is named");
            Assert.That(modal.IsOpen, Is.False);

            ExecuteEvents.Execute(hud.Status.BagChip.gameObject,
                new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, clickCount = 1 },
                ExecuteEvents.pointerClickHandler);

            Assert.That(modal.IsOpen, Is.True);
        }

        [UnityTest]
        public IEnumerator ChipBlocksWorldClicks_LikeTheOtherChips()
        {
            yield return StartHud(modal);

            var centre = HudLayout.WorldRect(hud.Status.BagChip).center;

            Assert.That(hud.Gate.IsOver(centre), Is.True, "a click on the chip must not order a move behind it");
        }

        [UnityTest]
        public IEnumerator WithoutAnInventoryPanel_ThereIsNoChip()
        {
            yield return StartHud(null);

            Assert.That(hud.Status.BagChip.gameObject.activeInHierarchy, Is.False);
        }
    }
}
#endif
