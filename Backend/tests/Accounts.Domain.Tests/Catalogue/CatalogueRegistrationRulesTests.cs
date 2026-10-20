using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Catalogue;

/// <summary>
/// Stage 11, owner ruling D7: a consumable must be stackable in a newly registered catalogue (format-v1 manifests report
/// consumption per definition), while GameCatalogue.Load keeps format 2's reading rules so every stored version stays readable.
/// </summary>
public class CatalogueRegistrationRulesTests
{
    static CatalogueDocument WithNonStackableKit()
    {
        var d = Golden.Document();
        return d with { Items = [.. d.Items.Select(i => i.Id == "item.gamma-kit" ? i with { Stackable = false, MaxStack = 1 } : i)] };
    }

    [Fact]
    public void The_golden_and_committed_catalogues_pass()
    {
        Assert.Empty(CatalogueRegistrationRules.Check(GameCatalogue.Load(Golden.Bytes()).Document));
        Assert.Empty(CatalogueRegistrationRules.Check(GameCatalogue.Embedded.Document));
    }

    [Fact]
    public void A_non_stackable_consumable_is_refused_for_registration() =>
        Assert.Equal(["incompatible: items[3] 'item.gamma-kit': a consumable must be stackable (registration rule, Stage 11)"],
            CatalogueRegistrationRules.Check(WithNonStackableKit()));

    [Fact]
    public void A_historical_catalogue_with_one_still_loads_under_its_original_rules()
    {
        var bytes = CanonicalCatalogueJson.ToBytes(WithNonStackableKit());
        var loaded = GameCatalogue.Load(bytes);
        Assert.Equal(CatalogueVersion.Of(bytes), loaded.Version);
        Assert.False(loaded.FindItem("item.gamma-kit")!.Stackable);
    }
}
