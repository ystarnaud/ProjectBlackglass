using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class AssetItemFactoryTests
{
    [Theory]
    [InlineData("Darius Walk Gun", "Darius_Walk_Gun")]
    [InlineData("Café Prop", "Café_Prop")]
    [InlineData("a:b*c", "a_b_c")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("..", "Asset")]
    [InlineData("", "Asset")]
    public void Names_are_sanitised(string stem, string expected) => Assert.Equal(expected, AssetNames.FromFileStem(stem));

    [Fact]
    public void Sanitised_names_always_validate()
    {
        foreach (var stem in new[] { "a/b", "x?y", "tab\there", "...", "trail. " })
            Assert.Null(PathRules.ValidateAssetName(AssetNames.FromFileStem(stem)));
    }

    [Fact]
    public void Create_classifies_names_and_applies_the_profile()
    {
        var item = AssetItemFactory.Create("C:\\x\\Darius Walk Gun.fbx", null, new AppSettings { DefaultSharedAvatarPath = "Assets/A.fbx" });
        Assert.Equal(ImportProfiles.Locomotion, item.ProfileId);
        Assert.Equal("Darius_Walk_Gun", item.Import.name);
        Assert.Equal("DariusWalkGun", item.Import.animation.clipName);
        Assert.Equal("C:\\x\\Darius Walk Gun.fbx", item.OriginalPath);
        Assert.Equal(item.Id, item.Import.id);
        Assert.Null(item.UnsupportedReason);
    }

    [Fact]
    public void Create_sets_the_suggested_environment_element()
    {
        var item = AssetItemFactory.Create("C:\\x\\wall_corner.fbx", null, new AppSettings());
        Assert.Equal("WallCorner", item.Import.environment.element);
    }

    [Fact]
    public void Unsupported_files_are_kept_with_a_reason()
    {
        var item = AssetItemFactory.Create("C:\\x\\Hero.glb", null, new AppSettings());
        Assert.NotNull(item.UnsupportedReason);
    }

    [Fact]
    public void Two_items_get_distinct_ids()
    {
        var a = AssetItemFactory.Create("C:\\x\\a.fbx", null, new AppSettings());
        var b = AssetItemFactory.Create("C:\\x\\a.fbx", null, new AppSettings());
        Assert.NotEqual(a.Id, b.Id);
    }
}
