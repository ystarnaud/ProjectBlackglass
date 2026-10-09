using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class CharacterContextTests
{
    [Theory]
    [InlineData("Assets/Art/Characters/EnemyUnit/Models/EnemyUnit.fbx", "EnemyUnit", "Assets/Art/Characters/EnemyUnit")]
    [InlineData("Assets/Art/Characters/EnemyUnit/EnemyUnit.fbx", "EnemyUnit", "Assets/Art/Characters/EnemyUnit")]
    [InlineData("Assets/Art/characters/Darius/Models/Darius Stand Idle.fbx", "Darius", "Assets/Art/characters/Darius")]
    [InlineData("Assets/Other/Kestrel/Kestrel.fbx", "Kestrel", "Assets/Other/Kestrel")]
    [InlineData("Assets/Other/Kestrel/Models/Kestrel.fbx", "Kestrel", "Assets/Other/Kestrel")]
    [InlineData("Assets/Kestrel.fbx", "Kestrel", "Assets")]
    public void The_character_is_found_from_its_model_path(string model, string name, string folder)
    {
        var c = CharacterContext.FromModel(model);
        Assert.Equal(name, c.Name);
        Assert.Equal(folder, c.Folder);
        Assert.Equal(model, c.ModelPath);
        Assert.Equal(folder + "/Animations", c.AnimationsFolder);
    }

    static AssetItem AnimationItem(string file, string? profile = null)
    {
        var settings = new AppSettings { DefaultSharedAvatarPath = "Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx" };
        var item = AssetItemFactory.Create(@"C:\Src\Animations\" + file, @"C:\Src\Animations", settings);
        if (profile != null && item.ProfileId != profile)
        {
            item.ProfileId = profile;
            ImportProfiles.Apply(item.Import, ImportProfiles.Get(profile), settings);
        }
        return item;
    }

    static readonly CharacterContext Enemy = CharacterContext.FromModel("Assets/Art/Characters/EnemyUnit/Models/EnemyUnit.fbx");

    [Fact]
    public void Apply_sets_name_clip_destination_and_avatar_of_an_animation()
    {
        var item = AnimationItem("EnemyUnit-Crouch-Idle-Rifle.fbx");
        AnimationBatch.Apply(item, Enemy);
        Assert.Equal("EnemyUnit Crouch Idle Rifle", item.Import.name);
        Assert.Equal("CrouchIdleRifle", item.Import.animation.clipName);
        Assert.Equal("Assets/Art/Characters/EnemyUnit/Animations", item.Import.destinationFolder);
        Assert.Equal("Assets/Art/Characters/EnemyUnit/Models/EnemyUnit.fbx", item.Import.animation.sharedAvatarPath);
    }

    [Fact]
    public void Apply_keeps_fields_the_user_edited()
    {
        var item = AnimationItem("EnemyUnit-Walk.fbx");
        item.Import.name = "My Walk";
        item.EditedFields.Add(ItemFields.Name);
        item.Import.destinationFolder = "Assets/Custom";
        item.EditedFields.Add(ItemFields.Destination);
        AnimationBatch.Apply(item, Enemy);
        Assert.Equal("My Walk", item.Import.name);
        Assert.Equal("Assets/Custom", item.Import.destinationFolder);
        Assert.Equal("Walk", item.Import.animation.clipName);
        Assert.Equal(Enemy.ModelPath, item.Import.animation.sharedAvatarPath);
    }

    [Fact]
    public void Apply_leaves_non_animation_items_alone()
    {
        var prop = AssetItemFactory.Create(@"C:\Src\Props\Crate.fbx", @"C:\Src\Props", new AppSettings());
        var before = (prop.Import.name, prop.Import.destinationFolder);
        AnimationBatch.Apply(prop, Enemy);
        Assert.Equal(before, (prop.Import.name, prop.Import.destinationFolder));
    }

    [Fact]
    public void Apply_does_not_change_the_profile_loop_or_root_flags()
    {
        var item = AnimationItem("EnemyUnit-Death.fbx", ImportProfiles.Death);
        var loop = item.Import.animation.loop;
        AnimationBatch.Apply(item, Enemy);
        Assert.Equal(ImportProfiles.Death, item.ProfileId);
        Assert.Equal(loop, item.Import.animation.loop);
    }

    [Fact]
    public void Real_enemy_unit_files_get_the_expected_profiles()
    {
        var expected = new Dictionary<string, string>
        {
            ["EnemyUnit-Aim-Pistol"] = ImportProfiles.Combat,
            ["EnemyUnit-Crouch-Aim-Pistol"] = ImportProfiles.Combat,
            ["EnemyUnit-Crouch-Death"] = ImportProfiles.Death,
            ["EnemyUnit-Hit-Rifle"] = ImportProfiles.Reaction,
            ["EnemyUnit-Reload-Rifle"] = ImportProfiles.Combat,
            ["EnemyUnit-Crouch-Idle-Rifle"] = ImportProfiles.Locomotion,
            ["EnemyUnit-Rifle-Idle"] = ImportProfiles.Locomotion,
            ["EnemyUnit-Run-Pistol"] = ImportProfiles.Locomotion,
            ["EnemyUnit-Throw"] = ImportProfiles.Combat,
        };
        foreach (var (stem, profile) in expected)
            Assert.Equal(profile, AssetItemFactory.Create($@"C:\Src\Animations\{stem}.fbx", @"C:\Src\Animations", new AppSettings()).ProfileId);
    }
}
