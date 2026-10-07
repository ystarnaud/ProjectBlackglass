using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Presentation adapter: reads what the unit is doing (how fast the root moves, cover, shots, damage, death) and
    /// writes the Animator parameters of a visual child. It never changes gameplay and nothing in gameplay reads it.
    /// Health, UnitAttacker and UnitCover are all optional. Speed comes from the root's own movement, so NavMesh
    /// orders and direct-control steering look the same; it runs on simulation time, so a tactical pause freezes it
    /// (the Animator itself stops with the time scale).
    /// Health deactivates the unit right after Died, which would cut a death animation short, so on death the visual
    /// is moved out under the unit's parent (the mission's Actors root, which the mission destroys on teardown) and
    /// destroyed after `corpseSeconds`.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitAnimationDriver : MonoBehaviour
    {
        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int CrouchedId = Animator.StringToHash("Crouched");
        static readonly int DeadId = Animator.StringToHash("Dead");
        static readonly int FireId = Animator.StringToHash("Fire");
        static readonly int ReloadId = Animator.StringToHash("Reload");
        static readonly int HitId = Animator.StringToHash("Hit");

        [SerializeField] Animator animator;
        [SerializeField, Min(0f)] float speedDamping = 0.1f;
        [SerializeField, Min(0f)] float corpseSeconds = 8f;
        // A move this fast (m/s) in one frame is a warp or spawn placement, not walking: it is ignored.
        [SerializeField, Min(1f)] float teleportSpeed = 40f;

        Health health;
        UnitAttacker attacker;
        UnitCover cover;
        Vector3 lastPosition;
        bool dead;

        /// <summary>The visual's Animator.</summary>
        public Animator Animator => animator;

        /// <summary>True once the unit has died and its visual was released.</summary>
        public bool HasDied => dead;

        internal void Initialize(Animator target, float damping = 0.1f, float corpse = 8f)
        {
            animator = target;
            speedDamping = damping;
            corpseSeconds = corpse;
        }

        void Awake()
        {
            TryGetComponent(out health);
            TryGetComponent(out attacker);
            TryGetComponent(out cover);
        }

        void OnEnable()
        {
            lastPosition = transform.position;
            if (health != null)
            {
                health.Damaged += OnDamaged;
                health.Died += OnDied;
            }
            if (attacker != null)
            {
                attacker.Attacked += OnShot;
                attacker.Missed += OnShot;
            }
        }

        void OnDisable()
        {
            if (health != null)
            {
                health.Damaged -= OnDamaged;
                health.Died -= OnDied;
            }
            if (attacker != null)
            {
                attacker.Attacked -= OnShot;
                attacker.Missed -= OnShot;
            }
        }

        void Update()
        {
            var position = transform.position;
            if (!SimulationTime.IsRunning)
            {
                lastPosition = position;
                return;
            }
            if (dead || !CanDrive)
                return;

            var flat = position - lastPosition;
            flat.y = 0f;
            lastPosition = position;
            var speed = flat.magnitude / Time.deltaTime;
            if (speed > teleportSpeed)
                speed = 0f;

            animator.SetFloat(SpeedId, speed, speedDamping, Time.deltaTime);
            animator.SetBool(CrouchedId, IsCrouching());
        }

        /// <summary>
        /// Plays the reload animation. Nothing calls this yet: there is no reload in the game, it is here for the
        /// system that adds one.
        /// </summary>
        public void PlayReload()
        {
            if (!dead && CanDrive)
                animator.SetTrigger(ReloadId);
        }

        bool CanDrive => animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null;

        // Occupying a low cover point: the unit ducks. Tall cover is stood behind.
        bool IsCrouching() =>
            cover != null && cover.Status == CoverStatus.Occupied && cover.Point != null && cover.Point.IsValid
            && cover.Point.Height == CoverHeight.Low;

        void OnShot(Health target)
        {
            if (!dead && CanDrive)
                animator.SetTrigger(FireId);
        }

        void OnDamaged(int amount)
        {
            // The killing blow raises Damaged with IsAlive already false; the death animation handles that one.
            if (health != null && health.IsAlive && CanDrive)
                animator.SetTrigger(HitId);
        }

        void OnDied()
        {
            if (dead)
                return;
            dead = true;
            if (animator == null)
                return;

            // Crouched stays as the last frame wrote it: UnitCover releases its point on the same Died event, and
            // possibly before this handler runs, so recomputing it here would stand a crouching unit up to die.
            if (CanDrive)
                animator.SetBool(DeadId, true);

            var visual = animator.transform;
            if (visual == transform)
                return;   // the animator is on the unit itself: nothing to release
            visual.SetParent(transform.parent, true);
            Destroy(visual.gameObject, corpseSeconds);
        }
    }
}
