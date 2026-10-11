#if UNITY_EDITOR
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>One particle system, described in plain numbers (world-unit sizes and speeds; scaled by the root's scale).</summary>
    internal sealed class PsSpec
    {
        public string Name = "Particles";
        public Material Material;
        public ParticleSystemRenderMode Render = ParticleSystemRenderMode.Billboard;
        public Vector2 Lifetime = new Vector2(0.5f, 0.5f);
        public Vector2 Size = new Vector2(0.2f, 0.2f);
        public Vector2 Speed = Vector2.zero;
        public int Burst = 1;
        public float Duration = 0.5f;
        public bool ShapeEnabled = true;
        public ParticleSystemShapeType Shape = ParticleSystemShapeType.Cone;
        public float ShapeAngle = 0f;
        public float ShapeRadius = 0.01f;
        public Color StartColor = Color.white;
        public Gradient ColorOverLifetime;
        public AnimationCurve SizeOverLifetime;
        public float Gravity;
        public float LengthScale = 2f;
        public Vector3 LocalPosition;
        public Vector3 LocalEuler;
        public bool RandomRotation = true;
        // Unity clips a particle to this fraction of the viewport (default 0.5): the big explosion layers must not be clipped when the camera is close.
        public float MaxParticleSize = 0.5f;
        // Render = Mesh: the mesh each particle draws, aligned to the system's own rotation (a flat quad lying on the ground, for the ring).
        public Mesh Mesh;
    }

    internal static class ParticleAuthoring
    {
        /// <summary>Alpha 1 until `hold` (0..1 of the lifetime), then fading to 0 at the end.</summary>
        public static Gradient FadeOut(float hold = 0.2f)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, Mathf.Clamp01(hold)), new GradientAlphaKey(0f, 1f) });
            return gradient;
        }

        /// <summary>Grows from `from` to `to` times the start size over the lifetime.</summary>
        public static AnimationCurve Grow(float from, float to) => AnimationCurve.EaseInOut(0f, from, 1f, to);

        public static ParticleSystem Build(Transform parent, PsSpec s)
        {
            var go = new GameObject(s.Name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = s.LocalPosition;
            go.transform.localEulerAngles = s.LocalEuler;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = s.Duration;
            main.startLifetime = new ParticleSystem.MinMaxCurve(s.Lifetime.x, s.Lifetime.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(s.Speed.x, s.Speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(s.Size.x, s.Size.y);
            main.startColor = s.StartColor;
            main.gravityModifier = s.Gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = Mathf.Max(8, s.Burst * 2);
            main.useUnscaledTime = false;
            if (s.RandomRotation)
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)s.Burst) });

            var shape = ps.shape;
            shape.enabled = s.ShapeEnabled;
            shape.shapeType = s.Shape;
            shape.angle = s.ShapeAngle;
            shape.radius = s.ShapeRadius;

            if (s.ColorOverLifetime != null)
            {
                var color = ps.colorOverLifetime;
                color.enabled = true;
                color.color = new ParticleSystem.MinMaxGradient(s.ColorOverLifetime);
            }
            if (s.SizeOverLifetime != null)
            {
                var size = ps.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, s.SizeOverLifetime);
            }

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = s.Material;
            renderer.renderMode = s.Render;
            renderer.lengthScale = s.LengthScale;
            renderer.maxParticleSize = s.MaxParticleSize;
            if (s.Render == ParticleSystemRenderMode.Mesh)
            {
                renderer.mesh = s.Mesh;
                renderer.alignment = ParticleSystemRenderSpace.Local;
            }
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ps;
        }
    }
}
#endif
