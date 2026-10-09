using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Blackglass.AssetStudio;

/// <summary>Reports which project files an import touched by comparing `git status` before and after. Read-only: never stages or commits.</summary>
public static class GitSnapshot
{
    public static IReadOnlyDictionary<string, string>? TryCapture(string projectPath)
    {
        var root = Git(projectPath, "rev-parse", "--show-toplevel")?.Trim();
        if (string.IsNullOrEmpty(root)) return null;
        var status = Git(projectPath, "status", "--porcelain=v1", "-z", "-uall");
        return status == null ? null : Parse(status, root.Replace('/', Path.DirectorySeparatorChar));
    }

    public static Dictionary<string, string> Parse(string porcelainZ, string repoRoot)
    {
        var map = new Dictionary<string, string>();
        var entries = porcelainZ.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4) continue;
            var status = entry[..2];
            var path = entry[3..];
            if (status[0] is 'R' or 'C') i++; // the next entry is the original path
            var full = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var stamp = File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks.ToString() : "-";
            map[path] = status + "|" + stamp;
        }
        return map;
    }

    public static IReadOnlyList<string> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        after.Where(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{Label(kv.Value[..2])}: {kv.Key}").ToList();

    static string Label(string status) =>
        status == "??" ? "new" : status.Contains('D') ? "deleted" : status.Contains('A') ? "added" : status.Contains('M') ? "modified" : status.Trim();

    static string? Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var error = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return null; }
    }
}
