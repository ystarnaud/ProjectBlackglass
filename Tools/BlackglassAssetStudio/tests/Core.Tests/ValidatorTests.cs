using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ValidatorTests
{
    static AssetItem Make(string profile, TempDir t, string file = "Kestrel.fbx", AppSettings? settings = null)
    {
        var src = t.Write("src/" + file);
        var item = AssetItemFactory.Create(src, null, settings ?? new AppSettings());
        ImportProfiles.Apply(item.Import, ImportProfiles.Get(profile), settings ?? new AppSettings());
        item.ProfileId = profile;
        return item;
    }

    static List<ValidationMessage> Errors(List<ValidationMessage> m) => m.Where(x => x.Severity == Severity.Error).ToList();

    [Fact]
    public void A_clean_character_has_no_errors()
    {
        using var t = new TempDir();
        var v = new ItemValidator(t.MakeProject());
        Assert.Empty(Errors(v.Validate(Make(ImportProfiles.Character, t))));
    }

    [Fact]
    public void Missing_source_is_an_error()
    {
        using var t = new TempDir();
        var item = Make(ImportProfiles.Character, t);
        File.Delete(item.OriginalPath);
        Assert.Contains(Errors(new ItemValidator(t.MakeProject()).Validate(item)), m => m.Text.Contains("does not exist"));
    }

    [Fact]
    public void Unsupported_files_are_errors_with_the_classifier_reason()
    {
        using var t = new TempDir();
        var src = t.Write("src/Hero.glb");
        var item = AssetItemFactory.Create(src, null, new AppSettings());
        Assert.Contains(Errors(new ItemValidator(t.MakeProject()).Validate(item)), m => m.Text.Contains("Blender"));
    }

    [Fact]
    public void Obj_is_refused_for_characters()
    {
        using var t = new TempDir();
        var src = t.Write("src/body.obj");
        var item = AssetItemFactory.Create(src, null, new AppSettings());
        ImportProfiles.Apply(item.Import, ImportProfiles.Get(ImportProfiles.Character), new AppSettings());
        item.ProfileId = ImportProfiles.Character;
        Assert.Contains(Errors(new ItemValidator(t.MakeProject()).Validate(item)), m => m.Text.Contains(".obj"));
    }

    [Fact]
    public void Bad_destination_and_name_are_errors()
    {
        using var t = new TempDir();
        var item = Make(ImportProfiles.Prop, t);
        item.Import.destinationFolder = "Packages/x";
        item.Import.name = "a/b";
        var errors = Errors(new ItemValidator(t.MakeProject()).Validate(item));
        Assert.Contains(errors, m => m.Text.Contains("Assets"));
        Assert.Contains(errors, m => m.Text.Contains("name"));
    }

    [Fact]
    public void Character_target_height_must_be_positive_only_when_normalising()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Character, t);
        item.Import.character.targetHeight = 0;
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("target height", StringComparison.OrdinalIgnoreCase));
        item.Import.character.normalizeHeight = false;
        Assert.Empty(Errors(new ItemValidator(project).Validate(item)));
    }

    [Fact]
    public void Humanoid_character_requires_an_avatar()
    {
        using var t = new TempDir();
        var item = Make(ImportProfiles.Character, t);
        item.Import.character.createAvatar = false;
        Assert.Contains(Errors(new ItemValidator(t.MakeProject()).Validate(item)), m => m.Text.Contains("Avatar"));
    }

    [Fact]
    public void Animation_needs_an_existing_shared_avatar()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Locomotion, t, "Walk.fbx");
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("shared Avatar"));
        item.Import.animation.sharedAvatarPath = "Assets/Missing.fbx";
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("Missing.fbx"));
        t.Write("Assets/Real.fbx");
        item.Import.animation.sharedAvatarPath = "Assets/Real.fbx";
        Assert.Empty(Errors(new ItemValidator(project).Validate(item)));
    }

    [Fact]
    public void Animation_needs_a_clip_name()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        t.Write("Assets/Real.fbx");
        var item = Make(ImportProfiles.Locomotion, t, "Walk.fbx");
        item.Import.animation.sharedAvatarPath = "Assets/Real.fbx";
        item.Import.animation.clipName = " ";
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("clip name"));
    }

    [Fact]
    public void Environment_needs_a_known_element_and_a_theme_when_registering()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Environment, t, "thing.fbx");
        item.Import.environment.element = "";
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("element"));
        item.Import.environment.element = "WallStraight";
        Assert.Empty(Errors(new ItemValidator(project).Validate(item)));
        item.Import.environment.themeMode = ThemeModes.Append;
        item.Import.environment.themePath = "Assets/Nope.asset";
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("theme"));
        item.Import.environment.themeMode = ThemeModes.Replace;
        t.Write("Assets/Theme.asset");
        item.Import.environment.themePath = "Assets/Theme.asset";
        item.Import.environment.replaceIndex = -1;
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("index"));
    }

    [Fact]
    public void Environment_scale_other_than_one_is_a_warning_not_a_silent_change()
    {
        using var t = new TempDir();
        var item = Make(ImportProfiles.Environment, t, "wall.fbx");
        item.Import.environment.scale = 2f;
        Assert.Contains(new ItemValidator(t.MakeProject()).Validate(item), m => m.Severity == Severity.Warning && m.Text.Contains("scale"));
    }

    [Fact]
    public void Existing_unowned_asset_is_an_error_until_overwrite_is_allowed()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Prop, t, "Crate.fbx");
        t.Write("Assets/Art/Props/Crate.fbx");
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("Allow overwrite"));
        item.Import.allowOverwrite = true;
        var after = new ItemValidator(project).Validate(item);
        Assert.Empty(Errors(after));
        Assert.Contains(after, m => m.Severity == Severity.Warning && m.Text.Contains("replace"));
    }

    [Fact]
    public void Existing_studio_owned_asset_is_an_update_not_a_conflict()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Prop, t, "Crate.fbx");
        t.Write("Assets/Art/Props/Crate.fbx");
        t.Write("Assets/Art/Props/Crate.fbx.meta", "fileFormatVersion: 2\nlabels:\n- BlackglassStudio\n");
        var m = new ItemValidator(project).Validate(item);
        Assert.Empty(Errors(m));
        Assert.Contains(m, x => x.Severity == Severity.Info && x.Text.Contains("update"));
    }

    [Fact]
    public void Two_items_with_the_same_target_are_both_rejected()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var a = Make(ImportProfiles.Prop, t, "Crate.fbx");
        var srcB = t.Write("src2/Crate.fbx");
        var b = AssetItemFactory.Create(srcB, null, new AppSettings());
        ImportProfiles.Apply(b.Import, ImportProfiles.Get(ImportProfiles.Prop), new AppSettings());
        b.ProfileId = ImportProfiles.Prop;
        var result = new ItemValidator(project).ValidateAll(new[] { a, b });
        Assert.Contains(result[a.Id], m => m.Severity == Severity.Error && m.Text.Contains("same asset"));
        Assert.Contains(result[b.Id], m => m.Severity == Severity.Error && m.Text.Contains("same asset"));
    }

    [Fact]
    public void Targets_that_differ_only_in_case_collide()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var a = Make(ImportProfiles.Prop, t, "crate.fbx");
        var b = Make(ImportProfiles.Prop, t, "CRATE2.fbx");
        b.Import.name = "CRATE";
        a.Import.name = "crate";
        Assert.Contains(new ItemValidator(project).ValidateAll(new[] { a, b })[a.Id], m => m.Text.Contains("same asset"));
    }
}
