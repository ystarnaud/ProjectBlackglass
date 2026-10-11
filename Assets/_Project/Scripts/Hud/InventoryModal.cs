using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Blackglass
{
    public enum InventoryCommand
    {
        Equip, Unequip, ToStash, ToBag, Use, QueueUse, Take, QueueTake, TakeAll, QueueTakeAll, DeployNew, DeploySame, Close,
    }

    /// <summary>
    /// The inventory and loadout panel: the one place the game has controller focus (decision 043 amends 042 for this panel
    /// only). It owns a screen-space canvas above the HUD, opens with the Inventory/Toggle action or when a loot container
    /// opens or a mission ends, and while it is open the InputModalGate disables every gameplay action and the event system
    /// navigates with the UI map. It draws an InventoryView and sends InventoryRequests; it keeps no item state, and every
    /// request names the inspected operative. Opening never changes the time scale or the tactical pause.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10)]   // after SquadRoster (0) and SquadInventory (5) have built the squad and its items
    public sealed class InventoryModal : MonoBehaviour
    {
        const int SortingOrder = 20;
        const int MaxRows = 14;
        const float RowHeight = 34f;
        const float RefreshSeconds = 0.25f;

        [SerializeField] SquadInventory inventory;
        [SerializeField] SquadRoster roster;
        [SerializeField] MissionDirector director;
        [SerializeField] IntelligenceService intelligence;
        [SerializeField] InputActionAsset controls;
        [SerializeField] InputActionReference toggle;
        [SerializeField] ActiveInputDevice inputDevice;
        [SerializeField] bool openOnStart = true;

        readonly InventoryView view = new InventoryView();
        readonly InventoryViewSources sources = new InventoryViewSources();
        readonly Dictionary<GameObject, (InventoryListKind kind, int index)> rowOf = new Dictionary<GameObject, (InventoryListKind, int)>();
        readonly Dictionary<InventoryCommand, Button> commandButtons = new Dictionary<InventoryCommand, Button>();
        readonly List<Button> tabButtons = new List<Button>();
        readonly List<Text> tabLabels = new List<Text>();
        readonly List<GameObject> navigationOrder = new List<GameObject>();
        Canvas canvas;
        InputModalGate gate;
        InventoryRequests requests;
        RowList bagRows, stashRows, containerRows;
        InventoryDragLayer drag;
        GameObject stashZone, containerZone;
        Button[] slotButtons;
        Text[] slotLabels;
        Text titleLabel, resultLabel, detailTitle, detailBody, messageLabel, hintLabel, bagHeader, sideHeader, reasonLabel;
        string inspectedId = string.Empty;
        InventorySelection selection;
        LootContainer container;
        GeneratedMission trackedMission;
        readonly Dictionary<LootContainer, Action<CommandableUnit>> handlers = new Dictionary<LootContainer, Action<CommandableUnit>>();
        MissionPhase lastPhase;
        MissionState lastState;
        string resultLine = string.Empty;
        string message = string.Empty;
        UnitItems watchedItems;
        int watchedUses, watchedUseFailures, watchedCollects;   // counts, not times: an order can finish in the click's frame
        CommandableUnit watchedSwapUnit;                          // set while a gear swap order is being followed
        int watchedEquips, watchedEquipFailures;
        float nextRefresh;
        bool dirty = true;
        bool shownSearched;
        bool built;
        bool started;
        InputActionReference navigateRef, submitRef, cancelRef;
        InputSystemUIInputModule navigatedModule;
        InputAction cancelAction, previousTabAction, nextTabAction;   // resolved on enable, polled every open frame
        string hint;                                                  // rebuilt on open and when the input family changes
        InputFamily hintFamily;
        bool hintLoadout;   // the module given the UI map while open (stripped again on close)

        public bool IsOpen { get; private set; }
        public string InspectedOperativeId => inspectedId;
        internal Canvas Canvas => canvas;
        internal string Message => message;
        internal InventorySelection Selection => selection;
        internal bool IsDragging => drag != null && drag.IsDragging;
        internal string ResultLine => resultLine;
        internal int ContainerRowCount => containerRows != null ? containerRows.Count : 0;
        internal IEnumerable<string> ContainerRowTexts => containerRows != null ? containerRows.Texts() : new string[0];
        internal Button ButtonFor(InventoryCommand command) => commandButtons.TryGetValue(command, out var b) ? b : null;

        internal void Initialize(SquadInventory squadInventory, SquadRoster squad, MissionDirector missionDirector,
            IntelligenceService intel, InputActionAsset actions, InputActionReference toggleAction, ActiveInputDevice device,
            bool openOnStart)
        {
            inventory = squadInventory;
            roster = squad;
            director = missionDirector;
            intelligence = intel;
            controls = actions;
            toggle = toggleAction;
            inputDevice = device;
            this.openOnStart = openOnStart;
        }

        // ---- lifecycle ----

        void OnEnable()
        {
            Build();
            cancelAction = controls != null ? controls.FindAction("UI/Cancel") : null;
            previousTabAction = controls != null ? controls.FindAction("UI/PreviousTab") : null;
            nextTabAction = controls != null ? controls.FindAction("UI/NextTab") : null;
            InputActionUtility.SetEnabled(true, toggle);
            if (inventory != null)
                inventory.Core.Changed += MarkDirty;
            requests = new InventoryRequests(inventory, director);
            sources.inventory = inventory;
            sources.roster = roster;
            sources.director = director;
            sources.intelligence = intelligence;
        }

        void OnDisable()
        {
            if (inventory != null && inventory.Core != null)
                inventory.Core.Changed -= MarkDirty;
            Unsubscribe();
            Close();
            InputActionUtility.SetEnabled(false, toggle);
        }

        void MarkDirty() => dirty = true;

        void Update()
        {
            if (!built || inventory == null)
                return;
            if (!started)
            {
                started = true;
                lastPhase = director != null ? director.Phase : MissionPhase.Inactive;
                lastState = director != null ? director.State : default;
                if (openOnStart && inventory.StartInLoadout && director != null && director.State == MissionState.Idle)
                    Open();
            }
            TrackMission();
            if (toggle != null && toggle.action != null && toggle.action.WasPressedThisFrame())
                Toggle();
            if (!IsOpen)
                return;
            gate.Enforce();
            ReadUiInput();
            if (!IsOpen)
                return;
            FollowFocus();
            WatchOutcome();
            // A search that completes while the container is shown reveals its contents on the next frame, not the next tick.
            if (container != null && container.IsSearched != shownSearched)
                dirty = true;
            if (dirty || Time.unscaledTime >= nextRefresh)
                Refresh();
        }

        // ---- opening and closing ----

        public void Toggle()
        {
            if (IsOpen)
                RequestClose();
            else
                Open();
        }

        // Between missions the panel is the only way forward, so the owner's rule is that leaving it (the Close button, the
        // toggle key, Cancel) deploys a new seed. A mission being generated keeps it open with the director's refusal shown.
        // In a mission, and for code that must hide the panel (disable, teardown), Close just closes.
        void RequestClose()
        {
            if (inventory != null && inventory.Core.Mode == InventoryMode.Loadout && director != null)
                Invoke(InventoryCommand.DeployNew);
            else
                Close();
        }

        public void Open()
        {
            if (IsOpen || !built || inventory == null)
                return;
            gate.Open();
            IsOpen = true;   // set at once: if anything below throws, Close (and OnDisable) still give the input back
            EnableNavigation(true);
            canvas.gameObject.SetActive(true);
            hint = null;   // bindings may have changed while the panel was closed
            dirty = true;
            Refresh();
            FocusFirst();
        }

        /// <summary>Opens the panel on a searched container, for (and inspecting) the operative who searched it.</summary>
        public void Open(LootContainer opened, CommandableUnit by)
        {
            container = opened;
            if (by != null && by.TryGetComponent<UnitIdentity>(out var identity) && !string.IsNullOrEmpty(identity.OperativeId))
                inspectedId = identity.OperativeId;
            selection = default;
            Open();
            dirty = true;
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            IsOpen = false;
            try
            {
                EnableNavigation(false);
            }
            finally
            {
                // The gameplay input comes back whatever happened to the navigation hand-back.
                if (drag != null)
                    drag.Cancel();
                gate.Close();
                if (canvas != null)
                    canvas.gameObject.SetActive(false);
                if (EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(null);
            }
        }

        internal void InspectOperative(string operativeId)
        {
            inspectedId = operativeId;
            selection = default;
            message = string.Empty;
            dirty = true;
        }

        // ---- mission tracking ----

        void TrackMission()
        {
            if (director == null)
                return;
            var mission = director.Current;
            if (mission != trackedMission)
            {
                Unsubscribe();
                trackedMission = mission;
                // A container opened in the frame the mission became current is kept; one from an older mission is not.
                if (!BelongsTo(mission, container))
                    container = null;
                if (mission != null)
                {
                    foreach (var loot in mission.LootContainers)
                    {
                        var opened = loot;
                        Action<CommandableUnit> handler = unit => OnContainerOpened(opened, unit);
                        handlers[opened] = handler;
                        opened.OpenRequested += handler;
                    }
                }
                dirty = true;
            }
            var state = director.State;
            if (state != lastState)
            {
                lastState = state;
                // A generation that failed after Deploy closed the panel leaves an empty scene: bring the panel back with the reason.
                if (state == MissionState.Failed && inventory != null && inventory.Core.Mode == InventoryMode.Loadout)
                {
                    resultLine = "MISSION COULD NOT BE GENERATED - deploy again";
                    Open();
                    dirty = true;
                }
            }
            var phase = director.Phase;
            if (phase != lastPhase)
            {
                lastPhase = phase;
                if (phase == MissionPhase.Success || phase == MissionPhase.Failure)
                {
                    resultLine = phase == MissionPhase.Success ? "MISSION SUCCESS - loot secured" : "MISSION FAILURE - pickups lost";
                    container = null;
                    selection = default;
                    Open();
                    dirty = true;
                }
                else if (phase == MissionPhase.Active)
                {
                    resultLine = string.Empty;
                }
            }
        }

        static bool BelongsTo(GeneratedMission mission, LootContainer loot)
        {
            if (mission == null || loot == null)
                return false;
            foreach (var candidate in mission.LootContainers)
            {
                if (candidate == loot)
                    return true;
            }
            return false;
        }

        void Unsubscribe()
        {
            foreach (var pair in handlers)
            {
                if (pair.Key != null)
                    pair.Key.OpenRequested -= pair.Value;
            }
            handlers.Clear();
            trackedMission = null;
        }

        void OnContainerOpened(LootContainer opened, CommandableUnit by)
        {
            if (opened != null && by != null)
                Open(opened, by);
        }

        // ---- UI input (polled actions; navigation itself is the event system's) ----

        void ReadUiInput()
        {
            if (cancelAction != null && cancelAction.WasPressedThisFrame())
            {
                RequestClose();
                return;
            }
            if (previousTabAction != null && previousTabAction.WasPressedThisFrame())
                StepTab(-1);
            else if (nextTabAction != null && nextTabAction.WasPressedThisFrame())
                StepTab(1);
        }

        void StepTab(int direction)
        {
            if (view.Tabs.Count == 0)
                return;
            var current = view.Tabs.FindIndex(t => t.IsInspected);
            var next = (current + direction + view.Tabs.Count) % view.Tabs.Count;
            InspectOperative(view.Tabs[next].OperativeId);
        }

        // While open, the event system's UI module navigates with the UI map; on close the module it was given to is
        // stripped again (decision 042: no focus outside this panel) and the runtime references are always destroyed.
        void EnableNavigation(bool on)
        {
            if (on)
            {
                var system = EventSystem.current;
                if (system == null || !system.TryGetComponent<InputSystemUIInputModule>(out var module) || controls == null)
                    return;
                navigateRef = InputActionReference.Create(controls.FindAction("UI/Navigate"));
                submitRef = InputActionReference.Create(controls.FindAction("UI/Submit"));
                cancelRef = InputActionReference.Create(controls.FindAction("UI/Cancel"));
                navigatedModule = module;
                module.move = navigateRef;
                module.submit = submitRef;
                module.cancel = cancelRef;
                return;
            }
            try
            {
                if (navigatedModule != null)
                    PointerOnlyInputModule.StripNavigation(navigatedModule);
            }
            finally
            {
                navigatedModule = null;
                foreach (var reference in new[] { navigateRef, submitRef, cancelRef })
                {
                    if (reference != null)
                        Destroy(reference);
                }
                navigateRef = submitRef = cancelRef = null;
            }
        }

        // The selection follows the focused row, so a pad moves through a list and then across to the action buttons.
        void FollowFocus()
        {
            var system = EventSystem.current;
            if (system == null)
                return;
            var focused = system.currentSelectedGameObject;
            if (focused == null || !focused.activeInHierarchy)
            {
                FocusFirst();
                return;
            }
            if (rowOf.TryGetValue(focused, out var row))
            {
                var id = IdAt(row.kind, row.index);
                if (!string.IsNullOrEmpty(id) && (selection.Kind != row.kind || selection.InstanceId != id))
                    Select(row.kind, id);
            }
        }

        void FocusFirst()
        {
            var system = EventSystem.current;
            if (system == null)
                return;
            foreach (var go in navigationOrder)
            {
                if (go != null && go.activeInHierarchy && go.TryGetComponent<Button>(out var button) && button.interactable)
                {
                    system.SetSelectedGameObject(go);
                    return;
                }
            }
        }

        // ---- selection and commands ----

        internal void Select(InventoryListKind kind, string instanceId)
        {
            selection = new InventorySelection(kind, instanceId);
            message = string.Empty;
            dirty = true;
        }

        string IdAt(InventoryListKind kind, int index)
        {
            switch (kind)
            {
                case InventoryListKind.Bag: return index < view.Bag.Count ? view.Bag[index].InstanceId : null;
                case InventoryListKind.Stash: return index < view.Stash.Count ? view.Stash[index].InstanceId : null;
                case InventoryListKind.Container: return index < view.Container.Count ? view.Container[index].InstanceId : null;
                case InventoryListKind.Slot: return index < view.Slots.Length && !string.IsNullOrEmpty(view.Slots[index].InstanceId) ? view.Slots[index].InstanceId : null;
                default: return null;
            }
        }

        internal void Invoke(InventoryCommand command)
        {
            if (inventory == null)
                return;
            Refresh();   // act on what is on screen now, never on a stale view
            var id = view.InspectedId;
            var picked = view.Selected.InstanceId;
            switch (command)
            {
                case InventoryCommand.Close:
                    RequestClose();
                    return;
                case InventoryCommand.DeployNew:
                case InventoryCommand.DeploySame:
                {
                    var result = command == InventoryCommand.DeployNew ? requests.DeployNew() : requests.DeploySame();
                    message = result.Message;
                    if (result.Ok)
                        Close();
                    break;
                }
                case InventoryCommand.Equip:
                {
                    var result = view.Selected.Kind == InventoryListKind.Stash ? requests.EquipFromStash(id, picked) : requests.Equip(id, picked);
                    message = result.Message;
                    if (result.Ok && view.Mode == InventoryMode.InMission)
                    {
                        // The swap takes a moment: follow it, so the line reports how it ended (done, failed or cancelled).
                        Watch(id);
                        watchedSwapUnit = InventoryViewBuilder.FindUnit(director, id);
                    }
                    break;
                }
                case InventoryCommand.Unequip:
                    message = view.Selected.Kind == InventoryListKind.None ? "Select an item." : RequestUnequip(id, picked);
                    break;
                case InventoryCommand.ToStash:
                    message = requests.ToStash(id, picked).Describe(view.DetailTitle);
                    break;
                case InventoryCommand.ToBag:
                    message = requests.FromStash(id, picked).Describe(view.DetailTitle);
                    break;
                case InventoryCommand.Use:
                case InventoryCommand.QueueUse:
                {
                    var result = requests.Use(id, picked, queue: command == InventoryCommand.QueueUse);
                    message = result.Message;
                    if (result.Ok)
                        Watch(id);
                    break;
                }
                case InventoryCommand.Take:
                case InventoryCommand.QueueTake:
                {
                    var result = requests.Take(id, container, picked, queue: command == InventoryCommand.QueueTake);
                    message = result.Message;
                    if (result.Ok)
                        Watch(id);
                    break;
                }
                case InventoryCommand.TakeAll:
                case InventoryCommand.QueueTakeAll:
                {
                    var result = requests.TakeAll(id, container, queue: command == InventoryCommand.QueueTakeAll);
                    message = result.Message;
                    if (result.Ok)
                        Watch(id);
                    break;
                }
            }
            dirty = true;
        }

        string RequestUnequip(string operativeId, string instanceId)
        {
            foreach (var slot in view.Slots)
            {
                if (slot.InstanceId == instanceId && !string.IsNullOrEmpty(instanceId))
                    return requests.Unequip(operativeId, slot.Slot).Message;
            }
            return "Select an equipped item.";
        }

        // After an accepted order, the result of the running order is read back here (it runs on simulation time).
        void Watch(string operativeId)
        {
            var unit = InventoryViewBuilder.FindUnit(director, operativeId);
            watchedItems = unit != null ? unit.GetComponent<UnitItems>() : null;
            watchedUses = watchedItems != null ? watchedItems.UsedCount : 0;
            watchedUseFailures = watchedItems != null ? watchedItems.UseFailureCount : 0;
            watchedCollects = watchedItems != null ? watchedItems.CollectCount : 0;
            watchedEquips = watchedItems != null ? watchedItems.EquipCount : 0;
            watchedEquipFailures = watchedItems != null ? watchedItems.EquipFailureCount : 0;
            watchedSwapUnit = null;
        }

        void WatchOutcome()
        {
            if (watchedItems == null)
                return;
            if (watchedSwapUnit != null)
            {
                if (watchedItems.EquipCount > watchedEquips)
                    message = "Gear swapped.";
                else if (watchedItems.EquipFailureCount > watchedEquipFailures)
                    message = InventoryViewBuilder.UseReason(watchedItems.LastEquipFailure);
                else if (!(watchedSwapUnit.CurrentCommand is EquipItemCommand))
                    message = "Swap cancelled.";
                else
                    return;   // still swapping
                watchedItems = null;
                watchedSwapUnit = null;
                dirty = true;
                return;
            }
            if (watchedItems.CollectCount > watchedCollects)
            {
                message = watchedItems.LastCollect.Describe();
                watchedItems = null;
                dirty = true;
            }
            else if (watchedItems.UsedCount > watchedUses)
            {
                message = "Item used.";
                watchedItems = null;
                dirty = true;
            }
            else if (watchedItems.UseFailureCount > watchedUseFailures)
            {
                message = InventoryViewBuilder.UseReason(watchedItems.LastUseFailure);
                watchedItems = null;
                dirty = true;
            }
        }

        // ---- drawing ----

        void Refresh()
        {
            dirty = false;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            sources.container = container;
            shownSearched = container != null && container.IsSearched;
            InventoryViewBuilder.Build(sources, inspectedId, selection, view);
            if (!view.HasInventory)
                return;
            inspectedId = view.InspectedId;
            if (view.Selected.IsNone)
                selection = default;

            HudFactory.SetText(titleLabel, view.Header + (view.InspectedName.Length > 0 ? "   -   " + view.InspectedName.ToUpperInvariant() : string.Empty));
            HudFactory.SetText(resultLabel, resultLine);
            ApplyTabs();
            for (var i = 0; i < slotButtons.Length; i++)
            {
                HudFactory.SetText(slotLabels[i], view.Slots[i].Title + ": " + view.Slots[i].ItemLabel);
                Tint(slotButtons[i], selection.Kind == InventoryListKind.Slot && !string.IsNullOrEmpty(view.Slots[i].InstanceId) && selection.InstanceId == view.Slots[i].InstanceId);
                slotButtons[i].interactable = !string.IsNullOrEmpty(view.Slots[i].InstanceId);
            }
            HudFactory.SetText(bagHeader, $"BAG  {view.Bag.Count}/{view.BagCapacity}");
            bagRows.Apply(view.Bag, selection, InventoryListKind.Bag);
            var showStash = view.Mode == InventoryMode.Loadout;
            var showContainer = view.HasContainer;
            HudFactory.SetText(sideHeader, showContainer ? "CONTAINER" : showStash ? "STASH" : string.Empty);
            HudFactory.SetActive(stashZone, showStash && !showContainer);
            HudFactory.SetActive(containerZone, showContainer);
            stashRows.Apply(showStash && !showContainer ? view.Stash : EmptyRows, selection, InventoryListKind.Stash);
            containerRows.Apply(showContainer ? view.Container : EmptyRows, selection, InventoryListKind.Container);
            HudFactory.SetText(detailTitle, view.DetailTitle);
            HudFactory.SetText(detailBody, view.DetailBody);

            ApplyCommand(InventoryCommand.Equip, view.Equip, view.Mode == InventoryMode.InMission ? $"Swap in ({UnitItems.SwapSeconds:0.#} s)" : "Equip");
            ApplyCommand(InventoryCommand.Unequip, view.Unequip, "Unequip");
            ApplyCommand(InventoryCommand.ToStash, view.ToStash, "Move to stash");
            ApplyCommand(InventoryCommand.ToBag, view.ToBag, "Move to bag");
            ApplyCommand(InventoryCommand.Use, view.Use, "Use now");
            ApplyCommand(InventoryCommand.QueueUse, view.QueueUse, "Queue use");
            ApplyCommand(InventoryCommand.Take, view.Take, "Take now");
            ApplyCommand(InventoryCommand.QueueTake, view.QueueTake, "Queue take");
            ApplyCommand(InventoryCommand.TakeAll, view.TakeAll, "Take what fits");
            ApplyCommand(InventoryCommand.QueueTakeAll, view.QueueTakeAll, "Queue take what fits");
            var loadoutMode = view.Mode == InventoryMode.Loadout;
            ShowCommand(InventoryCommand.DeployNew, loadoutMode);
            ShowCommand(InventoryCommand.DeploySame, loadoutMode);
            HudFactory.SetText(reasonLabel, FirstReason());
            HudFactory.SetText(messageLabel, message);
            HudFactory.SetText(commandButtons[InventoryCommand.Close].GetComponentInChildren<Text>(), loadoutMode ? "Close (deploy new seed)" : "Close");
            var family = inputDevice != null ? inputDevice.Family : InputFamily.KeyboardMouse;
            if (hint == null || family != hintFamily || loadoutMode != hintLoadout)
            {
                hintFamily = family;
                hintLoadout = loadoutMode;
                hint = Hint(family);
            }
            HudFactory.SetText(hintLabel, hint);
        }

        static readonly List<InventoryRow> EmptyRows = new List<InventoryRow>();

        void ApplyTabs()
        {
            for (var i = 0; i < tabButtons.Count; i++)
            {
                var on = i < view.Tabs.Count;
                HudFactory.SetActive(tabButtons[i].gameObject, on);
                if (!on)
                    continue;
                var tab = view.Tabs[i];
                HudFactory.SetText(tabLabels[i], tab.Name + (tab.IsDown ? " (down)" : string.Empty));
                Tint(tabButtons[i], tab.IsInspected);
            }
        }

        void ApplyCommand(InventoryCommand command, InventoryAction action, string label)
        {
            var button = commandButtons[command];
            HudFactory.SetActive(button.gameObject, action.Visible);
            if (!action.Visible)
                return;
            button.interactable = action.Enabled;
            HudFactory.SetText(button.GetComponentInChildren<Text>(), label);
        }

        void ShowCommand(InventoryCommand command, bool visible)
        {
            var button = commandButtons[command];
            HudFactory.SetActive(button.gameObject, visible);
            if (visible)
                button.interactable = director == null || director.State != MissionState.Generating;
        }

        string FirstReason()
        {
            foreach (var action in new[] { view.Equip, view.Unequip, view.ToStash, view.ToBag, view.Use, view.Take, view.TakeAll })
            {
                if (action.Visible && !action.Enabled && action.Reason.Length > 0)
                    return action.Reason;
            }
            return string.Empty;
        }

        string Hint(InputFamily family)
        {
            if (controls == null)
                return string.Empty;
            return $"{(hintLoadout ? "Close and deploy a new seed" : "Close")}: {PromptResolver.GetPrompt(toggle != null ? toggle.action : null, family)} / {PromptResolver.GetPrompt(cancelAction, family)}"
                + $"    Operative: {PromptResolver.GetPrompt(previousTabAction, family)} / {PromptResolver.GetPrompt(nextTabAction, family)}"
                + $"    Choose: {PromptResolver.GetPrompt(controls.FindAction("UI/Submit"), family)}";
        }

        static void Tint(Button button, bool selected)
        {
            var image = button.targetGraphic as Image;
            if (image != null)
                HudFactory.SetColor(image, selected ? new Color(HudTheme.Accent.r, HudTheme.Accent.g, HudTheme.Accent.b, 0.55f) : HudTheme.Panel);
        }

        // ---- construction ----

        void Build()
        {
            if (built)
                return;
            gate = new InputModalGate(controls, "UI", "Inventory");
            var canvasObject = new GameObject("InventoryCanvas", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, false);
            canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();
            var root = (RectTransform)canvasObject.transform;

            HudFactory.Box("Dim", root, new Color(0f, 0f, 0f, 0.6f), true);
            var panel = HudFactory.Box("Panel", root, new Color(HudTheme.Panel.r, HudTheme.Panel.g, HudTheme.Panel.b, 0.96f), true);
            HudFactory.Place(panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1500f, 900f));

            // Mouse drag and drop: the zones sit under the lists so a drop on a row or on the space around it lands in that list.
            drag = new InventoryDragLayer(root, IdAt, (kind, id, command) => { Select(kind, id); Invoke(command); }, Select,
                text => { message = text; dirty = true; });
            var rowsHeight = MaxRows * RowHeight + 6f;
            DropZone(panel, "SlotZone", InventoryListKind.Slot, 24f, 156f, 420f, 140f);
            DropZone(panel, "BagZone", InventoryListKind.Bag, 24f, 326f, 420f, rowsHeight);
            stashZone = DropZone(panel, "StashZone", InventoryListKind.Stash, 470f, 186f, 420f, rowsHeight).gameObject;
            containerZone = DropZone(panel, "ContainerZone", InventoryListKind.Container, 470f, 186f, 420f, rowsHeight).gameObject;

            titleLabel = Label(panel, "Title", HudTheme.FontBanner, HudTheme.Text, 24f, 16f, 1000f, 44f);
            resultLabel = Label(panel, "Result", HudTheme.FontLarge, HudTheme.Warn, 24f, 62f, 1000f, 30f);
            for (var i = 0; i < 6; i++)
            {
                var button = MakeButton(panel, "OperativeTab" + i, 24f + i * 230f, 100f, 220f, 40f, out var text);
                tabButtons.Add(button);
                tabLabels.Add(text);
                var index = i;
                button.onClick.AddListener(() =>
                {
                    if (index < view.Tabs.Count)
                        InspectOperative(view.Tabs[index].OperativeId);
                });
                navigationOrder.Add(button.gameObject);
            }

            slotButtons = new Button[3];
            slotLabels = new Text[3];
            for (var i = 0; i < 3; i++)
            {
                slotButtons[i] = MakeButton(panel, "Slot" + i, 24f, 160f + i * 44f, 420f, 38f, out slotLabels[i]);
                var index = i;
                slotButtons[i].onClick.AddListener(() => Select(InventoryListKind.Slot, IdAt(InventoryListKind.Slot, index) ?? string.Empty));
                rowOf[slotButtons[i].gameObject] = (InventoryListKind.Slot, i);
                drag.AttachSource(slotButtons[i].gameObject, InventoryListKind.Slot, i);
                drag.AttachZone(slotButtons[i].gameObject, InventoryListKind.Slot);
                navigationOrder.Add(slotButtons[i].gameObject);
            }
            bagHeader = Label(panel, "BagHeader", HudTheme.FontBody, HudTheme.TextDim, 24f, 300f, 420f, 24f);
            bagRows = new RowList(this, panel, "Bag", InventoryListKind.Bag, 24f, 330f, 420f);
            sideHeader = Label(panel, "SideHeader", HudTheme.FontBody, HudTheme.TextDim, 470f, 160f, 420f, 24f);
            stashRows = new RowList(this, panel, "Stash", InventoryListKind.Stash, 470f, 190f, 420f);
            containerRows = new RowList(this, panel, "Container", InventoryListKind.Container, 470f, 190f, 420f);

            detailTitle = Label(panel, "DetailTitle", HudTheme.FontLarge, HudTheme.Text, 920f, 160f, 560f, 32f);
            detailBody = Label(panel, "DetailBody", HudTheme.FontBody, HudTheme.Text, 920f, 198f, 560f, 210f);
            detailBody.verticalOverflow = VerticalWrapMode.Truncate;
            reasonLabel = Label(panel, "Reason", HudTheme.FontBody, HudTheme.Warn, 920f, 414f, 560f, 40f);

            var order = new[]
            {
                InventoryCommand.Equip, InventoryCommand.Unequip, InventoryCommand.ToStash, InventoryCommand.ToBag,
                InventoryCommand.Use, InventoryCommand.QueueUse, InventoryCommand.Take, InventoryCommand.QueueTake,
                InventoryCommand.TakeAll, InventoryCommand.QueueTakeAll,
            };
            for (var i = 0; i < order.Length; i++)
                AddCommand(panel, order[i], 920f + (i % 2) * 285f, 462f + (i / 2) * 46f, 275f, 40f);
            AddCommand(panel, InventoryCommand.DeployNew, 24f, 800f, 300f, 48f);
            AddCommand(panel, InventoryCommand.DeploySame, 334f, 800f, 300f, 48f);
            AddCommand(panel, InventoryCommand.Close, 1176f, 800f, 300f, 48f);
            HudFactory.SetText(commandButtons[InventoryCommand.DeployNew].GetComponentInChildren<Text>(), "Deploy (new seed)");
            HudFactory.SetText(commandButtons[InventoryCommand.DeploySame].GetComponentInChildren<Text>(), "Deploy (same seed)");
            HudFactory.SetText(commandButtons[InventoryCommand.Close].GetComponentInChildren<Text>(), "Close");

            messageLabel = Label(panel, "Message", HudTheme.FontLarge, HudTheme.Good, 24f, 750f, 1450f, 34f);
            hintLabel = Label(panel, "Hint", HudTheme.FontSmall, HudTheme.TextDim, 24f, 860f, 1450f, 24f);
            canvas.gameObject.SetActive(false);
            built = true;
        }

        RectTransform DropZone(RectTransform parent, string name, InventoryListKind kind, float x, float y, float width, float height)
        {
            var zone = HudFactory.Box(name, parent, new Color(0f, 0f, 0f, 0f), true);
            HudFactory.Place(zone, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));
            drag.AttachZone(zone.gameObject, kind);
            return zone;
        }

        void AddCommand(RectTransform parent, InventoryCommand command, float x, float y, float width, float height)
        {
            var button = MakeButton(parent, command.ToString(), x, y, width, height, out _);
            commandButtons[command] = button;
            button.onClick.AddListener(() => Invoke(command));
            navigationOrder.Add(button.gameObject);
        }

        Button MakeButton(RectTransform parent, string name, float x, float y, float width, float height, out Text label)
        {
            var rect = HudFactory.Box(name, parent, HudTheme.Panel, true);
            HudFactory.Place(rect, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            label = HudFactory.Label("Label", rect, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft);
            HudFactory.Place(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(width - 20f, height));
            return button;
        }

        static Text Label(RectTransform parent, string name, int size, Color color, float x, float y, float width, float height)
        {
            var label = HudFactory.Label(name, parent, size, color, TextAnchor.UpperLeft);
            HudFactory.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));
            return label;
        }

        /// <summary>A pooled column of row buttons; a row shows "[E] name xN  tag", and the selected row is tinted.</summary>
        sealed class RowList
        {
            readonly InventoryModal owner;
            readonly Button[] buttons = new Button[MaxRows];
            readonly Text[] labels = new Text[MaxRows];

            public RowList(InventoryModal owner, RectTransform parent, string name, InventoryListKind kind, float x, float y, float width)
            {
                this.owner = owner;
                for (var i = 0; i < MaxRows; i++)
                {
                    buttons[i] = owner.MakeButton(parent, name + "Row" + i, x, y + i * RowHeight, width, RowHeight - 2f, out labels[i]);
                    var index = i;
                    var listKind = kind;
                    buttons[i].onClick.AddListener(() =>
                    {
                        var id = owner.IdAt(listKind, index);
                        if (!string.IsNullOrEmpty(id))
                            owner.Select(listKind, id);
                    });
                    owner.rowOf[buttons[i].gameObject] = (kind, i);
                    owner.drag.AttachSource(buttons[i].gameObject, kind, i);
                    owner.drag.AttachZone(buttons[i].gameObject, kind);
                    owner.navigationOrder.Add(buttons[i].gameObject);
                    buttons[i].gameObject.SetActive(false);
                }
            }

            public int Count { get; private set; }

            public IEnumerable<string> Texts()
            {
                for (var i = 0; i < Count; i++)
                    yield return labels[i].text;
            }

            public void Apply(List<InventoryRow> rows, InventorySelection selection, InventoryListKind kind)
            {
                Count = Mathf.Min(rows.Count, MaxRows);
                for (var i = 0; i < MaxRows; i++)
                {
                    var on = i < Count;
                    HudFactory.SetActive(buttons[i].gameObject, on);
                    if (!on)
                        continue;
                    var row = rows[i];
                    var text = (row.IsEquipped ? "[E] " : string.Empty) + row.Label + (row.Quantity > 1 ? " x" + row.Quantity : string.Empty)
                        + (!string.IsNullOrEmpty(row.Tag) ? "   " + row.Tag : string.Empty);
                    HudFactory.SetText(labels[i], text);
                    Tint(buttons[i], selection.Kind == kind && selection.InstanceId == row.InstanceId);
                }
            }
        }
    }
}
