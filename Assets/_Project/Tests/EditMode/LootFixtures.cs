using UnityEditor;

namespace Blackglass.Tests
{
    /// <summary>The shipped loot data and the pure generation steps MissionDirector runs before building (layout, objectives, security, loot).</summary>
    internal static class LootFixtures
    {
        public const string LootTablePath = "Assets/_Project/Data/Items/LootTable.asset";
        public const string CataloguePath = "Assets/_Project/Data/Items/ItemCatalogue.asset";

        public static LootTable ShippedTable => AssetDatabase.LoadAssetAtPath<LootTable>(LootTablePath);
        public static ItemCatalogue ShippedCatalogue => AssetDatabase.LoadAssetAtPath<ItemCatalogue>(CataloguePath);

        /// <summary>The first attempt whose layout, objectives and security place, then its loot plan. False when none does.</summary>
        public static bool TryPlan(int seed, LootTable table, out LootPlan plan) => TryPlan(new MissionSettings { seed = seed }, table, out plan, out _);

        public static bool TryPlan(MissionSettings settings, LootTable table, out LootPlan plan, out MissionLayout layout)
        {
            var request = settings.Validated();
            for (var attempt = 1; attempt <= request.maxAttempts; attempt++)
            {
                if (!MissionGenerator.TryAttempt(request, attempt, out layout, out _)) continue;
                if (!ObjectivePlacer.TryPlace(layout, request, out var objectives, out _)) continue;
                if (!SecurityPlacer.TryPlace(layout, objectives, request, out var security, out _)) continue;
                plan = LootPlacer.Place(layout, objectives, security, table);
                return true;
            }
            plan = null;
            layout = null;
            return false;
        }
    }
}
