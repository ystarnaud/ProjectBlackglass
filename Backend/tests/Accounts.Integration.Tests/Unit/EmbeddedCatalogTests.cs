using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class EmbeddedCatalogTests
{
    [Fact]
    public void Embedded_catalog_starts_with_the_foundation()
    {
        Assert.Equal("0000_foundation.sql", MigrationCatalog.Embedded.Scripts[0].FileName);
        Assert.Equal(MigrationCatalog.Embedded.Scripts.Count - 1, MigrationCatalog.Embedded.LatestVersion);
    }
}
