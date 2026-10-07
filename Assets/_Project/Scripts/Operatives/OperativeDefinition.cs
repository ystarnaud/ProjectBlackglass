using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Who an operative is, as shared, immutable data: a stable id, a display name (changeable; never a key), a role, the
    /// complete unit prefab that represents them in a mission (replace it to change their model; their id, XP and picks
    /// live in PersistentOperativeState and are untouched), base health and speed, a weapon archetype and starting
    /// abilities. Holds no runtime state, ever: several operatives may share a role, archetype or ability asset.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Operative", fileName = "Operative")]
    public sealed class OperativeDefinition : ScriptableObject
    {
        public const int MaxAbilities = 4;

        // Authored once and never changed afterwards: a GUID string. A duplicated asset keeps the old id, so use
        // "Generate new id" on a copy; the roster refuses duplicates loudly.
        [SerializeField] string id = "";
        [SerializeField] string displayName = "Operative";
        [SerializeField] OperativeRole role;
        [SerializeField] GameObject unitPrefab;
        [SerializeField] Sprite portrait;
        [SerializeField, Min(1)] int baseMaxHealth = 100;
        [SerializeField, Min(0.5f)] float baseMoveSpeed = 5f;
        [SerializeField] CombatArchetype archetype;
        [SerializeField] AbilityDefinition[] abilities = new AbilityDefinition[0];

        public string Id => id;
        public string DisplayName => displayName;
        public OperativeRole Role => role;
        public GameObject UnitPrefab => unitPrefab;
        public Sprite Portrait => portrait;
        public int BaseMaxHealth => baseMaxHealth;
        public float BaseMoveSpeed => baseMoveSpeed;
        public CombatArchetype Archetype => archetype;
        public AbilityDefinition[] Abilities => abilities;

        /// <summary>False with the first problem found (what to fix in the asset).</summary>
        public bool IsValid(out string problem)
        {
            if (string.IsNullOrWhiteSpace(id))
                problem = "the id is blank";
            else if (string.IsNullOrWhiteSpace(displayName))
                problem = "the display name is blank";
            else if (role == null)
                problem = "there is no role";
            else if (unitPrefab == null)
                problem = "there is no unit prefab";
            else if (archetype == null)
                problem = "there is no combat archetype";
            else if (baseMaxHealth < 1)
                problem = "base health is below 1";
            else if (abilities == null || abilities.Length > MaxAbilities)
                problem = $"there are more than {MaxAbilities} abilities";
            else if (Array.IndexOf(abilities, null) >= 0)
                problem = "an ability slot is empty";
            else
                problem = null;
            return problem == null;
        }

#if UNITY_EDITOR
        [ContextMenu("Generate new id")]
        void GenerateNewId()
        {
            id = Guid.NewGuid().ToString();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        internal static OperativeDefinition Create(string id, string displayName, OperativeRole role, GameObject unitPrefab,
            int baseMaxHealth, float baseMoveSpeed, CombatArchetype archetype, AbilityDefinition[] abilities)
        {
            var definition = CreateInstance<OperativeDefinition>();
            definition.id = id;
            definition.displayName = displayName;
            definition.role = role;
            definition.unitPrefab = unitPrefab;
            definition.baseMaxHealth = baseMaxHealth;
            definition.baseMoveSpeed = baseMoveSpeed;
            definition.archetype = archetype;
            definition.abilities = abilities ?? new AbilityDefinition[0];
            return definition;
        }
    }
}
