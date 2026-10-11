using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>Moves the unit over the NavMesh, along paths or by direct steering. The only component that talks to the NavMeshAgent.</summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class UnitMover : MonoBehaviour
    {
        const float SnapRadius = 2f;
        const float ArrivalTolerance = 0.1f;
        const int ApproachDirections = 12;
        const int ApproachCornerLimit = 256;   // a path of more corners than this is measured by its first 256 (only used to compare candidates)
        const float ApproachSnapRadius = 0.4f;
        const float ApproachMargin = 0.3f;   // a stand point must lie this far inside the reach: the arrival tolerance (stopping distance + tolerance) must still be in range
        static readonly float[] ApproachRings = { 0.55f, 0.8f };   // fractions of the reach, nearest the object first

        [SerializeField, Min(0f)] float speed = 5f;
        [SerializeField, Min(0f)] float angularSpeed = 720f;
        [SerializeField, Min(0f)] float acceleration = 20f;
        [SerializeField, Min(0f)] float stoppingDistance = 0.1f;

        NavMeshAgent agent;
        int moveRequestFrame = -1;
        // Reused by CanReach so reachability checks allocate nothing. A plain C# object, so ??= is fine.
        NavMeshPath reachPath;
        Vector3[] cornerBuffer;

        NavMeshAgent Agent => agent != null ? agent : agent = GetComponent<NavMeshAgent>();

        /// <summary>Height of the unit's pivot above the NavMesh (the agent's base offset, 1 m for the prototype capsules).</summary>
        public float PivotHeight => Agent.baseOffset;

        /// <summary>Walking speed in metres per second (what the agent uses).</summary>
        public float Speed => speed;

        /// <summary>Changes the walking speed, also on the agent. Safe before Awake (the unit's hierarchy may still be inactive).</summary>
        internal void SetSpeed(float value)
        {
            speed = Mathf.Max(0f, value);
            Agent.speed = speed;
        }

        /// <summary>
        /// True when the unit is at the end of its path, or has no path left to walk. The path ends at the closest
        /// reachable point, so a destination the NavMesh only partly reaches still counts as arrived there.
        /// Never true on the frame a move was requested, while the agent's path data is still the old one.
        /// </summary>
        public bool HasArrived
        {
            get
            {
                if (!Agent.isOnNavMesh || Agent.pathPending || Time.frameCount == moveRequestFrame)
                    return false;
                if (!Agent.hasPath)
                    return true;
                var offset = Agent.pathEndPosition - transform.position;
                offset.y = 0f;
                return offset.magnitude <= Agent.stoppingDistance + ArrivalTolerance;
            }
        }

        /// <summary>The path length still to walk, false while the path is being computed or the agent has none. No side effects.</summary>
        public bool TryGetRemainingDistance(out float distance)
        {
            distance = 0f;
            if (!Agent.isOnNavMesh || Agent.pathPending || !Agent.hasPath)
                return false;
            distance = Agent.remainingDistance;
            return !float.IsInfinity(distance);
        }

        /// <summary>True if MoveTo(point) would be accepted: on a NavMesh, with a walkable point within 2 m. No side effects.</summary>
        public bool CanMoveTo(Vector3 point) =>
            Agent.isOnNavMesh && NavMesh.SamplePosition(point, out _, SnapRadius, NavMesh.AllAreas);

        /// <summary>
        /// Nearest walkable point within SnapRadius (2 m) of `point`, if any: the tolerance MoveTo and CanMoveTo use,
        /// so a pivot-height point (1 m up) or one in the erosion band beside a wall still snaps. No side effects.
        /// </summary>
        public bool TrySnap(Vector3 point, out Vector3 onNavMesh)
        {
            if (NavMesh.SamplePosition(point, out var hit, SnapRadius, NavMesh.AllAreas))
            {
                onNavMesh = hit.position;
                return true;
            }
            onNavMesh = point;
            return false;
        }

        /// <summary>
        /// True when a complete path exists from the agent to a walkable point within 2 m of `point`. A partial path
        /// (the point is on another NavMesh island, or ringed by walls) is not reachable. No side effects.
        /// </summary>
        public bool CanReach(Vector3 point)
        {
            if (!Agent.isOnNavMesh || !TrySnap(point, out var destination))
                return false;
            reachPath ??= new NavMeshPath();
            // The agent's own CalculatePath starts from its NavMesh location, so no snapping of the source is needed.
            return Agent.CalculatePath(destination, reachPath) && reachPath.status == NavMeshPathStatus.PathComplete;
        }

        /// <summary>
        /// The walkable point within `reach` of `target` that this unit can get to soonest by path. A solid target (a crate, a
        /// terminal) has no walkable point of its own, and snapping its centre picks whatever walkable point is nearest the
        /// centre, which can be on the far side of a wall. Tries two rings of points around it and keeps the one with the
        /// shortest complete path. False when none has a path: the caller then walks to the target itself. No side effects.
        /// </summary>
        public bool TryApproachPoint(Vector3 target, float reach, out Vector3 point)
        {
            point = target;
            if (!Agent.isOnNavMesh || reach <= 0.2f)
                return false;
            reachPath ??= new NavMeshPath();
            cornerBuffer ??= new Vector3[ApproachCornerLimit];
            var best = float.PositiveInfinity;
            foreach (var factor in ApproachRings)
            {
                for (var step = 0; step < ApproachDirections; step++)
                {
                    var angle = step * Mathf.PI * 2f / ApproachDirections;
                    var ring = reach * factor;
                    var candidate = target + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ring;
                    if (!NavMesh.SamplePosition(candidate, out var hit, ApproachSnapRadius, NavMesh.AllAreas))
                        continue;
                    var flat = hit.position - target;
                    flat.y = 0f;
                    if (flat.magnitude > reach - ApproachMargin)
                        continue;
                    if (!Agent.CalculatePath(hit.position, reachPath) || reachPath.status != NavMeshPathStatus.PathComplete)
                        continue;
                    var length = 0f;
                    var count = reachPath.GetCornersNonAlloc(cornerBuffer);
                    for (var i = 1; i < count; i++)
                        length += Vector3.Distance(cornerBuffer[i - 1], cornerBuffer[i]);
                    if (length < best)
                    {
                        best = length;
                        point = hit.position;
                    }
                }
            }
            return best < float.PositiveInfinity;
        }

        void Awake()
        {
            Agent.speed = speed;
            Agent.angularSpeed = angularSpeed;
            Agent.acceleration = acceleration;
            Agent.stoppingDistance = stoppingDistance;
        }

        /// <summary>Starts moving to the nearest walkable point. Returns false (and logs a warning) if there is none.</summary>
        public bool MoveTo(Vector3 point)
        {
            if (!Agent.isOnNavMesh)
            {
                Debug.LogWarning($"{name} cannot move: it is not on a NavMesh.", this);
                return false;
            }
            if (!NavMesh.SamplePosition(point, out var hit, SnapRadius, NavMesh.AllAreas))
            {
                Debug.LogWarning($"{name} cannot move: no walkable NavMesh point within {SnapRadius} m of {point}.", this);
                return false;
            }
            Agent.isStopped = false;
            moveRequestFrame = Time.frameCount;
            return Agent.SetDestination(hit.position);
        }

        /// <summary>
        /// Moves the unit this frame in a horizontal direction at its normal speed, turning toward it. Call once per
        /// simulation frame while steering. Drops any path and leftover velocity first, so it never fights a path or
        /// coasts. Uses scaled time: does nothing while paused. A direction longer than 1 is clamped; zero does nothing.
        /// </summary>
        public void Steer(Vector3 direction)
        {
            if (!SimulationTime.IsRunning || !Agent.isOnNavMesh)
                return;
            var deltaTime = Time.deltaTime;
            direction.y = 0f;
            direction = Vector3.ClampMagnitude(direction, 1f);
            if (direction == Vector3.zero)
                return;

            if (Agent.hasPath || Agent.pathPending || Agent.velocity != Vector3.zero)
                Stop();
            // Move keeps the unit on the NavMesh and slides it along walls.
            Agent.Move(direction * (Agent.speed * deltaTime));
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction),
                Agent.angularSpeed * deltaTime);
        }

        /// <summary>Stops immediately (no coasting) and clears the path.</summary>
        public void Stop()
        {
            if (!Agent.isOnNavMesh)
                return;
            Agent.ResetPath();
            Agent.velocity = Vector3.zero;
        }
    }
}
