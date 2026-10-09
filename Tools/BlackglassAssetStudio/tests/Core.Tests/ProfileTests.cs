using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ProfileTests
{
    static ImportItem Apply(string profileId, AppSettings? settings = null)
    {
        var item = new ImportItem { name = "Kestrel" };
        ImportProfiles.Apply(item, ImportProfiles.Get(profileId), settings ?? new AppSettings());
        return item;
    }

    [Fact]
    public void There_are_seven_profiles() => Assert.Equal(7, ImportProfiles.All.Count);

    [Fact]
    public void Unknown_profile_id_throws() => Assert.Throws<ArgumentException>(() => ImportProfiles.Get("Nope"));

    [Fact]
    public void For_maps_kind_and_category_back_to_the_profile()
    {
        Assert.Equal(ImportProfiles.Death, ImportProfiles.For(ProfileIds.HumanoidAnimation, AnimationCategories.Death).Id);
        Assert.Equal(ImportProfiles.Prop, ImportProfiles.For(ProfileIds.GenericProp, "").Id);
    }

    [Fact]
    public void Character_defaults_use_the_settings_target_height()
    {
        var item = Apply(ImportProfiles.Character, new AppSettings { DefaultTargetHeight = 1.9f });
        Assert.Equal(ProfileIds.HumanoidCharacter, item.profile);
        Assert.Equal(1.9f, item.character.targetHeight);
        Assert.True(item.character.normalizeHeight);
        Assert.True(item.character.rigHumanoid);
        Assert.True(item.character.generatePrefab);
        Assert.Equal("Assets/Art/Characters/Kestrel", item.destinationFolder);
    }

    [Theory]
    [InlineData(ImportProfiles.Locomotion, AnimationCategories.Locomotion, LoopModes.Yes, true, true, true)]
    [InlineData(ImportProfiles.Combat, AnimationCategories.Combat, LoopModes.No, false, true, false)]
    [InlineData(ImportProfiles.Reaction, AnimationCategories.Reaction, LoopModes.No, false, true, false)]
    [InlineData(ImportProfiles.Death, AnimationCategories.Death, LoopModes.No, false, true, true)]
    public void Animation_defaults(string profile, string category, string loop, bool rot, bool height, bool xz)
    {
        var item = Apply(profile, new AppSettings { DefaultSharedAvatarPath = "Assets/A.fbx" });
        Assert.Equal(ProfileIds.HumanoidAnimation, item.profile);
        Assert.Equal(category, item.animation.category);
        Assert.Equal(loop, item.animation.loop);
        Assert.Equal(rot, item.animation.bakeRotation);
        Assert.Equal(height, item.animation.bakeHeight);
        Assert.Equal(xz, item.animation.bakePositionXZ);
        Assert.Equal("Assets/A.fbx", item.animation.sharedAvatarPath);
        Assert.Equal("Assets/Art/Animations", item.destinationFolder);
    }

    [Fact]
    public void Prop_and_environment_never_normalise_scale()
    {
        Assert.Equal(1f, Apply(ImportProfiles.Prop).prop.scale);
        var env = Apply(ImportProfiles.Environment);
        Assert.Equal(1f, env.environment.scale);
        Assert.Equal(ThemeModes.None, env.environment.themeMode);
        Assert.Equal("Assets/_Project/Environment/Modules", env.destinationFolder);
    }

    [Fact]
    public void Obj_is_allowed_for_props_and_modules_only()
    {
        Assert.DoesNotContain(".obj", ImportProfiles.Get(ImportProfiles.Character).Extensions);
        Assert.DoesNotContain(".obj", ImportProfiles.Get(ImportProfiles.Locomotion).Extensions);
        Assert.Contains(".obj", ImportProfiles.Get(ImportProfiles.Prop).Extensions);
        Assert.Contains(".obj", ImportProfiles.Get(ImportProfiles.Environment).Extensions);
    }
}
