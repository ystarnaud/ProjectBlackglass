using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One sound: clips to pick from, loudness, pitch spread, voice priority (0 most important), falloff distances and a minimum interval between plays.</summary>
    [Serializable]
    public sealed class AudioCue
    {
        [SerializeField] AudioClip[] clips = new AudioClip[0];
        [SerializeField, Range(0f, 1f)] float volume = 1f;
        [SerializeField, Range(0.5f, 2f)] float pitchMin = 0.95f;
        [SerializeField, Range(0.5f, 2f)] float pitchMax = 1.05f;
        [SerializeField, Range(0, 256)] int priority = 128;
        [SerializeField, Min(0.1f)] float minDistance = 4f;
        [SerializeField, Min(1f)] float maxDistance = 45f;
        [SerializeField, Min(0f)] float minInterval = 0.06f;

        public float Volume => volume;
        public float PitchMin => pitchMin;
        public float PitchMax => Mathf.Max(pitchMin, pitchMax);
        public int Priority => priority;
        public float MinDistance => minDistance;
        public float MaxDistance => Mathf.Max(minDistance + 0.1f, maxDistance);
        public float MinInterval => minInterval;

        public bool IsEmpty
        {
            get
            {
                if (clips == null)
                    return true;
                for (var i = 0; i < clips.Length; i++)
                    if (clips[i] != null)
                        return false;
                return true;
            }
        }

        /// <summary>A random non-missing clip, or null when there is none.</summary>
        public AudioClip Pick(CosmeticRandom random)
        {
            if (IsEmpty)
                return null;
            var count = 0;
            for (var i = 0; i < clips.Length; i++)
                if (clips[i] != null)
                    count++;
            var chosen = Mathf.Min(count - 1, (int)(random.Value() * count));
            for (var i = 0; i < clips.Length; i++)
            {
                if (clips[i] == null)
                    continue;
                if (chosen-- == 0)
                    return clips[i];
            }
            return null;
        }

        internal static AudioCue Create(float volume = 1f, int priority = 128, float minInterval = 0.06f, params AudioClip[] clips) =>
            new AudioCue { volume = volume, priority = priority, minInterval = minInterval, clips = clips ?? new AudioClip[0] };

        internal AudioCue WithFalloff(float min, float max)
        {
            minDistance = min;
            maxDistance = max;
            return this;
        }
    }

    /// <summary>The tracer: a pooled prefab (a LineRenderer with a TracerView) and how the streak looks and moves. Speed 0 or less draws it at once.</summary>
    [Serializable]
    public sealed class TracerStyle
    {
        [SerializeField] GameObject prefab;
        [SerializeField, Min(0.005f)] float width = 0.035f;
        [SerializeField, Min(0.1f)] float length = 6f;
        [SerializeField] float speed = 300f;
        [SerializeField] Color startColor = new Color(1f, 0.88f, 0.6f, 0.95f);
        [SerializeField] Color endColor = new Color(1f, 0.6f, 0.25f, 0f);

        public GameObject Prefab => prefab;
        public float Width => width;
        public float Length => length;
        public float Speed => speed;
        public Color StartColor => startColor;
        public Color EndColor => endColor;
        public bool IsUsable => prefab != null;

        internal static TracerStyle Create(GameObject prefab, float width = 0.035f, float length = 6f, float speed = 300f) =>
            new TracerStyle { prefab = prefab, width = width, length = length, speed = speed };
    }

    /// <summary>The effect and sound for an impact on one kind of surface.</summary>
    [Serializable]
    public sealed class SurfaceImpact
    {
        [SerializeField] ImpactSurfaceKind surface;
        [SerializeField] GameObject prefab;
        [SerializeField, Min(0.1f)] float scale = 1f;
        [SerializeField] AudioCue cue = new AudioCue();

        public ImpactSurfaceKind Surface => surface;
        public GameObject Prefab => prefab;
        public float Scale => scale;
        public AudioCue Cue => cue;

        internal static SurfaceImpact Create(ImpactSurfaceKind surface, GameObject prefab, AudioCue cue = null, float scale = 1f) =>
            new SurfaceImpact { surface = surface, prefab = prefab, scale = scale, cue = cue ?? new AudioCue() };

        internal void FillFrom(GameObject newPrefab, AudioCue newCue)
        {
            if (prefab == null && newPrefab != null)
                prefab = newPrefab;
            if (cue.IsEmpty && newCue != null && !newCue.IsEmpty)
                cue = newCue;
        }
    }

    /// <summary>
    /// How a weapon or an ability looks and sounds, as shared immutable data: the muzzle effect, the tracer, the impact per
    /// surface, the firing sound, the recoil, and (for an area ability) the explosion at a reference radius. It holds no
    /// playback state and decides nothing. Every part is optional; a missing part is simply not shown.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Combat Presentation Profile", fileName = "CombatPresentation")]
    public sealed class CombatPresentationProfile : ScriptableObject
    {
        [Header("Weapon")]
        [SerializeField] GameObject muzzleEffect;
        [SerializeField, Min(0.1f)] float muzzleScale = 1f;
        [SerializeField] TracerStyle tracer = new TracerStyle();
        [SerializeField] AudioCue fireCue = new AudioCue();
        [SerializeField] SurfaceImpact[] impacts = new SurfaceImpact[0];
        [SerializeField, Min(0f)] float recoilKick = 0.035f;
        [SerializeField, Min(0.01f)] float recoilSeconds = 0.12f;

        [Header("Area ability")]
        [SerializeField] GameObject areaEffect;
        // The gameplay radius the area prefab was authored for; the instance is scaled by radius / referenceRadius.
        [SerializeField, Min(0f)] float referenceRadius = 3f;
        [SerializeField] AudioCue areaCue = new AudioCue();
        [SerializeField, Range(0f, 1f)] float cameraImpulse = 0.6f;

        [Header("Support and characters")]
        [SerializeField] GameObject pulseEffect;
        [SerializeField] AudioCue pulseCue = new AudioCue();
        [SerializeField] AudioCue deathCue = new AudioCue();

        public GameObject MuzzleEffect => muzzleEffect;
        public float MuzzleScale => muzzleScale;
        public TracerStyle Tracer => tracer ?? (tracer = new TracerStyle());
        public AudioCue FireCue => fireCue ?? (fireCue = new AudioCue());
        public IReadOnlyList<SurfaceImpact> Impacts => impacts ?? Array.Empty<SurfaceImpact>();
        public float RecoilKick => recoilKick;
        public float RecoilSeconds => recoilSeconds;
        public GameObject AreaEffect => areaEffect;
        public float ReferenceRadius => referenceRadius;
        public AudioCue AreaCue => areaCue ?? (areaCue = new AudioCue());
        public float CameraImpulse => cameraImpulse;
        public GameObject PulseEffect => pulseEffect;
        public AudioCue PulseCue => pulseCue ?? (pulseCue = new AudioCue());
        public AudioCue DeathCue => deathCue ?? (deathCue = new AudioCue());

        /// <summary>The entry for this surface, else the Default entry, else null.</summary>
        public SurfaceImpact ImpactFor(ImpactSurfaceKind kind)
        {
            SurfaceImpact fallback = null;
            foreach (var entry in Impacts)
            {
                if (entry == null)
                    continue;
                if (entry.Surface == kind)
                    return entry;
                if (entry.Surface == ImpactSurfaceKind.Default)
                    fallback = entry;
            }
            return fallback;
        }

        /// <summary>What is wrong with this profile, one line each (empty when it is fine). The asset builder and the tests assert it is empty.</summary>
        public List<string> Problems()
        {
            var problems = new List<string>();
            CheckPrefab(problems, "muzzle effect", muzzleEffect);
            CheckPrefab(problems, "tracer", Tracer.Prefab);
            CheckPrefab(problems, "area effect", areaEffect);
            CheckPrefab(problems, "pulse effect", pulseEffect);
            if (areaEffect != null && referenceRadius <= 0f)
                problems.Add("the area effect needs a reference radius above 0");
            var seen = new HashSet<ImpactSurfaceKind>();
            foreach (var entry in Impacts)
            {
                if (entry == null)
                    continue;
                if (!seen.Add(entry.Surface))
                    problems.Add($"the {entry.Surface} impact is listed twice");
                if (entry.Prefab == null && entry.Cue.IsEmpty)
                    problems.Add($"the {entry.Surface} impact has neither an effect nor a sound");
                CheckPrefab(problems, entry.Surface + " impact", entry.Prefab);
            }
            return problems;
        }

        static void CheckPrefab(List<string> problems, string what, GameObject prefab)
        {
            if (prefab == null)
                return;
            if (prefab.GetComponentInChildren<Collider>(true) != null)
                problems.Add($"the {what} prefab has a collider (effects must never enter gameplay queries)");
            if (prefab.GetComponentInChildren<Rigidbody>(true) != null)
                problems.Add($"the {what} prefab has a rigidbody");
        }

        /// <summary>A profile that shows nothing. Used when a unit has no profile and the presenter has no fallback asset.</summary>
        public static CombatPresentationProfile CreateEmpty()
        {
            var profile = CreateInstance<CombatPresentationProfile>();
            profile.hideFlags = HideFlags.DontSave;
            return profile;
        }

        internal void SetMuzzle(GameObject prefab, float scale) { muzzleEffect = prefab; muzzleScale = scale; }
        internal void SetTracer(TracerStyle style) => tracer = style;
        internal void SetFireCue(AudioCue cue) => fireCue = cue;
        internal void SetImpacts(params SurfaceImpact[] entries) => impacts = entries;

        /// <summary>Adds the surface's entry, or fills the missing prefab or sound of the existing one. Never overwrites a value that is set.</summary>
        internal void EnsureImpact(ImpactSurfaceKind kind, GameObject prefab, AudioCue cue)
        {
            foreach (var entry in Impacts)
            {
                if (entry != null && entry.Surface == kind)
                {
                    entry.FillFrom(prefab, cue);
                    return;
                }
            }
            var list = new List<SurfaceImpact>(Impacts) { SurfaceImpact.Create(kind, prefab, cue) };
            impacts = list.ToArray();
        }
        internal void SetRecoil(float kick, float seconds) { recoilKick = kick; recoilSeconds = seconds; }
        internal void SetArea(GameObject prefab, float radius, AudioCue cue, float impulse)
        {
            areaEffect = prefab;
            referenceRadius = radius;
            areaCue = cue ?? new AudioCue();
            cameraImpulse = impulse;
        }
        internal void SetPulse(GameObject prefab, AudioCue cue) { pulseEffect = prefab; pulseCue = cue ?? new AudioCue(); }
        internal void SetDeathCue(AudioCue cue) => deathCue = cue ?? new AudioCue();
    }
}
