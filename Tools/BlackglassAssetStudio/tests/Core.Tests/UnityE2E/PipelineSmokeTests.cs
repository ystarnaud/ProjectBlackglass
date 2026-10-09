using Xunit;

namespace Blackglass.AssetStudio.Tests;

// One Unity instance per project: all E2E classes share a collection so xunit does not run them in parallel.
[Collection("UnityE2E")]
public class PipelineSmokeTests
{
    [Fact]
    public async Task Unity_reads_the_manifest_and_returns_a_structured_result_for_every_item()
    {
        if (!E2EFixture.Enabled) return;
        var settings = E2EFixture.Settings();
        var item = E2EFixture.Item(E2EFixture.Source("Models/Darius Stand Idle.fbx"), ImportProfiles.Prop, "Smoke_Prop", settings);
        try
        {
            var outcome = await E2EFixture.Run(settings, item);

            Assert.Null(outcome.FatalError);
            Assert.NotNull(outcome.Result);
            Assert.Equal(outcome.RunId, outcome.Result!.runId);
            Assert.StartsWith(ProjectLocator.ReadEditorVersion(settings.ProjectPath)!, outcome.Result.unityVersion);
            Assert.Equal(item.Id, outcome.Result.items.Single().id);
            Assert.True(File.Exists(outcome.LogPath));
        }
        finally { E2EFixture.CleanScratch(); }
    }
}
