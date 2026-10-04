using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>Moves the unit over the NavMesh. The only component that talks to the NavMeshAgent.</summary>
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

        NavMeshAgent Agent => agent != null ? agent : agent = GetComponent<NavMeshAgent>();

        /// <summary>True when the unit is at its destination, or its path has ended (nothing left to walk).</summary>
        public bool HasArrived
        {
            get
            {
                if (!Agent.isOnNavMesh || Agent.pathPending)
                    return false;
                var offset = Agent.destination - transform.position;
                offset.y = 0f;
                return offset.magnitude <= Agent.stoppingDistance + ArrivalTolerance || !Agent.hasPath;
            }
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
            return Agent.SetDestination(hit.position);
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
