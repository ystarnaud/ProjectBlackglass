using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Debug view of a unit's orders: a line from the unit through each order in sequence, and a small disc at every
    /// move, cover or ground-ability destination. Reads the unit's orders every frame and never changes them. Works while paused.
    /// Has no colliders, so it never blocks click raycasts.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(LineRenderer))]
    public sealed class CommandQueueView : MonoBehaviour
    {
        [SerializeField] Material markerMaterial;
        [SerializeField, Min(0.05f)] float markerDiameter = 0.6f;
        // The prototype ground is flat at y = 0; the line and markers float just above it.
        [SerializeField] float groundHeight = 0.05f;
        // Optional: with fog on, an order aimed at a target the player cannot see draws no point.
        [SerializeField] IntelligenceService intelligence;

        readonly List<Vector3> points = new List<Vector3>();
        readonly List<GameObject> markers = new List<GameObject>();
        CommandableUnit unit;
        LineRenderer line;
        int markersInUse;

        internal int LinePointCount => line.positionCount;
        internal IReadOnlyList<GameObject> Markers => markers;

        internal int ActiveMarkerCount
        {
            get
            {
                var count = 0;
                foreach (var marker in markers)
                {
                    if (marker.activeSelf)
                        count++;
                }
                return count;
            }
        }

        internal void Initialize(Material marker) => markerMaterial = marker;

        internal void SetIntelligence(IntelligenceService service) => intelligence = service;

        void Awake()
        {
            unit = GetComponent<CommandableUnit>();
            line = GetComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 0;
        }

        void LateUpdate()
        {
            points.Clear();
            markersInUse = 0;
            points.Add(OnGround(transform.position));
            AddOrder(unit.CurrentCommand);
            foreach (var pending in unit.PendingCommands)
                AddOrder(pending);

            if (points.Count < 2)
            {
                line.positionCount = 0;
            }
            else
            {
                line.positionCount = points.Count;
                for (var i = 0; i < points.Count; i++)
                    line.SetPosition(i, points[i]);
            }

            for (var i = markersInUse; i < markers.Count; i++)
                markers[i].SetActive(false);
        }

        void AddOrder(UnitCommand command)
        {
            switch (command)
            {
                case MoveCommand move:
                    var destination = OnGround(move.Destination);
                    points.Add(destination);
                    ShowMarker(destination);
                    break;
                case MoveToCoverCommand toCover when toCover.Point != null && toCover.Point.IsValid:
                    var spot = OnGround(toCover.Point.Position);
                    points.Add(spot);
                    ShowMarker(spot);
                    break;
                case AttackCommand attack when attack.Target != null && attack.Target.gameObject.activeInHierarchy
                    && Knowledge.IsShown(intelligence, attack.Target):
                    points.Add(OnGround(attack.Target.transform.position));
                    break;
                case InteractCommand interact when interact.Target != null && interact.Target.gameObject.activeInHierarchy:
                    points.Add(OnGround(interact.Target.Position));
                    break;
                case AbilityCommand ability when ability.Definition != null
                    && (ability.Definition.TargetMode == AbilityTargetMode.Ground
                        || (ability.Target != null && ability.Target.gameObject.activeInHierarchy && Knowledge.IsShown(intelligence, ability.Target))):
                    var aim = OnGround(ability.AimPoint);
                    points.Add(aim);
                    if (ability.Definition.TargetMode == AbilityTargetMode.Ground)
                        ShowMarker(aim);
                    break;
            }
        }

        Vector3 OnGround(Vector3 point) => new Vector3(point.x, groundHeight, point.z);

        void ShowMarker(Vector3 position)
        {
            if (markersInUse == markers.Count)
                markers.Add(CreateMarker());
            var marker = markers[markersInUse++];
            marker.transform.SetPositionAndRotation(position, Quaternion.identity);
            marker.SetActive(true);
        }

        GameObject CreateMarker()
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = "MoveMarker";
            DestroyImmediate(marker.GetComponent<Collider>());
            // Parented to the unit for cleanup only; its world position is set every frame.
            marker.transform.SetParent(transform, false);
            marker.transform.localScale = new Vector3(markerDiameter, 0.01f, markerDiameter);
            var markerRenderer = marker.GetComponent<Renderer>();
            if (markerMaterial != null)
                markerRenderer.sharedMaterial = markerMaterial;
            markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            markerRenderer.receiveShadows = false;
            return marker;
        }
    }
}
