using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class UnityLauncherTests
{
    [Fact]
    public void Arguments_keep_paths_with_spaces_as_single_elements()
    {
        var args = UnityLauncher.BuildArguments("D:\\My Games\\Blackglass", "C:\\Users\\A B\\m.json", "C:\\Users\\A B\\r.json", "C:\\Users\\A B\\u.log");
        Assert.Contains("-batchmode", args);
        Assert.Equal("D:\\My Games\\Blackglass", args[args.IndexOf("-projectPath") + 1]);
        Assert.Equal(UnityLauncher.EntryMethod, args[args.IndexOf("-executeMethod") + 1]);
        Assert.Equal("C:\\Users\\A B\\m.json", args[args.IndexOf("-blackglassManifest") + 1]);
        Assert.Equal("C:\\Users\\A B\\r.json", args[args.IndexOf("-blackglassResult") + 1]);
        Assert.Equal("C:\\Users\\A B\\u.log", args[args.IndexOf("-logFile") + 1]);
        Assert.Contains("-quit", args);
    }

    [Fact]
    public void Project_is_open_only_while_the_lockfile_is_held()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        Assert.False(UnityLauncher.IsProjectOpen(project));
        var lockfile = t.Write("Temp/UnityLockfile");
        Assert.False(UnityLauncher.IsProjectOpen(project)); // stale file from a crash, not held
        using (new FileStream(lockfile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(UnityLauncher.IsProjectOpen(project));
        Assert.False(UnityLauncher.IsProjectOpen(project));
    }
}
