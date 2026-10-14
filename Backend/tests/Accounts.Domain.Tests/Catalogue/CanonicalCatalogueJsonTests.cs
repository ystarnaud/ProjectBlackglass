using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Catalogue;

public class CanonicalCatalogueJsonTests
{
    [Fact]
    public void The_writer_produces_the_shared_golden_vector()
        => Assert.Equal(Golden.Bytes(), CanonicalCatalogueJson.ToBytes(Golden.Document()));

    [Fact]
    public void The_version_is_the_shared_vectors_version()
    {
        Assert.Matches("^sha256:[0-9a-f]{64}$", Golden.Version());
        Assert.Equal(Golden.Version(), CatalogueVersion.Of(Golden.Bytes()));
    }

    [Theory]
    [InlineData("item.medkit", true)]
    [InlineData("2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03", true)]
    [InlineData("item.medkit\n", false)]   // .NET's $ also matches before a final newline
    [InlineData("a..b", false)]
    [InlineData("-a", false)]
    [InlineData("a.", false)]
    [InlineData("Item.Medkit", false)]
    [InlineData("", false)]
    public void Ids_follow_the_id_rule_exactly(string id, bool wellFormed)
        => Assert.Equal(wellFormed, CanonicalCatalogueJson.IsWellFormedId(id));

    [Fact]
    public void Ids_are_at_most_64_characters()
    {
        Assert.True(CanonicalCatalogueJson.IsWellFormedId(new string('a', 64)));
        Assert.False(CanonicalCatalogueJson.IsWellFormedId(new string('a', 65)));
    }

    [Fact]
    public void The_writer_refuses_a_string_that_is_not_an_id()
    {
        var d = Golden.Document();
        d = d with { Items = [d.Items[0] with { Id = "Item Alpha" }, .. d.Items.Skip(1)] };
        Assert.Throws<ArgumentException>(() => CanonicalCatalogueJson.Write(d));
    }
}
