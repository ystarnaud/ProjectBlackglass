using System.Security.Cryptography;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class StagingTests
{
    static AssetItem Item(TempDir t, string relative, string content = "fbx-bytes")
    {
        var path = t.Write(relative, content);
        return AssetItemFactory.Create(path, null, new AppSettings());
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Stages_files_with_spaces_and_non_ascii_names_and_leaves_the_source_untouched()
    {
        using var t = new TempDir();
        var item = Item(t, "src/Darius Walk Café.fbx");
        var before = Hash(item.OriginalPath);
        var stamp = File.GetLastWriteTimeUtc(item.OriginalPath);

        var run = new RunStaging(t.Combine("runs")).Create(new[] { item });

        Assert.True(File.Exists(run.StagedPaths[item.Id]));
        Assert.Equal(before, Hash(run.StagedPaths[item.Id]));
        Assert.Equal(before, Hash(item.OriginalPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(item.OriginalPath));
        Assert.EndsWith("Darius Walk Café.fbx", run.StagedPaths[item.Id]);
    }

    [Fact]
    public void Same_file_name_from_two_folders_gets_separate_staging_folders()
    {
        using var t = new TempDir();
        var a = Item(t, "one/Walk.fbx", "A");
        var b = Item(t, "two/Walk.fbx", "B");
        var run = new RunStaging(t.Combine("runs")).Create(new[] { a, b });
        Assert.NotEqual(run.StagedPaths[a.Id], run.StagedPaths[b.Id]);
        Assert.Equal("A", File.ReadAllText(run.StagedPaths[a.Id]));
        Assert.Equal("B", File.ReadAllText(run.StagedPaths[b.Id]));
    }

    [Fact]
    public void Sibling_fbm_texture_folder_is_staged_too()
    {
        using var t = new TempDir();
        var item = Item(t, "src/Hero.fbx");
        t.Write("src/Hero.fbm/diffuse.png", "png");
        var run = new RunStaging(t.Combine("runs")).Create(new[] { item });
        var staged = Path.Combine(Path.GetDirectoryName(run.StagedPaths[item.Id])!, "Hero.fbm", "diffuse.png");
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void Run_paths_live_under_the_runs_root()
    {
        using var t = new TempDir();
        var run = new RunStaging(t.Combine("runs")).Create(new[] { Item(t, "src/a.fbx") });
        Assert.StartsWith(t.Combine("runs"), run.RunDir);
        Assert.Equal(Path.Combine(run.RunDir, "manifest.json"), run.ManifestPath);
        Assert.Equal(Path.Combine(run.RunDir, "result.json"), run.ResultPath);
        Assert.Equal(Path.Combine(run.RunDir, "unity.log"), run.LogPath);
    }

    [Fact]
    public void Only_the_newest_runs_are_kept()
    {
        using var t = new TempDir();
        var staging = new RunStaging(t.Combine("runs"));
        var item = Item(t, "src/a.fbx");
        var first = staging.Create(new[] { item }, new DateTime(2026, 1, 1, 10, 0, 0));
        for (var i = 0; i < RunStaging.KeepRuns + 2; i++)
            staging.Create(new[] { item }, new DateTime(2026, 2, 1, 10, 0, i));
        var dirs = Directory.GetDirectories(t.Combine("runs"));
        Assert.Equal(RunStaging.KeepRuns, dirs.Length);
        Assert.False(Directory.Exists(first.RunDir));
    }

    [Fact]
    public void Foreign_folders_in_the_runs_root_are_never_deleted()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Combine("runs", "my-notes"));
        var staging = new RunStaging(t.Combine("runs"));
        var item = Item(t, "src/a.fbx");
        for (var i = 0; i < RunStaging.KeepRuns + 2; i++)
            staging.Create(new[] { item }, new DateTime(2026, 2, 1, 10, 0, i));
        Assert.True(Directory.Exists(t.Combine("runs", "my-notes")));
    }
}
