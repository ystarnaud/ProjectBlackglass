using System.Text;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Support;

public static class Catalogs
{
    public static MigrationCatalog Of(params (string FileName, string Sql)[] files) =>
        MigrationCatalog.Parse(files.Select(f => (f.FileName, Encoding.UTF8.GetBytes(f.Sql))));

    public static MigrationCatalog RealPlus(params (string FileName, string Sql)[] extra) =>
        MigrationCatalog.Parse(MigrationCatalog.EmbeddedFiles().Concat(extra.Select(f => (f.FileName, Encoding.UTF8.GetBytes(f.Sql)))));
}
