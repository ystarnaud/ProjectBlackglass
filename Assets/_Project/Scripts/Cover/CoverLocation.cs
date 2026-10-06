using UnityEngine;

namespace Blackglass
{
    /// <summary>Whether the obstacle can be shot over (Low, firing cover) or blocks sight (Tall, hiding cover).</summary>
    public enum CoverHeight
    {
        Low,
        Tall,
    }

    /// <summary>Where on the obstacle a location sits: along a face (Low obstacles only), or at an outward-opening end of a tall wall (a corner, always with peek data).</summary>
    public enum CoverPlacement
    {
        Face,
        Corner,
    }

    /// <summary>
    /// A place a unit can stand to be protected by one obstacle: the single runtime cover representation. Plain data
    /// plus the claim, no scene object, so geometry (the prototype arena today, a mission generator later) produces
    /// it and every consumer treats it the same. Protection is geometric: a ray from the attacker's eye to the
    /// defender's feet must cross the obstacle. UnitCover is the only writer of claims.
    /// </summary>
    public sealed class CoverLocation
    {
        readonly Collider obstacle;
        // A destroyed collider reads as null, same as one never wired; remember which it was.
        readonly bool hadObstacle;
        bool warnedAboutObstacle;
        bool retired;

        internal CoverLocation(string name, Vector3 position, Vector3 facing, Collider obstacle,
            float hitChance = 0.5f, CoverHeight height = CoverHeight.Low, CoverPlacement placement = CoverPlacement.Face,
            Vector3 peekDirection = default, Vector3 peekPoint = default)
        {
            Name = name ?? string.Empty;
            Position = position;
            facing.y = 0f;
            Facing = facing.sqrMagnitude > 0.0001f ? facing.normalized : Vector3.forward;
            this.obstacle = obstacle;
            hadObstacle = obstacle != null;
            HitChance = Mathf.Clamp01(hitChance);
            Height = height;
            Placement = placement;
            peekDirection.y = 0f;
            if (peekDirection.sqrMagnitude > 0.0001f)
            {
                PeekDirection = peekDirection.normalized;
                PeekPoint = peekPoint;
            }
        }

        /// <summary>A debug label, e.g. Cover_LowWall_L_S1. Not a key.</summary>
        public string Name { get; }

        /// <summary>The stand point, at ground level.</summary>
        public Vector3 Position { get; }

        /// <summary>The flat unit direction from the stand point into the obstacle: the protected side is its far side.</summary>
        public Vector3 Facing { get; }

        /// <summary>The one collider that protects this location (the source geometry).</summary>
        public Collider Obstacle => obstacle;

        /// <summary>A protected shot's chance to hit.</summary>
        public float HitChance { get; }

        public CoverHeight Height { get; }

        public CoverPlacement Placement { get; }

        /// <summary>Corner only: the flat unit direction along the wall, out past its end. Zero without a peek.</summary>
        public Vector3 PeekDirection { get; }

        /// <summary>Corner only: where a peeking unit would stand to see round the end. Zero without a peek.</summary>
        public Vector3 PeekPoint { get; }

        public bool HasPeek => PeekDirection != Vector3.zero;

        /// <summary>The unit that reserved or occupies this location, or null. Written only by UnitCover.</summary>
        public UnitCover Claimant { get; private set; }

        public bool IsClaimed => Claimant != null;

        public bool IsClaimedBy(UnitCover unit) => unit != null && Claimant == unit;

        /// <summary>Claimed, and the claimant stands on it.</summary>
        public bool IsOccupied => Claimant != null && Claimant.Status == CoverStatus.Occupied;

        /// <summary>
        /// False once retired, or when the obstacle it was built from is gone, disabled or inactive. A location that
        /// never had an obstacle (test locations) stays valid until retired.
        /// </summary>
        public bool IsValid => !retired
            && (!hadObstacle || (obstacle != null && obstacle.enabled && obstacle.gameObject.activeInHierarchy));

        /// <summary>
        /// True when the location is valid and unclaimed or already this unit's; a retired or invalid location cannot be
        /// claimed. A destroyed claimant counts as none.
        /// </summary>
        internal bool TryClaim(UnitCover claimant)
        {
            if (claimant == null)
                throw new System.ArgumentNullException(nameof(claimant));
            if (!IsValid)
                return false;
            if (Claimant != null && Claimant != claimant)
                return false;
            Claimant = claimant;
            return true;
        }

        /// <summary>Frees the location if `claimant` holds it; otherwise nothing happens.</summary>
        internal void Release(UnitCover claimant)
        {
            if (Claimant == claimant)
                Claimant = null;
        }

        /// <summary>Takes the location out of play for good: drops the claim and makes IsValid false. Used by the registry on a rebuild.</summary>
        internal void Retire()
        {
            retired = true;
            Claimant = null;
        }

        /// <summary>
        /// True when the ray from the attacker's eye (pivot + LineOfSight.EyeHeight) to the defender's feet (the
        /// defender's x/z at this location's height) hits the obstacle. Only the wired collider is tested, so units and
        /// other geometry never confuse it. With no obstacle: false, with one warning.
        /// </summary>
        public bool ProtectsFrom(Vector3 attackerPivot, Vector3 defenderPivot)
        {
            if (obstacle == null)
            {
                if (!warnedAboutObstacle)
                {
                    Debug.LogWarning($"{Name} has no obstacle collider, so it protects nobody.");
                    warnedAboutObstacle = true;
                }
                return false;
            }
            var eye = attackerPivot + Vector3.up * LineOfSight.EyeHeight;
            var feet = new Vector3(defenderPivot.x, Position.y, defenderPivot.z);
            var toFeet = feet - eye;
            var distance = toFeet.magnitude;
            if (distance <= 0.001f)
                return false;
            return obstacle.Raycast(new Ray(eye, toFeet / distance), out _, distance);
        }
    }
}
