using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class LogExcerptTests
{
    [Fact]
    public void Picks_the_last_error_lines_only()
    {
        using var t = new TempDir();
        var lines = new List<string> { "Loading project", "Assets/X.cs(3,4): error CS1002: ; expected" };
        for (var i = 0; i < 10; i++) lines.Add($"[AssetPipeline] ERROR item {i} failed");
        lines.Add("done");
        var log = t.Write("unity.log", string.Join("\n", lines));
        var errors = LogExcerpt.ErrorLines(log, 3);
        Assert.Equal(3, errors.Count);
        Assert.Contains("item 9", errors[^1]);
    }

    [Fact]
    public void Missing_log_gives_nothing() => Assert.Empty(LogExcerpt.ErrorLines("C:\\no\\such.log"));

    [Fact]
    public void Last_line_works_on_a_file_another_process_is_writing()
    {
        using var t = new TempDir();
        var path = t.Combine("unity.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = System.Text.Encoding.UTF8.GetBytes("first\nsecond line\n");
        writer.Write(bytes); writer.Flush();
        Assert.Equal("second line", LogExcerpt.LastLine(path));
    }

    [Fact]
    public void An_unreadable_log_gives_nothing_instead_of_throwing()
    {
        using var t = new TempDir();
        var path = t.Write("unity.log", "error CS0001: x");
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
            System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            Assert.Empty(LogExcerpt.ErrorLines(path));
            Assert.Equal("", LogExcerpt.LastLine(path));
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }
}
