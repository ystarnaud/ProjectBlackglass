using System.Security.Cryptography;
using Blackglass.Accounts.Domain.Catalogue;
using Xunit.Abstractions;

namespace Blackglass.Accounts.Domain.Tests.Catalogue;

public class CatalogueLoadTests(ITestOutputHelper output)
{
    static string Committed => Path.Combine(AppContext.BaseDirectory, "catalogue", "catalogue.json");

    [Fact]
    public void The_golden_vector_loads_with_the_shared_version()
    {
        var catalogue = GameCatalogue.Load(Golden.Bytes());
        Assert.Equal(Golden.Version(), catalogue.Version);
        Assert.Equal(Golden.Bytes(), CanonicalCatalogueJson.ToBytes(catalogue.Document));
    }

    [Fact]
    public void The_committed_catalogue_loads_and_its_version_is_the_sha256_of_the_file()
    {
        var bytes = File.ReadAllBytes(Committed);
        var catalogue = GameCatalogue.Load(bytes);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), catalogue.Version);
        output.WriteLine($"catalogueVersion (committed file): {catalogue.Version}");
    }

    [Fact]
    public void The_embedded_catalogue_is_the_committed_file()
        => Assert.Equal(File.ReadAllBytes(Committed), GameCatalogue.EmbeddedBytes());

    [Fact]
    public void The_embedded_catalogue_loads()
        => Assert.Equal(GameCatalogue.Load(File.ReadAllBytes(Committed)).Version, GameCatalogue.Embedded.Version);

    [Fact]
    public void Lookups_find_items_and_operatives_by_id()
    {
        var catalogue = GameCatalogue.Load(Golden.Bytes());
        Assert.Equal(3, catalogue.FindItem("item.gamma-kit")!.MaxStack);
        Assert.Null(catalogue.FindItem("item.unknown"));
        Assert.Equal("role.beta", catalogue.FindOperative(Golden.Two)!.RoleId);
        Assert.Null(catalogue.FindOperative("00000000-0000-4000-8000-000000000009"));
    }
}
