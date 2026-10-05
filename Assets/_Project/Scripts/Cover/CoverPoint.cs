using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A place a unit can stand to be protected by one obstacle. Position: the transform (ground level). Forward: the
    /// flat direction into the obstacle, used only to place the marker's direction nub. Protection is geometric: a ray
    /// from the attacker's eye to the defender's feet must cross the wired obstacle.
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
