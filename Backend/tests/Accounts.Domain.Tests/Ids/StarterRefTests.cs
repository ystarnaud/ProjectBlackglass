using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.Accounts.Domain.Tests.Ids;

public class StarterRefTests
{
    const string Op = "2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03";

    [Fact]
    public void Operative_ref() =>
        Assert.Equal($"starter:a123456789:op:{Op}", StarterRef.Operative(123456789, Op));

    [Fact]
    public void Operative_item_ref() =>
        Assert.Equal($"starter:a1:op:{Op}:item:item.medkit:n0", StarterRef.OperativeItem(1, Op, "item.medkit", 0));

    [Fact]
    public void Stash_item_ref() =>
        Assert.Equal("starter:a1:stash:item:item.service-rifle:n2", StarterRef.StashItem(1, "item.service-rifle", 2));

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void Account_must_be_positive(long account)
    {
        Assert.ThrowsAny<ArgumentException>(() => StarterRef.Operative(account, Op));
        Assert.ThrowsAny<ArgumentException>(() => StarterRef.OperativeItem(account, Op, "item.medkit", 0));
        Assert.ThrowsAny<ArgumentException>(() => StarterRef.StashItem(account, "item.medkit", 0));
    }

    public static TheoryData<string?> BadIds() => new()
    {
        "Item.Medkit", "a:b", "", null, new string('a', 65), "a..b", "-a", "a b",
    };

    [Theory]
    [MemberData(nameof(BadIds))]
    public void Ids_must_be_well_formed(string? bad)
    {
        Assert.ThrowsAny<ArgumentException>(() => StarterRef.Operative(1, bad!));
        Assert.ThrowsAny<ArgumentException>(() => StarterRef.OperativeItem(1, bad!, "item.medkit", 0));
        Assert.ThrowsAny<ArgumentException>(() => StarterRef.OperativeItem(1, Op, bad!, 0));
        Assert.ThrowsAny<ArgumentException>(() => StarterRef.StashItem(1, bad!, 0));
    }

    [Fact]
    public void A_64_character_id_is_accepted() =>
        Assert.Equal($"starter:a1:stash:item:{new string('a', 64)}:n0", StarterRef.StashItem(1, new string('a', 64), 0));

    [Fact]
    public void K_must_not_be_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StarterRef.OperativeItem(1, Op, "item.medkit", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StarterRef.StashItem(1, "item.medkit", -1));
    }
}
