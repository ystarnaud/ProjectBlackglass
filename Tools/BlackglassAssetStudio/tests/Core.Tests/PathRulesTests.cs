using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class PathRulesTests
{
    [Theory]
    [InlineData("Assets/Art/Characters/Kestrel")]
    [InlineData("Assets/My Folder/Sub Folder")]
    [InlineData("Assets\\Art\\Props")]
    public void Valid_destinations(string folder) => Assert.Null(PathRules.ValidateDestination(folder));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Assets")]
    [InlineData("assets/x")]
    [InlineData("Packages/com.x/y")]
    [InlineData("Assets/../Library")]
    [InlineData("Assets//x")]
    [InlineData("C:/x/y")]
    [InlineData("Assets/a:b")]
    [InlineData("Assets/a?b")]
    [InlineData("Assets/trailing./x")]
    [InlineData("Assets/x /y")]
    public void Invalid_destinations(string folder) => Assert.NotNull(PathRules.ValidateDestination(folder));

    [Theory]
    [InlineData("Kestrel")]
    [InlineData("Darius_Walk_Gun")]
    [InlineData("Café Prop")]
    public void Valid_names(string name) => Assert.Null(PathRules.ValidateAssetName(name));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData(".hidden")]
    [InlineData("name.")]
    public void Invalid_names(string name) => Assert.NotNull(PathRules.ValidateAssetName(name));

    [Fact]
    public void Normalize_uses_forward_slashes_and_no_trailing_slash() =>
        Assert.Equal("Assets/A/B", PathRules.Normalize("Assets\\A\\B/ "));
}
