using System.Text;
using System.Text.Json;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.Domain.Settlement;

namespace Blackglass.Accounts.Domain.Tests.Settlement;

/// <summary>
/// The shared manifest-v1 vectors (Backend/contracts/manifest-v1, S9-B4): every valid input canonicalizes to exactly its
/// .canonical.json bytes and .hash.txt line, every invalid input is refused with exactly its .code.txt, and the retention and
/// receipt vectors match byte for byte. The expected files were produced by an independent script (tools/manifest_vectors.py),
/// not by this code. Stage 11's Unity EditMode tests must pass the same files.
/// </summary>
public class GoldenVectorTests
{
    static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "manifest-v1");

    static IEnumerable<string> Inputs(string folder) =>
        Directory.EnumerateFiles(Path.Combine(Root, folder), "*.input.json").Order(StringComparer.Ordinal);

    static string Stem(string input) => input[..^".input.json".Length];

    [Fact]
    public void Every_valid_vector_canonicalizes_to_its_bytes_and_hash()
    {
        var inputs = Inputs("valid").ToList();
        Assert.True(inputs.Count >= 12, "the valid vector set lost cases");
        foreach (var input in inputs)
        {
            var parsed = ResultManifest.Parse(File.ReadAllBytes(input));
            var ok = Assert.IsType<ParseResult.Parsed>(parsed);
            Assert.True(File.ReadAllBytes(Stem(input) + ".canonical.json").AsSpan().SequenceEqual(ok.CanonicalBytes), $"canonical bytes differ for {Path.GetFileName(input)}");
            Assert.Equal(File.ReadAllText(Stem(input) + ".hash.txt").TrimEnd('\n'), ok.ManifestHash);
            // The canonical form is a fixed point.
            Assert.Equal(ok.CanonicalBytes, Assert.IsType<ParseResult.Parsed>(ResultManifest.Parse(ok.CanonicalBytes)).CanonicalBytes);
        }
    }

    [Fact]
    public void Every_invalid_vector_is_refused_with_its_code()
    {
        var inputs = Inputs("invalid").ToList();
        var codes = new HashSet<string>();
        foreach (var input in inputs)
        {
            var expected = File.ReadAllText(Stem(input) + ".code.txt").TrimEnd('\n');
            var refused = ResultManifest.Parse(File.ReadAllBytes(input));
            Assert.True(refused is ParseResult.Malformed m && m.Code == expected,
                $"{Path.GetFileName(input)}: expected {expected}, got {(refused as ParseResult.Malformed)?.Code ?? "parsed"}");
            codes.Add(expected);
        }
        Assert.Equal(ManifestCodes.All.Where(c => c != ManifestCodes.TooLarge).Order(StringComparer.Ordinal), codes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_retention_vector_ranks_as_recorded()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root, "retention"), "*.json").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var v = JsonDocument.Parse(File.ReadAllBytes(file)).RootElement;
            var terms = InsuranceTerms.Parse(v.GetProperty("terms").GetRawText()).LootRetention;
            Assert.True(Base58Id.TryParse(v.GetProperty("accountId").GetString(), out var account));
            var candidates = v.GetProperty("candidates").EnumerateArray().Select(c => new RetentionCandidate(c.GetProperty("containerIndex").GetInt32(),
                c.GetProperty("line").GetInt32(), c.GetProperty("definitionId").GetString()!, Enum.Parse<ItemCategory>(c.GetProperty("category").GetString()!),
                c.GetProperty("quantity").GetInt32())).ToList();
            var expected = v.GetProperty("expected").EnumerateArray().Select(e => new RetainedLot(e.GetProperty("containerIndex").GetInt32(),
                e.GetProperty("line").GetInt32(), e.GetProperty("definitionId").GetString()!, e.GetProperty("units").GetInt32())).ToList();
            Assert.Equal(expected, RetentionRanker.Select(Convert.FromHexString(v.GetProperty("saltHex").GetString()!), account, candidates, terms));
        }
    }

    [Fact]
    public void Every_receipt_vector_writes_its_exact_bytes()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root, "receipt"), "*.input.json").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var data = ReadReceipt(JsonDocument.Parse(File.ReadAllBytes(file)).RootElement);
            var expected = File.ReadAllBytes(Stem(file) + ".receipt.json");
            Assert.True(expected.AsSpan().SequenceEqual(ReceiptWriter.Write(data)), $"receipt bytes differ for {Path.GetFileName(file)}: {Encoding.UTF8.GetString(ReceiptWriter.Write(data))}");
        }
    }

    static long Id(JsonElement e) => Base58Id.TryParse(e.GetString(), out var id) ? id : throw new FormatException("bad id");

    static ReceiptData ReadReceipt(JsonElement v)
    {
        var energy = v.GetProperty("energy");
        var ins = v.GetProperty("insurance");
        var insurance = ins.GetProperty("covered").GetBoolean()
            ? new ReceiptInsurance(true, ins.GetProperty("policyId").GetString(), ins.GetProperty("policyVersion").GetInt32(), ins.GetProperty("applied").GetBoolean(),
                [.. ins.GetProperty("refunds").EnumerateArray().Select(r => new RefundLine(r.GetProperty("definitionId").GetString()!, r.GetProperty("quantity").GetInt32()))],
                [.. ins.GetProperty("retained").EnumerateArray().Select(r => new ReceiptRetained(r.GetProperty("definitionId").GetString()!, r.GetProperty("quantity").GetInt32(),
                    [.. r.GetProperty("itemIds").EnumerateArray().Select(Id)]))],
                [.. ins.GetProperty("xp").EnumerateArray().Select(x => new XpGrant(Id(x.GetProperty("operativeId")), x.GetProperty("amount").GetInt32()))])
            : ReceiptInsurance.NotCovered;
        return new ReceiptData(Guid.Parse(v.GetProperty("attemptId").GetString()!), Id(v.GetProperty("accountId")), v.GetProperty("outcome").GetString()!,
            DateTime.Parse(v.GetProperty("settledAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            v.GetProperty("catalogueVersion").GetString()!, energy.GetProperty("cost").GetInt32(), energy.GetProperty("state").GetString()!, energy.GetProperty("refunded").GetInt32(),
            [.. v.GetProperty("consumed").EnumerateArray().Select(c => new ReceiptConsumed(Id(c.GetProperty("operativeId")), c.GetProperty("definitionId").GetString()!, c.GetProperty("quantity").GetInt32()))],
            [.. v.GetProperty("granted").EnumerateArray().Select(g => new ReceiptGrant(Id(g.GetProperty("operativeId")), g.GetProperty("definitionId").GetString()!, g.GetProperty("quantity").GetInt32(),
                [.. g.GetProperty("itemIds").EnumerateArray().Select(Id)]))],
            [.. v.GetProperty("xp").EnumerateArray().Select(x => new XpGrant(Id(x.GetProperty("operativeId")), x.GetProperty("amount").GetInt32()))],
            [.. v.GetProperty("operatives").EnumerateArray().Select(o => new ReceiptOperative(Id(o.GetProperty("operativeId")),
                o.GetProperty("alive").ValueKind == JsonValueKind.Null ? null : o.GetProperty("alive").GetBoolean()))],
            insurance, v.GetProperty("inventoryVersion").GetInt64());
    }
}
