using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// The play client's controls (Input System; decision 052, D7). A left click on:
    /// - an own operative selects it (Ctrl adds or removes);
    /// - a living hostile this client is sent attacks it with every selected operative;
    /// - open floor moves them there (Shift queues);
    /// - an obstacle does nothing and says so.
    /// X stops; Tab cycles one operative; Ctrl+A selects all. F follows the primary operative, and while following, WASD steers it
    /// (camera-relative NetMoveIntent); Esc ends follow. Input only requests: every order is one of the four existing
    /// NetRequests. Nothing leaves while the client is not Ready, after the mission ended, or for a dead or unowned unit.
    /// </summary>
    public sealed class NetPlayControls : MonoBehaviour
    {
        [SerializeField] NetClient client;
        [SerializeField] Camera viewCamera;
        [SerializeField] TacticalCameraController cameraRig;
        [SerializeField] NetMapView mapView;
        [SerializeField] InputActionReference commandAction;
        [SerializeField] InputActionReference pointerAction;
        [SerializeField] InputActionReference queueAction;
        [SerializeField] InputActionReference stopAction;
        [SerializeField] InputActionReference cancelAction;
        [SerializeField] InputActionReference panAction;

        readonly NetSelection selection = new NetSelection();
        readonly NetIntentSender sender = new NetIntentSender();
        readonly List<(int Id, Vector3 Position)> pickable = new List<(int, Vector3)>();
        InputAction cycle;
        InputAction selectAll;
        InputAction follow;
        bool framed;

        /// <summary>Where requests go (tests replace it). Never called unless the client is Ready.</summary>
        internal Func<NetRequest, uint> Send;
        /// <summary>True for a screen point a HUD panel covers; such a click commands nothing (the HUD sets it).</summary>
        internal Func<Vector2, bool> Blocks = _ => false;

        public NetSelection Selection => selection;
        public bool Following { get; private set; }
        /// <summary>The last click's explanation when it did nothing ("Blocked — pick open floor"), for the HUD.</summary>
        public string LastFeedback { get; private set; }

        internal void Initialize(NetClient netClient, Camera camera, TacticalCameraController rig, NetMapView map,
            InputActionReference command, InputActionReference pointer, InputActionReference queue,
            InputActionReference stop, InputActionReference cancel, InputActionReference pan)
        {
            client = netClient;
            viewCamera = camera;
            cameraRig = rig;
            mapView = map;
            commandAction = command;
            pointerAction = pointer;
            queueAction = queue;
            stopAction = stop;
            cancelAction = cancel;
            panAction = pan;
        }

        public bool IsSelected(int unitId) => selection.Contains(unitId);

        void OnEnable()
        {
            cycle = new InputAction("CycleOperative", InputActionType.Button, "<Keyboard>/tab");
            selectAll = new InputAction("SelectAll", InputActionType.Button);
            selectAll.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", "<Keyboard>/ctrl").With("Button", "<Keyboard>/a");
            follow = new InputAction("Follow", InputActionType.Button, "<Keyboard>/f");
            cycle.Enable();
            selectAll.Enable();
            follow.Enable();
            InputActionUtility.SetEnabled(true, commandAction, pointerAction, queueAction, stopAction, cancelAction, panAction);
            if (commandAction != null)
                commandAction.action.performed += OnCommand;
        }

        void OnDisable()
        {
            if (commandAction != null)
                commandAction.action.performed -= OnCommand;
            foreach (var action in new[] { cycle, selectAll, follow })
            {
                if (action == null)
                    continue;
                action.Disable();
                action.Dispose();
            }
        }

        bool CanOrder => client != null && client.State == NetClientState.Ready && client.Record.Result == NetMissionResult.None;

        uint Request(NetRequest request) => Send != null ? Send(request) : client.Send(request);

        void Update()
        {
            if (client == null)
                return;
            selection.Retain(IsLivingOwn);
            if (!CanOrder)
            {
                Following = false;
                return;
            }
            FrameOnce();
            if (cycle.WasPressedThisFrame())
                selection.Next(LivingOwnIds());
            if (selectAll.WasPressedThisFrame())
                SelectAllLiving();
            if (follow.WasPressedThisFrame())
                SetFollowing(!Following && selection.Primary != 0);
            if (cancelAction != null && cancelAction.action.WasPressedThisFrame())
                SetFollowing(false);
            if (stopAction != null && stopAction.action.WasPressedThisFrame())
                StopSelected();
            if (Following && selection.Primary == 0)
                SetFollowing(false);
            if (Following)
            {
                var axes = panAction != null ? panAction.action.ReadValue<Vector2>() : Vector2.zero;
                var yaw = cameraRig != null ? cameraRig.Yaw : 0f;
                var direction = Quaternion.Euler(0f, yaw, 0f) * new Vector3(axes.x, 0f, axes.y);
                if (sender.Next(direction, Time.realtimeSinceStartupAsDouble, out var send))
                    Request(new NetMoveIntent(selection.Primary, send));
            }
        }

        void LateUpdate()
        {
            if (!Following || cameraRig == null)
                return;
            var primary = StateOf(selection.Primary);
            if (primary != null)
                cameraRig.FocusOn(primary.transform.position);
        }

        void SetFollowing(bool value)
        {
            if (Following && !value && CanOrder && sender.ReleaseBeforeOrder(out var release))
                Request(new NetMoveIntent(selection.Primary, release));
            Following = value;
        }

        void OnCommand(InputAction.CallbackContext context)
        {
            if (pointerAction == null)
                return;
            var keyboard = Keyboard.current;
            CommandAtScreen(pointerAction.action.ReadValue<Vector2>(),
                queueAction != null && queueAction.action.IsPressed(),
                keyboard != null && keyboard.ctrlKey.isPressed);
        }

        /// <summary>One left click at a screen point (the input callback and tests call this).</summary>
        internal void CommandAtScreen(Vector2 screen, bool append, bool toggle)
        {
            LastFeedback = null;
            if (!CanOrder || viewCamera == null || Blocks(screen))
                return;
            var ray = viewCamera.ScreenPointToRay(screen);
            pickable.Clear();
            foreach (var state in NetClient.VisibleStates())
                if (state.Alive && (state.Hostile || IsOwn(state.UnitId)))
                    pickable.Add((state.UnitId, state.transform.position));
            var unitId = NetPick.Unit(ray, pickable);
            if (unitId != 0)
            {
                if (IsOwn(unitId))
                {
                    if (toggle)
                        selection.Toggle(unitId);
                    else
                        selection.Only(unitId);
                    return;
                }
                AttackWithSelected(unitId);
                return;
            }
            if (selection.Count == 0)
            {
                LastFeedback = "Select an operative first (click one, Tab or Ctrl+A)";
                return;
            }
            if (mapView == null || !mapView.Built)
            {
                // No map authorized: the ground plane, as the net-test client does.
                if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var distance))
                    MoveSelected(ray.GetPoint(distance), append);
                return;
            }
            switch (NetPick.Ground(ray, mapView.Layout, mapView.Obstacles, out var point))
            {
                case NetPick.GroundHit.Floor:
                    MoveSelected(point, append);
                    break;
                case NetPick.GroundHit.Blocked:
                    LastFeedback = "Blocked — pick open floor";
                    break;
                default:
                    LastFeedback = "Off the map";
                    break;
            }
        }

        internal void MoveSelected(Vector3 point, bool append)
        {
            if (!CanOrder)
                return;
            if (sender.ReleaseBeforeOrder(out var release) && selection.Primary != 0)
                Request(new NetMoveIntent(selection.Primary, release));
            foreach (var id in selection.Selected)
                Request(new NetMoveTo(id, point, append));
        }

        internal void AttackWithSelected(int hostileId)
        {
            if (!CanOrder)
                return;
            if (selection.Count == 0)
            {
                LastFeedback = "Select an operative first (click one, Tab or Ctrl+A)";
                return;
            }
            foreach (var id in selection.Selected)
                Request(new NetAttack(id, hostileId));
        }

        internal void StopSelected()
        {
            if (!CanOrder)
                return;
            foreach (var id in selection.Selected)
                Request(new NetStop(id));
        }

        internal void SelectAllLiving() => selection.All(LivingOwnIds());

        // Once the map and the squad are both here: bound the camera to the map and put it over the squad, primary selected.
        void FrameOnce()
        {
            if (framed || cameraRig == null || mapView == null || !mapView.Built)
                return;
            var own = LivingOwnIds();
            if (own.Count == 0)
                return;
            var bounds = mapView.Layout.WorldBounds;
            cameraRig.SetBounds(new Rect(bounds.min.x, bounds.min.z, bounds.size.x, bounds.size.z));
            var centre = Vector3.zero;
            foreach (var id in own)
                centre += StateOf(id).transform.position;
            cameraRig.FocusOn(centre / own.Count);
            if (selection.Count == 0)
                selection.Only(own[0]);
            framed = true;
        }

        bool IsOwn(int unitId) => client != null && unitId >= client.FirstOwnedId && unitId < client.FirstOwnedId + client.OwnedCount;

        bool IsLivingOwn(int unitId)
        {
            if (!IsOwn(unitId))
                return false;
            var state = StateOf(unitId);
            return state != null && state.Alive;
        }

        List<int> LivingOwnIds()
        {
            var ids = new List<int>();
            foreach (var state in NetClient.VisibleStates())
                if (!state.Hostile && state.Alive && IsOwn(state.UnitId))
                    ids.Add(state.UnitId);
            ids.Sort();
            return ids;
        }

        static NetUnitState StateOf(int unitId)
        {
            foreach (var state in NetClient.VisibleStates())
                if (state.UnitId == unitId)
                    return state;
            return null;
        }
    }
}
