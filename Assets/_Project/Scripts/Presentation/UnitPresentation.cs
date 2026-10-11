using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Per-unit presentation adapter: listens to what its unit's gameplay components resolved (a discharged shot, an ability),
    /// resolves the weapon's profile and muzzle, and hands the request to the scene's CombatPresenter. It keeps the weapon's
    /// visual recoil. It never changes gameplay and nothing in gameplay reads it. Added by the mission spawner (and by hand in
    /// test scenes), never baked into a prefab, so rigs without a presenter are untouched. Records are delivered only to live
    /// subscribers: enabling it later replays nothing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitPresentation : MonoBehaviour
    {
        [SerializeField] CombatPresenter presenter;
        Health health;
        UnitAttacker attacker;
        UnitAbilities abilities;
        UnitWeaponVisual weapon;
        bool subscribed;
        readonly SequenceFilter seen = new SequenceFilter();
        readonly RecoilEnvelope recoil = new RecoilEnvelope();
        Transform recoilTarget;
        Vector3 recoilBase;

        const float FlashSeconds = 0.12f;
        // Multiplies the base colour while flashing (HDR-style overshoot, so a white body still visibly brightens).
        const float FlashBoost = 2.5f;
        // The unit pivot is the capsule centre, about this far above the floor.
        const float PivotToFloor = 0.95f;
        // Used when the unit has no capsule collider.
        const float DefaultBodyRadius = 0.5f;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        SkinnedMeshRenderer[] bodies = new SkinnedMeshRenderer[0];
        MaterialPropertyBlock block;
        float flashRemaining;
        bool flashing;
        int damagedFrame = -1;
        int damagedClearCount;

        public CombatPresenter Presenter => presenter;

        public void Bind(CombatPresenter target)
        {
            Unsubscribe();
            presenter = target;
            if (isActiveAndEnabled)
                Subscribe();
        }

        void OnEnable()
        {
            if (presenter != null)
                Subscribe();
        }

        void OnDisable()
        {
            Unsubscribe();
            EndRecoil();
            EndFlash();
            damagedFrame = -1;   // a pending burst never survives a disable (it would otherwise show on re-enable)
        }

        void Subscribe()
        {
            if (subscribed)
                return;
            subscribed = true;
            TryGetComponent(out health);
            TryGetComponent(out attacker);
            TryGetComponent(out abilities);
            TryGetComponent(out weapon);
            bodies = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (attacker != null)
                attacker.Fired += OnFired;
            if (abilities != null)
            {
                abilities.ShotResolved += OnAbilityShot;
                abilities.AreaResolved += OnAreaResolved;
            }
            if (weapon != null)
                weapon.Changed += OnWeaponChanged;
            if (health != null)
            {
                health.Damaged += OnDamaged;
                health.Died += OnDied;
                health.Healed += OnHealed;
            }
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            subscribed = false;
            if (attacker != null)
                attacker.Fired -= OnFired;
            if (abilities != null)
            {
                abilities.ShotResolved -= OnAbilityShot;
                abilities.AreaResolved -= OnAreaResolved;
            }
            if (weapon != null)
                weapon.Changed -= OnWeaponChanged;
            if (health != null)
            {
                health.Damaged -= OnDamaged;
                health.Died -= OnDied;
                health.Healed -= OnHealed;
            }
        }

        // Exception firewall: a presentation bug must never make the gameplay call that raised the event throw or skip its bookkeeping.
        void OnFired(ShotRecord record) => DeliverSafely(record);
        void OnAbilityShot(ShotRecord record) => DeliverSafely(record);

        void OnAreaResolved(AreaRecord record)
        {
            try
            {
                Deliver(record);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        void DeliverSafely(ShotRecord record)
        {
            try
            {
                Deliver(record);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        internal void Deliver(in ShotRecord record)
        {
            if (presenter == null || !seen.TryAccept(record.EmitterId, record.Sequence))
                return;
            var profile = ProfileFor(record);
            // Effects spawn at the muzzle's pose at the instant of the shot; the recoil is a consequence of it, so it comes after.
            var shown = presenter.ShowShot(record, profile, weapon != null ? weapon.Muzzle : null);
            if (shown && !record.IsMelee)
                Kick(profile);
        }

        internal void Deliver(in AreaRecord record)
        {
            if (presenter == null || !seen.TryAccept(record.EmitterId, record.Sequence))
                return;
            presenter.ShowArea(record, record.Ability != null ? record.Ability.Presentation : null);
        }

        // An ability's own profile wins; else the weapon in hand; else the presenter's fallback.
        CombatPresentationProfile ProfileFor(in ShotRecord record)
        {
            if (record.Ability != null && record.Ability.Presentation != null)
                return record.Ability.Presentation;
            var shown = weapon != null ? weapon.Shown : null;
            if (shown != null && shown.Presentation != null)
                return shown.Presentation;
            return presenter.Fallback;
        }

        // ---- recoil: a visual-only kick of the managed weapon model along its own barrel axis ----

        void Kick(CombatPresentationProfile profile)
        {
            if (weapon == null || weapon.Current == null || weapon.Muzzle == null || profile.RecoilKick <= 0f)
                return;
            var model = weapon.Current.transform;
            if (recoilTarget != model)
            {
                EndRecoil();
                recoilTarget = model;
                recoilBase = model.localPosition;
            }
            recoil.Kick(profile.RecoilKick, profile.RecoilSeconds);
            ApplyRecoil();
        }

        void Update()
        {
            if (!SimulationTime.IsRunning)
                return;
            if (recoil.IsActive)
            {
                recoil.Advance(Time.deltaTime);
                ApplyRecoil();
            }
            if (flashing)
            {
                flashRemaining -= Time.deltaTime;
                if (flashRemaining <= 0f)
                    EndFlash();
                else
                    ApplyFlash();
            }
        }

        // Damage no shot already depicted (a blast, retaliation, anything else) shows one small neutral burst, once the
        // frame's shots have been presented. The killing blow shows none: the unit is already gone. Damage dealt after this
        // LateUpdate (an OnGUI button, a later LateUpdate) is presented on the next one, still against that frame's shots.
        void LateUpdate()
        {
            if (damagedFrame < 0 || damagedFrame > Time.frameCount)
                return;
            var frame = damagedFrame;
            damagedFrame = -1;
            try
            {
                if (presenter == null || health == null || !health.IsAlive)
                    return;
                // The presenter was cleared since the damage (mission change, presentation switched off): nothing pending survives it.
                if (presenter.ClearCount != damagedClearCount)
                    return;
                if (presenter.ConsumeCharacterImpact(health, frame))
                    return;
                BurstPlacement(out var point, out var normal);
                presenter.ShowCharacterBurst(health, point, normal);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        // The default burst starts on the body's surface at chest height, on the side facing the viewer (the main camera, flattened
        // to the horizontal plane; straight back when there is none), tilted slightly up. On the axis it would sit inside the body.
        void BurstPlacement(out Vector3 point, out Vector3 normal)
        {
            var radius = DefaultBodyRadius;
            if (TryGetComponent(out CapsuleCollider capsule))
            {
                var scale = transform.lossyScale;
                radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            }
            var direction = Vector3.back;
            var viewer = Camera.main;
            if (viewer != null)
            {
                var toViewer = viewer.transform.position - transform.position;
                toViewer.y = 0f;
                if (toViewer.sqrMagnitude > 1e-4f)
                    direction = toViewer.normalized;
            }
            point = transform.position + Vector3.up * ShotRecord.BodyHeight + direction * radius;
            normal = (direction + Vector3.up * 0.25f).normalized;
        }

        // ---- character feedback: a hit flash on the unit's own bodies, a default burst, the death and heal cues ----

        void OnDamaged(int amount)
        {
            try
            {
                // A unit the player may not see shows no reaction.
                if (presenter == null || !presenter.IsActive || !presenter.Observes(health))
                    return;
                flashRemaining = FlashSeconds;
                flashing = true;
                damagedFrame = Time.frameCount;
                damagedClearCount = presenter.ClearCount;
                ApplyFlash();
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        void OnDied()
        {
            try
            {
                EndFlash();
                damagedFrame = -1;
                if (presenter != null)
                    presenter.ShowDeath(health, transform.position);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        void OnHealed(int restored)
        {
            try
            {
                if (presenter != null && restored > 0)
                    presenter.ShowPulse(health, transform.position - Vector3.up * PivotToFloor);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        // The flash is a property block on this unit's own skinned bodies, on top of the team tint: no material is shared or modified.
        void ApplyFlash()
        {
            if (bodies.Length == 0)
                return;
            var k = Mathf.Clamp01(flashRemaining / FlashSeconds);
            // Resolved each time, so a tint added after this component subscribed is honoured and never wiped.
            var baseColor = TryGetComponent(out UnitTeamTint tint) ? tint.TeamColor : Color.white;
            var boost = 1f + FlashBoost * k;
            var color = new Color(baseColor.r * boost, baseColor.g * boost, baseColor.b * boost, baseColor.a);
            block = block ?? new MaterialPropertyBlock();
            foreach (var body in bodies)
            {
                if (body == null)
                    continue;
                body.GetPropertyBlock(block);
                block.SetColor(BaseColorId, color);
                body.SetPropertyBlock(block);
            }
        }

        void EndFlash()
        {
            var wasFlashing = flashing;
            flashing = false;
            flashRemaining = 0f;
            if (!wasFlashing)
                return;
            // Restored on the cached bodies, the ones the flash was applied to: on death the animation driver may already have
            // detached the visual into a corpse, where the tint's own Apply (a search below this unit) no longer finds it.
            var tinted = TryGetComponent(out UnitTeamTint tint);
            if (tinted)
                block = block ?? new MaterialPropertyBlock();
            foreach (var body in bodies)
            {
                if (body == null)
                    continue;
                if (!tinted)
                {
                    body.SetPropertyBlock(null);
                    continue;
                }
                body.GetPropertyBlock(block);
                block.SetColor(BaseColorId, tint.TeamColor);
                body.SetPropertyBlock(block);
            }
        }

        void ApplyRecoil()
        {
            if (recoilTarget == null || weapon == null || weapon.Muzzle == null)
            {
                recoil.Reset();
                return;
            }
            var offset = recoil.Offset;
            if (offset <= 0f)
            {
                recoilTarget.localPosition = recoilBase;
                return;
            }
            var back = -weapon.Muzzle.forward * offset;
            recoilTarget.localPosition = recoilBase + (recoilTarget.parent != null ? recoilTarget.parent.InverseTransformVector(back) : back);
        }

        void OnWeaponChanged() => EndRecoil();

        void EndRecoil()
        {
            recoil.Reset();
            if (recoilTarget != null)
                recoilTarget.localPosition = recoilBase;
            recoilTarget = null;
        }
    }
}
