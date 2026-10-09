using System.Text.RegularExpressions;

namespace Blackglass.AssetStudio;

public sealed record StagedRun(string RunId, string RunDir, string ManifestPath, string ResultPath, string LogPath,
    IReadOnlyDictionary<string, string> StagedPaths);

/// <summary>Copies sources into a per-run folder so the project only ever receives imported assets and the originals are never opened for writing.</summary>
public sealed class RunStaging
{
    public const int KeepRuns = 10;
    static readonly Regex RunFolder = new(@"^\d{8}-\d{6}-[0-9a-f]{4}$");
    readonly string root;

    public RunStaging(string runsRoot) => root = runsRoot;

    public StagedRun Create(IReadOnlyList<AssetItem> items, DateTime? now = null)
    {
        Directory.CreateDirectory(root);
        Prune(KeepRuns - 1);
        var runId = $"{now ?? DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var runDir = Path.Combine(root, runId);
        var staged = new Dictionary<string, string>();
        foreach (var item in items)
        {
            var dir = Path.Combine(runDir, "staged", item.Id);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(item.OriginalPath));
            File.Copy(item.OriginalPath, target, overwrite: true);
            var fbm = Path.Combine(Path.GetDirectoryName(item.OriginalPath)!, Path.GetFileNameWithoutExtension(item.OriginalPath) + ".fbm");
            if (Directory.Exists(fbm)) CopyDirectory(fbm, Path.Combine(dir, Path.GetFileName(fbm)));
            staged[item.Id] = target;
        }
        return new StagedRun(runId, runDir, Path.Combine(runDir, "manifest.json"), Path.Combine(runDir, "result.json"),
            Path.Combine(runDir, "unity.log"), staged);
    }

    void Prune(int keep)
    {
        var old = Directory.GetDirectories(root).Where(d => RunFolder.IsMatch(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(keep);
        foreach (var dir in old)
        {
            try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
