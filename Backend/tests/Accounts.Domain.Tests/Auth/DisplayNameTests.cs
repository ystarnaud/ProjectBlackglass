using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.Accounts.Domain.Tests.Auth;

public class DisplayNameTests
{
    [Theory]
    [InlineData("Tester One", true)]
    [InlineData("QA – Ünïcode ok", true)]
    [InlineData("x", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(" leading", false)]
    [InlineData("trailing ", false)]
    [InlineData("new\nline", false)]
    [InlineData("tab\there", false)]
    [InlineData("nul\u0000", false)]
    [InlineData("del\u007f", false)]
    public void Display_names_follow_the_rule(string? name, bool valid) => Assert.Equal(valid, DisplayName.IsValid(name));

    [Fact]
    public void Display_names_are_at_most_64_characters()
    {
        Assert.True(DisplayName.IsValid(new string('n', 64)));
        Assert.False(DisplayName.IsValid(new string('n', 65)));
    }
}
