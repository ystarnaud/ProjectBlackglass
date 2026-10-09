using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ClassifierTests
{
    [Theory]
    [InlineData("C:\\x\\Idle.fbx", ImportProfiles.Locomotion)]
    [InlineData("C:\\x\\Walking.fbx", ImportProfiles.Locomotion)]
    [InlineData("C:\\x\\Run_Gun.fbx", ImportProfiles.Locomotion)]
    [InlineData("C:\\x\\Fire.fbx", ImportProfiles.Combat)]
    [InlineData("C:\\x\\Reload.fbx", ImportProfiles.Combat)]
    [InlineData("C:\\x\\Hit Reaction.fbx", ImportProfiles.Reaction)]
    [InlineData("C:\\x\\Darius Crouch Death.fbx", ImportProfiles.Death)]
    [InlineData("C:\\x\\Die.fbx", ImportProfiles.Death)]
    [InlineData("C:\\x\\DariusFire.fbx", ImportProfiles.Combat)]
    public void Animation_keywords(string path, string expected) =>
        Assert.Equal(expected, AssetClassifier.Classify(path).ProfileId);

    [Fact]
    public void Death_beats_locomotion_when_both_appear() =>
        Assert.Equal(ImportProfiles.Death, AssetClassifier.Classify("C:\\x\\Crouch Death.fbx").ProfileId);

    [Theory]
    [InlineData("C:\\x\\wall_straight.fbx", "WallStraight")]
    [InlineData("C:\\x\\Wall Corner A.fbx", "WallCorner")]
    [InlineData("C:\\x\\wall_end.fbx", "WallEnd")]
    [InlineData("C:\\x\\Wall Junction.fbx", "WallJunction")]
    [InlineData("C:\\x\\door_frame.fbx", "DoorFrame")]
    [InlineData("C:\\x\\Floor.fbx", "Floor")]
    [InlineData("C:\\x\\pillar.fbx", "Pillar")]
    [InlineData("C:\\x\\low_cover_long.fbx", "LowCoverLong")]
    [InlineData("C:\\x\\LowCover.fbx", "LowCover")]
    [InlineData("C:\\x\\crate.fbx", "Crate")]
    public void Environment_keywords_suggest_an_element(string path, string element)
    {
        var c = AssetClassifier.Classify(path);
        Assert.Equal(ImportProfiles.Environment, c.ProfileId);
        Assert.Equal(element, c.Element);
    }

    [Fact]
    public void Unknown_stem_is_a_generic_prop_with_a_reason()
    {
        var c = AssetClassifier.Classify("C:\\x\\Teapot.fbx");
        Assert.Equal(ImportProfiles.Prop, c.ProfileId);
        Assert.False(string.IsNullOrWhiteSpace(c.Reason));
        Assert.True(c.Supported);
    }

    [Theory]
    [InlineData("C:\\x\\Character.glb")]
    [InlineData("C:\\x\\Character.gltf")]
    [InlineData("C:\\x\\notes.txt")]
    public void Unsupported_extensions(string path)
    {
        var c = AssetClassifier.Classify(path);
        Assert.False(c.Supported);
        Assert.Contains("FBX", c.Reason);
    }

    [Fact]
    public void Glb_message_explains_the_decision() =>
        Assert.Contains("Blender", AssetClassifier.Classify("C:\\x\\a.glb").Reason);

    [Fact]
    public void Nearest_folder_hint_wins_over_the_name()
    {
        Assert.Equal(ImportProfiles.Environment, AssetClassifier.Classify("C:\\a\\Environment\\Idle.fbx").ProfileId);
        Assert.Equal(ImportProfiles.Prop, AssetClassifier.Classify("C:\\a\\Weapons\\Rifle.fbx").ProfileId);
        var anim = AssetClassifier.Classify("C:\\a\\Darius\\Animations\\Teapot.fbx");
        Assert.Equal(ImportProfiles.Locomotion, anim.ProfileId);
    }

    [Fact]
    public void Character_folder_is_a_weak_hint_that_animation_keywords_override()
    {
        Assert.Equal(ImportProfiles.Character, AssetClassifier.Classify("C:\\a\\Characters\\Kestrel.fbx").ProfileId);
        Assert.Equal(ImportProfiles.Combat, AssetClassifier.Classify("C:\\a\\Characters\\Fire.fbx").ProfileId);
    }

    [Fact]
    public void Folders_above_the_drop_folder_are_ignored()
    {
        var c = AssetClassifier.Classify("C:\\Props\\Pack\\Teapot.fbx", "C:\\Props\\Pack");
        Assert.Equal(ImportProfiles.Prop, c.ProfileId); // generic default, not because of 'Props'
        Assert.DoesNotContain("Props", c.Reason);
    }

    [Fact]
    public void Clip_name_is_a_pascal_case_stem() =>
        Assert.Equal("DariusWalkGun", AssetClassifier.Classify("C:\\x\\Darius Walk Gun.fbx").ClipName);
}
