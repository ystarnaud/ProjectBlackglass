using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ManifestBuilderTests
{
    [Fact]
    public void Items_point_at_staged_files_with_normalised_destinations_and_profile_kind()
    {
        using var t = new TempDir();
        var src = t.Write("src/Darius Death.fbx");
        var item = AssetItemFactory.Create(src, null, new AppSettings());
        item.Import.destinationFolder = "Assets\\Art\\Animations\\";
        var run = new RunStaging(t.Combine("runs")).Create(new[] { item });

        var m = ManifestBuilder.Build(run, new[] { item });

        Assert.Equal(ContractInfo.SchemaVersion, m.schemaVersion);
        Assert.Equal(run.RunId, m.runId);
        Assert.Equal(run.ResultPath, m.resultPath);
        Assert.Single(m.items);
        Assert.Equal(item.Id, m.items[0].id);
        Assert.Equal(ProfileIds.HumanoidAnimation, m.items[0].profile);
        Assert.Equal(AnimationCategories.Death, m.items[0].animation.category);
        Assert.Equal(run.StagedPaths[item.Id], m.items[0].sourcePath);
        Assert.Equal("Assets/Art/Animations", m.items[0].destinationFolder);
    }

    [Fact]
    public void Building_does_not_change_the_list_items()
    {
        using var t = new TempDir();
        var item = AssetItemFactory.Create(t.Write("src/Crate.fbx"), null, new AppSettings());
        var original = item.Import.sourcePath;
        var run = new RunStaging(t.Combine("runs")).Create(new[] { item });
        ManifestBuilder.Build(run, new[] { item });
        Assert.Equal(original, item.Import.sourcePath);
    }
}
