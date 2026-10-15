using Blackglass.Accounts.IntegrationTests.Support;
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

    [Fact]
    public void Next_version_files_follow_the_real_catalog()
    {
        var latest = MigrationCatalog.Embedded.LatestVersion;
        Assert.Equal($"{latest + 1:D4}_probe.sql", Catalogs.NextVersionFile("probe"));
        Assert.Equal($"{latest + 2:D4}_probe_more.sql", Catalogs.NextVersionFile("probe_more", 2));
        // A real catalog plus the derived files always parses (a hard-coded number would collide once that version exists).
        Assert.Equal(latest + 2, Catalogs.RealPlus((Catalogs.NextVersionFile("probe"), "select 1;"), (Catalogs.NextVersionFile("probe_more", 2), "select 2;")).LatestVersion);
    }
}
