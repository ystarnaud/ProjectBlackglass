using Xunit;

namespace Blackglass.AssetStudio.Tests;

// One Unity instance per project: all E2E classes share a collection so xunit does not run them in parallel.
[Collection("UnityE2E")]
public class AnimationE2ETests
{
    [Fact]
    public async Task Locomotion_combat_and_death_clips_import_with_their_loop_and_bake_settings()
    {
        if (!E2EFixture.Enabled) return;
        var settings = E2EFixture.Settings();
        try
        {
            var character = E2EFixture.Item(E2EFixture.Source("Models/Darius Stand Idle.fbx"), ImportProfiles.Character, "E2E_Anim_Char", settings,
                i => i.character.generatePrefab = false);
            var first = await E2EFixture.Run(settings, character);
            Assert.True(first.Success, first.FatalError ?? string.Join("; ", first.Result!.items.SelectMany(i => i.errors)));

            settings.DefaultSharedAvatarPath = "Assets/_AssetStudioScratch/E2E_Anim_Char.fbx";
            AssetItem Clip(string file, string profile, string name) =>
                E2EFixture.Item(E2EFixture.Source("Animations/" + file), profile, name, settings, i => i.animation.clipName = name);
            var walk = Clip("Darius Walk Gun.fbx", ImportProfiles.Locomotion, "Walk");
            var fire = Clip("Darius Fire.fbx", ImportProfiles.Combat, "Fire");
            var death = Clip("Darius Death.fbx", ImportProfiles.Death, "Death");
            var second = await E2EFixture.Run(settings, walk, fire, death);

            Assert.Null(second.FatalError);
            var byId = second.Result!.items.ToDictionary(i => i.id);
            foreach (var item in new[] { walk, fire, death })
                Assert.True(byId[item.Id].success, string.Join("; ", byId[item.Id].errors));
            Assert.True(byId[walk.Id].clips.Single().loop);
            Assert.False(byId[fire.Id].clips.Single().loop);
            Assert.False(byId[death.Id].clips.Single().loop);
            Assert.True(byId[death.Id].clips.Single().bakePositionXZ);
            Assert.True(byId[walk.Id].clips.Single().duration > 0.2f);
        }
        finally { E2EFixture.CleanScratch(); }
    }
}
