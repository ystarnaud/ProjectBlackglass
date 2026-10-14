using System.Security.Cryptography;
using System.Text;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class MigrationCatalogTests
{
    static (string, byte[]) File(string name, string text) => (name, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Parses_and_orders_by_version()
    {
        var catalog = MigrationCatalog.Parse([File("0001_b.sql", "select 2;"), File("0000_a.sql", "select 1;")]);
        Assert.Equal([0, 1], catalog.Scripts.Select(s => s.Version));
        Assert.Equal("a", catalog.Scripts[0].Name);
        Assert.Equal("0000_a.sql", catalog.Scripts[0].FileName);
        Assert.Equal(1, catalog.LatestVersion);
    }

    [Fact]
    public void Checksum_is_sha256_of_the_normalized_text()
    {
        var script = MigrationCatalog.Parse([File("0000_a.sql", "select 1;\n")]).Scripts[0];
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("select 1;\n")), script.Checksum);
    }

    [Fact]
    public void Crlf_and_bom_do_not_change_the_checksum_or_the_sql()
    {
        var lf = MigrationCatalog.Parse([File("0000_a.sql", "create table t (id int);\nselect 1;\n")]).Scripts[0];
        var crlfBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("create table t (id int);\r\nselect 1;\r\n")).ToArray();
        var crlf = MigrationCatalog.Parse([("0000_a.sql", crlfBytes)]).Scripts[0];
        Assert.Equal(lf.Checksum, crlf.Checksum);
        Assert.Equal(lf.Sql, crlf.Sql);
    }

    [Theory]
    [InlineData("1_a.sql")]
    [InlineData("0000-a.sql")]
    [InlineData("0000_A.sql")]
    [InlineData("0000_a.SQL")]
    [InlineData("0000_.sql")]
    [InlineData("0000_a b.sql")]
    public void Refuses_badly_named_files(string name)
    {
        var e = Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File(name, "select 1;")]));
        Assert.Contains(name, e.Message);
    }

    [Fact]
    public void Refuses_a_gap()
    {
        var e = Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File("0000_a.sql", "select 1;"), File("0002_c.sql", "select 3;")]));
        Assert.Contains("0001", e.Message);
    }

    [Fact]
    public void Refuses_a_catalog_that_does_not_start_at_0000()
    {
        Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File("0001_a.sql", "select 1;")]));
    }

    [Fact]
    public void Refuses_a_duplicate_version()
    {
        var e = Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File("0000_a.sql", "select 1;"), File("0000_b.sql", "select 2;")]));
        Assert.Contains("0000", e.Message);
    }

    [Fact]
    public void Refuses_empty_and_whitespace_files()
    {
        Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([File("0000_a.sql", " \r\n\t")]));
    }

    [Fact]
    public void Refuses_invalid_utf8()
    {
        Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([("0000_a.sql", new byte[] { 0x73, 0xC3, 0x28 })]));
    }

    [Fact]
    public void Refuses_an_empty_catalog()
    {
        Assert.Throws<MigrationCatalogException>(() => MigrationCatalog.Parse([]));
    }
}
