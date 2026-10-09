using Xunit;

namespace Blackglass.AssetStudio.Tests;

// One Unity instance per project: all E2E classes share a collection so xunit does not run them in parallel.
[Collection("UnityE2E")]
public class PropEnvironmentE2ETests
{
    [Fact]
    public async Task Prop_and_environment_module_import_in_one_run()
    {
        if (!E2EFixture.Enabled) return;
        var settings = E2EFixture.Settings();
        try
        {
            var prop = E2EFixture.Item(E2EFixture.Source("Weapons/Rifle.fbx"), ImportProfiles.Prop, "E2E_Rifle", settings);
            var wall = E2EFixture.Item(E2EFixture.Source("Models/Darius Stand Idle.fbx"), ImportProfiles.Environment, "E2E_Wall", settings,
                i => i.environment.element = "WallStraight");
            var outcome = await E2EFixture.Run(settings, prop, wall);

            Assert.Null(outcome.FatalError);
            var byId = outcome.Result!.items.ToDictionary(i => i.id);
            Assert.True(byId[prop.Id].success, string.Join("; ", byId[prop.Id].errors));
            Assert.True(byId[wall.Id].success, string.Join("; ", byId[wall.Id].errors));
            Assert.Contains("Assets/_AssetStudioScratch/E2E_Rifle.prefab", byId[prop.Id].createdPrefabs);
            Assert.Contains(byId[wall.Id].warnings, w => w.StartsWith("Y is"));
            Assert.Equal("", byId[wall.Id].registeredInTheme);
        }
        finally { E2EFixture.CleanScratch(); }
    }
}
