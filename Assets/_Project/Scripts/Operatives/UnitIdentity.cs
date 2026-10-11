using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Ties a mission unit to a persistent operative. The unit is disposable and keeps no progression: the operative's
    /// definition and state live in the SquadRoster, and this component only remembers which member it is and applies the
    /// member's effective configuration, with its equipment when a SquadInventory is bound, to the unit's own components
    /// (Health, UnitMover, UnitAttacker, UnitAbilities, and the UnitWeaponVisual model).
    /// While the unit is active it re-applies whenever the roster reports a change to its operative, so a level-up or
    /// a pick shows in a running mission. A deactivated (dead) unit hears nothing and is not re-configured.
    /// </summary>
    public sealed class UnitIdentity : MonoBehaviour
    {
        SquadRoster roster;
        RosterMember member;
        SquadInventory inventory;
        InventorySession watchedSession;
        bool subscribed;

        public string OperativeId => member != null ? member.Id : string.Empty;
        public OperativeDefinition Definition => member != null ? member.Definition : null;
        public PersistentOperativeState State => member != null ? member.State : null;
        public string DisplayName => member != null ? member.Definition.DisplayName : name;
        public string RoleName => member != null && member.Definition.Role != null ? member.Definition.Role.DisplayName : "-";
        /// <summary>The configuration last applied to this unit.</summary>
        public EffectiveConfiguration Effective { get; private set; }
        public bool HasConfiguration { get; private set; }
        /// <summary>The squad inventory this unit's equipment comes from, or null (no equipment system).</summary>
        public SquadInventory Inventory => inventory;

        /// <summary>Binds the unit to a roster member and applies its configuration now. Safe on an inactive object.</summary>
        internal void Bind(SquadRoster squad, RosterMember operative, SquadInventory items = null)
        {
            Unsubscribe();
            roster = squad;
            member = operative;
            inventory = items;
            Reapply();
            if (isActiveAndEnabled)
                Subscribe();
        }

        void OnEnable() => Subscribe();

        void OnDisable() => Unsubscribe();

        void Subscribe()
        {
            if (subscribed || roster == null)
                return;
            roster.Changed += OnRosterChanged;
            // A swap of gear in a running mission (the timed swap order) shows at once: the new weapon, armor and modifiers.
            watchedSession = inventory != null ? inventory.Core : null;
            if (watchedSession != null)
                watchedSession.EquipmentChanged += OnRosterChanged;
            subscribed = true;
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            if (roster != null)
                roster.Changed -= OnRosterChanged;
            if (watchedSession != null)
                watchedSession.EquipmentChanged -= OnRosterChanged;
            watchedSession = null;
            subscribed = false;
        }

        void OnRosterChanged(string operativeId)
        {
            if (member != null && operativeId == member.Id)
                Reapply();
        }

        void Reapply()
        {
            if (roster == null || member == null)
                return;
            // Recomputed from the roster state and the unit's equipment every time; never added to the last result.
            var equipped = inventory != null ? inventory.EquippedFor(member.Id) : default;
            Effective = roster.Evaluate(member, equipped);
            HasConfiguration = true;
            Apply(Effective, equipped);
        }

        void Apply(EffectiveConfiguration config, EquippedItems equipped)
        {
            if (TryGetComponent<Health>(out var health))
                health.SetMax(config.MaxHealth);
            if (TryGetComponent<UnitMover>(out var mover))
                mover.SetSpeed(config.MoveSpeed);
            if (TryGetComponent<UnitAttacker>(out var attacker))
                attacker.ApplyEffective(config.AttackRole, config.AttackRange, config.AttackDamage, config.AttackInterval, config.HasWeapon);
            if (TryGetComponent<UnitAbilities>(out var abilities))
                abilities.SetModifiers(config.AbilityPower, config.AbilityCooldownMultiplier);
            if (equipped.IsSet && TryGetComponent<UnitWeaponVisual>(out var visual))
                visual.Show(equipped.Weapon != null ? equipped.Weapon.WeaponVisual : null);
        }
    }
}
