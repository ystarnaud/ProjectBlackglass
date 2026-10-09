#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// The HUD's clickable elements: each request calls the gameplay API the existing input calls (and nothing else), the
    /// views raise them on a click, every request has a controller equivalent in all four pad families (spec section 8),
    /// the pointer gate blocks world clicks only over the visible panels, and a pad never moves UI focus or clicks the HUD.
    /// </summary>
    public class HudRequestsTests : InputTestFixture
    {
        static readonly string[] PadGroups = { "Xbox", "PlayStation", "Nintendo", "Gamepad" };
        static readonly Vector3 GroundPoint = new Vector3(2f, 0f, 2f);

        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        AbilityRig rig;
        PointerBlocker blocker;
        HudSources sources;
        InputActionReference modifier;
        HudRequests requests;
        GameObject hudObject;
        TacticalHud hud;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = AbilityRig.Build(actions, withCursor: false);
            blocker = rig.World.Track(new GameObject("PointerBlocker")).AddComponent<PointerBlocker>();
            rig.Input.SetPointerBlocker(blocker);
            sources = new HudSources
            {
                activeCharacter = rig.Active, selection = rig.Selection, tacticalPause = rig.Pause, encounter = rig.Encounter,
                abilityTargeting = rig.Targeting, commandInput = rig.Input, controls = actions, camera = rig.ViewCamera,
            };
            modifier = TestControls.Ref(actions, "Commands/QueueModifier");
            requests = new HudRequests(sources, modifier);
        }

        public override void TearDown()
        {
            if (hudObject != null)
                Object.DestroyImmediate(hudObject);
            foreach (var eventSystem in Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
                Object.DestroyImmediate(eventSystem.gameObject);
            rig.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        /// <summary>A live HUD (canvas, pointer-only EventSystem, LateUpdate) on the rig, wired to the blocker.</summary>
        IEnumerator StartHud()
        {
            hudObject = new GameObject("hud");
            hudObject.SetActive(false);
            hud = hudObject.AddComponent<TacticalHud>();
            hud.Initialize(sources, blocker);
            hud.SetQueueModifier(modifier);
            hudObject.SetActive(true);
            yield return null;
            yield return null;
        }

        SquadCardView CardOf(SelectableUnit unit)
        {
            for (var i = 0; i < hud.Squad.CardCapacity; i++)
            {
                var card = hud.Squad.CardAt(i);
                if (card.Root.gameObject.activeInHierarchy && card.Card.Unit == unit.Unit)
                    return card;
            }
            Assert.Fail($"no visible card for {unit.name}");
            return null;
        }

        static void Click(GameObject target, int clickCount = 1)
        {
            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, clickCount = clickCount };
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
        }

        static Vector2 Centre(RectTransform rect) => HudLayout.WorldRect(rect).center;

        // ---- requests ----

        [UnityTest]
        public IEnumerator SelectUnit_SelectsOnlyThatUnit_AndChangesNothingElse()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);

            requests.SelectUnit(rig.Ally.Unit, additive: false);

            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Ally }));
            Assert.That(rig.Active.Unit, Is.SameAs(rig.Caster.Unit), "selecting is not taking control");
            Assert.That(rig.Ally.Unit.CurrentCommand, Is.Null);
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.Null);
            Assert.That(rig.Pause.IsPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator SelectUnit_Additive_TogglesTheUnit()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);

            requests.SelectUnit(rig.Ally.Unit, additive: true);
            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Caster, rig.Ally }));

            requests.SelectUnit(rig.Ally.Unit, additive: true);
            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Caster }));
        }

        [UnityTest]
        public IEnumerator AdditiveHeld_ReadsTheQueueModifierAction()
        {
            yield return null;
            Assert.That(requests.AdditiveHeld, Is.False);
            Press(keyboard.leftShiftKey);
            yield return null;
            Assert.That(requests.AdditiveHeld, Is.True);
            Release(keyboard.leftShiftKey);
            yield return null;
            Assert.That(requests.AdditiveHeld, Is.False);
        }

        [UnityTest]
        public IEnumerator TakeControl_FollowsActiveCharacterRules()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);

            Assert.That(requests.TakeControl(rig.Ally.Unit), Is.True);
            Assert.That(rig.Active.Unit, Is.SameAs(rig.Ally.Unit));
            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Ally }), "like Tab: the new character is the only selected unit");

            Assert.That(requests.TakeControl(rig.Ally.Unit), Is.False, "already in control");
            rig.CasterHealth.TakeDamage(10000);
            yield return null;
            Assert.That(requests.TakeControl(rig.Caster.Unit), Is.False, "a dead unit cannot take control");
            Assert.That(rig.Active.Unit, Is.SameAs(rig.Ally.Unit));
        }

        [UnityTest]
        public IEnumerator ToggleAbility_ArmsThenDisarmsTheSameSlot_AndSwitchesSlots()
        {
            yield return null;

            requests.ToggleAbility(0);
            Assert.That(rig.Targeting.IsArmed, Is.True);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(0));

            requests.ToggleAbility(1);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(1), "another slot arms that one");

            requests.ToggleAbility(1);
            Assert.That(rig.Targeting.IsArmed, Is.False, "the armed slot again disarms");
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.Null, "arming is not an order");
        }

        [UnityTest]
        public IEnumerator ClearOrders_EmptiesTheQueue_LikeTheStopAction()
        {
            var companionA = rig.World.CreateCompanion(new Vector3(-3f, 0f, -9f), rig.Active, rig.Encounter);
            var companionB = rig.World.CreateCompanion(new Vector3(3f, 0f, -9f), rig.Active, rig.Encounter);
            var selectableA = companionA.gameObject.AddComponent<SelectableUnit>();
            companionB.gameObject.AddComponent<SelectableUnit>();
            rig.Selection.AddToRoster(selectableA);
            yield return null;
            var a = companionA.GetComponent<CommandableUnit>();
            var b = companionB.GetComponent<CommandableUnit>();
            foreach (var unit in new[] { a, b })
            {
                unit.Issue(new MoveCommand(unit.transform.position + new Vector3(0f, 0f, 6f)));
                unit.Issue(new MoveCommand(unit.transform.position + new Vector3(0f, 0f, 9f)), IssueMode.Append);
            }
            yield return null;
            Assert.That(a.PendingCommands.Count, Is.EqualTo(1));
            Assert.That(b.PendingCommands.Count, Is.EqualTo(1));
            var stopsA = a.StopCount;
            var stopsB = b.StopCount;

            rig.Selection.Select(selectableA);
            Press(keyboard.xKey);       // the Stop action, on the selected companion A
            requests.ClearOrders(b);    // the HUD's CLEAR, on companion B
            yield return null;
            Release(keyboard.xKey);
            yield return null;
            yield return null;

            Assert.That(b.CurrentCommand, Is.Null);
            Assert.That(b.PendingCommands, Is.Empty);
            Assert.That(a.CurrentCommand, Is.Null, "precondition: the Stop action stopped A");
            Assert.That(b.StopCount - stopsB, Is.EqualTo(a.StopCount - stopsA), "the same Stop");
            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { selectableA }), "clearing orders does not select");
        }

        [UnityTest]
        public IEnumerator ClearOrders_ParksAnAttachedCompanion_LikeTheStopAction()
        {
            // Inside the leader's magnet and with no explicit order: attached, so only the Stop can park them.
            var companionA = rig.World.CreateCompanion(new Vector3(-2f, 0f, -8f), rig.Active, rig.Encounter);
            var companionB = rig.World.CreateCompanion(new Vector3(2f, 0f, -8f), rig.Active, rig.Encounter);
            var selectableA = companionA.gameObject.AddComponent<SelectableUnit>();
            companionB.gameObject.AddComponent<SelectableUnit>();
            rig.Selection.AddToRoster(selectableA);
            yield return null;
            yield return null;
            var a = companionA.GetComponent<CommandableUnit>();
            var b = companionB.GetComponent<CommandableUnit>();
            Assert.That(companionA.IsParked, Is.False, "precondition: A is attached");
            Assert.That(companionB.IsParked, Is.False, "precondition: B is attached");
            Assert.That(a.CurrentCommand, Is.Null);
            Assert.That(b.CurrentCommand, Is.Null);
            var stopsA = a.StopCount;
            var stopsB = b.StopCount;

            rig.Selection.Select(selectableA);
            Press(keyboard.xKey);       // the Stop action, on the selected companion A
            requests.ClearOrders(b);    // the HUD's CLEAR, on companion B
            yield return null;
            Release(keyboard.xKey);
            yield return null;
            yield return null;

            Assert.That(a.StopCount - stopsA, Is.EqualTo(1), "the Stop action issued one Stop");
            Assert.That(b.StopCount - stopsB, Is.EqualTo(1), "CLEAR issued one Stop");
            Assert.That(companionA.IsParked, Is.True, "the Stop action parks");
            Assert.That(companionB.IsParked, Is.True, "CLEAR parks the same way");
        }

        [UnityTest]
        public IEnumerator ToggleFollow_AndTogglePause_FlipTheirFlags()
        {
            yield return null;
            Assert.That(rig.Active.IsFollowOn, Is.True);
            requests.ToggleFollow();
            Assert.That(rig.Active.IsFollowOn, Is.False);
            requests.ToggleFollow();
            Assert.That(rig.Active.IsFollowOn, Is.True);

            requests.TogglePause();
            Assert.That(rig.Pause.IsPaused, Is.True);
            requests.TogglePause();
            Assert.That(rig.Pause.IsPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator ADestroyedOrDeadUnit_IsASafeNoOp()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            var gone = rig.Ally.Unit;
            Object.DestroyImmediate(rig.Ally.gameObject);

            Assert.DoesNotThrow(() => requests.SelectUnit(gone, false));
            Assert.DoesNotThrow(() => requests.SelectUnit(gone, true));
            Assert.DoesNotThrow(() => requests.ClearOrders(gone));
            Assert.That(requests.TakeControl(gone), Is.False);
            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Caster }));
            Assert.That(rig.Active.Unit, Is.SameAs(rig.Caster.Unit));

            var nobody = new HudRequests(new HudSources(), null);
            Assert.DoesNotThrow(() =>
            {
                nobody.SelectUnit(rig.Caster.Unit, false);
                nobody.TakeControl(rig.Caster.Unit);
                nobody.ToggleAbility(0);
                nobody.ClearOrders(rig.Caster.Unit);
                nobody.ToggleFollow();
                nobody.TogglePause();
            });
            Assert.That(nobody.AdditiveHeld, Is.False);
            Assert.DoesNotThrow(() => new HudRequests(null, null).TogglePause());
        }

        [UnityTest]
        public IEnumerator SelectUnit_OnADeadUnit_KeepsTheSelection()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            rig.AllyHealth.TakeDamage(10000);
            yield return null;

            requests.SelectUnit(rig.Ally.Unit, false);
            requests.SelectUnit(rig.Ally.Unit, true);

            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Caster }));
        }

        // ---- views raise the requests ----

        [UnityTest]
        public IEnumerator ACardClick_Selects_ADoubleClick_TakesControl_AndTheModifierAdds()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            yield return StartHud();

            Click(CardOf(rig.Ally).Root.gameObject, 1);
            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Ally }));
            Assert.That(rig.Active.Unit, Is.SameAs(rig.Caster.Unit));

            Press(keyboard.leftShiftKey);
            yield return null;
            Click(CardOf(rig.Caster).Root.gameObject, 1);
            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Ally, rig.Caster }), "the queue modifier adds");
            Release(keyboard.leftShiftKey);
            yield return null;

            Click(CardOf(rig.Ally).Root.gameObject, 2);
            Assert.That(rig.Active.Unit, Is.SameAs(rig.Ally.Unit), "a double click takes control");
            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Ally }));
        }

        [UnityTest]
        public IEnumerator SlotClearFollowAndPauseClicks_SendTheirRequests()
        {
            yield return null;
            rig.Caster.Unit.Issue(new MoveCommand(rig.Caster.transform.position + new Vector3(0f, 0f, 4f)));
            rig.Caster.Unit.Issue(new MoveCommand(rig.Caster.transform.position + new Vector3(0f, 0f, 8f)), IssueMode.Append);
            yield return StartHud();

            var slot = hud.Operative.SlotAt(1);
            Assert.That(slot.Root.gameObject.activeInHierarchy, Is.True);
            Click(slot.Root.gameObject);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(1));
            Click(slot.Root.gameObject);
            Assert.That(rig.Targeting.IsArmed, Is.False);

            Assert.That(hud.Operative.ClearButton.gameObject.activeInHierarchy, Is.True, "the queue has orders");
            Click(hud.Operative.ClearButton.gameObject);
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.Null);
            Assert.That(rig.Caster.Unit.PendingCommands, Is.Empty);

            Assert.That(hud.Status.FollowChip.gameObject.activeInHierarchy, Is.True);
            Click(hud.Status.FollowChip.gameObject);
            Assert.That(rig.Active.IsFollowOn, Is.False);

            Assert.That(hud.Status.PauseChip.gameObject.activeInHierarchy, Is.True);
            Assert.That(hud.Status.PauseChipLabel.text, Is.EqualTo(HudText.PauseChip(false)));
            Click(hud.Status.PauseChip.gameObject);
            Assert.That(rig.Pause.IsPaused, Is.True);
            yield return null;
            Assert.That(hud.Status.PauseChip.gameObject.activeInHierarchy, Is.True, "the chip stays to resume");
            Assert.That(hud.Status.PauseChipLabel.text, Is.EqualTo(HudText.PauseChip(true)));
            Click(hud.Status.PauseChip.gameObject);
            Assert.That(rig.Pause.IsPaused, Is.False);
        }

        // ---- spec section 8: every mouse request has a controller equivalent in every pad family ----

        [TestCase("Character/PreviousCharacter", false, TestName = "SquadCard_HasAControllerEquivalent_PreviousCharacter")]
        [TestCase("Character/NextCharacter", false, TestName = "SquadCard_HasAControllerEquivalent_NextCharacter")]
        [TestCase("Commands/QueueModifier", false, TestName = "AdditiveSelect_HasAControllerEquivalent_QueueModifier")]
        [TestCase("Commands/Confirm", false, TestName = "AdditiveSelect_HasAControllerEquivalent_Confirm")]
        [TestCase("Commands/Ability1", true, TestName = "AbilitySlot_HasAControllerEquivalent_Ability1")]
        [TestCase("Commands/Ability2", true, TestName = "AbilitySlot_HasAControllerEquivalent_Ability2")]
        [TestCase("Commands/Ability3", true, TestName = "AbilitySlot_HasAControllerEquivalent_Ability3")]
        [TestCase("Commands/Ability4", true, TestName = "AbilitySlot_HasAControllerEquivalent_Ability4")]
        [TestCase("Commands/Stop", false, TestName = "ClearOrders_HasAControllerEquivalent_Stop")]
        [TestCase("Character/ToggleFollow", false, TestName = "FollowChip_HasAControllerEquivalent_ToggleFollow")]
        [TestCase("Commands/ToggleTacticalPause", false, TestName = "PauseChip_HasAControllerEquivalent_ToggleTacticalPause")]
        public void EveryHudRequest_HasAControllerActionBoundInEveryPadFamily(string actionPath, bool chord)
        {
            var action = actions.FindAction(actionPath, throwIfNotFound: true);
            foreach (var group in PadGroups)
            {
                var bindings = action.bindings.Where(b => InGroup(b, group)).ToArray();
                Assert.That(bindings.Any(b => !b.isComposite && !string.IsNullOrEmpty(b.path)), Is.True, $"{actionPath} has no {group} binding");
                if (chord)
                    Assert.That(HasChordIn(action, group), Is.True,
                        $"{actionPath} in {group}: one composite whose modifier and button parts are both bound in {group}");
            }
        }

        static bool InGroup(InputBinding binding, string group) =>
            binding.groups != null && binding.groups.Split(InputBinding.Separator).Contains(group);

        // A composite followed by its own parts, among them a bound "modifier" and a bound "button" in this group.
        static bool HasChordIn(InputAction action, string group)
        {
            var bindings = action.bindings;
            for (var i = 0; i < bindings.Count; i++)
            {
                if (!bindings[i].isComposite)
                    continue;
                bool modifier = false, button = false;
                for (var j = i + 1; j < bindings.Count && bindings[j].isPartOfComposite; j++)
                {
                    var part = bindings[j];
                    if (!InGroup(part, group) || string.IsNullOrEmpty(part.path))
                        continue;
                    modifier |= part.name == "modifier";
                    button |= part.name == "button";
                }
                if (modifier && button)
                    return true;
            }
            return false;
        }

        // ---- pointer gate ----

        [Test]
        public void TheGate_IsOverOnlyActiveRegisteredRects()
        {
            var root = HudLayout.CreateRoot(1920f, 1080f);
            rig.World.Track(root.gameObject);
            root.position = new Vector3(960f, 540f, 0f);   // bottom-left at the screen origin, like an overlay canvas
            var panel = HudFactory.Rect("Panel", root);
            HudFactory.Place(panel, Vector2.zero, new Vector2(100f, 100f), new Vector2(200f, 50f));
            var other = HudFactory.Rect("Other", root);
            HudFactory.Place(other, Vector2.zero, new Vector2(500f, 500f), new Vector2(50f, 50f));
            var unregistered = HudFactory.Rect("Unregistered", root);
            HudFactory.Place(unregistered, Vector2.zero, new Vector2(800f, 800f), new Vector2(50f, 50f));
            var gate = new HudPointerGate();
            gate.Register(panel);
            gate.Register(other);

            Assert.That(gate.IsOver(new Vector2(150f, 120f)), Is.True, "inside");
            Assert.That(gate.IsOver(new Vector2(520f, 520f)), Is.True, "inside the second");
            Assert.That(gate.IsOver(new Vector2(99f, 120f)), Is.False, "left of it");
            Assert.That(gate.IsOver(new Vector2(150f, 151f)), Is.False, "above it");
            Assert.That(gate.IsOver(new Vector2(820f, 820f)), Is.False, "an unregistered rect");

            panel.gameObject.SetActive(false);
            Assert.That(gate.IsOver(new Vector2(150f, 120f)), Is.False, "an inactive rect");
            panel.gameObject.SetActive(true);
            root.gameObject.SetActive(false);
            Assert.That(gate.IsOver(new Vector2(150f, 120f)), Is.False, "inactive in the hierarchy");
            root.gameObject.SetActive(true);
            Object.DestroyImmediate(other.gameObject);
            Assert.That(gate.IsOver(new Vector2(520f, 520f)), Is.False, "a destroyed rect is skipped");
            Assert.That(gate.IsOver(new Vector2(150f, 120f)), Is.True);
        }

        [Test]
        public void TheHudGate_BlocksTheVisiblePanels_AndNotTheirEmptySpace()
        {
            var hostObject = rig.World.Track(new GameObject("hud"));
            hostObject.SetActive(false);
            hud = hostObject.AddComponent<TacticalHud>();
            hud.Initialize(new HudSources(), null);
            var root = HudLayout.CreateRoot(1920f, 1080f);
            rig.World.Track(root.gameObject);
            root.position = new Vector3(960f, 540f, 0f);
            hud.BuildInto(root);
            var s = hud.Snapshot;
            s.Squad.Add(new HudSquadCard { Name = "Alpha", MaxHealth = 100, Health = 100 });
            s.Squad.Add(new HudSquadCard { Name = "Bravo", MaxHealth = 100, Health = 100 });
            s.IsPaused = true;
            hud.ApplySnapshot();
            HudLayout.Rebuild(root);
            var gate = hud.Gate;

            var top = HudLayout.WorldRect(hud.Squad.CardAt(0).Root);
            var bottom = HudLayout.WorldRect(hud.Squad.CardAt(1).Root);
            Assert.That(gate.IsOver(top.center), Is.True, "a card blocks");
            Assert.That(gate.IsOver(bottom.center), Is.True);
            Assert.That(gate.IsOver(new Vector2(top.center.x, (top.yMin + bottom.yMax) * 0.5f)), Is.True, "the gap between cards blocks");
            var zone = HudLayout.WorldRect(hud.Squad.Root);
            Assert.That(zone.yMax, Is.GreaterThan(top.yMax + 100f), "precondition: the zone has free space above two cards");
            Assert.That(gate.IsOver(new Vector2(top.center.x, top.yMax + 40f)), Is.False, "empty squad space above the cards does not block");

            Assert.That(gate.IsOver(Centre(hud.Operative.Root)), Is.True, "the operative panel blocks");
            Assert.That(gate.IsOver(Centre(hud.Status.PauseBanner)), Is.False, "the pause banner does not block");
            Assert.That(gate.IsOver(Centre(hud.Root)), Is.False, "the full-screen root and the world marks do not block");
            Assert.That(gate.IsOver(Centre(hud.Target.Root)), Is.False, "a hidden target panel does not block");
            Assert.That(gate.IsOver(Centre(hud.Prompts.Root)), Is.False, "an empty prompt zone does not block");

            s.Target = new HudTarget { Visible = true, Name = "Hostile", MaxHealth = 10, Health = 10 };
            s.Prompts.Add(new HudPromptEntry { Prompt = "P", Label = "Label" });
            s.HasFollow = true;
            s.HasPause = true;
            s.HasMission = true;
            s.Squad.Clear();
            hud.ApplySnapshot();
            HudLayout.Rebuild(root);
            Assert.That(hud.Target.Root.gameObject.activeInHierarchy, Is.True, "precondition: the target panel is shown");
            Assert.That(gate.IsOver(Centre(hud.Target.Root)), Is.False,
                "the target panel is information only: it never blocks, even when shown (it follows the hover)");
            Assert.That(gate.IsOver(Centre(hud.Prompts.Background)), Is.True, "shown prompts block");
            Assert.That(gate.IsOver(Centre(hud.Status.FollowChip)), Is.True, "the follow chip blocks");
            Assert.That(gate.IsOver(Centre(hud.Status.PauseChip)), Is.True, "the pause chip blocks");
            Assert.That(gate.IsOver(Centre(hud.Objectives.Root)), Is.True, "the objectives block");
            Assert.That(gate.IsOver(top.center), Is.False, "no squad, no block");
        }

        // ---- end to end with PlayerCommandInput ----

        [UnityTest]
        public IEnumerator ALeftClickOnASquadCard_IssuesNoOrder_AndOffTheHudOneIs()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            yield return StartHud();
            var card = CardOf(rig.Ally);
            var onCard = Centre(card.Root);
            Assert.That(blocker.IsBlocking(onCard), Is.True, "the HUD installed its gate");
            Assert.That(rig.Input.ResolveAt(onCard).Kind, Is.EqualTo(PointerTargetKind.Ground),
                "precondition: without the HUD this click would be a ground order");

            Set(mouse.position, onCard);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            yield return null;

            Assert.That(rig.Caster.Unit.CurrentCommand, Is.Null, "no world order through the HUD");
            Assert.That(rig.Ally.Unit.CurrentCommand, Is.Null);
            Assert.That(rig.Selection.Selected, Is.EqualTo(new[] { rig.Ally }), "the click went to the card instead");

            rig.Selection.Select(rig.Caster);
            var ground = (Vector2)rig.ScreenPointOf(GroundPoint);
            Assert.That(blocker.IsBlocking(ground), Is.False, "precondition: the ground point is clear of the HUD");
            Set(mouse.position, ground);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;

            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator AHostileBehindTheTargetZone_KeepsThePanelSteady_AndCanStillBeAttacked()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            yield return StartHud();
            var zone = Centre(hud.Target.Root);
            Assert.That(zone.x, Is.InRange(0f, Screen.width), "precondition: the target zone is on screen");
            Assert.That(zone.y, Is.InRange(0f, Screen.height), "precondition: the target zone is on screen");

            // An observed hostile (no intelligence: everything is observed) right behind the zone's centre.
            var ray = rig.ViewCamera.ScreenPointToRay(zone);
            Assert.That(new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var distance), Is.True);
            rig.FarHostile.transform.position = ray.GetPoint(distance) + Vector3.up;
            Physics.SyncTransforms();
            var pointed = rig.Input.ResolveAt(zone);
            Assert.That(pointed.Kind, Is.EqualTo(PointerTargetKind.Hostile), "precondition: the hostile is under the zone centre");
            Assert.That(pointed.Hostile, Is.SameAs(rig.FarHostile));

            Set(mouse.position, zone);
            yield return TestWorld.WaitUntil(() => hud.Snapshot.Target.Visible, 2f);
            Assert.That(hud.Snapshot.Target.Visible, Is.True, "hovering the hostile shows the target panel");
            for (var frame = 0; frame < 15; frame++)
            {
                yield return null;
                Assert.That(hud.Snapshot.Target.Visible, Is.True, $"frame {frame}: the target stays in the snapshot");
                Assert.That(hud.Target.Root.gameObject.activeSelf, Is.True, $"frame {frame}: the panel does not flicker");
            }
            Assert.That(blocker.IsBlocking(zone), Is.False, "the shown target panel does not block");

            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<AttackCommand>(), "a click there still attacks the hostile");
            Assert.That(((AttackCommand)rig.Caster.Unit.CurrentCommand).Target, Is.SameAs(rig.FarHostile));
        }

        [Test]
        public void EveryRaycastTarget_LiesInsideTheGate_WithMaximumContent()
        {
            var hostObject = rig.World.Track(new GameObject("hud"));
            hostObject.SetActive(false);
            hud = hostObject.AddComponent<TacticalHud>();
            hud.Initialize(new HudSources(), null);
            var root = HudLayout.CreateRoot(1920f, 1080f);
            rig.World.Track(root.gameObject);
            root.position = new Vector3(960f, 540f, 0f);
            hud.BuildInto(root);
            var s = hud.Snapshot;
            s.HasMission = true;
            s.PhaseText = "in progress";
            s.Objectives.Add(new HudObjectiveRow { Kind = HudObjectiveKind.Active, Text = "Reach the terminal" });
            s.Extraction = HudExtractionState.Active;
            s.ExtractionInside = 1;
            s.ExtractionRequired = 3;
            s.IsPaused = true;
            s.HasPause = true;
            s.HasFollow = true;
            for (var i = 0; i < 6; i++)
                s.Squad.Add(new HudSquadCard { Name = "Operative " + i, MaxHealth = 100, Health = 100 });
            s.HasControlled = true;
            s.ControlledName = "Operative 0";
            for (var i = 0; i < 4; i++)
                s.Abilities.Add(new HudAbilitySlot { Slot = i, Name = "Ability " + i, Prompt = "P" + i });
            s.QueueOwner = "Operative 0";
            s.Queue.Add(new HudCommandStep { Number = 1, Text = "Move", IsCurrent = true });
            s.CanClearOrders = true;
            s.Prompts.Add(new HudPromptEntry { Prompt = "P", Label = "Label" });
            s.Target = new HudTarget { Visible = true, Name = "Hostile", MaxHealth = 10, Health = 10 };
            hud.ApplySnapshot();
            HudLayout.Rebuild(root);

            var targets = 0;
            foreach (var graphic in root.GetComponentsInChildren<Graphic>(false))
            {
                if (!graphic.raycastTarget)
                    continue;
                targets++;
                var r = HudLayout.WorldRect(graphic.rectTransform);
                Vector2[] points =
                {
                    r.center, new Vector2(r.xMin + 0.5f, r.yMin + 0.5f), new Vector2(r.xMax - 0.5f, r.yMin + 0.5f),
                    new Vector2(r.xMin + 0.5f, r.yMax - 0.5f), new Vector2(r.xMax - 0.5f, r.yMax - 0.5f),
                };
                foreach (var point in points)
                    Assert.That(hud.Gate.IsOver(point), Is.True, $"{graphic.name} catches clicks at {point} outside the gate");
            }
            Assert.That(targets, Is.GreaterThanOrEqualTo(6 + 4 + 1 + 2), "cards, slots, CLEAR and the two chips");
        }

        [UnityTest]
        public IEnumerator InitializeWhileEnabled_MovesTheGateToTheNewBlocker()
        {
            yield return StartHud();
            var other = rig.World.Track(new GameObject("OtherBlocker")).AddComponent<PointerBlocker>();

            hud.Initialize(sources, other);
            Assert.That(blocker.HasTest, Is.False, "the old blocker no longer carries the gate");
            Assert.That(other.HasTest, Is.True);

            hudObject.SetActive(false);
            Assert.That(other.HasTest, Is.False);
        }

        [UnityTest]
        public IEnumerator DisablingTheHud_RemovesTheBlockerTestAndTheHoverGate()
        {
            yield return StartHud();
            Assert.That(blocker.HasTest, Is.True);
            Assert.That(hud.Builder.PointerOverHud, Is.Not.Null);

            hudObject.SetActive(false);
            Assert.That(blocker.HasTest, Is.False);
            Assert.That(hud.Builder.PointerOverHud, Is.Null);

            hudObject.SetActive(true);
            Assert.That(blocker.HasTest, Is.True, "enabling again reinstalls it");
        }

        // ---- a pad never drives the HUD ----

        [UnityTest]
        public IEnumerator PadInput_NeverMovesUiFocus_NorClicksTheHud()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            yield return null;
            rig.Caster.Unit.Issue(new MoveCommand(rig.Caster.transform.position + new Vector3(0f, 0f, 4f)));
            rig.Caster.Unit.Issue(new MoveCommand(rig.Caster.transform.position + new Vector3(0f, 0f, 8f)), IssueMode.Append);
            yield return StartHud();
            var clicks = 0;
            for (var i = 0; i < hud.Squad.CardCapacity; i++)
                hud.Squad.CardAt(i).Clicked += (u, twice) => clicks++;
            for (var i = 0; i < hud.Operative.SlotCapacity; i++)
                hud.Operative.SlotAt(i).Clicked += slot => clicks++;
            hud.Operative.ClearClicked += u => clicks++;
            hud.Status.FollowClicked += () => clicks++;
            hud.Status.PauseClicked += () => clicks++;
            Assert.That(EventSystem.current, Is.Not.Null);

            ButtonControl[] buttons = { pad.buttonSouth, pad.buttonEast, pad.buttonWest, pad.buttonNorth, pad.startButton, pad.dpad.down, pad.dpad.up };
            Set(pad.leftStick, new Vector2(0f, -1f));
            yield return null;
            Set(pad.leftStick, Vector2.zero);
            yield return null;
            foreach (var button in buttons)
            {
                Press(button);
                yield return null;
                Release(button);
                yield return null;
                Assert.That(EventSystem.current.currentSelectedGameObject, Is.Null, $"{button.name} moved UI focus");
            }
            Assert.That(clicks, Is.Zero, "no HUD element was pressed by the pad");

            // Even with an element focused by code, the pad's submit button does not press it.
            EventSystem.current.SetSelectedGameObject(hud.Status.FollowChip.gameObject);
            Press(pad.buttonSouth);
            yield return null;
            Release(pad.buttonSouth);
            yield return null;
            Assert.That(clicks, Is.Zero, "submit does not press a focused HUD element");
        }
    }
}
#endif
