using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// The net-test client's controls (Input System): WASD steers (sent through NetIntentSender), a click on the ground moves
    /// (Shift queues it), a click on a red unit attacks it, X stops, 1 and 2 pick the unit. Input only requests; every order
    /// is a NetRequest the server may refuse. A ground click first releases any held steering, because CommandableUnit
    /// drops an order that is accepted while it is steered.
    /// </summary>
    public sealed class NetTestControls : MonoBehaviour
    {
        [SerializeField] NetClient client;
        [SerializeField] Camera viewCamera;

        readonly NetIntentSender sender = new NetIntentSender();
        InputAction move;
        InputAction click;
        InputAction stop;
        InputAction pickFirst;
        InputAction pickSecond;
        int selected;

        internal void Initialize(NetClient netClient, Camera camera)
        {
            client = netClient;
            viewCamera = camera;
        }

        void OnEnable()
        {
            move = new InputAction("Move", InputActionType.Value);
            move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            click = new InputAction("Click", InputActionType.Button, "<Mouse>/leftButton");
            stop = new InputAction("Stop", InputActionType.Button, "<Keyboard>/x");
            pickFirst = new InputAction("PickFirst", InputActionType.Button, "<Keyboard>/1");
            pickSecond = new InputAction("PickSecond", InputActionType.Button, "<Keyboard>/2");
            move.Enable();
            click.Enable();
            stop.Enable();
            pickFirst.Enable();
            pickSecond.Enable();
        }

        void OnDisable()
        {
            foreach (var action in new[] { move, click, stop, pickFirst, pickSecond })
            {
                if (action == null)
                    continue;
                action.Disable();
                action.Dispose();
            }
        }

        void Update()
        {
            if (client == null || client.State != NetClientState.Ready || client.OwnedCount == 0)
                return;
            var now = Time.realtimeSinceStartupAsDouble;
            if (pickFirst.WasPressedThisFrame())
                selected = 0;
            if (pickSecond.WasPressedThisFrame() && client.OwnedCount > 1)
                selected = 1;
            var unitId = client.FirstOwnedId + Mathf.Min(selected, client.OwnedCount - 1);

            var axes = move.ReadValue<Vector2>();
            if (sender.Next(new Vector3(axes.x, 0f, axes.y), now, out var direction))
                client.Send(new NetMoveIntent(unitId, direction));
            if (stop.WasPressedThisFrame())
                client.Send(new NetStop(unitId));
            if (click.WasPressedThisFrame() && Mouse.current != null && viewCamera != null)
                OnClick(unitId);
        }

        void OnClick(int unitId)
        {
            var ray = viewCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out var hit, 200f))
            {
                var target = hit.collider.GetComponentInParent<NetUnitState>();
                if (target != null && target.Hostile)
                {
                    client.Send(new NetAttack(unitId, target.UnitId));
                    return;
                }
            }
            if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var distance))
            {
                if (sender.ReleaseBeforeOrder(out var release))
                    client.Send(new NetMoveIntent(unitId, release));
                var append = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
                client.Send(new NetMoveTo(unitId, ray.GetPoint(distance), append));
            }
        }
    }
}
