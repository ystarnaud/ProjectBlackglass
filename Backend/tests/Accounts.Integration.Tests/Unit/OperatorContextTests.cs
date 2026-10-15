using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>Review B: operator names and reasons go into audit rows and log lines, so control characters (a forged log
/// line after "\n", a NUL PostgreSQL refuses) are refused up front.</summary>
public class OperatorContextTests
{
    [Theory]
    [InlineData("ops\nAdmin created account 2", "reason")]
    [InlineData("ops\r", "reason")]
    [InlineData("nul\u0000", "reason")]
    [InlineData("esc\u001b[31m", "reason")]
    [InlineData("ops", "line one\nforged line")]
    [InlineData("ops", "tab\there")]
    [InlineData("ops", "del\u007f")]
    [InlineData("ops", "c1\u0085")]
    public void Control_characters_are_refused(string op, string reason) =>
        Assert.Throws<ArgumentException>(() => OperatorContext.Create(op, reason));

    [Fact]
    public void Ordinary_text_is_kept_trimmed_with_a_fresh_correlation_id()
    {
        var a = OperatorContext.Create("  Ops Team – é  ", " rotate after leak ");
        var b = OperatorContext.Create("ops", "r");
        Assert.Equal(("Ops Team – é", "rotate after leak"), (a.Operator, a.Reason));
        Assert.NotEqual(a.CorrelationId, b.CorrelationId);
        Assert.Equal(7, a.CorrelationId.Version);
    }
}
