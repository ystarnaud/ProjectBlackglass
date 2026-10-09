using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class AssetNamingTests
{
    static ImportItem Item(string profile, string name = "Kestrel", string source = "C:\\x\\a.FBX") =>
        new ImportItem { profile = profile, name = name, sourcePath = source, destinationFolder = "Assets/Art/Characters/Kestrel" };

    [Fact]
    public void Model_path_uses_name_and_lowercase_source_extension() =>
        Assert.Equal("Assets/Art/Characters/Kestrel/Kestrel.fbx", AssetNaming.ModelPath(Item(ProfileIds.HumanoidCharacter)));

    [Fact]
    public void Character_prefab_has_visual_suffix_and_follows_generate_flag()
    {
        var item = Item(ProfileIds.HumanoidCharacter);
        Assert.Equal("Assets/Art/Characters/Kestrel/Kestrel_Visual.prefab", AssetNaming.PrefabPath(item));
        item.character.generatePrefab = false;
        Assert.Null(AssetNaming.PrefabPath(item));
    }

    [Fact]
    public void Animation_has_no_prefab() => Assert.Null(AssetNaming.PrefabPath(Item(ProfileIds.HumanoidAnimation)));

    [Fact]
    public void Prop_prefab_follows_generate_flag_and_env_always_has_one()
    {
        var prop = Item(ProfileIds.GenericProp, "Crate");
        Assert.Equal("Assets/Art/Characters/Kestrel/Crate.prefab", AssetNaming.PrefabPath(prop));
        prop.prop.generatePrefab = false;
        Assert.Null(AssetNaming.PrefabPath(prop));
        Assert.NotNull(AssetNaming.PrefabPath(Item(ProfileIds.EnvironmentModule, "Wall")));
    }
}
