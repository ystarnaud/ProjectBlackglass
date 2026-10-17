using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Hosts;

namespace Blackglass.Accounts.Domain.Tests.Hosts;

public class HostRulesTests
{
    const string Catalogue = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(OpaqueSecret.HostSecretPrefix)]
    [InlineData(OpaqueSecret.ProcessCredentialPrefix)]
    [InlineData(OpaqueSecret.JoinTicketPrefix)]
    [InlineData(OpaqueSecret.AccountKeyPrefix)]
    [InlineData(OpaqueSecret.AccessTokenPrefix)]
    public void New_secret_prefixes_round_trip_and_refuse_each_others_text(string prefix)
    {
        string[] all = [OpaqueSecret.HostSecretPrefix, OpaqueSecret.ProcessCredentialPrefix, OpaqueSecret.JoinTicketPrefix,
                        OpaqueSecret.AccountKeyPrefix, OpaqueSecret.AccessTokenPrefix];
        Assert.Equal(5, all.Distinct().Count());
        var secret = OpaqueSecret.New(prefix);
        Assert.Matches($"^{prefix}_[A-Za-z0-9_-]{{43}}$", secret.Text);
        Assert.True(OpaqueSecret.TryParse(secret.Text, prefix, out var parsed));
        Assert.Equal(secret.Hash(), parsed!.Hash());
        foreach (var other in all.Where(p => p != prefix))
            Assert.False(OpaqueSecret.TryParse(secret.Text, other, out _));
    }

    [Theory]
    [InlineData("r1", Catalogue, true)]
    [InlineData("2026.10.17-a_b", Catalogue, true)]
    [InlineData("A", Catalogue, true)]
    [InlineData("", Catalogue, false)]
    [InlineData(null, Catalogue, false)]
    [InlineData("-r1", Catalogue, false)]
    [InlineData(".r1", Catalogue, false)]
    [InlineData("r 1", Catalogue, false)]
    [InlineData("r1\n", Catalogue, false)]
    [InlineData("r/1", Catalogue, false)]
    [InlineData("r1", "sha256:0123", false)]
    [InlineData("r1", "sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("r1", "md5:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("r1", Catalogue + "\n", false)]
    [InlineData("r1", null, false)]
    public void BuildIdentity_parses_strictly(string? revision, string? catalogue, bool ok)
    {
        Assert.Equal(ok, BuildIdentity.TryParse(revision, catalogue, out var build));
        if (ok) Assert.Equal(new BuildIdentity(revision!, catalogue!), build);
        else Assert.Null(build);
    }

    [Fact]
    public void BuildIdentity_allows_64_characters_and_refuses_65()
    {
        Assert.True(BuildIdentity.TryParse("a" + new string('b', 63), Catalogue, out _));
        Assert.False(BuildIdentity.TryParse("a" + new string('b', 64), Catalogue, out _));
    }

    [Fact]
    public void PortAllocator_returns_the_lowest_free_port_or_null()
    {
        Assert.Equal(7000, PortAllocator.Lowest(7000, 3, new HashSet<int>()));
        Assert.Equal(7001, PortAllocator.Lowest(7000, 3, new HashSet<int> { 7000, 7002 }));
        Assert.Equal(7002, PortAllocator.Lowest(7000, 3, new HashSet<int> { 7000, 7001 }));
        Assert.Null(PortAllocator.Lowest(7000, 3, new HashSet<int> { 7000, 7001, 7002 }));
        Assert.Null(PortAllocator.Lowest(7000, 0, new HashSet<int>()));
        Assert.Equal(7000, PortAllocator.Lowest(7000, 2, new HashSet<int> { 6999, 7002 })); // ports outside the range don't count
    }
}
