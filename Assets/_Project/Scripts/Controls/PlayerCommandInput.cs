using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>
    /// Translates the player's input into requests. Left button: a click on a friendly unit selects it, a click
    /// anywhere else gives an order (attack the clicked target, work the clicked terminal, move into the cover point within coverClickRadius of the click, or move to the clicked point), and a drag box-selects.
    /// The order goes to the selected units whenever any are selected or the game is paused; with nothing selected in
    /// real time it goes to the active character. Shift adds to the selection or queues the order. X stops the selected units, Esc clears the
    /// selection, Space toggles tactical pause. A controller does the same through the tactical cursor: Confirm acts on what
    /// it is on (the same Act path) while the cursor is shown and, while it is hidden, interacts with the terminal in reach, else attacks like Attack, Attack orders an attack on the cursor's, the chosen or the best hostile, the queue
    /// modifier (Shift / LT) queues, Cancel (Esc / the family's cancel button) clears the selection. While an ability is armed (AbilityTargeting) a click or the cursor's Confirm picks its target instead of giving an order, and Cancel disarms before it clears the selection. A terminal under the pointer is an Interact order (CommandResolver); the Interact action (keyboard) and a controller Confirm with the cursor hidden interact with the terminal in reach, else Confirm attacks as before. Contains no movement or combat rules.
    /// </summary>
    public sealed class PlayerCommandInput : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;
        [SerializeField] TacticalPause tacticalPause;
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;
        [SerializeField] CoverRegistry coverRegistry;
        // The controller's pointer. Without it Confirm and Attack do nothing; while it is inactive both
        // attack the chosen or best hostile; mouse clicks never use it.
        [SerializeField] TacticalCursor cursor;
        // Optional. While an ability is armed, a click or the cursor's Confirm picks its target instead of giving an order.
        [SerializeField] AbilityTargeting abilityTargeting;
        // Optional. The mission's terminals: Interact and a context Confirm look here for one within reach.
        [SerializeField] InteractableRegistry interactables;
        // Optional: unknown terminals and hidden hostiles are not offered (decision 037).
        [SerializeField] IntelligenceService intelligence;
        // Optional. A press that begins where this says the pointer is over the HUD is not a world click, order or box.
        [SerializeField] PointerBlocker pointerBlocker;

        [Header("Input")]
        [SerializeField] InputActionReference commandAction;
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference togglePauseAction;
        [SerializeField] InputActionReference modifierAction;
        [SerializeField] InputActionReference stopAction;
        [SerializeField] InputActionReference clearSelectionAction;
        [SerializeField] InputActionReference confirmAction;
        [SerializeField] InputActionReference attackAction;
        [SerializeField] InputActionReference interactAction;

        [Header("Tuning")]
        // A press that moves further than this is a box-selection drag, not a click.
        [SerializeField, Min(0f)] float dragThresholdPixels = ClickDragDetector.DefaultThresholdPixels;
        [SerializeField, Min(1f)] float maxClickDistance = 500f;
        [SerializeField] LayerMask clickableLayers = ~0;
        [SerializeField, Min(0.5f)] float groupSpacing = GroupOrders.DefaultSpacing;
        // A ground click this close to a cover point orders the unit into that point instead of onto the ground.
        [SerializeField, Min(0f)] float coverClickRadius = 0.5f;
        // How close the active character must be for Interact or a context Confirm to find a terminal.
        [SerializeField, Min(0.5f)] float contextInteractRadius = 3f;

        readonly List<CommandableUnit> orderedUnits = new List<CommandableUnit>();
        readonly List<SelectableUnit> boxedUnits = new List<SelectableUnit>();
        ClickDragDetector clickDetector;
        Vector2 pressPosition;
        bool pressBlocked;

        /// <summary>True while the left button is held and has moved far enough to be a box selection.</summary>
        public bool IsDragging => clickDetector != null && clickDetector.IsDragging;

        /// <summary>The box being dragged, in screen pixels (origin bottom-left). Only meaningful while IsDragging.</summary>
        public Rect DragRect => ScreenBox.FromCorners(pressPosition, PointerPosition);

        internal bool IsCoverWired => coverRegistry != null;

        internal void Initialize(Camera camera, UnitSelection unitSelection, TacticalPause pause,
            InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause,
            InputActionReference modifier, InputActionReference stop, InputActionReference clearSelection,
            ActiveCharacter active = null, CoverRegistry registry = null, TacticalCursor tacticalCursor = null,
            InputActionReference confirm = null, InputActionReference attack = null, AbilityTargeting targeting = null)
        {
            viewCamera = camera;
            selection = unitSelection;
            tacticalPause = pause;
            commandAction = command;
            pointerPositionAction = pointerPosition;
            togglePauseAction = togglePause;
            modifierAction = modifier;
            stopAction = stop;
            clearSelectionAction = clearSelection;
            activeCharacter = active;
            coverRegistry = registry;
            cursor = tacticalCursor;
            confirmAction = confirm;
            attackAction = attack;
            abilityTargeting = targeting;
        }

        internal void SetIntelligence(IntelligenceService service) => intelligence = service;

        internal void SetPointerBlocker(PointerBlocker blocker) => pointerBlocker = blocker;

        /// <summary>The pointer position in screen pixels (the same value clicks use).</summary>
        public Vector2 PointerScreenPosition => PointerPosition;

        /// <summary>True while the queue modifier (Shift / LT) is held.</summary>
        public bool ModifierIsHeld => ModifierHeld;

        /// <summary>What a click at this screen point would act on, resolved exactly as a click resolves it. No side effects.</summary>
        public PointerTarget ResolveAt(Vector2 screen) => viewCamera == null ? PointerTarget.None : ResolveTarget(screen);

        /// <summary>Wires terminal interaction: the registry Interact looks in, and the Interact action. Call before enabling.</summary>
        internal void WireInteraction(InteractableRegistry registry, InputActionReference interact)
        {
            interactables = registry;
            interactAction = interact;
        }

        /// <summary>
        /// The terminal the Interact key (and a context Confirm with the cursor hidden) would act on right now, or null: the
        /// nearest available terminal within reach of any ordered unit that no other living unit is already working.
        /// </summary>
        public MissionInteractable NearbyInteractable =>
            TryFindReachableTerminal(OrderedUnits(), out var terminal) ? terminal : null;

        /// <summary>
        /// The terminal the interact prompt should name, or null. Keyboard: what Interact would act on. Controller with the
        /// cursor shown: Confirm acts on what the cursor is on, so only a terminal under it counts; with the cursor hidden:
        /// what the context Confirm would act on.
        /// </summary>
        public MissionInteractable PromptInteractable(bool controller)
        {
            if (controller && cursor != null && cursor.IsActive)
                return cursor.Target.Kind == PointerTargetKind.Interactable ? cursor.Target.Interactable : null;
            return NearbyInteractable;
        }

        void Awake() => clickDetector = new ClickDragDetector(dragThresholdPixels);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, commandAction, pointerPositionAction, togglePauseAction,
                modifierAction, stopAction, clearSelectionAction, confirmAction, attackAction, interactAction);
            if (commandAction != null)
            {
                commandAction.action.started += OnCommandPressed;
                commandAction.action.canceled += OnCommandReleased;
            }
            if (togglePauseAction != null)
                togglePauseAction.action.performed += OnTogglePause;
            if (stopAction != null)
                stopAction.action.performed += OnStop;
            if (clearSelectionAction != null)
                clearSelectionAction.action.performed += OnClearSelection;
            if (confirmAction != null)
                confirmAction.action.performed += OnConfirm;
            if (attackAction != null)
                attackAction.action.performed += OnAttack;
            if (interactAction != null)
                interactAction.action.performed += OnInteract;
        }

        void OnDisable()
        {
            pressBlocked = false;
            if (commandAction != null)
            {
                commandAction.action.started -= OnCommandPressed;
                commandAction.action.canceled -= OnCommandReleased;
            }
            if (togglePauseAction != null)
                togglePauseAction.action.performed -= OnTogglePause;
            if (stopAction != null)
                stopAction.action.performed -= OnStop;
            if (clearSelectionAction != null)
                clearSelectionAction.action.performed -= OnClearSelection;
            if (confirmAction != null)
                confirmAction.action.performed -= OnConfirm;
            if (attackAction != null)
                attackAction.action.performed -= OnAttack;
            if (interactAction != null)
                interactAction.action.performed -= OnInteract;
            InputActionUtility.SetEnabled(false, commandAction, pointerPositionAction, togglePauseAction,
                modifierAction, stopAction, clearSelectionAction, confirmAction, attackAction, interactAction);
        }

        void Update()
        {
            if (clickDetector.IsPressed)
                clickDetector.Track(PointerPosition);
        }

        Vector2 PointerPosition => InputActionUtility.Read<Vector2>(pointerPositionAction);
        bool ModifierHeld => InputActionUtility.IsPressed(modifierAction);

        void OnCommandPressed(InputAction.CallbackContext context)
        {
            pressPosition = PointerPosition;
            // Assigned on every press, so a flag left over from an abandoned press can never swallow this one.
            pressBlocked = pointerBlocker != null && pointerBlocker.IsBlocking(pressPosition);
            if (pressBlocked)
                return;
            clickDetector.Press(pressPosition);
        }

        void OnCommandReleased(InputAction.CallbackContext context)
        {
            // A cancel while the button is still physically down is not a release: changing the input family re-resolves
            // the bindings and cancels the action under a held button. Abandon the press instead of issuing a click.
            if (context.control is ButtonControl button && button.isPressed)
            {
                clickDetector.Cancel();
                pressBlocked = false;
                return;
            }
            if (pressBlocked)
            {
                pressBlocked = false;
                return;
            }
            var wasPressed = clickDetector.IsPressed;
            var pointer = PointerPosition;
            if (clickDetector.Release(pointer))
                HandleClick(pointer);
            else if (wasPressed)
                SelectInBox(ScreenBox.FromCorners(pressPosition, pointer));
        }

        void OnTogglePause(InputAction.CallbackContext context)
        {
            if (tacticalPause != null)
                tacticalPause.Toggle();
        }

        void OnStop(InputAction.CallbackContext context) =>
            GroupOrders.Issue(SelectedUnits(), new StopCommand(), IssueMode.Replace);

        // Controller counterpart of a left click: with the cursor shown, acts on what it is on through the same Act path.
        // With the cursor hidden (the camera owns the right stick) there is nothing to point at, so it first tries the terminal
        // in reach, else attacks like Attack.
        void OnConfirm(InputAction.CallbackContext context)
        {
            if (cursor == null)
                return;
            if (cursor.IsActive)
                Act(cursor.Refresh());
            else if (!TryInteractNearby())
                AttackBestHostile();
        }

        // Attack is not an ability order: pressing it while an ability is armed disarms it first, then attacks as usual.
        void OnAttack(InputAction.CallbackContext context)
        {
            if (abilityTargeting != null && abilityTargeting.IsArmed)
                abilityTargeting.Disarm();
            AttackBestHostile();
        }

        // The Interact action (keyboard): the same order a click on the terminal gives, for the terminal in reach. An armed
        // ability owns the next press, so Interact waits.
        void OnInteract(InputAction.CallbackContext context)
        {
            if (abilityTargeting != null && abilityTargeting.IsArmed)
                return;
            TryInteractNearby();
        }

        // Interact with the terminal in reach of whoever is ordered (see TryFindReachableTerminal); the order goes to all of
        // them and the queue modifier appends. True when a terminal was found, so a context Confirm that finds one never also
        // attacks, whether or not a unit could take the order.
        bool TryInteractNearby()
        {
            var units = OrderedUnits();
            if (!TryFindReachableTerminal(units, out var terminal))
                return false;
            GroupOrders.Issue(units, new InteractCommand(terminal), ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
            return true;
        }

        // The nearest available terminal within contextInteractRadius of any of the units, skipping one that another living
        // unit is already working (an order there would be refused, so Confirm should attack instead). The one rule behind
        // the Interact key, the context Confirm and the prompt, so they cannot disagree.
        bool TryFindReachableTerminal(List<CommandableUnit> units, out MissionInteractable terminal)
        {
            terminal = null;
            if (interactables == null)
                return false;
            var bestDistance = contextInteractRadius;
            foreach (var unit in units)
            {
                if (unit == null)
                    continue;
                foreach (var item in interactables.Items)
                {
                    if (item == null || !item.IsAvailable || item.IsInUseByOther(unit) || !Knowledge.CanInteract(intelligence, item))
                        continue;
                    var distance = CoverRules.FlatDistance(unit.transform.position, item.Position);
                    if (distance > bestDistance)
                        continue;
                    terminal = item;
                    bestDistance = distance;
                }
            }
            return terminal != null;
        }

        // Attack the cursor's hostile, else the chosen soft target, else the best hostile ahead of whoever is ordered.
        void AttackBestHostile()
        {
            if (cursor == null)
                return;
            var units = OrderedUnits();
            var anchor = AttackAnchor(units);
            if (anchor == null)
                return;
            var hostile = cursor.PickAttackTarget(anchor.transform.position, AttackFacing(anchor));
            if (hostile == null)
                return;
            GroupOrders.Issue(units, new AttackCommand(hostile), ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
        }

        // Whose position "nearest" is measured from: the character being played, else the first unit being ordered.
        CommandableUnit AttackAnchor(List<CommandableUnit> units)
        {
            if (activeCharacter != null && activeCharacter.HasUnit)
                return activeCharacter.Unit;
            return units.Count > 0 ? units[0] : null;
        }

        // "Ahead": the direction the unit is being steered, else where the camera looks.
        Vector3 AttackFacing(CommandableUnit anchor)
        {
            var facing = anchor.MoveIntent;
            if (facing == Vector3.zero && viewCamera != null)
                facing = viewCamera.transform.forward;
            facing.y = 0f;
            return facing;
        }

        void OnClearSelection(InputAction.CallbackContext context)
        {
            // The first Cancel backs out of an armed ability; only the next one clears the selection.
            if (abilityTargeting != null && abilityTargeting.IsArmed)
            {
                abilityTargeting.Disarm();
                return;
            }
            if (selection != null)
                selection.Clear();
        }

        void HandleClick(Vector2 screenPoint)
        {
            if (viewCamera == null)
                return;
            Act(ResolveTarget(screenPoint));
        }

        PointerTarget ResolveTarget(Vector2 screenPoint) =>
            PointerTargetResolver.Resolve(viewCamera, screenPoint, maxClickDistance, clickableLayers, coverRegistry,
                coverClickRadius, intelligence);

        // The one "do what the player pointed at" path: a mouse click and the controller cursor both end here. A friendly
        // unit is selected (the queue modifier adds or removes it); anything else is an order, queued with the modifier.
        internal void Act(PointerTarget target)
        {
            if (abilityTargeting != null && abilityTargeting.IsArmed)
            {
                abilityTargeting.Confirm(target, ModifierHeld);
                return;
            }
            switch (target.Kind)
            {
                case PointerTargetKind.None:
                    return;
                case PointerTargetKind.Friendly:
                    if (selection == null)
                        return;
                    if (ModifierHeld)
                        selection.Toggle(target.Friendly);
                    else
                        selection.Select(target.Friendly);
                    return;
                default:
                    var command = CommandResolver.Resolve(target.Hostile, target.Point, target.Cover, target.Interactable);
                    GroupOrders.Issue(OrderedUnits(), command, ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
                    return;
            }
        }

        void SelectInBox(Rect box)
        {
            if (selection == null || viewCamera == null)
                return;
            boxedUnits.Clear();
            foreach (var unit in selection.Roster)
            {
                if (unit != null && unit.isActiveAndEnabled &&
                    ScreenBox.Contains(box, viewCamera.WorldToScreenPoint(unit.transform.position)))
                    boxedUnits.Add(unit);
            }
            if (ModifierHeld)
                selection.Add(boxedUnits);
            else
                selection.SetSelection(boxedUnits);
        }

        // Who a ground or enemy click orders: the selection whenever paused or anything is selected; otherwise the
        // active character. Paused planning and real-time group orders therefore follow one rule, and a lone click
        // with nothing selected still drives the controlled character.
        List<CommandableUnit> OrderedUnits()
        {
            var paused = tacticalPause != null && tacticalPause.IsPaused;
            var hasSelection = selection != null && selection.Selected.Count > 0;
            if (paused || hasSelection || activeCharacter == null || !activeCharacter.HasUnit)
                return SelectedUnits();
            orderedUnits.Clear();
            orderedUnits.Add(activeCharacter.Unit);
            return orderedUnits;
        }

        List<CommandableUnit> SelectedUnits()
        {
            orderedUnits.Clear();
            if (selection != null)
            {
                foreach (var selectable in selection.Selected)
                    orderedUnits.Add(selectable.Unit);
            }
            return orderedUnits;
        }
    }
}
