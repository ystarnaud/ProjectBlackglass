using Blackglass.Accounts.Domain.Accounts;

namespace Blackglass.Accounts.Domain.Tests.Accounts;

/// <summary>
/// The public account id: the canonical Bitcoin Base58 form of a positive bigint (decision 048 amendment). The expected
/// strings are known answers derived outside this encoder: a published test vector (draft-msporny-base58,
/// 0x0000287fb4cd → "11233QC4", whose numeric part 0x287fb4cd is "233QC4"), the owner's example, and boundaries computed
/// with Python's arbitrary-precision integers.
/// </summary>
public class AccountIdTests
{
    public static TheoryData<long, string> KnownAnswers() => new()
    {
        { 1, "2" },
        { 57, "z" },
        { 58, "21" },
        { 3363, "zz" },                              // 58^2 - 1
        { 3364, "211" },                             // 58^2
        { 0x287fb4cd, "233QC4" },                    // published vector
        { 123456789, "BukQL" },                      // the owner's example
        { 2147483647, "4GmR58" },                    // int.MaxValue
        { 4294967296, "7YXq9H" },                    // 2^32
        { 9007199254740992, "2DLNrMSKuh" },          // 2^53: JavaScript's last exact integer
        { 9007199254740993, "2DLNrMSKui" },          // 2^53 + 1: not representable as a JavaScript number
        { 430804206899405823, "zzzzzzzzzz" },        // 58^10 - 1: the largest 10-character id
        { 430804206899405824, "21111111111" },       // 58^10: the first 11-character id
        { long.MaxValue, "NQm6nKp8qFC" },            // PostgreSQL bigint maximum
    };

    [Theory]
    [MemberData(nameof(KnownAnswers))]
    public void Format_gives_the_known_answer(long id, string text) => Assert.Equal(text, AccountId.Format(id));

    [Theory]
    [MemberData(nameof(KnownAnswers))]
    public void TryParse_reads_the_known_answer(long id, string text)
    {
        Assert.True(AccountId.TryParse(text, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Format_refuses_zero_and_negative_ids(long id) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountId.Format(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]               // zero
    [InlineData("11")]              // zero with a redundant leading zero digit
    [InlineData("12")]              // 1 with a redundant leading zero digit (non-canonical)
    [InlineData("1BukQL")]          // the owner's example, non-canonical
    [InlineData("0")]               // excluded characters
    [InlineData("O")]
    [InlineData("I")]
    [InlineData("l")]
    [InlineData("Buk0L")]
    [InlineData("bukql")]           // 'l' is not in the alphabet
    [InlineData(" BukQL")]          // whitespace anywhere
    [InlineData("BukQL ")]
    [InlineData("Buk QL")]
    [InlineData("BukQL\n")]
    [InlineData("\tBukQL")]
    [InlineData("+BukQL")]          // signs, prefixes and suffixes
    [InlineData("-BukQL")]
    [InlineData("0xBukQL")]
    [InlineData("BukQL=")]
    [InlineData("acct_BukQL")]
    [InlineData("２")]          // full-width digit two
    [InlineData("Buk\u0000QL")]
    [InlineData("NQm6nKp8qFD")]     // long.MaxValue + 1
    [InlineData("zzzzzzzzzzz")]     // 58^11 - 1: eleven characters, beyond bigint
    [InlineData("211111111111")]    // twelve characters
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void TryParse_refuses_anything_but_the_canonical_form(string? text)
    {
        Assert.False(AccountId.TryParse(text, out var id));
        Assert.Equal(0, id);
    }

    [Fact]
    public void Parsing_is_case_sensitive()
    {
        Assert.True(AccountId.TryParse("BUKQL", out var upper));
        Assert.True(AccountId.TryParse("BukQL", out var mixed));
        Assert.NotEqual(upper, mixed);
        Assert.Equal(123456789, mixed);
    }

    [Fact]
    public void Random_ids_round_trip_canonically_without_padding()
    {
        var random = new Random(48);
        for (var i = 0; i < 10_000; i++)
        {
            var id = random.NextInt64(1, long.MaxValue) >> random.Next(0, 63);
            if (id == 0) id = 1;
            var text = AccountId.Format(id);
            Assert.InRange(text.Length, 1, AccountId.MaxLength);
            Assert.NotEqual('1', text[0]);
            Assert.True(AccountId.TryParse(text, out var back));
            Assert.Equal(id, back);
        }
    }

    [Fact]
    public void The_alphabet_is_bitcoin_base58()
    {
        Assert.Equal("123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz", AccountId.Alphabet);
        Assert.Equal(58, AccountId.Alphabet.Distinct().Count());
        Assert.Equal(11, AccountId.MaxLength);
    }
}
