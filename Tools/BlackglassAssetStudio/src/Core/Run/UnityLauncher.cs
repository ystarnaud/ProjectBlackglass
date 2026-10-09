using System.Diagnostics;

namespace Blackglass.AssetStudio;

public interface IUnityProcess
{
    Task<int> RunAsync(string exe, IReadOnlyList<string> arguments, IProgress<string>? progress, string logPath);
}

public sealed class UnityProcess : IUnityProcess
{
    public async Task<int> RunAsync(string exe, IReadOnlyList<string> arguments, IProgress<string>? progress, string logPath)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unity did not start.");
        var exited = process.WaitForExitAsync();
        var clock = Stopwatch.StartNew();
        while (!exited.IsCompleted)
        {
            await Task.WhenAny(exited, Task.Delay(1000)).ConfigureAwait(false);
            if (!exited.IsCompleted)
                progress?.Report($"Unity is working ({clock.Elapsed:mm\\:ss}) {LogExcerpt.LastLine(logPath)}".TrimEnd());
        }
        return process.ExitCode;
    }
}

public static class UnityLauncher
{
    public const string EntryMethod = "Blackglass.AssetPipeline.AssetPipelineRunner.RunFromCommandLine";

    /// <summary>The Editor holds an exclusive lock on Temp/UnityLockfile while the project is open. A stale file from a crash is not locked.</summary>
    public static bool IsProjectOpen(string projectPath)
    {
        var lockfile = Path.Combine(projectPath, "Temp", "UnityLockfile");
        if (!File.Exists(lockfile)) return false;
        try
        {
            using var fs = new FileStream(lockfile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static List<string> BuildArguments(string projectPath, string manifestPath, string resultPath, string logPath) => new()
    {
        "-batchmode", "-projectPath", projectPath,
        "-executeMethod", EntryMethod,
        "-blackglassManifest", manifestPath,
        "-blackglassResult", resultPath,
        "-quit", "-logFile", logPath,
    };
}
