using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ContractTests
{
    static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifest-v1.json"));

    [Fact]
    public void Golden_fixture_deserialises()
    {
        var m = ManifestJson.Deserialize<ImportManifest>(Fixture());
        Assert.Equal(1, m.schemaVersion);
        Assert.Equal(3, m.items.Length);
        Assert.Equal(ProfileIds.HumanoidCharacter, m.items[0].profile);
        Assert.Equal(1.85f, m.items[0].character.targetHeight);
        Assert.Contains(" ", m.items[0].sourcePath);
        Assert.Equal("Walk", m.items[1].animation.clipName);
        Assert.True(m.items[1].allowOverwrite);
        Assert.Equal(AnimationCategories.Locomotion, m.items[1].animation.category);
        Assert.Equal("WallStraight", m.items[2].environment.element);
        Assert.Equal(ThemeModes.Append, m.items[2].environment.themeMode);
    }

    [Fact]
    public void Round_trip_is_stable()
    {
        var first = ManifestJson.Serialize(ManifestJson.Deserialize<ImportManifest>(Fixture()));
        var second = ManifestJson.Serialize(ManifestJson.Deserialize<ImportManifest>(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Blocks_missing_from_json_keep_usable_defaults()
    {
        var m = ManifestJson.Deserialize<ImportManifest>("{\"schemaVersion\":1,\"items\":[{\"id\":\"a\"}]}");
        Assert.NotNull(m.items[0].character);
        Assert.NotNull(m.items[0].animation);
        Assert.Equal(1f, m.items[0].prop.scale);
        Assert.Equal(1f, m.items[0].environment.scale);
    }

    [Fact]
    public void Result_round_trips()
    {
        var r = new ImportResult { runId = "r", success = true, unityVersion = "6000.3.25f1" };
        var item = new ItemResult { id = "i", success = true, measuredHeight = 2.4f, appliedScale = 0.77f, scaleSource = ScaleSources.Auto };
        item.importedAssets.Add("Assets/x.fbx");
        item.clips.Add(new ClipReport { name = "Walk", duration = 1.03f, loop = true });
        r.items.Add(item);
        var back = ManifestJson.Deserialize<ImportResult>(ManifestJson.Serialize(r));
        Assert.Equal("Assets/x.fbx", back.items[0].importedAssets[0]);
        Assert.Equal("Walk", back.items[0].clips[0].name);
        Assert.Equal(ScaleSources.Auto, back.items[0].scaleSource);
    }

    [Fact]
    public void Environment_element_list_has_the_twelve_known_elements()
    {
        Assert.Equal(12, EnvironmentElements.All.Length);
        Assert.Contains("WallStraight", EnvironmentElements.All);
    }
}
