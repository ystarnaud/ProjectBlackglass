namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>
/// Rules a catalogue must meet to be newly registered, on top of format 2's reading rules (CatalogueValidator, which
/// GameCatalogue.Load keeps applying unchanged to every stored version, so a historical catalogue never becomes unreadable).
/// CatalogueHistoryStore.RegisterAsync applies them only when it inserts a new version; Unity's exporter refuses the same.
/// </summary>
public static class CatalogueRegistrationRules
{
    /// <summary>Every problem, in item order; an empty list means the catalogue may be registered.</summary>
    public static IReadOnlyList<string> Check(CatalogueDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var problems = new List<string>();
        for (var i = 0; i < document.Items.Count; i++)
        {
            var item = document.Items[i];
            // Stage 11, owner ruling D7: format-v1 manifests report consumption per definition (V8 needs a stackable
            // consumable), so a consumable that doesn't stack could never be used without a ledger mismatch.
            if (item.Category == ItemCategory.Consumable && !item.Stackable)
                problems.Add($"incompatible: items[{i}] '{item.Id}': a consumable must be stackable (registration rule, Stage 11)");
        }
        return problems;
    }
}
