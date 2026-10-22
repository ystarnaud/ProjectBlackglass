using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One replicated avatar's look in the play client: the character model with its rifle (colliders stripped), a side ring
    /// (blue own, red hostile) and a selection ring. Its Animator is driven only from what was replicated: Speed from the
    /// interpolated position, Dead from Alive, Fire on a relayed shot, Hit on a health drop. It decides nothing.
    /// </summary>
    public sealed class NetAvatarView : MonoBehaviour
    {
        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int DeadId = Animator.StringToHash("Dead");
        static readonly int FireId = Animator.StringToHash("Fire");
        static readonly int HitId = Animator.StringToHash("Hit");
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        internal static readonly Color OwnColour = new Color(0.2f, 0.45f, 0.95f);
        internal static readonly Color HostileColour = new Color(0.9f, 0.2f, 0.2f);
        internal static readonly Color OtherColour = new Color(0.25f, 0.75f, 0.3f);
        internal static readonly Color SelectedColour = new Color(1f, 0.85f, 0.2f);
        // UnitAnimationDriver's guard: a move this fast in one frame is a placement, not walking.
        const float TeleportSpeed = 40f;
        const float SpeedDamping = 0.1f;

        Animator animator;
        UnitWeaponVisual weapon;
        Renderer sideRing;
        Renderer selectionRing;
        MaterialPropertyBlock block;
        Vector3 last;
        bool started;

        internal static NetAvatarView Create(NetUnitState state, GameObject visualPrefab, WeaponVisualAsset weaponAsset, Material ringMaterial)
        {
            var holder = new GameObject("PlayVisual_" + state.UnitId);
            holder.transform.SetParent(state.transform, false);
            var view = holder.AddComponent<NetAvatarView>();
            if (visualPrefab != null)
            {
                var model = Instantiate(visualPrefab, holder.transform);
                EnvironmentVisualBuilder.StripColliders(model);
                view.animator = model.GetComponentInChildren<Animator>();
                view.weapon = model.AddComponent<UnitWeaponVisual>();
                view.weapon.Show(weaponAsset);
            }
            else
            {
                var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                DestroyImmediate(capsule.GetComponent<Collider>());
                capsule.transform.SetParent(holder.transform, false);
                capsule.transform.localPosition = new Vector3(0f, 1f, 0f);
            }
            view.sideRing = Ring(holder.transform, "SideRing", 1.1f, 0.02f, ringMaterial);
            view.selectionRing = Ring(holder.transform, "SelectionRing", 1.5f, 0.01f, ringMaterial);
            return view;
        }

        static Renderer Ring(Transform parent, string ringName, float diameter, float height, Material material)
        {
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = ringName;
            DestroyImmediate(ring.GetComponent<Collider>());
            ring.transform.SetParent(parent, false);
            ring.transform.localPosition = new Vector3(0f, height, 0f);
            ring.transform.localScale = new Vector3(diameter, 0.005f, diameter);
            var renderer = ring.GetComponent<Renderer>();
            if (material != null)
                renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return renderer;
        }

        /// <summary>Flat metres per second between two positions; 0 for no time or for a jump faster than TeleportSpeed.</summary>
        internal static float Speed(Vector3 previous, Vector3 current, float deltaTime)
        {
            if (deltaTime <= 0f)
                return 0f;
            var flat = current - previous;
            flat.y = 0f;
            var speed = flat.magnitude / deltaTime;
            return speed > TeleportSpeed ? 0f : speed;
        }

        /// <summary>The side ring (own, hostile or other colour) and the selection ring, for tests of what a player can tell apart.</summary>
        internal Renderer SideRing => sideRing;
        internal Renderer SelectionRing => selectionRing;

        internal Vector3 MuzzlePoint => weapon != null && weapon.Muzzle != null ? weapon.Muzzle.position : transform.position + Vector3.up * 1.4f;
        internal Vector3 ChestPoint => transform.position + Vector3.up * 1.2f;

        internal void Tick(NetUnitState state, bool owned, bool selected, float deltaTime)
        {
            var position = transform.position;
            if (animator != null)
            {
                if (started)
                    animator.SetFloat(SpeedId, Speed(last, position, deltaTime), SpeedDamping, deltaTime);
                animator.SetBool(DeadId, !state.Alive);
            }
            last = position;
            started = true;
            Tint(sideRing, state.Hostile ? HostileColour : owned ? OwnColour : OtherColour);
            sideRing.enabled = state.Alive;
            Tint(selectionRing, SelectedColour);
            selectionRing.enabled = selected && state.Alive;
        }

        internal void Fire()
        {
            if (animator != null)
                animator.SetTrigger(FireId);
        }

        internal void Hit()
        {
            if (animator != null)
                animator.SetTrigger(HitId);
        }

        void Tint(Renderer renderer, Color colour)
        {
            if (block == null)
                block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, colour);
            renderer.SetPropertyBlock(block);
        }
    }
}
