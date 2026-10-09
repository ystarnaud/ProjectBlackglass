using Xunit;

namespace Blackglass.AssetStudio.Tests;

// One Unity instance per project: all E2E classes share a collection so xunit does not run them in parallel.
[Collection("UnityE2E")]
public class CharacterE2ETests
{
    [Fact]
    public async Task Character_goes_through_the_real_pipeline_and_the_source_is_untouched()
    {
        if (!E2EFixture.Enabled) return;
        var settings = E2EFixture.Settings();
        var source = E2EFixture.Source("Models/Darius Stand Idle.fbx");
        var before = E2EFixture.Hash(source);
        var item = E2EFixture.Item(source, ImportProfiles.Character, "E2E_Char", settings, i => i.character.targetHeight = 1.85f);
        try
        {
            var outcome = await E2EFixture.Run(settings, item);

            Assert.Null(outcome.FatalError);
            var r = outcome.Result!.items.Single();
            Assert.True(r.success, string.Join("; ", r.errors));
            // A fresh copy is imported with Unity's default settings: Darius' own .meta compensates its centimetre-declared units
            // with globalScale 100, so the copy measures 1.88 cm and the visual correction is about x100 (with a warning).
            Assert.InRange(r.measuredHeight, 0.0175f, 0.0195f);
            var expected = 1.85f / r.measuredHeight;
            Assert.InRange(r.appliedScale, expected * 0.999f, expected * 1.001f);
            Assert.NotEmpty(r.warnings);
            Assert.True(r.avatar.valid && r.avatar.isHuman);
            Assert.Contains("Assets/_AssetStudioScratch/E2E_Char_Visual.prefab", r.createdPrefabs);
            Assert.Equal(before, E2EFixture.Hash(source));
            Assert.DoesNotContain(outcome.ChangedFiles, f => f.EndsWith("Darius Stand Idle.fbx") || f.EndsWith("Darius Stand Idle.fbx.meta"));

            // Same item again: the ownership label the first (real Unity) run wrote must make this an update, not a conflict.
            var between = new ItemValidator(settings.ProjectPath).Validate(item);
            Assert.DoesNotContain(between, m => m.Severity == Severity.Error);
            Assert.Contains(between, m => m.Severity == Severity.Info && m.Text.Contains("will be updated"));

            var second = await E2EFixture.Run(settings, item);

            Assert.Null(second.FatalError);
            var r2 = second.Result!.items.Single();
            Assert.True(r2.success, string.Join("; ", r2.errors));
            Assert.Contains("Assets/_AssetStudioScratch/E2E_Char.fbx", r2.changedAssets);
            Assert.Contains("Assets/_AssetStudioScratch/E2E_Char_Visual.prefab", r2.changedAssets);
            Assert.Equal(before, E2EFixture.Hash(source));
        }
        finally { E2EFixture.CleanScratch(); }
    }
}
