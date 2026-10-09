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

    [Theory]
    [InlineData("Assets/.hidden/x")]
    [InlineData("Assets/Art/.git")]
    [InlineData("Assets/Art~/x")]
    [InlineData("Assets/backup~")]
    [InlineData("Assets/CON")]
    [InlineData("Assets/con/x")]
    [InlineData("Assets/Art/NUL")]
    [InlineData("Assets/AUX.v2/x")]
    [InlineData("Assets/Com1")]
    [InlineData("Assets/lpt9")]
    public void Destinations_with_ignored_or_reserved_segments_are_refused(string folder) =>
        Assert.NotNull(PathRules.ValidateDestination(folder));

    [Theory]
    [InlineData("Assets/Console")]
    [InlineData("Assets/COM0")]
    [InlineData("Assets/COM10")]
    [InlineData("Assets/Lpt")]
    [InlineData("Assets/Nulls")]
    [InlineData("Assets/a.b.c")]
    public void Look_alike_destinations_stay_valid(string folder) => Assert.Null(PathRules.ValidateDestination(folder));

    [Theory]
    [InlineData("hidden~")]
    [InlineData("~")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Prn")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("CON.v2")]
    [InlineData("NUL.txt")]
    public void Names_that_unity_ignores_or_windows_reserves_are_refused(string name) =>
        Assert.NotNull(PathRules.ValidateAssetName(name));

    [Theory]
    [InlineData("Console")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("LPT")]
    [InlineData("a~b")]
    [InlineData("Nullable")]
    public void Look_alike_names_stay_valid(string name) => Assert.Null(PathRules.ValidateAssetName(name));

    [Theory]
    [InlineData("CON", "Asset")]
    [InlineData("backup~", "Asset")]
    [InlineData("Con Prop", "Con_Prop")]
    public void File_stems_never_become_ignored_or_reserved_names(string stem, string expected) =>
        Assert.Equal(expected, AssetNames.FromFileStem(stem));

    [Fact]
    public void Normalize_uses_forward_slashes_and_no_trailing_slash() =>
        Assert.Equal("Assets/A/B", PathRules.Normalize("Assets\\A\\B/ "));
}
