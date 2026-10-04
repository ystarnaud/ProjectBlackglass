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

        [SerializeField, Min(0f)] float speed = 5f;
        [SerializeField, Min(0f)] float angularSpeed = 720f;
        [SerializeField, Min(0f)] float acceleration = 20f;
        [SerializeField, Min(0f)] float stoppingDistance = 0.1f;

        NavMeshAgent agent;
        int moveRequestFrame = -1;

        NavMeshAgent Agent => agent != null ? agent : agent = GetComponent<NavMeshAgent>();

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

        /// <summary>True if MoveTo(point) would be accepted: on a NavMesh, with a walkable point within 2 m. No side effects.</summary>
        public bool CanMoveTo(Vector3 point) =>
            Agent.isOnNavMesh && NavMesh.SamplePosition(point, out _, SnapRadius, NavMesh.AllAreas);

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
            // Time.deltaTime keeps last frame's value on the frame the game is paused, so check the time scale too.
            var deltaTime = Time.deltaTime;
            if (deltaTime <= 0f || Time.timeScale <= 0f || !Agent.isOnNavMesh)
                return;
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
