using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>
    /// Translates the player's input into requests. Left button: a click on a friendly unit selects it, a click
    /// anywhere else gives an order (attack the clicked target, move into the cover point within coverClickRadius of the click, or move to the clicked point), and a drag box-selects.
    /// The order goes to the selected units whenever any are selected or the game is paused; with nothing selected in
    /// real time it goes to the active character. Shift adds to the selection or queues the order. X stops the selected units, Esc clears the
    /// selection, Space toggles tactical pause. Contains no movement or combat rules.
    /// </summary>
    public sealed class PlayerCommandInput : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;
        [SerializeField] TacticalPause tacticalPause;
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;
        [SerializeField] CoverRegistry coverRegistry;

        [Header("Input")]
        [SerializeField] InputActionReference commandAction;
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference togglePauseAction;
        [SerializeField] InputActionReference modifierAction;
        [SerializeField] InputActionReference stopAction;
        [SerializeField] InputActionReference clearSelectionAction;

        [Header("Tuning")]
        // A press that moves further than this is a box-selection drag, not a click.
        [SerializeField, Min(0f)] float dragThresholdPixels = ClickDragDetector.DefaultThresholdPixels;
        [SerializeField, Min(1f)] float maxClickDistance = 500f;
        [SerializeField] LayerMask clickableLayers = ~0;
        [SerializeField, Min(0.5f)] float groupSpacing = GroupOrders.DefaultSpacing;
        // A ground click this close to a cover point orders the unit into that point instead of onto the ground.
        [SerializeField, Min(0f)] float coverClickRadius = 0.5f;

        readonly List<CommandableUnit> orderedUnits = new List<CommandableUnit>();
        readonly List<SelectableUnit> boxedUnits = new List<SelectableUnit>();
        static readonly Func<CoverLocation, bool> acceptAny = _ => true;
        ClickDragDetector clickDetector;
        Vector2 pressPosition;

        /// <summary>True while the left button is held and has moved far enough to be a box selection.</summary>
        public bool IsDragging => clickDetector != null && clickDetector.IsDragging;

        /// <summary>The box being dragged, in screen pixels (origin bottom-left). Only meaningful while IsDragging.</summary>
        public Rect DragRect => ScreenBox.FromCorners(pressPosition, PointerPosition);

        internal bool IsCoverWired => coverRegistry != null;

        internal void Initialize(Camera camera, UnitSelection unitSelection, TacticalPause pause,
            InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause,
            InputActionReference modifier, InputActionReference stop, InputActionReference clearSelection,
            ActiveCharacter active = null, CoverRegistry registry = null)
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
        }

        void Awake() => clickDetector = new ClickDragDetector(dragThresholdPixels);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, commandAction, pointerPositionAction, togglePauseAction,
                modifierAction, stopAction, clearSelectionAction);
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
        }

        void OnDisable()
        {
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
            InputActionUtility.SetEnabled(false, commandAction, pointerPositionAction, togglePauseAction,
                modifierAction, stopAction, clearSelectionAction);
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
            clickDetector.Press(pressPosition);
        }

        void OnCommandReleased(InputAction.CallbackContext context)
        {
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

        void OnClearSelection(InputAction.CallbackContext context)
        {
            if (selection != null)
                selection.Clear();
        }

        void HandleClick(Vector2 screenPoint)
        {
            if (viewCamera == null)
                return;

            // While paused no physics steps run, so push moved transforms to physics before raycasting.
            Physics.SyncTransforms();
            var ray = viewCamera.ScreenPointToRay(screenPoint);
            if (!Physics.Raycast(ray, out var hit, maxClickDistance, clickableLayers, QueryTriggerInteraction.Ignore))
                return;

            var friendly = hit.collider.GetComponentInParent<SelectableUnit>();
            if (friendly != null)
            {
                if (selection == null)
                    return;
                if (ModifierHeld)
                    selection.Toggle(friendly);
                else
                    selection.Select(friendly);
                return;
            }

            CoverLocation cover = null;
            if (coverRegistry != null)
                CoverRules.TryChooseNearest(coverRegistry.Points, hit.point, coverClickRadius, acceptAny, out cover);
            var command = CommandResolver.Resolve(hit.collider.GetComponentInParent<Health>(), hit.point, cover);
            GroupOrders.Issue(OrderedUnits(), command, ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
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
