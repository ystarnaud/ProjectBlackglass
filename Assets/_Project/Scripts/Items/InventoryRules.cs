using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The authored inventory rules the backend catalogue carries (format 2, inventory.bagCapacity): how many entries one
    /// operative's bag holds. The backend validates the exported value and holds no constant of its own. The offline
    /// scene's SquadInventory keeps its own serialized bagCapacity; InventoryRulesSceneTests fails if the two drift apart.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Inventory Rules", fileName = "InventoryRules")]
    public sealed class InventoryRules : ScriptableObject
    {
        /// <summary>The range the backend accepts for the exported capacity.</summary>
        public const int MinBagCapacity = 1;
        public const int MaxBagCapacity = 64;

        [SerializeField, Min(1)] int bagCapacity = 8;

        public int BagCapacity => bagCapacity;

        internal static InventoryRules Create(int bagCapacity)
        {
            var asset = CreateInstance<InventoryRules>();
            asset.bagCapacity = bagCapacity;
            return asset;
        }
    }
}
