using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Where a unit stands with respect to its cover point.</summary>
    public enum CoverStatus
    {
        None,
        /// <summary>A MoveToCover order is walking there; nobody else may take the point.</summary>
        Reserved,
        /// <summary>The unit stands on the point and is protected by its obstacle.</summary>
        Occupied,
    }

    /// <summary>
    /// The unit's cover: the one point it has reserved or occupies. The only writer of CoverPoint claims. A reservation
    /// is driven by CommandableUnit (a MoveToCover order); an occupancy by standing on the point, by order (TryOccupy)
    /// or by chance (standing still within occupyRadius of a free point, which needs the registry). Health is optional,
    /// as everywhere: looked up lazily, Died subscribed to only when present. Runs on simulation time.
    /// </summary>
    public sealed class UnitCover : MonoBehaviour
    {
        [SerializeField] CoverRegistry registry;
        [SerializeField, Min(0f)] float occupyRadius = 0.6f;
        [SerializeField, Min(0f)] float leaveRadius = 1f;
        // Below this flat speed a unit counts as standing; above it a unit walking across a point claims nothing.
        [SerializeField, Min(0f)] float stillSpeed = 0.5f;

        // The method group converted once; passing IsUnclaimed directly would allocate a delegate every frame.
        static readonly Func<CoverPoint, bool> isUnclaimed = IsUnclaimed;

        Health ownHealth;
        Vector3 lastFlatPosition;
        bool hasLastPosition;

        /// <summary>The reserved or occupied point, else null.</summary>
        public CoverPoint Point { get; private set; }

        public CoverStatus Status { get; private set; }

        /// <summary>True while an occupancy came from a MoveToCover arrival; false for one reached by standing there.</summary>
        public bool OccupiedByOrder { get; private set; }

        /// <summary>True when a registry is wired, so standing on a free point occupies it.</summary>
        public bool IsWired => registry != null;

        public float OccupyRadius => occupyRadius;

        /// <summary>The held point's hit chance, or 1 (every shot lands) without one.</summary>
        public float HitChance => Point != null ? Point.HitChance : 1f;

        // A Health present at OnEnable is subscribed to; one added later is found by this lookup only (no Died event).
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();

        internal void Initialize(CoverRegistry coverRegistry, float occupy = 0.6f, float leave = 1f, float still = 0.5f)
        {
            registry = coverRegistry;
            occupyRadius = occupy;
            leaveRadius = leave;
            stillSpeed = still;
        }

        /// <summary>
        /// Claims the point as reserved. If another unit holds it: false, nothing changes (including any point this
        /// unit already holds). If it is this unit's own point: the status is kept (an occupied point stays occupied).
        /// Otherwise any other point this unit held, reserved or occupied, is released first, so a unit never holds
        /// two points.
        /// </summary>
        public bool TryReserve(CoverPoint point)
        {
            if (point == null)
                throw new ArgumentNullException(nameof(point));
            if (OwnHealth != null && !OwnHealth.IsAlive)
                return false;   // a corpse reserves nothing
            if (point == Point)
                return true;
            if (!point.TryClaim(this))
                return false;
            Release();
            Point = point;
            Status = CoverStatus.Reserved;
            OccupiedByOrder = false;
            return true;
        }

        /// <summary>
        /// Held (reserved or occupied) and within occupyRadius → Occupied, by order. Returns whether the unit now
        /// occupies its point. Called by CommandableUnit when a cover walk arrives.
        /// </summary>
        public bool TryOccupy()
        {
            if (Status == CoverStatus.None || !IsWithin(occupyRadius))
                return false;
            Status = CoverStatus.Occupied;
            OccupiedByOrder = true;
            return true;
        }

        /// <summary>Releases only a reservation; an occupancy is left alone.</summary>
        public void ReleaseReservation()
        {
            if (Status == CoverStatus.Reserved)
                Release();
        }

        /// <summary>Releases whatever is held.</summary>
        public void Release()
        {
            if (Point != null)
                Point.Release(this);
            Point = null;
            Status = CoverStatus.None;
            OccupiedByOrder = false;
        }

        /// <summary>Occupied, and the point protects this unit from an attacker at that pivot.</summary>
        public bool IsProtectedFrom(Vector3 attackerPivot) =>
            Status == CoverStatus.Occupied && Point != null && Point.ProtectsFrom(attackerPivot, transform.position);

        void OnEnable()
        {
            hasLastPosition = false;
            if (OwnHealth != null)
                OwnHealth.Died += OnDied;
        }

        // Death is covered twice: Health deactivates the unit (this runs), and Died handles a Health that does not.
        void OnDisable()
        {
            if (OwnHealth != null)
                OwnHealth.Died -= OnDied;
            Release();
        }

        void OnDied() => Release();

        void Update()
        {
            if (!SimulationTime.IsRunning)
                return;
            // Health may deactivate nothing on death, so a dead unit must not claim again after OnDied released its point.
            if (OwnHealth != null && !OwnHealth.IsAlive)
                return;
            var flat = transform.position;
            flat.y = 0f;
            // Tracked here rather than read from the mover, so paths, direct steering and avoidance pushes all count.
            // Without a previous position or a positive delta the speed is unknown, which counts as not standing still.
            var speed = hasLastPosition && Time.deltaTime > 0f
                ? Vector3.Distance(flat, lastFlatPosition) / Time.deltaTime
                : float.PositiveInfinity;
            lastFlatPosition = flat;
            hasLastPosition = true;

            if (Status != CoverStatus.None && (Point == null || !Point.gameObject.activeInHierarchy))
            {
                Release();   // the cover became invalid
                return;
            }
            if (Status == CoverStatus.Occupied)
            {
                if (!IsWithin(leaveRadius))
                    Release();
                return;
            }
            if (Status == CoverStatus.Reserved || registry == null || speed >= stillSpeed)
                return;
            if (CoverRules.TryChooseNearest(registry.Points, transform.position, occupyRadius, isUnclaimed, out var nearest)
                && nearest.TryClaim(this))
            {
                Point = nearest;
                Status = CoverStatus.Occupied;
                OccupiedByOrder = false;
            }
        }

        static bool IsUnclaimed(CoverPoint point) => !point.IsClaimed;

        bool IsWithin(float radius) => Point != null && CoverRules.FlatDistance(transform.position, Point.Position) <= radius;
    }
}
