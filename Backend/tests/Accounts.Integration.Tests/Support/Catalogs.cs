using System.Text;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Support;

public static class Catalogs
{
    public static MigrationCatalog Of(params (string FileName, string Sql)[] files) =>
        MigrationCatalog.Parse(files.Select(f => (f.FileName, Encoding.UTF8.GetBytes(f.Sql))));

    /// <summary>A test migration numbered <paramref name="offset"/> after the real catalog, so it never collides with a real migration.</summary>
    public static string NextVersionFile(string name, int offset = 1) => $"{MigrationCatalog.Embedded.LatestVersion + offset:D4}_{name}.sql";

    public static MigrationCatalog RealPlus(params (string FileName, string Sql)[] extra) =>
        MigrationCatalog.Parse(MigrationCatalog.EmbeddedFiles().Concat(extra.Select(f => (f.FileName, Encoding.UTF8.GetBytes(f.Sql)))));
}
