using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class SettingsTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var t = new TempDir();
        var s = new SettingsStore(t.Combine("settings.json")).Load();
        Assert.Equal(1.85f, s.DefaultTargetHeight);
        Assert.Equal("", s.ProjectPath);
    }

    [Fact]
    public void Save_then_load_round_trips_including_spaces_and_other_drives()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.Combine("sub", "settings.json"));
        store.Save(new AppSettings { ProjectPath = "D:\\My Games\\Project Blackglass", DefaultTargetHeight = 1.9f, DefaultSharedAvatarPath = "Assets/A B.fbx" });
        var back = store.Load();
        Assert.Equal("D:\\My Games\\Project Blackglass", back.ProjectPath);
        Assert.Equal(1.9f, back.DefaultTargetHeight);
        Assert.Equal("Assets/A B.fbx", back.DefaultSharedAvatarPath);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_without_throwing()
    {
        using var t = new TempDir();
        var path = t.Write("settings.json", "{ this is not json");
        var s = new SettingsStore(path).Load();
        Assert.Equal(1.85f, s.DefaultTargetHeight);
    }

    [Fact]
    public void Non_positive_target_height_in_file_is_replaced_by_the_default()
    {
        using var t = new TempDir();
        var path = t.Write("settings.json", "{\"DefaultTargetHeight\": -3}");
        Assert.Equal(1.85f, new SettingsStore(path).Load().DefaultTargetHeight);
    }

    [Fact]
    public void Runs_root_uses_the_staging_folder_when_set()
    {
        Assert.Equal("D:\\Stage", AppPaths.RunsRoot(new AppSettings { StagingFolder = "D:\\Stage" }));
        Assert.Equal(AppPaths.DefaultRunsRoot, AppPaths.RunsRoot(new AppSettings()));
    }
}
