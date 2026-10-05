using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A place a unit can stand to be protected by one obstacle. Position: the transform (ground level). Forward: the
    /// flat direction into the obstacle, used only to place the marker's direction nub. Protection is geometric: a ray
    /// from the attacker's eye to the defender's feet must cross the wired obstacle. Holds its claimant; UnitCover is
    /// the only writer.
    /// </summary>
    public sealed class CoverPoint : MonoBehaviour
    {
        [SerializeField] Collider obstacle;
        [SerializeField, Range(0f, 1f)] float hitChance = 0.5f;

        bool warnedAboutObstacle;

        public Vector3 Position => transform.position;

        /// <summary>The transform's forward, flattened and normalised; world forward if it points straight up or down.</summary>
        public Vector3 Forward
        {
            get
            {
                var forward = transform.forward;
                forward.y = 0f;
                return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
            }
        }

        /// <summary>A protected shot's chance to hit (the one tunable per point).</summary>
        public float HitChance => hitChance;

        public Collider Obstacle => obstacle;

        /// <summary>The unit that reserved or occupies this point, or null. Written only by UnitCover.</summary>
        public UnitCover Claimant { get; private set; }

        public bool IsClaimed => Claimant != null;

        public bool IsClaimedBy(UnitCover unit) => unit != null && Claimant == unit;

        /// <summary>Claimed, and the claimant stands on it.</summary>
        public bool IsOccupied => Claimant != null && Claimant.Status == CoverStatus.Occupied;

        /// <summary>True when the point is unclaimed or already this unit's. A destroyed claimant counts as none.</summary>
        internal bool TryClaim(UnitCover claimant)
        {
            if (claimant == null)
                throw new System.ArgumentNullException(nameof(claimant));
            if (Claimant != null && Claimant != claimant)
                return false;
            Claimant = claimant;
            return true;
        }

        /// <summary>Frees the point if `claimant` holds it; otherwise nothing happens.</summary>
        internal void Release(UnitCover claimant)
        {
            if (Claimant == claimant)
                Claimant = null;
        }

        /// <summary>
        /// True when the ray from the attacker's eye (pivot + LineOfSight.EyeHeight) to the defender's feet (the
        /// defender's x/z at this point's height) hits the obstacle. Only the wired collider is tested, so units and
        /// other geometry never confuse it. With no obstacle wired: false, with one warning.
        /// </summary>
        public bool ProtectsFrom(Vector3 attackerPivot, Vector3 defenderPivot)
        {
            if (obstacle == null)
            {
                if (!warnedAboutObstacle)
                {
                    Debug.LogWarning($"{name} has no obstacle collider wired, so it protects nobody.", this);
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

        internal void Initialize(Collider obstacleCollider, float chanceToHit = 0.5f)
        {
            obstacle = obstacleCollider;
            hitChance = chanceToHit;
        }
    }
}
