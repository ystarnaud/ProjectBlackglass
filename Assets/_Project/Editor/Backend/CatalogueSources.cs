using System.Collections.Generic;
using UnityEditor;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// The authored assets the backend catalogue is exported from. LoadProject reads the project's fixed paths and every
    /// OperativeDefinition under the definitions folder; a missing asset stays null and CatalogueBuilder reports it.
    /// </summary>
    public sealed class CatalogueSources
    {
        public const string ItemCataloguePath = "Assets/_Project/Data/Items/ItemCatalogue.asset";
        public const string LootTablePath = "Assets/_Project/Data/Items/LootTable.asset";
        public const string StarterPath = "Assets/_Project/Data/Items/StarterLoadout.asset";
        public const string ProgressionPath = "Assets/_Project/Data/Operatives/Progression.asset";
        public const string OperativeFolder = "Assets/_Project/Data/Operatives/Definitions";

        public ItemCatalogue Items;
        public LootTable LootTable;
        public ProgressionTrack Progression;
        public StarterLoadout Starter;
        public readonly List<OperativeDefinition> Operatives = new List<OperativeDefinition>();

        public static CatalogueSources LoadProject()
        {
            var sources = new CatalogueSources
            {
                Items = AssetDatabase.LoadAssetAtPath<ItemCatalogue>(ItemCataloguePath),
                LootTable = AssetDatabase.LoadAssetAtPath<LootTable>(LootTablePath),
                Progression = AssetDatabase.LoadAssetAtPath<ProgressionTrack>(ProgressionPath),
                Starter = AssetDatabase.LoadAssetAtPath<StarterLoadout>(StarterPath),
            };
            foreach (var guid in AssetDatabase.FindAssets("t:OperativeDefinition", new[] { OperativeFolder }))
                sources.Operatives.Add(AssetDatabase.LoadAssetAtPath<OperativeDefinition>(AssetDatabase.GUIDToAssetPath(guid)));
            return sources;
        }
    }
}
