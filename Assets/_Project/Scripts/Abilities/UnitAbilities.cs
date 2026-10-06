using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What a validation found: the first failure (None when the ability can be used) and the numbers previews show.</summary>
    public readonly struct AbilityCheck
    {
        public AbilityCheck(AbilityFailure failure, float distance, bool sightClear, bool targetInCover, float hitChance)
        {
            Failure = failure;
            Distance = distance;
            SightClear = sightClear;
            TargetInCover = targetInCover;
            HitChance = hitChance;
        }

        public AbilityFailure Failure { get; }
        public bool IsValid => Failure == AbilityFailure.None;
        /// <summary>Flat distance from the caster to the target or point; 0 when there is no aim yet.</summary>
        public float Distance { get; }
        /// <summary>Whether the line from the caster's eye to the target (or a unit standing at the point) is clear.</summary>
        public bool SightClear { get; }
        /// <summary>The ability treats cover as applying and the target holds cover that protects it from the caster.</summary>
        public bool TargetInCover { get; }
        /// <summary>The cover's chance to be hit when TargetInCover, else 1.</summary>
        public float HitChance { get; }
    }

    /// <summary>
    /// A unit's abilities: up to four definitions (shared, immutable data) and, per slot, the scaled time at which the
    /// slot is ready again. Cooldowns therefore freeze while the game is paused and belong to the unit, so they survive
    /// control-mode changes and character switching, and two units sharing one definition never share a cooldown. Check
    /// is the single validation (AbilityRules plus the facts gathered here); TryUse validates and then applies the effect
    /// through Health, with cover and line of sight from the same systems basic attacks use. Instant: it does not wait
    /// for the pause service, whoever calls it (CommandableUnit) decides when. Reports failures through LastFailure and
    /// events, never the Console.
    /// </summary>
    [RequireComponent(typeof(UnitAttacker))]
    public sealed class UnitAbilities : MonoBehaviour
    {
        // A blast point is on the ground; sight to it is judged as if a unit stood there (its pivot is 1 m up).
        const float GroundAimHeight = 1f;

        [SerializeField] List<AbilityDefinition> abilities = new List<AbilityDefinition>();
        // Who is on which side. Without it nothing is hostile and only the caster itself counts as friendly.
        [SerializeField] Encounter encounter;

        readonly List<float> readyAt = new List<float>();
        readonly List<Health> areaBuffer = new List<Health>();
        Health ownHealth;
        UnitAttacker attacker;

        public int Count => abilities.Count;

        /// <summary>The definition in a slot, or null for an empty or out-of-range slot.</summary>
        public AbilityDefinition Definition(int slot) => slot >= 0 && slot < abilities.Count ? abilities[slot] : null;

        /// <summary>The slot holding this definition, or -1.</summary>
        public int IndexOf(AbilityDefinition ability) => ability == null ? -1 : abilities.IndexOf(ability);

        /// <summary>False once this unit's Health (if it has one) has died.</summary>
        public bool IsAlive => OwnHealth == null || OwnHealth.IsAlive;

        /// <summary>Seconds of scaled time until the slot is ready; 0 when ready or when there is no such slot.</summary>
        public float CooldownRemaining(int slot)
        {
            if (slot < 0 || slot >= abilities.Count)
                return 0f;
            EnsureSlots();
            return Mathf.Max(0f, readyAt[slot] - Time.time);
        }

        public bool IsReady(int slot) => CooldownRemaining(slot) <= 0f;

        /// <summary>The reason of the most recent refusal or failure (None before any).</summary>
        public AbilityFailure LastFailure { get; private set; }

        public AbilityDefinition LastFailedAbility { get; private set; }

        /// <summary>Unscaled time of the last failure, so the debug HUD can show it for a few seconds even while paused.</summary>
        public float LastFailureTime { get; private set; }

        /// <summary>Successful uses since creation (debug counter).</summary>
        public int UsedCount { get; private set; }

        /// <summary>Raised after an ability was used (also when cover turned its single shot away).</summary>
        public event Action<AbilityDefinition> Used;

        /// <summary>Raised whenever a use or an order was refused or failed, with the reason.</summary>
        public event Action<AbilityDefinition, AbilityFailure> Failed;

        /// <summary>Raised when cover turned a unit-targeted or area hit away.</summary>
        public event Action<AbilityDefinition, Health> Missed;

        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        internal void Initialize(Encounter currentEncounter, params AbilityDefinition[] definitions)
        {
            encounter = currentEncounter;
            abilities.Clear();
            abilities.AddRange(definitions);
            readyAt.Clear();
        }

        /// <summary>
        /// Validates one use. Unit abilities take a target and no point; ground abilities take a point (null = none yet)
        /// and no target. Full checks everything; Static only the checks that do not depend on where the caster will
        /// be (for an order queued behind others). The reported distance, sight and cover are filled in whenever there
        /// is something to measure, so a preview can show them even for a refused use.
        /// </summary>
        public AbilityCheck Check(AbilityDefinition ability, Health target, Vector3? point, AbilityCheckScope scope = AbilityCheckScope.Full)
        {
            var slot = IndexOf(ability);
            var unitMode = ability != null && ability.TargetMode == AbilityTargetMode.Unit;
            var hasTarget = unitMode && target != null;
            var hasPosition = ability != null && !unitMode && point.HasValue;
            var aim = hasTarget ? target.transform.position : hasPosition ? point.Value : transform.position;
            var distance = hasTarget || hasPosition ? CoverRules.FlatDistance(transform.position, aim) : 0f;

            var sightClear = true;
            var inCover = false;
            var hitChance = 1f;
            if (hasTarget)
            {
                sightClear = Attacker.HasLineOfSight(target);
                inCover = ability.CoverRule == AbilityCoverRule.Applies && IsCovered(target, transform.position, out hitChance);
            }
            else if (hasPosition)
            {
                sightClear = Attacker.HasLineOfSightToPoint(aim + Vector3.up * GroundAimHeight);
            }

            var facts = new AbilityFacts(IsAlive, slot >= 0, hasTarget, hasTarget && IsActive(target),
                hasTarget && IsOnRequiredSide(ability, target), hasPosition, CooldownRemaining(slot), distance);
            var failure = AbilityRules.CheckBasics(ability, facts, scope);
            if (failure == AbilityFailure.None && scope == AbilityCheckScope.Full)
                failure = AbilityRules.CheckSight(ability, sightClear);
            return new AbilityCheck(failure, distance, sightClear, inCover, hitChance);
        }

        /// <summary>
        /// Validates in full and, when valid, uses the ability: faces the aim, starts the cooldown, applies the effect.
        /// A refusal records its reason (LastFailure, Failed) and costs nothing. Returns whether it was used.
        /// </summary>
        public bool TryUse(AbilityCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            var ability = command.Definition;
            var check = CheckCommand(command, AbilityCheckScope.Full);
            if (!check.IsValid)
            {
                RecordFailure(ability, check.Failure);
                return false;
            }

            var aim = command.AimPoint;
            FaceTowards(aim);
            EnsureSlots();
            readyAt[abilities.IndexOf(ability)] = Time.time + ability.Cooldown;
            Apply(ability, command.Target, aim);
            UsedCount++;
            Used?.Invoke(ability);
            return true;
        }

        /// <summary>The order would start now: the full check passes. Records the reason when it does not.</summary>
        public bool CanStartNow(AbilityCommand command) => Accept(command, AbilityCheckScope.Full);

        /// <summary>The order may wait behind others: only who and what are checked. Records the reason when it fails.</summary>
        public bool CanQueue(AbilityCommand command) => Accept(command, AbilityCheckScope.Static);

        /// <summary>Records a failure found outside this class (a click on nothing), so it shows like any other.</summary>
        public void ReportFailure(AbilityDefinition ability, AbilityFailure failure) => RecordFailure(ability, failure);

        /// <summary>
        /// The living, active units hostile to this unit whose pivot is inside the blast radius at `center`. Used by the
        /// effect and by previews, so both always agree. Empty without an encounter.
        /// </summary>
        public void CollectArea(AbilityDefinition ability, Vector3 center, List<Health> into)
        {
            into.Clear();
            if (ability == null || encounter == null)
                return;
            var opponents = encounter.OpponentsOf(OwnHealth);
            for (var i = 0; i < opponents.Count; i++)
            {
                var candidate = opponents[i];
                if (candidate != null && IsActive(candidate) && AbilityRules.IsInArea(center, candidate.transform.position, ability.Radius))
                    into.Add(candidate);
            }
        }

        AbilityCheck CheckCommand(AbilityCommand command, AbilityCheckScope scope)
        {
            var ability = command.Definition;
            var unitMode = ability.TargetMode == AbilityTargetMode.Unit;
            return Check(ability, unitMode ? command.Target : null, unitMode ? (Vector3?)null : command.Point, scope);
        }

        bool Accept(AbilityCommand command, AbilityCheckScope scope)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            var check = CheckCommand(command, scope);
            if (check.IsValid)
                return true;
            RecordFailure(command.Definition, check.Failure);
            return false;
        }

        void Apply(AbilityDefinition ability, Health target, Vector3 aim)
        {
            var source = OwnHealth;
            switch (ability.Effect)
            {
                case AbilityEffect.Heal:
                    target.Heal(ability.Amount);
                    break;
                case AbilityEffect.Damage when ability.TargetMode == AbilityTargetMode.Unit:
                    Hit(ability, target, transform.position, source);
                    break;
                case AbilityEffect.Damage:
                    // A snapshot first: a victim dying (and deactivating) mid-blast must not change who else is hit.
                    CollectArea(ability, aim, areaBuffer);
                    for (var i = 0; i < areaBuffer.Count; i++)
                        Hit(ability, areaBuffer[i], aim, source);
                    break;
            }
        }

        // The effect on one unit: cover (when the ability says it applies) may turn it away; otherwise it takes the damage.
        void Hit(AbilityDefinition ability, Health victim, Vector3 from, Health source)
        {
            var lands = true;
            if (ability.CoverRule == AbilityCoverRule.Applies && victim.TryGetComponent<UnitCover>(out var cover)
                && cover.IsProtectedFrom(from))
                lands = CoverRules.ResolveHit(true, cover.HitChance, Attacker.RollHit());
            if (!lands)
            {
                Missed?.Invoke(ability, victim);
                return;
            }
            victim.TakeDamage(ability.Amount, source);
        }

        static bool IsCovered(Health target, Vector3 from, out float hitChance)
        {
            hitChance = 1f;
            if (!target.TryGetComponent<UnitCover>(out var cover) || !cover.IsProtectedFrom(from))
                return false;
            hitChance = cover.HitChance;
            return true;
        }

        static bool IsActive(Health unit) => unit.IsAlive && unit.gameObject.activeInHierarchy;

        // Hostile: opposite sides. Friendly: the same side, or the caster itself (also without an encounter).
        bool IsOnRequiredSide(AbilityDefinition ability, Health target)
        {
            var self = OwnHealth;
            if (ability.TargetSide == AbilityTargetSide.Friendly)
                return (self != null && target == self) || (encounter != null && encounter.AreAllied(self, target));
            return encounter != null && encounter.AreHostile(self, target);
        }

        void RecordFailure(AbilityDefinition ability, AbilityFailure failure)
        {
            LastFailure = failure;
            LastFailedAbility = ability;
            LastFailureTime = Time.unscaledTime;
            Failed?.Invoke(ability, failure);
        }

        void EnsureSlots()
        {
            while (readyAt.Count < abilities.Count)
                readyAt.Add(0f);
        }

        void FaceTowards(Vector3 point)
        {
            var direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}
