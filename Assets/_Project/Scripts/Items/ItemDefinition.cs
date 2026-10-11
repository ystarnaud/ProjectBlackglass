using UnityEngine;

namespace Blackglass
{
    public enum ItemCategory { Weapon, Armor, Utility, Consumable }

    /// <summary>The equipment slot an item fits; None for items that are only carried (consumables).</summary>
    public enum ItemSlot { None, Weapon, Armor, Utility }

    /// <summary>
    /// A kind of item as shared, immutable data: a stable authored id (never the display name), a category, the slot it
    /// equips to, how many fit in one stack, passive stat modifiers, the combat archetype a weapon brings, the healing a
    /// consumable gives, and optional presentation (icon, weapon model). Holds no quantity, owner, cooldown or mission
    /// state: owned items are ItemEntry values in an ItemInventory.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Item", fileName = "Item")]
    public sealed class ItemDefinition : ScriptableObject
    {
        [SerializeField] string id = "";
        [SerializeField] string displayName = "Item";
        [SerializeField, TextArea] string description = "";
        [SerializeField] ItemCategory category;
        [SerializeField] ItemSlot slot;
        [SerializeField, Min(1)] int maxStack = 1;
        [SerializeField] StatModifiers modifiers;
        [SerializeField] CombatArchetype weapon;
        [SerializeField, Min(0)] int healAmount;
        [SerializeField] Sprite icon;
        [SerializeField] WeaponVisualAsset weaponVisual;

        public string Id => id;
        public string DisplayName => displayName;
        public string Description => description;
        public ItemCategory Category => category;
        public ItemSlot Slot => slot;
        public int MaxStack => maxStack;
        public bool IsStackable => maxStack > 1;
        public StatModifiers Modifiers => modifiers;
        public CombatArchetype Weapon => weapon;
        public int HealAmount => healAmount;
        public Sprite Icon => icon;
        public WeaponVisualAsset WeaponVisual => weaponVisual;

        /// <summary>False with the first problem found (what to fix in the asset).</summary>
        public bool IsValid(out string problem)
        {
            if (string.IsNullOrWhiteSpace(id))
                problem = "the id is blank";
            else if (string.IsNullOrWhiteSpace(displayName))
                problem = "the display name is blank";
            else if (maxStack < 1)
                problem = "the maximum stack is below 1";
            else if (category == ItemCategory.Weapon && slot != ItemSlot.Weapon)
                problem = "a weapon must use the Weapon slot";
            else if (category == ItemCategory.Weapon && weapon == null)
                problem = "a weapon needs a combat archetype";
            else if (category == ItemCategory.Armor && slot != ItemSlot.Armor)
                problem = "armor must use the Armor slot";
            else if (category == ItemCategory.Utility && slot != ItemSlot.Utility)
                problem = "a utility item must use the Utility slot";
            else if (category == ItemCategory.Consumable && slot != ItemSlot.None)
                problem = "a consumable has no equipment slot";
            else if (category == ItemCategory.Consumable && healAmount < 1)
                problem = "a consumable needs a heal amount of at least 1";
            else if (category != ItemCategory.Consumable && maxStack != 1)
                problem = "equipment cannot stack";
            else if (category != ItemCategory.Weapon && weapon != null)
                problem = "only a weapon carries a combat archetype";
            else
                problem = null;
            return problem == null;
        }

        internal static ItemDefinition Create(string id, string displayName, ItemCategory category, ItemSlot slot, int maxStack = 1,
            StatModifiers modifiers = default, CombatArchetype weapon = null, int healAmount = 0)
        {
            var item = CreateInstance<ItemDefinition>();
            item.id = id;
            item.displayName = displayName;
            item.category = category;
            item.slot = slot;
            item.maxStack = maxStack;
            item.modifiers = modifiers;
            item.weapon = weapon;
            item.healAmount = healAmount;
            item.name = displayName;
            return item;
        }

        internal void SetWeaponVisual(WeaponVisualAsset visual) => weaponVisual = visual;

        internal void SetDescription(string text) => description = text ?? string.Empty;
    }
}
