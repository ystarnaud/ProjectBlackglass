using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.Accounts.Domain.Tests.Auth;

public class AccountHandleTests
{
    [Theory]
    [InlineData("tester-1", true)]
    [InlineData("qa.alpha", true)]
    [InlineData("Tester", false)]
    [InlineData("a..b", false)]
    [InlineData("tester\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Handles_follow_the_id_rule(string? handle, bool valid) => Assert.Equal(valid, AccountHandle.IsValid(handle));

    [Fact]
    public void Handles_are_at_most_64_characters()
    {
        Assert.True(AccountHandle.IsValid(new string('a', 64)));
        Assert.False(AccountHandle.IsValid(new string('a', 65)));
    }
}
