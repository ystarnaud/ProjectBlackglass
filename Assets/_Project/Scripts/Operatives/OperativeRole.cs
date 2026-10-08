using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A role/specialisation as shared, immutable data: a name and a small bonus every operative of the role gets. Roles are
    /// data, not code, so a new or hybrid role needs no new class. Holds no runtime state.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Operative Role", fileName = "Role")]
    public sealed class OperativeRole : ScriptableObject
    {
        [SerializeField] string displayName = "Role";
        [SerializeField, TextArea] string description = "";
        [SerializeField] StatModifiers bonus;

        public string DisplayName => displayName;
        public string Description => description;
        public StatModifiers Bonus => bonus;

        internal static OperativeRole Create(string displayName, string description, StatModifiers bonus)
        {
            var role = CreateInstance<OperativeRole>();
            role.displayName = displayName;
            role.description = description;
            role.bonus = bonus;
            return role;
        }
    }
}
