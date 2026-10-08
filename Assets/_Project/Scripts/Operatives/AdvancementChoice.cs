using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One thing an operative can pick when it reaches a new rank, as shared, immutable data. The id is the stable key a
    /// persistent state stores (never the display name or the asset reference), so a choice can be renamed freely.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Advancement Choice", fileName = "Choice")]
    public sealed class AdvancementChoice : ScriptableObject
    {
        [SerializeField] string id = "choice";
        [SerializeField] string displayName = "Choice";
        [SerializeField, TextArea] string description = "";
        [SerializeField] StatModifiers modifiers;

        public string Id => id;
        public string DisplayName => displayName;
        public string Description => description;
        public StatModifiers Modifiers => modifiers;

        internal static AdvancementChoice Create(string id, string displayName, string description, StatModifiers modifiers)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A choice needs a stable id.", nameof(id));
            var choice = CreateInstance<AdvancementChoice>();
            choice.id = id;
            choice.displayName = displayName;
            choice.description = description;
            choice.modifiers = modifiers;
            return choice;
        }
    }
}
