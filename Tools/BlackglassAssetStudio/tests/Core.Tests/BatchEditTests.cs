using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class BatchEditTests
{
    [Fact]
    public void Equal_values_give_that_value_and_are_not_mixed()
    {
        var (mixed, value) = BatchEdit.Common(new[] { "Assets/A", "Assets/A", "Assets/A" });
        Assert.False(mixed);
        Assert.Equal("Assets/A", value);
    }

    [Fact]
    public void Different_values_are_mixed_and_give_the_default()
    {
        var (mixed, value) = BatchEdit.Common(new[] { "Yes", "No", "Yes" });
        Assert.True(mixed);
        Assert.Null(value);
    }

    [Fact]
    public void A_single_value_is_not_mixed()
    {
        var (mixed, value) = BatchEdit.Common(new[] { true });
        Assert.False(mixed);
        Assert.True(value);
    }

    [Fact]
    public void Nothing_selected_is_not_mixed_and_gives_the_default()
    {
        var (mixed, value) = BatchEdit.Common(Array.Empty<bool>());
        Assert.False(mixed);
        Assert.False(value);
    }

    [Fact]
    public void Comparison_is_exact_so_a_case_difference_is_mixed()
    {
        Assert.True(BatchEdit.Common(new[] { "Assets/a", "Assets/A" }).Mixed);
    }

    [Fact]
    public void Booleans_that_differ_are_mixed()
    {
        Assert.True(BatchEdit.Common(new[] { true, false }).Mixed);
    }
}
