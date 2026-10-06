using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Debug view of an armed ability, in the world (placeholder look): a white circle of the ability's range around the
    /// caster, a line from the caster to the aim, and at the aim a ring (the blast radius for a ground ability, a
    /// target ring for a unit), green while the preview is valid and red while it is not. Reads AbilityTargeting every
    /// frame, never changes it, has no colliders (so it never blocks a click ray), and works while paused.
    /// </summary>
    public sealed class AbilityTargetingView : MonoBehaviour
    {
        const int CircleSegments = 48;
        const float TargetRingRadius = 0.9f;
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Color RangeColor = new Color(0.9f, 0.9f, 0.9f, 1f);
        internal static readonly Color ValidColor = new Color(0.2f, 1f, 0.3f, 1f);
        internal static readonly Color InvalidColor = new Color(1f, 0.25f, 0.2f, 1f);

        [SerializeField] AbilityTargeting targeting;
        [SerializeField] Material lineMaterial;
        // The prototype ground is flat at y = 0; the lines float just above it.
        [SerializeField] float groundHeight = 0.08f;
        [SerializeField, Min(0.01f)] float lineWidth = 0.08f;

        LineRenderer rangeCircle;
        LineRenderer aimCircle;
        LineRenderer aimLine;
        MaterialPropertyBlock block;

        internal bool IsShowingRange => rangeCircle != null && rangeCircle.enabled;
        internal bool IsShowingAim => aimCircle != null && aimCircle.enabled;
        internal LineRenderer RangeCircle => rangeCircle;
        internal LineRenderer AimCircle => aimCircle;
        internal Color AimColor { get; private set; }

        internal void Initialize(AbilityTargeting abilityTargeting, Material material)
        {
            targeting = abilityTargeting;
            lineMaterial = material;
        }

        void Awake()
        {
            block = new MaterialPropertyBlock();
            rangeCircle = CreateLine("AbilityRange", true);
            aimCircle = CreateLine("AbilityAimRing", true);
            aimLine = CreateLine("AbilityAimLine", false);
            HideAll();
        }

        void LateUpdate()
        {
            if (targeting == null || !targeting.IsArmed || targeting.Caster == null || targeting.ArmedAbility == null)
            {
                HideAll();
                return;
            }

            var ability = targeting.ArmedAbility;
            var origin = targeting.Caster.transform.position;
            DrawCircle(rangeCircle, origin, ability.Range, RangeColor);

            var preview = targeting.Preview;
            if (!preview.HasAim)
            {
                aimCircle.enabled = false;
                aimLine.enabled = false;
                return;
            }

            var color = preview.IsValid ? ValidColor : InvalidColor;
            AimColor = color;
            var radius = ability.TargetMode == AbilityTargetMode.Ground ? ability.Radius : TargetRingRadius;
            DrawCircle(aimCircle, preview.Point, radius, color);

            aimLine.positionCount = 2;
            aimLine.SetPosition(0, Flat(origin));
            aimLine.SetPosition(1, Flat(preview.Point));
            Paint(aimLine, color);
            aimLine.enabled = true;
        }

        void OnDisable() => HideAll();

        void HideAll()
        {
            if (rangeCircle != null)
                rangeCircle.enabled = false;
            if (aimCircle != null)
                aimCircle.enabled = false;
            if (aimLine != null)
                aimLine.enabled = false;
        }

        void DrawCircle(LineRenderer line, Vector3 center, float radius, Color color)
        {
            line.positionCount = CircleSegments;
            for (var i = 0; i < CircleSegments; i++)
            {
                var angle = i * Mathf.PI * 2f / CircleSegments;
                line.SetPosition(i, Flat(center) + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius);
            }
            Paint(line, color);
            line.enabled = true;
        }

        Vector3 Flat(Vector3 point) => new Vector3(point.x, groundHeight, point.z);

        void Paint(LineRenderer line, Color color)
        {
            line.GetPropertyBlock(block);
            block.SetColor(BaseColor, color);
            line.SetPropertyBlock(block);
        }

        // Each line lives on a collider-free child so no component of the host is touched.
        LineRenderer CreateLine(string objectName, bool loop)
        {
            var child = new GameObject(objectName);
            child.transform.SetParent(transform, false);
            var line = child.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = loop;
            line.widthMultiplier = lineWidth;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (lineMaterial != null)
                line.sharedMaterial = lineMaterial;
            return line;
        }
    }
}
