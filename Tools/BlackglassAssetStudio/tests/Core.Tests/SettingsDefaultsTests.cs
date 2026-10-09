using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class SettingsDefaultsTests
{
    [Fact]
    public void Fills_the_project_path_by_walking_up_from_the_app_folder()
    {
        using var t = new TempDir();
        t.MakeProject();
        var app = t.Combine("Tools", "BlackglassAssetStudio", "bin");
        Directory.CreateDirectory(app);
        var s = new AppSettings();
        SettingsDefaults.Fill(s, app);
        Assert.Equal(t.Path, s.ProjectPath);
    }

    [Fact]
    public void Never_overwrites_values_the_user_set()
    {
        using var t = new TempDir();
        t.MakeProject();
        var elsewhere = @"D:\Elsewhere";
        var s = new AppSettings { ProjectPath = elsewhere };
        SettingsDefaults.Fill(s, t.Path);
        Assert.Equal(elsewhere, s.ProjectPath);
    }

    [Fact]
    public void Suggests_the_darius_model_as_shared_avatar_only_when_it_exists()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var s = new AppSettings { ProjectPath = project };
        SettingsDefaults.Fill(s, t.Path);
        Assert.Equal("", s.DefaultSharedAvatarPath);

        t.Write("Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx");
        SettingsDefaults.Fill(s, t.Path);
        Assert.Equal("Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx", s.DefaultSharedAvatarPath);
    }
}
