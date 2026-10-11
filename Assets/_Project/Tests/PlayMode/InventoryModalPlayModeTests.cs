#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    public class InventoryModalPlayModeTests : InputTestFixture
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        Keyboard keyboard;
        InputActionAsset actions;
        MissionRig rig;
        SquadRoster roster;
        SquadInventory inventory;
        InventoryModal modal;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }.Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"), Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
            rig.AddLoot(Load<LootTable>(Items + "LootTable.asset"));
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
            TestControls.Reset(actions);
            base.TearDown();
        }

        string Id(int i) => roster.Members[i].Id;

        IEnumerator Tap(UnityEngine.InputSystem.Controls.KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheToggleKey_OpensAndCloses_WithoutTouchingTimeOrPause()
        {
            inventory.BeginMission("toggle-test");   // in a mission the toggle only shows and hides; between missions closing deploys
            rig.Pause.Pause();
            var scaleBefore = Time.timeScale;
            yield return null;

            yield return Tap(keyboard.iKey);
            Assert.That(modal.IsOpen, Is.True);
            Assert.That(rig.Pause.IsPaused, Is.True, "opening does not change the pause state");
            Assert.That(Time.timeScale, Is.EqualTo(scaleBefore));

            yield return Tap(keyboard.iKey);
            Assert.That(modal.IsOpen, Is.False);
            Assert.That(rig.Pause.IsPaused, Is.True);
            Assert.That(Time.timeScale, Is.EqualTo(scaleBefore));
        }

        [UnityTest]
        public IEnumerator ClosingTheLoadout_ByAnyRoute_DeploysANewSeed()
        {
            foreach (var route in new System.Action[]
                {
                    () => modal.Invoke(InventoryCommand.Close),
                    () => modal.Toggle(),
                })
            {
                modal.Open();
                yield return null;
                var before = rig.Director.InstanceId;

                route();
                yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 30f);

                Assert.That(modal.IsOpen, Is.False, "the panel closed");
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), "and a mission was generated");
                Assert.That(rig.Director.InstanceId, Is.Not.EqualTo(before), "a new mission instance");
                Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.InMission));
                rig.Director.Clear();   // back to the loadout for the next route
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator ClosingTheLoadoutWhileAMissionIsGenerating_KeepsThePanelOpen()
        {
            modal.Open();
            yield return null;
            Assert.That(rig.Director.GenerateNew(), Is.True, "precondition: a generation is under way");

            modal.Invoke(InventoryCommand.Close);

            Assert.That(modal.IsOpen, Is.True, "the deploy was refused, so the panel stays to say why");
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 30f);
            modal.Close();
        }

        [UnityTest]
        public IEnumerator InAMission_TheCloseButtonAndTheToggleJustHideThePanel()
        {
            inventory.BeginMission("m");
            modal.Open();
            yield return null;
            var instance = rig.Director.InstanceId;

            modal.Invoke(InventoryCommand.Close);

            Assert.That(modal.IsOpen, Is.False);
            Assert.That(rig.Director.InstanceId, Is.EqualTo(instance), "no new mission");
        }

        [UnityTest]
        public IEnumerator Open_DisablesGameplayInput_AndCloseRestoresExactly()
        {
            actions.FindAction("Commands/Command").Enable();   // on before opening, so "off while open" means something
            actions.FindAction("Character/Move").Enable();
            var before = actions.actionMaps.SelectMany(m => m.actions).ToDictionary(a => a, a => a.enabled);

            modal.Open();
            yield return null;
            Assert.That(actions.FindAction("Commands/Command").enabled, Is.False);
            Assert.That(actions.FindAction("Character/Move").enabled, Is.False);
            Assert.That(actions.FindAction("UI/Submit").enabled, Is.True);

            modal.Close();
            yield return null;

            foreach (var pair in before)
                Assert.That(pair.Key.enabled, Is.EqualTo(pair.Value), pair.Key.name);
        }

        [UnityTest]
        public IEnumerator AGameplayActionReEnabledWhileOpen_IsSwitchedOffNextFrame_AndRestoredOnClose()
        {
            var command = actions.FindAction("Commands/Command");
            modal.Open();
            yield return null;
            Assert.That(command.enabled, Is.False, "precondition: the gate holds gameplay off");

            command.Enable();                    // another component switches a gameplay action back on mid-open
            yield return null;
            Assert.That(command.enabled, Is.False, "the modal enforces the gate every frame while open");

            modal.Close();
            yield return null;
            Assert.That(command.enabled, Is.True, "the component wanted it on, so Close restores it on");
        }

        [UnityTest]
        public IEnumerator DisablingTheModal_WhileOpen_RestoresTheInput()
        {
            // The project-wide actions asset may start enabled: switch Submit off first, so the restore is observable.
            actions.FindAction("UI/Submit").Disable();
            modal.Open();
            Assert.That(actions.FindAction("UI/Submit").enabled, Is.True, "precondition: the modal turned the UI map on");
            var wasOn = actions.FindAction("Inventory/Toggle").enabled;

            modal.enabled = false;
            yield return null;

            Assert.That(modal.IsOpen, Is.False);
            Assert.That(actions.FindAction("UI/Submit").enabled, Is.False);
            Assert.That(wasOn, Is.True);
            modal.enabled = true;
        }

        [UnityTest]
        public IEnumerator InMission_EquipIsRefusedWithAMessage_AndNothingChanges()
        {
            yield return rig.Generate(12345);
            var weapon = inventory.Core.Working.Loadout(Id(0)).Bag.Entries.First(e => e.DefinitionId == "item.service-rifle").InstanceId;
            modal.Open();
            modal.Select(InventoryListKind.Bag, weapon);
            yield return null;

            modal.Invoke(InventoryCommand.Unequip);
            yield return null;

            Assert.That(modal.Message, Does.Contain("during a mission"));
            Assert.That(inventory.EquippedFor(Id(0)).Weapon != null, Is.True);
        }

        [UnityTest]
        public IEnumerator InLoadout_EquipAndUnequip_ActOnTheInspectedOperative_AndRepeatingDoesNotAccumulate()
        {
            modal.Open();
            modal.InspectOperative(Id(2));
            var stash = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.light-vest").InstanceId;
            modal.Select(InventoryListKind.Stash, stash);
            yield return null;
            modal.Invoke(InventoryCommand.ToBag);
            var vest = inventory.Core.Session.Loadout(Id(2)).Bag.Entries.First(e => e.DefinitionId == "item.light-vest").InstanceId;
            var baseHealth = roster.Evaluate(roster.Members[2]).MaxHealth;

            for (var i = 0; i < 4; i++)
            {
                modal.Select(InventoryListKind.Bag, vest);
                yield return null;
                modal.Invoke(InventoryCommand.Equip);
                modal.Select(InventoryListKind.Slot, vest);
                yield return null;
                modal.Invoke(InventoryCommand.Unequip);
            }
            modal.Select(InventoryListKind.Bag, vest);
            yield return null;
            modal.Invoke(InventoryCommand.Equip);

            Assert.That(inventory.EquippedFor(Id(2)).Armor.Id, Is.EqualTo("item.light-vest"));
            Assert.That(inventory.EquippedFor(Id(0)).Armor == null, Is.True, "another operative is unchanged");
            var kit = roster.Evaluate(roster.Members[2], inventory.EquippedFor(Id(2)));
            Assert.That(kit.MaxHealth, Is.EqualTo(baseHealth + 20), "four equip/unequip cycles added nothing extra");
        }

        [UnityTest]
        public IEnumerator UnsearchedContainerContents_NeverAppearInAnyText_UntilSearched()
        {
            yield return rig.Generate(12345);
            var container = rig.Director.Current.LootContainers[0];
            modal.Open(container, rig.Director.Friendlies[0]);
            yield return null;
            yield return null;

            Assert.That(modal.ContainerRowCount, Is.EqualTo(0), "unsearched: no contents are listed, not even a count");
            Assert.That(modal.ContainerRowTexts, Is.Empty);

            container.InitializeWith(container.Contents, searched: true);
            yield return null;
            yield return null;
            Assert.That(modal.ContainerRowCount, Is.EqualTo(container.Contents.Count));
            var names = container.Contents.Entries.Select(e => inventory.Catalogue.Find(e.DefinitionId).DisplayName).ToArray();
            foreach (var row in modal.ContainerRowTexts.Zip(names, (text, name) => (text, name)))
                Assert.That(row.text, Does.Contain(row.name));
        }

        [UnityTest]
        public IEnumerator SearchingAContainer_OpensTheLootView_ForTheSearchingUnit()
        {
            yield return rig.Generate(12345);
            yield return null;
            var container = rig.Director.Current.LootContainers[0];
            var unit = rig.Director.Friendlies[1];
            UnityEngine.AI.NavMesh.SamplePosition(container.Position, out var stand, 2f, UnityEngine.AI.NavMesh.AllAreas);
            unit.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(stand.position);
            yield return null;

            unit.Issue(new InteractCommand(container.Interactable));
            yield return TestWorld.WaitUntil(() => modal.IsOpen, 6f);

            Assert.That(modal.IsOpen, Is.True);
            Assert.That(modal.InspectedOperativeId, Is.EqualTo(Id(1)), "the loot view shows, and acts for, the operative who searched");
        }

        [UnityTest]
        public IEnumerator TheMissionEnding_OpensTheLoadout_WithTheResult()
        {
            yield return rig.Generate(12345);
            foreach (var unit in rig.Director.Friendlies)
                unit.GetComponent<Health>().TakeDamage(10000);
            yield return TestWorld.WaitUntil(() => modal.IsOpen, 5f);

            Assert.That(modal.IsOpen, Is.True);
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(modal.ResultLine, Does.Contain("FAILURE"));
        }

        [UnityTest]
        public IEnumerator Deploy_StartsAMission_AndClosesThePanel()
        {
            modal.Open();
            yield return null;

            modal.Invoke(InventoryCommand.DeployNew);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(modal.IsOpen, Is.False);
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.InMission));
        }

        [UnityTest]
        public IEnumerator Navigation_IsGivenToTheUiModule_OnlyWhileOpen()
        {
            var host = rig.World.Track(new GameObject("EventSystem"));
            host.AddComponent<EventSystem>();
            var module = host.AddComponent<PointerOnlyInputModule>();
            yield return null;
            Assert.That(module.move == null && module.submit == null && module.cancel == null, Is.True, "precondition: pointer only");

            modal.Open();
            Assert.That(module.move != null && module.move.action == actions.FindAction("UI/Navigate"), Is.True, "move");
            Assert.That(module.submit != null && module.submit.action == actions.FindAction("UI/Submit"), Is.True, "submit");
            Assert.That(module.cancel != null && module.cancel.action == actions.FindAction("UI/Cancel"), Is.True, "cancel");
            yield return null;

            modal.Close();
            Assert.That(module.move == null && module.submit == null && module.cancel == null, Is.True, "Close strips navigation");

            modal.Open();
            yield return null;
            modal.enabled = false;
            Assert.That(module.move == null && module.submit == null && module.cancel == null, Is.True, "disabling strips navigation");
            modal.enabled = true;
        }

        [UnityTest]
        public IEnumerator TheCancelKey_InTheLoadout_DeploysANewSeed_AndInAMissionOnlyHides()
        {
            modal.Open();
            yield return null;
            var before = rig.Director.InstanceId;

            yield return Tap(keyboard.escapeKey);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 30f);

            Assert.That(modal.IsOpen, Is.False);
            Assert.That(rig.Director.InstanceId, Is.Not.EqualTo(before), "closing the loadout with Escape deployed a new mission");
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.InMission));

            var running = rig.Director.InstanceId;
            modal.Open();
            yield return null;
            yield return Tap(keyboard.escapeKey);

            Assert.That(modal.IsOpen, Is.False);
            Assert.That(rig.Director.InstanceId, Is.EqualTo(running), "in a mission Escape only hides the panel");
        }

        [UnityTest]
        public IEnumerator TheTabKeys_StepTheInspectedOperative_AndWrap()
        {
            modal.Open();
            modal.InspectOperative(Id(0));
            yield return null;

            yield return Tap(keyboard.eKey);
            Assert.That(modal.InspectedOperativeId, Is.EqualTo(Id(1)), "next");
            yield return Tap(keyboard.qKey);
            Assert.That(modal.InspectedOperativeId, Is.EqualTo(Id(0)), "previous");
            yield return Tap(keyboard.qKey);
            Assert.That(modal.InspectedOperativeId, Is.EqualTo(Id(2)), "previous wraps to the last operative");
        }

        [UnityTest]
        public IEnumerator AMissionSuccess_OpensTheLoadout_WithTheResult()
        {
            rig.Director.Settings.hackTerminal = false;   // eliminate, then extract: the quickest mission to win in a test
            yield return rig.Generate(12345);
            foreach (var hostile in rig.Director.Hostiles)
            {
                hostile.GetComponent<EnemyAI>().enabled = false;
                hostile.GetComponent<Health>().TakeDamage(10000);
            }
            yield return TestWorld.WaitUntil(() => rig.Director.Phase == MissionPhase.ExtractionOpen, 2f);
            Assert.That(rig.Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen), "precondition");
            Assert.That(rig.Director.Friendlies[0].GetComponent<NavMeshAgent>().Warp(rig.Director.Current.ExtractionZone.position), Is.True);
            yield return TestWorld.WaitUntil(() => modal.IsOpen, 5f);

            Assert.That(rig.Director.Phase, Is.EqualTo(MissionPhase.Success));
            Assert.That(modal.IsOpen, Is.True);
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(modal.ResultLine, Does.Contain("SUCCESS"));
        }

        [UnityTest]
        public IEnumerator DeployButtons_AreHiddenInAMission()
        {
            yield return rig.Generate(12345);
            modal.Open();
            yield return null;

            Assert.That(modal.ButtonFor(InventoryCommand.DeployNew).gameObject.activeSelf, Is.False);
            Assert.That(modal.ButtonFor(InventoryCommand.DeploySame).gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator DeployButtons_AreNotInteractable_WhileTheDirectorIsGenerating()
        {
            Assert.That(rig.Director.Generate(12345), Is.True);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Generating), "precondition");
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout), "precondition: the mission has not begun yet");

            modal.Open();

            Assert.That(modal.ButtonFor(InventoryCommand.DeployNew).gameObject.activeSelf, Is.True);
            Assert.That(modal.ButtonFor(InventoryCommand.DeployNew).interactable, Is.False);
            Assert.That(modal.ButtonFor(InventoryCommand.DeploySame).interactable, Is.False);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);
        }

        [UnityTest]
        public IEnumerator TheButtons_PerformTheirCommands_InTheLoadout()
        {
            modal.Open();
            modal.InspectOperative(Id(2));
            var stash = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.light-vest").InstanceId;
            modal.Select(InventoryListKind.Stash, stash);
            yield return null;
            modal.ButtonFor(InventoryCommand.ToBag).onClick.Invoke();
            var vest = inventory.Core.Session.Loadout(Id(2)).Bag.Entries.First(e => e.DefinitionId == "item.light-vest").InstanceId;

            modal.Select(InventoryListKind.Bag, vest);
            yield return null;
            modal.ButtonFor(InventoryCommand.Equip).onClick.Invoke();
            Assert.That(inventory.EquippedFor(Id(2)).Armor.Id, Is.EqualTo("item.light-vest"), "Equip button");

            modal.Select(InventoryListKind.Slot, vest);
            yield return null;
            modal.ButtonFor(InventoryCommand.Unequip).onClick.Invoke();
            Assert.That(inventory.EquippedFor(Id(2)).Armor == null, Is.True, "Unequip button");

            modal.ButtonFor(InventoryCommand.DeployNew).onClick.Invoke();
            Assert.That(modal.IsOpen, Is.False, "Deploy button closes the panel");
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Generating), "Deploy button starts a mission");
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);
        }

        [UnityTest]
        public IEnumerator TheUseButton_UsesTheItem_AndReportsIt()
        {
            yield return rig.Generate(12345);
            foreach (var hostile in rig.Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
            var unit = rig.Director.Friendlies[0];
            unit.GetComponent<Health>().TakeDamage(30);
            var items = unit.GetComponent<UnitItems>();
            var medkit = inventory.Core.Working.Loadout(Id(0)).Bag.Entries.First(e => e.DefinitionId == "item.medkit").InstanceId;
            modal.Open();
            modal.InspectOperative(Id(0));
            modal.Select(InventoryListKind.Bag, medkit);
            yield return null;

            modal.ButtonFor(InventoryCommand.Use).onClick.Invoke();
            yield return TestWorld.WaitUntil(() => items.UsedCount > 0 && modal.Message == "Item used.", 3f);

            Assert.That(items.UsedCount, Is.EqualTo(1));
            Assert.That(modal.Message, Is.EqualTo("Item used."));
        }

        // ---- take outcomes: the order can finish in the very frame the button was pressed ----

        /// <summary>Runs a call in Update before the units update, as an event-system click can land in the same frame.</summary>
        [DefaultExecutionOrder(-1000)]
        sealed class BeforeUnits : MonoBehaviour
        {
            public System.Action Pending;

            void Update()
            {
                var call = Pending;
                Pending = null;
                call?.Invoke();
            }
        }

        LootContainer container;
        CommandableUnit taker;

        IEnumerator ClickBeforeUnits(System.Action call)
        {
            rig.World.Track(new GameObject("BeforeUnits")).AddComponent<BeforeUnits>().Pending = call;
            yield return null;
            yield return null;
            yield return null;
        }

        // A searched container with these contents, placed within reach of the first operative, shown in the panel.
        IEnumerator AContainerBesideTheTaker(ItemInventory contents)
        {
            yield return rig.Generate(12345);
            foreach (var hostile in rig.Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
            taker = rig.Director.Friendlies[0];
            container = rig.Director.Current.LootContainers[0];
            container.transform.position = taker.transform.position + Vector3.right * 1.2f;
            container.InitializeWith(contents, searched: true);
            modal.Open(container, taker);
            yield return null;
            Assert.That(taker.GetComponent<UnitItems>().InRange(container, taker.transform.position), Is.True, "precondition: within reach");
        }

        ItemInventory Holding(string definitionId, int quantity)
        {
            var contents = new ItemInventory(0);
            contents.Add(inventory.Catalogue.Find(definitionId), quantity);
            return contents;
        }

        [UnityTest]
        public IEnumerator ATakeThatFinishesInTheClickFrame_ShowsItsOutcome()
        {
            yield return AContainerBesideTheTaker(Holding("item.medkit", 1));
            modal.Select(InventoryListKind.Container, container.Contents.Entries.First().InstanceId);
            yield return null;

            yield return ClickBeforeUnits(() => modal.Invoke(InventoryCommand.Take));

            var items = taker.GetComponent<UnitItems>();
            Assert.That(items.CollectCount, Is.EqualTo(1), "precondition: the take ran");
            Assert.That(modal.Message, Is.EqualTo(items.LastCollect.Describe()));
            Assert.That(modal.Message, Does.StartWith("Took"));
        }

        [UnityTest]
        public IEnumerator ATakeIntoAFullBag_ShowsBagFull()
        {
            yield return AContainerBesideTheTaker(Holding("item.light-vest", 12));

            yield return ClickBeforeUnits(() => modal.Invoke(InventoryCommand.TakeAll));

            Assert.That(taker.GetComponent<UnitItems>().CollectCount, Is.EqualTo(1), "precondition: the take ran");
            Assert.That(modal.Message, Does.StartWith("Bag full"));
            Assert.That(container.Contents.Count, Is.GreaterThan(0), "what did not fit stays in the container");
        }

        [UnityTest]
        public IEnumerator TakingFromAContainerEmptiedMeanwhile_ShowsNothingLeft()
        {
            yield return AContainerBesideTheTaker(Holding("item.medkit", 1));
            var only = container.Contents.Entries.First().InstanceId;

            yield return ClickBeforeUnits(() =>
            {
                modal.Invoke(InventoryCommand.TakeAll);         // accepted: there is something to take
                container.Contents.Remove(only, 1);           // and then someone else empties it before the order runs
            });

            Assert.That(taker.GetComponent<UnitItems>().CollectCount, Is.EqualTo(1), "precondition: the take ran");
            Assert.That(modal.Message, Is.EqualTo("Nothing left to take."));
        }

        [UnityTest]
        public IEnumerator EveryActionButton_IsAUiButton_WiredToItsCommand()
        {
            modal.Open();
            yield return null;

            foreach (InventoryCommand command in System.Enum.GetValues(typeof(InventoryCommand)))
                Assert.That(modal.ButtonFor(command) != null, Is.True, command.ToString());
        }
    }
}
#endif
