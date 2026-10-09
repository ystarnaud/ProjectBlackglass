using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class AnimationNamingTests
{
    [Theory]
    [InlineData("EnemyUnit-Aim-Pistol", "EnemyUnit", "Aim Pistol", "AimPistol")]
    [InlineData("EnemyUnit-Crouch-Idle-Rifle", "EnemyUnit", "Crouch Idle Rifle", "CrouchIdleRifle")]
    [InlineData("EnemyUnit-Run", "EnemyUnit", "Run", "Run")]
    [InlineData("Enemy Unit - Walk (1)", "EnemyUnit", "Walk", "Walk")]
    [InlineData("EnemyUnitRunRifle", "EnemyUnit", "Run Rifle", "RunRifle")]
    [InlineData("enemyunit_stand_ground", "EnemyUnit", "Stand Ground", "StandGround")]
    [InlineData("Walking", "EnemyUnit", "Walking", "Walking")]
    [InlineData("Standing Idle mixamo.com", "EnemyUnit", "Standing Idle", "StandingIdle")]
    [InlineData("Walking Without Skin", "EnemyUnit", "Walking", "Walking")]
    [InlineData("Enemy Unit", "Enemy Unit", "Clip", "Clip")]
    [InlineData("Darius Hit Reaction", "Darius", "Hit Reaction", "HitReaction")]
    public void Action_words_drop_the_character_prefix_and_export_noise(string stem, string character, string words, string clip)
    {
        var w = AnimationNaming.ActionWords(stem, character);
        Assert.Equal(words, string.Join(' ', w));
        Assert.Equal(clip, AnimationNaming.ClipName(w));
    }

    [Fact]
    public void Asset_name_is_character_then_action_with_spaces() =>
        Assert.Equal("EnemyUnit Aim Pistol", AnimationNaming.AssetName("EnemyUnit", AnimationNaming.ActionWords("EnemyUnit-Aim-Pistol", "EnemyUnit")));

    [Fact]
    public void Only_a_leading_character_name_is_removed()
    {
        var w = AnimationNaming.ActionWords("Walk-EnemyUnit", "EnemyUnit");
        Assert.Equal("Walk Enemy Unit", string.Join(' ', w));
    }

    [Fact]
    public void All_24_enemy_unit_files_give_distinct_clean_names()
    {
        var stems = new[]
        {
            "EnemyUnit-Aim-Pistol", "EnemyUnit-Aim-Rifle", "EnemyUnit-Crouch-Aim-Pistol", "EnemyUnit-Crouch-Death", "EnemyUnit-Crouch-Idle-Rifle",
            "EnemyUnit-Crouch-Throw", "EnemyUnit-Crouch-Walk", "EnemyUnit-Crouch", "EnemyUnit-Death", "EnemyUnit-Fall", "EnemyUnit-Fire-Rifle",
            "EnemyUnit-Hit-Pistol", "EnemyUnit-Hit-Rifle", "EnemyUnit-Reload-Rifle", "EnemyUnit-Rifle-Idle", "EnemyUnit-Run-Pistol",
            "EnemyUnit-Run-Rifle", "EnemyUnit-Run", "EnemyUnit-Stand-Crouch", "EnemyUnit-Stand-Ground", "EnemyUnit-Throw", "EnemyUnit-Walk-Pistol",
            "EnemyUnit-Walk-Rifle", "EnemyUnit-Walk",
        };
        var assetNames = stems.Select(s => AnimationNaming.AssetName("EnemyUnit", AnimationNaming.ActionWords(s, "EnemyUnit"))).ToList();
        var clipNames = stems.Select(s => AnimationNaming.ClipName(AnimationNaming.ActionWords(s, "EnemyUnit"))).ToList();
        Assert.Equal(24, assetNames.Distinct().Count());
        Assert.Equal(24, clipNames.Distinct().Count());
        Assert.All(assetNames, n => Assert.StartsWith("EnemyUnit ", n));
        Assert.All(clipNames, n => Assert.DoesNotContain("Enemy", n));
    }
}
