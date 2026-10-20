using System.Text;
using Blackglass.Accounts.Domain.Settlement;
using static Blackglass.Accounts.Domain.Tests.Settlement.SettlementFixtures;

namespace Blackglass.Accounts.Domain.Tests.Settlement;

/// <summary>Format v1's strict parser and canonical form (S9-B4, plan §G). The golden vectors pin the byte forms.</summary>
public class ResultManifestTests
{
    static string Canonical() => Encoding.UTF8.GetString(Manifest().ToCanonicalBytes());

    static ParseResult Parse(string text) => ResultManifest.Parse(Encoding.UTF8.GetBytes(text));

    static string CodeOf(string text) => Assert.IsType<ParseResult.Malformed>(Parse(text)).Code;

    [Fact]
    public void The_canonical_form_round_trips_and_hashes_its_own_bytes()
    {
        var parsed = Assert.IsType<ParseResult.Parsed>(Parse(Canonical()));
        Assert.Equal(Canonical(), Encoding.UTF8.GetString(parsed.CanonicalBytes));
        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(parsed.CanonicalBytes)), parsed.ManifestHash);
        Assert.Equal(Manifest().Operatives.Count, parsed.Manifest.Operatives.Count);
    }

    [Fact]
    public void Whitespace_and_member_order_are_canonicalized()
    {
        var pretty = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(Canonical()).RootElement,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var parsed = Assert.IsType<ParseResult.Parsed>(Parse(pretty));
        Assert.Equal(Canonical(), Encoding.UTF8.GetString(parsed.CanonicalBytes));
    }

    [Fact]
    public void A_body_over_the_cap_is_too_large()
    {
        var body = new byte[ResultManifest.MaxBytes + 1];
        Array.Fill(body, (byte)' ');
        Assert.Equal(ManifestCodes.TooLarge, Assert.IsType<ParseResult.Malformed>(ResultManifest.Parse(body)).Code);
        Assert.Equal(ManifestCodes.NotJson, Assert.IsType<ParseResult.Malformed>(ResultManifest.Parse(new byte[ResultManifest.MaxBytes])).Code);
    }

    [Theory]
    [InlineData("not json", "NotJson")]
    [InlineData("[]", "WrongType")]
    [InlineData("{\"formatVersion\":1}", "MissingMember")]
    public void Refusal_classes(string body, string code) => Assert.Equal(code, CodeOf(body));

    [Fact]
    public void Each_value_class_has_its_code()
    {
        var c = Canonical();
        Assert.Equal("DuplicateKey", CodeOf(c.Replace("{\"accountId\"", "{\"outcome\":\"success\",\"accountId\"", StringComparison.Ordinal)));
        Assert.Equal("UnknownMember", CodeOf(c.Replace("{\"accountId\"", "{\"extra\":1,\"accountId\"", StringComparison.Ordinal)));
        Assert.Equal("UnsupportedFormat", CodeOf(c.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal)));
        Assert.Equal("NotInteger", CodeOf(c.Replace("\"outcomeSequence\":8812", "\"outcomeSequence\":8812.0", StringComparison.Ordinal)));
        Assert.Equal("NotInteger", CodeOf(c.Replace("\"outcomeSequence\":8812", "\"outcomeSequence\":8.812e3", StringComparison.Ordinal)));
        Assert.Equal("NotInteger", CodeOf(c.Replace("\"outcomeSequence\":8812", "\"outcomeSequence\":-0", StringComparison.Ordinal)));
        Assert.Equal("NotJson", CodeOf(c.Replace("\"outcomeSequence\":8812", "\"outcomeSequence\":08812", StringComparison.Ordinal)));
        Assert.Equal("OutOfBounds", CodeOf(c.Replace("\"serverElapsedMs\":1200000", "\"serverElapsedMs\":86400001", StringComparison.Ordinal)));
        Assert.Equal("NonCanonicalId", CodeOf(c.Replace(Attempt.ToString(), Attempt.ToString().ToUpperInvariant(), StringComparison.Ordinal)));
        Assert.Equal("BadEnum", CodeOf(c.Replace("\"outcome\":\"success\"", "\"outcome\":\"victory\"", StringComparison.Ordinal)));
        Assert.Equal("BackendOnlyOutcome", CodeOf(c.Replace("\"outcome\":\"success\"", "\"outcome\":\"infra_void\"", StringComparison.Ordinal)));
        Assert.Equal("WrongType", CodeOf(c.Replace("\"alive\":true", "\"alive\":1", StringComparison.Ordinal)));
        Assert.Equal("BadValue", CodeOf(c.Replace("\"buildRevision\":\"r1\"", "\"buildRevision\":\"r 1\"", StringComparison.Ordinal)));
        Assert.Equal("DuplicateEntry", CodeOf(c.Replace("\"held\":[{\"containerIndex\":0,\"line\":0,\"origin\":\"loot\",\"quantity\":1}",
            "\"held\":[{\"containerIndex\":0,\"line\":0,\"origin\":\"loot\",\"quantity\":1},{\"containerIndex\":0,\"line\":0,\"origin\":\"loot\",\"quantity\":1}", StringComparison.Ordinal)));
        Assert.Equal("TooDeep", CodeOf(c.Replace("\"alive\":true", "\"alive\":{\"a\":[{}]}", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_malformed_body_reports_its_claimed_ids_only_when_canonical()
    {
        var c = Canonical();
        var bad = Assert.IsType<ParseResult.Malformed>(Parse(c.Replace("\"outcome\":\"success\"", "\"outcome\":\"victory\"", StringComparison.Ordinal)));
        Assert.Equal((Attempt, Account), (bad.ClaimedAttemptId, bad.ClaimedAccountId));
        var upper = Assert.IsType<ParseResult.Malformed>(Parse(c.Replace(Attempt.ToString(), Attempt.ToString().ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("\"outcome\":\"success\"", "\"outcome\":\"victory\"", StringComparison.Ordinal)));
        Assert.Null(upper.ClaimedAttemptId);
        Assert.Equal(Account, upper.ClaimedAccountId);
        Assert.Equal((null, null), (Assert.IsType<ParseResult.Malformed>(Parse("nope")).ClaimedAttemptId, Assert.IsType<ParseResult.Malformed>(Parse("nope")).ClaimedAccountId));
    }

    /// <summary>
    /// Stage 11 final review M1: a root member name holding a lone surrogate escape, long enough (9 raw bytes or more) that
    /// NameEquals has to unescape it, is NotJson like every other lone surrogate the parser reads, not an exception. Such a
    /// name is neither claim member, so the claims stay what the short name (no unescaping) already gave: the body's ids.
    /// </summary>
    [Theory]
    [InlineData("\\ud800")]
    [InlineData("\\udc00xyz")]
    [InlineData("attemptI\\ud800")]
    public void A_lone_surrogate_in_a_member_name_is_NotJson_with_the_bodys_claims(string name)
    {
        var bad = Assert.IsType<ParseResult.Malformed>(Parse(Canonical().Replace("{\"accountId\"", "{\"" + name + "\":1,\"accountId\"", StringComparison.Ordinal)));
        Assert.Equal(("NotJson", (Guid?)Attempt, (long?)Account), (bad.Code, bad.ClaimedAttemptId, bad.ClaimedAccountId));
    }

    [Fact]
    public void The_seed_has_no_member_in_the_format()
    {
        Assert.DoesNotContain("seed", Canonical(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UnknownMember", CodeOf(Canonical().Replace("{\"accountId\"", "{\"seed\":5,\"accountId\"", StringComparison.Ordinal)));
    }
}
