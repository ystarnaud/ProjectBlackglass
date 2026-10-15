using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.Accounts.Domain.Tests.Ids;

/// <summary>Base58Id is the Stage 3 account-id codec for any owned id; it must agree with AccountId everywhere.</summary>
public class Base58IdTests
{
    public static TheoryData<long, string> Vectors() => new()
    {
        { 1, "2" },
        { 123456789, "BukQL" },
        { long.MaxValue, "NQm6nKp8qFC" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Matches_AccountId_for_every_vector(long id, string text)
    {
        Assert.Equal(text, Base58Id.Format(id));
        Assert.Equal(AccountId.Format(id), Base58Id.Format(id));
        Assert.True(Base58Id.TryParse(text, out var parsed));
        Assert.Equal(id, parsed);
        Assert.True(AccountId.TryParse(text, out var viaAccount));
        Assert.Equal(parsed, viaAccount);
    }

    [Fact]
    public void Constants_match_AccountId()
    {
        Assert.Equal(11, Base58Id.MaxLength);
        Assert.Equal(AccountId.MaxLength, Base58Id.MaxLength);
        Assert.Equal(AccountId.Alphabet, Base58Id.Alphabet);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Format_refuses_zero_and_negative(long id) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Base58Id.Format(id));

    [Theory]
    [InlineData("1BukQL")]        // leading zero digit
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("Buk0L")]
    [InlineData("BukOL")]
    [InlineData("BukIL")]
    [InlineData("Bukl")]
    [InlineData(" BukQL")]
    [InlineData("BukQL ")]
    [InlineData("Buk QL")]
    [InlineData("BukQL\n")]
    [InlineData("222222222222")]  // 12 characters
    [InlineData("NQm6nKp8qFD")]   // overflow: long.MaxValue + 1
    [InlineData("zzzzzzzzzzz")]   // 11 characters, far past long.MaxValue
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_what_AccountId_refuses(string? text)
    {
        Assert.False(Base58Id.TryParse(text, out var id));
        Assert.Equal(0, id);
        Assert.False(AccountId.TryParse(text, out _));
    }
}
