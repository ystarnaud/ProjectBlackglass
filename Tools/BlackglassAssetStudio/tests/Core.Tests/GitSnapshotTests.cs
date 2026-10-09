using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class GitSnapshotTests
{
    [Fact]
    public void Parses_nul_separated_porcelain_and_skips_rename_origins()
    {
        using var t = new TempDir();
        t.Write("Assets/a b.fbx");
        var text = " M Assets/old.cs\0?? Assets/a b.fbx\0R  Assets/new.cs\0Assets/was.cs\0";
        var map = GitSnapshot.Parse(text, t.Path);
        Assert.Equal(3, map.Count);
        Assert.True(map.ContainsKey("Assets/a b.fbx"));
        Assert.True(map.ContainsKey("Assets/new.cs"));
        Assert.False(map.ContainsKey("Assets/was.cs"));
    }

    [Fact]
    public void Diff_lists_new_and_changed_entries_but_not_unchanged_or_vanished_ones()
    {
        var before = new Dictionary<string, string> { ["a"] = " M|1", ["b"] = " M|1", ["gone"] = " M|1" };
        var after = new Dictionary<string, string> { ["a"] = " M|1", ["b"] = " M|2", ["c"] = "??|5" };
        var diff = GitSnapshot.Diff(before, after);
        Assert.Equal(2, diff.Count);
        Assert.Contains(diff, d => d.Contains("b") && d.Contains("modified"));
        Assert.Contains(diff, d => d.Contains("c") && d.Contains("new"));
    }

    [Fact]
    public void TryCapture_in_a_temp_repo_sees_new_files_and_outside_a_repo_returns_null()
    {
        using var t = new TempDir();
        Assert.Null(GitSnapshot.TryCapture(t.Path));
        using var init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q")
            { WorkingDirectory = t.Path, UseShellExecute = false, CreateNoWindow = true })!;
        init.WaitForExit();
        t.Write("Assets/new file.txt");
        var snap = GitSnapshot.TryCapture(t.Path);
        Assert.NotNull(snap);
        Assert.True(snap!.ContainsKey("Assets/new file.txt"));
    }

    [Fact]
    public void A_file_modified_again_while_already_modified_is_detected_by_timestamp()
    {
        using var t = new TempDir();
        var path = t.Write("Assets/x.cs");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = GitSnapshot.Parse(" M Assets/x.cs\0", t.Path);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));
        var after = GitSnapshot.Parse(" M Assets/x.cs\0", t.Path);
        Assert.Single(GitSnapshot.Diff(before, after));
    }
}
