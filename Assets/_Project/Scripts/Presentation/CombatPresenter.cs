using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The one scene-level presentation service. UnitPresentation components hand it what gameplay resolved; it decides what
    /// the player may see and hear (the knowledge gate), spawns pooled effects under explicit budgets, plays budgeted audio,
    /// sends a camera impulse and clears everything on a mission change. It never touches gameplay state, and every part is
    /// optional: a missing prefab, clip or profile just means nothing is shown. Effects follow the simulation clock, so a
    /// tactical pause freezes them; audio is paused through TacticalPause.Changed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatPresenter : MonoBehaviour
    {
        const float MuzzleLifetime = 1.6f;
        const float ImpactLifetime = 1.4f;
        const float AreaLifetime = 4f;
        const float PulseLifetime = 1.4f;
        const float FallbackMuzzleForward = 0.6f;
        const float SurfaceOffset = 0.3f;

        [SerializeField] PresentationSettings settings = new PresentationSettings();
        [SerializeField] IntelligenceService intelligence;
        [SerializeField] TacticalPause pause;
        [SerializeField] CameraImpulse cameraImpulse;
        [SerializeField] CombatPresentationProfile fallbackProfile;
        [SerializeField] CombatPresentationProfile supportProfile;

        readonly CosmeticRandom random = new CosmeticRandom();
        readonly HashSet<UnityEngine.Object> warned = new HashSet<UnityEngine.Object>();
        readonly Dictionary<Health, int> impactFrames = new Dictionary<Health, int>();
        readonly List<Health> staleBuffer = new List<Health>();
        EffectPool pool;
        EffectBudget budget;
        CombatAudio combatAudio;
        Transform container;
        CombatPresentationProfile emptyFallback;
        Func<Vector3, PointVisibility> queryDelegate;
        Func<Vector3, PointVisibility> pointOverride;
        Func<Health, bool> unitOverride;
        bool subscribed;

        public PresentationSettings Settings => settings;
        public bool IsActive => isActiveAndEnabled && settings.enabled;
        public EffectBudget Budget { get { EnsureBuilt(); return budget; } }
        public CombatAudio Audio { get { EnsureBuilt(); return combatAudio; } }
        public int ActiveEffects => pool == null ? 0 : pool.ActiveCount;
        public int ActiveOf(EffectCategory category) => pool == null ? 0 : pool.CountOf(category);
        /// <summary>Counts the Clear calls; a unit's deferred reaction to damage taken before a Clear is dropped, never shown after it.</summary>
        internal int ClearCount { get; private set; }

        /// <summary>Turning it off releases everything that is playing.</summary>
        public bool Enabled
        {
            get => settings.enabled;
            set
            {
                if (settings.enabled == value)
                    return;
                settings.enabled = value;
                if (!value)
                    Clear();
            }
        }

        /// <summary>The fallback profile (an empty one, warned about once, when none is assigned).</summary>
        public CombatPresentationProfile Fallback
        {
            get
            {
                if (fallbackProfile != null)
                    return fallbackProfile;
                if (emptyFallback == null)
                {
                    emptyFallback = CombatPresentationProfile.CreateEmpty();
                    WarnOnce(this, "CombatPresenter has no fallback profile; units without a presentation profile show nothing.");
                }
                return emptyFallback;
            }
        }

        public CombatPresentationProfile SupportProfile => supportProfile != null ? supportProfile : Fallback;

        internal void Bind(IntelligenceService service, TacticalPause pauseService, CameraImpulse impulse,
            CombatPresentationProfile fallback, CombatPresentationProfile support)
        {
            Unsubscribe();
            intelligence = service;
            pause = pauseService;
            cameraImpulse = impulse;
            fallbackProfile = fallback;
            supportProfile = support;
            if (isActiveAndEnabled)
                Subscribe();
        }

        /// <summary>Test and preview hook: replaces the knowledge queries (never used by the mission scenes).</summary>
        internal void UseKnowledge(Func<Vector3, PointVisibility> points, Func<Health, bool> units)
        {
            pointOverride = points;
            unitOverride = units;
        }

        internal void ClearKnowledgeOverride()
        {
            pointOverride = null;
            unitOverride = null;
        }

        void Awake() => EnsureBuilt();

        void OnEnable()
        {
            EnsureBuilt();
            Subscribe();
        }

        void OnDisable()
        {
            Unsubscribe();
            Clear();
        }

        void OnDestroy()
        {
            if (container != null)
                Destroy(container.gameObject);
        }

        void EnsureBuilt()
        {
            if (pool != null)
                return;
            container = new GameObject("PresentationPool").transform;
            pool = new EffectPool(container);
            budget = new EffectBudget(settings.quality);
            queryDelegate = QueryPoint;
            var audioObject = new GameObject("CombatAudio");
            audioObject.transform.SetParent(transform, false);
            combatAudio = audioObject.AddComponent<CombatAudio>();
            combatAudio.Initialize();
        }

        void Subscribe()
        {
            if (subscribed)
                return;
            subscribed = true;
            if (pause != null)
            {
                pause.Changed += OnPauseChanged;
                combatAudio.SetPaused(pause.IsPaused);
            }
            if (intelligence != null)
                intelligence.Changed += Regate;
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            subscribed = false;
            if (pause != null)
                pause.Changed -= OnPauseChanged;
            if (intelligence != null)
                intelligence.Changed -= Regate;
        }

        void OnPauseChanged(bool paused) => combatAudio.SetPaused(paused);

        void Update()
        {
            if (pool == null || !SimulationTime.IsRunning)
                return;
            pool.Sweep(Release);
        }

        // ---- knowledge ----

        /// <summary>Whether the player may see this unit now. A missing unit (a record without a shooter) is not observed: a missing gate input means do not show.</summary>
        public bool Observes(Health unit) =>
            unit != null && (unitOverride != null ? unitOverride(unit) : Knowledge.IsObserved(intelligence, unit));

        public PointVisibility QueryPoint(Vector3 point) =>
            pointOverride != null ? pointOverride(point) : Knowledge.PointVisibilityOf(intelligence, point);

        /// <summary>
        /// Observes, except that a unit that has just died is judged by where it died: gameplay applies the damage before the
        /// shot is announced, and a dead unit is never "observed", so a killing shot would otherwise lose its body impact.
        /// </summary>
        internal bool ObservesAt(Health unit, Vector3 point) =>
            Observes(unit) || (unit != null && !unit.IsAlive && QueryPoint(point) == PointVisibility.Visible);

        static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        /// <summary>Hides or shows every long-lived effect according to the current knowledge, without restarting any.</summary>
        internal void Regate()
        {
            if (pool == null)
                return;
            var active = pool.Active;
            for (var i = 0; i < active.Count; i++)
            {
                var effect = active[i];
                if (effect != null && effect.HasGatePoint)
                    effect.SetVisible(PresentationVisibility.AllowsPoint(queryDelegate, effect.GatePoint));
            }
        }

        // ---- requests from UnitPresentation ----

        /// <summary>Shows what the player may see of a resolved shot. Returns whether the shooter's own effects were allowed (the caller kicks the weapon only then).</summary>
        internal bool ShowShot(in ShotRecord record, CombatPresentationProfile profile, Transform muzzle)
        {
            if (!IsActive)
                return false;
            EnsureBuilt();
            profile = profile != null ? profile : Fallback;
            var shooterObserved = Observes(record.Source);
            var victimObserved = record.Target != null && ObservesAt(record.Target, record.AimPoint);

            Vector3 from;
            if (muzzle != null)
            {
                from = muzzle.position;
            }
            else
            {
                var toAim = record.AimPoint - record.Origin;
                from = record.Origin + (toAim.sqrMagnitude > 1e-4f ? toAim.normalized : Vector3.forward) * FallbackMuzzleForward;
                if (record.Source != null && !record.IsMelee && profile.MuzzleEffect != null)
                    WarnOnce(record.Source, $"{record.Source.name} fired without a Muzzle on its weapon; effects start in front of the unit.");
            }

            if (!IsFinite(from))
                return false;
            var endpoint = ShotGeometry.Resolve(record, from, random);
            var onCharacter = endpoint.Surface == ImpactSurfaceKind.Character;
            var gatePoint = onCharacter ? endpoint.ImpactPoint : endpoint.ImpactPoint + endpoint.ImpactNormal * SurfaceOffset;
            var allowed = PresentationVisibility.EvaluateShot(shooterObserved, victimObserved, queryDelegate, from,
                endpoint.HasTracer, endpoint.End, endpoint.HasImpact, gatePoint, onCharacter);

            var aim = endpoint.HasTracer ? endpoint.End - from : record.AimPoint - from;
            var rotation = IsFinite(aim) && aim.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(aim) : Quaternion.identity;
            if (allowed.Muzzle && !record.IsMelee)
            {
                Spawn(profile.MuzzleEffect, EffectCategory.Muzzle, from, rotation, profile.MuzzleScale, MuzzleLifetime, false, default);
                PlayCue(profile.FireCue, from);
            }
            if (allowed.Tracer)
                SpawnTracer(profile.Tracer, from, endpoint.End);
            if (allowed.Impact)
            {
                PlayImpact(profile, endpoint.Surface, endpoint.ImpactPoint, endpoint.ImpactNormal);
                if (onCharacter && record.Target != null)
                    MarkImpact(record.Target);
            }
            return allowed.Muzzle;
        }

        internal void ShowArea(in AreaRecord record, CombatPresentationProfile profile)
        {
            if (!IsActive)
                return;
            EnsureBuilt();
            profile = profile != null ? profile : Fallback;
            var center = record.Center;
            if (QueryPoint(center) != PointVisibility.Visible)
                return;
            if (profile.AreaEffect != null)
            {
                var scale = record.Radius / Mathf.Max(0.01f, profile.ReferenceRadius);
                Spawn(profile.AreaEffect, EffectCategory.Area, center + Vector3.up * 0.15f, Quaternion.identity, scale, AreaLifetime, true, center);
            }
            PlayCue(profile.AreaCue, center);
            if (cameraImpulse != null && settings.cameraShake > 0f)
                cameraImpulse.Add(profile.CameraImpulse * settings.cameraShake * cameraImpulse.DistanceFactor(center));
        }

        internal void ShowPulse(Health unit, Vector3 point)
        {
            if (!IsActive || unit == null || !Observes(unit))
                return;
            EnsureBuilt();
            var profile = SupportProfile;
            Spawn(profile.PulseEffect, EffectCategory.Pulse, point, Quaternion.identity, 1f, PulseLifetime, true, point);
            PlayCue(profile.PulseCue, point);
        }

        internal void ShowDeath(Health unit, Vector3 point)
        {
            if (!IsActive || unit == null || !ObservesAt(unit, point))
                return;
            EnsureBuilt();
            PlayCue(Fallback.DeathCue, point);
        }

        internal void ShowCharacterBurst(Health victim, Vector3 point, Vector3 normal)
        {
            if (!IsActive || victim == null || !Observes(victim))
                return;
            EnsureBuilt();
            PlayImpact(Fallback, ImpactSurfaceKind.Character, point, normal);
        }

        /// <summary>True once when a shot placed a character impact on this victim in `sinceFrame` (the frame it was damaged) or later.</summary>
        internal bool ConsumeCharacterImpact(Health victim, int sinceFrame)
        {
            if (victim == null || !impactFrames.TryGetValue(victim, out var frame))
                return false;
            impactFrames.Remove(victim);
            return frame >= sinceFrame;
        }

        void MarkImpact(Health victim)
        {
            if (impactFrames.Count > 64)
            {
                staleBuffer.Clear();
                foreach (var pair in impactFrames)
                    if (pair.Key == null || pair.Value < Time.frameCount - 2)
                        staleBuffer.Add(pair.Key);
                foreach (var stale in staleBuffer)
                    impactFrames.Remove(stale);
            }
            impactFrames[victim] = Time.frameCount;
        }

        // ---- spawning ----

        PooledEffect Spawn(GameObject prefab, EffectCategory category, Vector3 position, Quaternion rotation, float scale,
            float lifetime, bool lingering, Vector3 gatePoint)
        {
            if (prefab == null)
                return null;
            if (!budget.TryAcquire(category))
            {
                if (category != EffectCategory.Area || !RecycleOldest(category) || !budget.TryAcquire(category))
                    return null;
            }
            var effect = pool.Rent(prefab);
            var light = effect.Lights.Length > 0 && budget.TryAcquireLight();
            effect.HoldsLight = light;
            effect.Begin(category, position, rotation, scale, lifetime, light);
            pool.Track(effect);
            if (lingering)
                effect.SetGate(gatePoint);
            return effect;
        }

        void SpawnTracer(TracerStyle style, Vector3 from, Vector3 to)
        {
            if (style == null || !style.IsUsable || !budget.TryAcquire(EffectCategory.Tracer))
                return;
            var effect = pool.Rent(style.Prefab);
            if (effect.Tracer == null)
            {
                budget.Release(EffectCategory.Tracer);
                pool.Return(effect);
                WarnOnce(style.Prefab, "The tracer prefab has no TracerView component.");
                return;
            }
            effect.HoldsLight = false;
            effect.Begin(EffectCategory.Tracer, from, Quaternion.identity, 1f, 10f, false);
            effect.SetLifetime(effect.Tracer.Launch(from, to, style));
            pool.Track(effect);
        }

        void PlayImpact(CombatPresentationProfile profile, ImpactSurfaceKind surface, Vector3 point, Vector3 normal)
        {
            var entry = profile.ImpactFor(surface);
            if (entry == null)
                entry = Fallback.ImpactFor(surface);
            if (entry == null)
                return;
            var rotation = normal.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(normal) : Quaternion.identity;
            Spawn(entry.Prefab, EffectCategory.Impact, point, rotation, entry.Scale, ImpactLifetime, true, point + normal * SurfaceOffset);
            PlayCue(entry.Cue, point);
        }

        void PlayCue(AudioCue cue, Vector3 position)
        {
            if (cue == null || cue.IsEmpty)
                return;
            combatAudio.Play(cue, position, settings.masterVolume, random);
        }

        bool RecycleOldest(EffectCategory category)
        {
            PooledEffect oldest = null;
            foreach (var effect in pool.Active)
                if (effect != null && effect.Category == category && (oldest == null || effect.EndTime < oldest.EndTime))
                    oldest = effect;
            if (oldest == null)
                return false;
            Release(oldest);
            return true;
        }

        void Release(PooledEffect effect)
        {
            budget.Release(effect.Category);
            if (effect.HoldsLight)
                budget.ReleaseLight();
            pool.Return(effect);
        }

        /// <summary>Releases every effect, stops every sound and forgets pending impacts (mission change, disable).</summary>
        public void Clear()
        {
            ClearCount++;
            if (pool == null)
                return;
            // Safe during teardown: a destroyed pool container took its effects with it (ReleaseAll only forgets destroyed
            // ones), and the audio child may already be gone.
            pool.ReleaseAll(Release);
            budget.Reset();
            if (combatAudio != null)
                combatAudio.StopAll();
            impactFrames.Clear();
            if (cameraImpulse != null)
                cameraImpulse.Clear();
        }

        public void SetQuality(PresentationQuality quality)
        {
            EnsureBuilt();
            Clear();
            settings.quality = quality;
            budget = new EffectBudget(quality);
        }

        /// <summary>Logs a message once per key, so a misconfiguration is reported without flooding the Console.</summary>
        internal void WarnOnce(UnityEngine.Object key, string message)
        {
            if (key == null || !warned.Add(key))
                return;
            Debug.LogWarning(message, key);
        }
    }
}
