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

    [Fact]
    public void The_writer_refuses_a_string_that_is_not_an_id()
    {
        var d = Golden.Document();
        d = d with { Items = [d.Items[0] with { Id = "Item Alpha" }, .. d.Items.Skip(1)] };
        Assert.Throws<ArgumentException>(() => CanonicalCatalogueJson.Write(d));
    }
}
