using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One pooled presentation instance. Caches its particle systems, renderers and lights once, plays them from the start on
    /// Begin, ends on a scaled-time deadline (so a pause neither ages nor releases it) and resets completely on return to the
    /// pool: particles and trails cleared, lights and renderers restored, scale and parent restored, gate state forgotten.
    /// A short light envelope (lightSeconds, scaled time) fades any lights it holds.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PooledEffect : MonoBehaviour
    {
        [SerializeField, Min(0f)] float lightSeconds = 0.1f;

        ParticleSystem[] particles = new ParticleSystem[0];
        Renderer[] renderers = new Renderer[0];
        Light[] lights = new Light[0];
        float[] lightIntensity = new float[0];
        TracerView tracer;
        float lightAge;
        bool visible = true;

        public GameObject Prefab { get; internal set; }
        public EffectCategory Category { get; private set; }
        public float EndTime { get; private set; }
        public bool IsActive { get; private set; }
        public bool Expired => IsActive && Time.time >= EndTime;
        /// <summary>True while this instance counts against the light budget.</summary>
        public bool HoldsLight { get; internal set; }
        /// <summary>True while the instance sits in the pool's idle stack (set on return, cleared on rent); makes a double return harmless.</summary>
        internal bool InPool { get; set; }
        public bool HasGatePoint { get; private set; }
        public Vector3 GatePoint { get; private set; }
        public bool IsVisible => visible;
        public TracerView Tracer => tracer;
        public ParticleSystem[] Particles => particles;
        public Light[] Lights => lights;

        internal static PooledEffect Create(GameObject prefab, Transform container)
        {
            var instance = Instantiate(prefab, container);
            instance.name = prefab.name;
            if (!instance.TryGetComponent(out PooledEffect effect))
                effect = instance.AddComponent<PooledEffect>();
            effect.Prefab = prefab;
            effect.Build();
            instance.SetActive(false);
            return effect;
        }

        void Build()
        {
            particles = GetComponentsInChildren<ParticleSystem>(true);
            renderers = GetComponentsInChildren<Renderer>(true);
            lights = GetComponentsInChildren<Light>(true);
            lightIntensity = new float[lights.Length];
            for (var i = 0; i < lights.Length; i++)
            {
                lightIntensity[i] = lights[i].intensity;
                lights[i].enabled = false;
            }
            TryGetComponent(out tracer);
        }

        internal void Begin(EffectCategory category, Vector3 position, Quaternion rotation, float scale, float lifetime, bool allowLights)
        {
            Category = category;
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = Vector3.one * scale;
            gameObject.SetActive(true);
            IsActive = true;
            EndTime = Time.time + lifetime;
            lightAge = 0f;
            visible = true;
            HasGatePoint = false;
            foreach (var r in renderers)
                r.forceRenderingOff = false;
            for (var i = 0; i < lights.Length; i++)
            {
                lights[i].intensity = lightIntensity[i];
                lights[i].enabled = allowLights;
            }
            foreach (var ps in particles)
            {
                ps.Clear(false);
                ps.Play(false);
            }
        }

        /// <summary>Replaces the scaled-time lifetime, measured from now (a tracer learns its real duration after Begin).</summary>
        internal void SetLifetime(float seconds) => EndTime = Time.time + seconds;

        /// <summary>Marks this effect as long-lived at a world point, so it can be hidden and shown again as the player's knowledge changes.</summary>
        internal void SetGate(Vector3 point)
        {
            HasGatePoint = true;
            GatePoint = point;
        }

        /// <summary>Hides or shows the effect without stopping or restarting it.</summary>
        public void SetVisible(bool show)
        {
            if (show == visible)
                return;
            visible = show;
            foreach (var r in renderers)
                r.forceRenderingOff = !show;
            for (var i = 0; i < lights.Length; i++)
                lights[i].enabled = show && HoldsLight && lightAge < lightSeconds;
        }

        void Update()
        {
            if (!IsActive || lights.Length == 0 || !SimulationTime.IsRunning)
                return;
            lightAge += Time.deltaTime;
            var left = lightSeconds <= 0f ? 0f : Mathf.Clamp01(1f - lightAge / lightSeconds);
            for (var i = 0; i < lights.Length; i++)
            {
                if (!lights[i].enabled)
                    continue;
                if (left <= 0f)
                    lights[i].enabled = false;
                else
                    lights[i].intensity = lightIntensity[i] * left * left;
            }
        }

        internal void ResetForPool(Transform container)
        {
            foreach (var ps in particles)
            {
                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(false);
            }
            if (tracer != null)
                tracer.ResetState();
            for (var i = 0; i < lights.Length; i++)
            {
                lights[i].enabled = false;
                lights[i].intensity = lightIntensity[i];
            }
            foreach (var r in renderers)
                r.forceRenderingOff = false;
            transform.SetParent(container, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            gameObject.SetActive(false);
            IsActive = false;
            InPool = true;
            HasGatePoint = false;
            HoldsLight = false;
            visible = true;
            EndTime = 0f;
            lightAge = 0f;
        }
    }
}
