using System.Collections.Generic;
using System.Globalization;

namespace Blackglass
{
    /// <summary>The few words the inventory panel shows about an item, as pure functions of the definition (placeholder text, no art).</summary>
    public static class ItemText
    {
        static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        public static string Describe(ItemDefinition item)
        {
            if (item == null)
                return string.Empty;
            switch (item.Category)
            {
                case ItemCategory.Weapon:
                    var w = item.Weapon;
                    return $"{w.Role} weapon\nDamage {w.Damage}   Range {N(w.Range)} m   Interval {N(w.AttackInterval)} s";
                case ItemCategory.Consumable:
                    return $"Restores {item.HealAmount} health to the user.\nNot used at full health.";
                default:
                    return Modifiers(item.Modifiers);
            }
        }

        /// <summary>What swapping to `candidate` would change, against what is in the same slot now (null = nothing). Empty for items that do not equip.</summary>
        public static string Compare(ItemDefinition candidate, ItemDefinition current)
        {
            if (candidate == null || candidate.Slot == ItemSlot.None)
                return string.Empty;
            if (candidate.Category == ItemCategory.Weapon)
            {
                var a = current != null ? current.Weapon : null;
                var b = candidate.Weapon;
                return $"vs {(current != null ? current.DisplayName : "none")}\n" +
                       $"Damage {(a != null ? a.Damage.ToString(CultureInfo.InvariantCulture) : "-")} -> {b.Damage}\n" +
                       $"Range {(a != null ? N(a.Range) : "-")} -> {N(b.Range)} m\n" +
                       $"Interval {(a != null ? N(a.AttackInterval) : "-")} -> {N(b.AttackInterval)} s";
            }
            var now = current != null ? Modifiers(current.Modifiers) : string.Empty;
            return $"{Modifiers(candidate.Modifiers)}\nnow: {(current != null ? current.DisplayName : "none")}{(now.Length > 0 ? " (" + now.Replace("\n", ", ") + ")" : string.Empty)}";
        }

        public static string Modifiers(StatModifiers m)
        {
            var lines = new List<string>();
            if (m.maxHealth != 0)
                lines.Add($"{Sign(m.maxHealth)}{m.maxHealth} max health");
            if (m.moveSpeed != 0f)
                lines.Add($"{Sign(m.moveSpeed)}{N(m.moveSpeed)} m/s speed");
            if (m.attackDamage != 0f)
                lines.Add($"{Sign(m.attackDamage)}{N(m.attackDamage * 100f)}% attack damage");
            if (m.abilityPower != 0f)
                lines.Add($"{Sign(m.abilityPower)}{N(m.abilityPower * 100f)}% ability power");
            if (m.abilityCooldownReduction != 0f)
                lines.Add($"-{N(m.abilityCooldownReduction * 100f)}% ability cooldown");
            return string.Join("\n", lines);
        }

        static string Sign(float value) => value >= 0f ? "+" : string.Empty;
    }
}
