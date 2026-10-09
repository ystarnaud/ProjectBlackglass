using System.ComponentModel;
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public sealed record RunOutcome(string? FatalError, string RunId, string RunDir, string LogPath, int ExitCode,
    ImportResult? Result, IReadOnlyList<string> ChangedFiles)
{
    public bool Success => FatalError == null && Result is { success: true };
}

public sealed class ImportRunner
{
    readonly AppSettings settings;
    readonly IUnityProcess process;
    readonly Func<string, bool> isProjectOpen;
    readonly Func<string, IReadOnlyDictionary<string, string>?> gitSnapshot;

    public ImportRunner(AppSettings settings, IUnityProcess process, Func<string, bool>? isProjectOpen = null,
        Func<string, IReadOnlyDictionary<string, string>?>? gitSnapshot = null)
    {
        this.settings = settings;
        this.process = process;
        this.isProjectOpen = isProjectOpen ?? UnityLauncher.IsProjectOpen;
        this.gitSnapshot = gitSnapshot ?? GitSnapshot.TryCapture;
    }

    public async Task<RunOutcome> RunAsync(IReadOnlyList<AssetItem> items, IProgress<string>? progress = null)
    {
        static RunOutcome Fatal(string message, StagedRun? run = null, int exit = -1, IReadOnlyList<string>? changed = null) =>
            new(message, run?.RunId ?? "", run?.RunDir ?? "", run?.LogPath ?? "", exit, null, changed ?? Array.Empty<string>());

        if (items.Count == 0) return Fatal("There is nothing to import. Add files first.");
        if (!ProjectLocator.IsProject(settings.ProjectPath))
            return Fatal("The project path in Settings is not a Unity project (it needs an Assets folder and ProjectSettings/ProjectVersion.txt).");
        var unity = UnityLocator.Resolve(settings, settings.ProjectPath);
        if (unity.Path == null) return Fatal(unity.Message);
        if (isProjectOpen(settings.ProjectPath))
            return Fatal("The Unity Editor has this project open. Close it, then try again (Unity allows only one instance per project).");

        var validation = new ItemValidator(settings.ProjectPath).ValidateAll(items);
        var errors = items.SelectMany(i => validation[i.Id].Where(m => m.Severity == Severity.Error).Select(m => $"{i.Import.name}: {m.Text}")).ToList();
        if (errors.Count > 0) return Fatal("Fix these before importing:\n  " + string.Join("\n  ", errors));

        progress?.Report("Staging files...");
        StagedRun run;
        try { run = new RunStaging(AppPaths.RunsRoot(settings)).Create(items); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Fatal($"Could not stage the source files: {e.Message}"); }

        File.WriteAllText(run.ManifestPath, ManifestJson.Serialize(ManifestBuilder.Build(run, items)));
        var projectPath = settings.ProjectPath;
        // git runs as a blocking child process; keep it off the caller's (UI) thread.
        var before = await Task.Run(() => gitSnapshot(projectPath));

        progress?.Report("Starting Unity...");
        int exit;
        try
        {
            exit = await process.RunAsync(unity.Path, UnityLauncher.BuildArguments(projectPath, run.ManifestPath, run.ResultPath, run.LogPath), progress, run.LogPath);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        { return Fatal($"Could not start Unity: {e.Message}", run); }

        var after = await Task.Run(() => gitSnapshot(projectPath));
        IReadOnlyList<string> changed = before != null && after != null ? GitSnapshot.Diff(before, after) : Array.Empty<string>();

        var (result, readError) = ReadResult(run.ResultPath);
        if (result == null)
        {
            var lines = LogExcerpt.ErrorLines(run.LogPath);
            var message = $"Unity finished (exit code {exit}) without a usable result. {readError}\nUnity log: {run.LogPath}";
            if (lines.Count > 0) message += "\nLast errors in the log:\n  " + string.Join("\n  ", lines);
            return Fatal(message, run, exit, changed);
        }
        return new RunOutcome(null, run.RunId, run.RunDir, run.LogPath, exit, result, changed);
    }

    static (ImportResult?, string) ReadResult(string path)
    {
        if (!File.Exists(path)) return (null, "No result file was written.");
        try
        {
            var result = ManifestJson.Deserialize<ImportResult>(File.ReadAllText(path));
            return result.schemaVersion == ContractInfo.SchemaVersion
                ? (result, "")
                : (null, $"The result file has schema version {result.schemaVersion}; this app expects {ContractInfo.SchemaVersion}.");
        }
        catch (System.Text.Json.JsonException e) { return (null, $"The result file could not be read: {e.Message}"); }
    }
}
