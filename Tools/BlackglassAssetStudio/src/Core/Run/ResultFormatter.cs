using System.Text;
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public static class ResultFormatter
{
    public static string Format(ImportResult r, Func<string, string> nameOf, IReadOnlyList<string> changedFiles, string logPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Unity {r.unityVersion}: {(r.success ? "SUCCESS" : "FAILED")}");
        foreach (var e in r.errors) sb.AppendLine($"  ERROR: {e}");
        foreach (var w in r.warnings) sb.AppendLine($"  warning: {w}");
        foreach (var item in r.items)
        {
            sb.AppendLine();
            sb.AppendLine($"[{(item.success ? " OK " : "FAIL")}] {nameOf(item.id)}");
            foreach (var a in item.importedAssets) sb.AppendLine($"  imported: {a}");
            foreach (var p in item.createdPrefabs) sb.AppendLine($"  prefab:   {p}");
            foreach (var c in item.changedAssets) sb.AppendLine($"  updated:  {c}");
            if (!string.IsNullOrEmpty(item.registeredInTheme)) sb.AppendLine($"  theme:    {item.registeredInTheme}");
            if (item.measuredHeight > 0)
                sb.AppendLine($"  height:   measured {item.measuredHeight:0.000} m, target {item.targetHeight:0.000} m, visual scale {item.appliedScale:0.0000} ({item.scaleSource})");
            if (item.dimensions.x > 0 || item.dimensions.y > 0 || item.dimensions.z > 0)
                sb.AppendLine($"  size:     {item.dimensions.x:0.###} x {item.dimensions.y:0.###} x {item.dimensions.z:0.###} m");
            if (item.avatar.valid || item.avatar.isHuman || !string.IsNullOrEmpty(item.avatar.message))
                sb.AppendLine($"  avatar:   {(item.avatar.valid ? "valid" : "INVALID")}, {(item.avatar.isHuman ? "humanoid" : "not humanoid")} {item.avatar.message}".TrimEnd());
            foreach (var c in item.clips)
                sb.AppendLine($"  clip:     {c.name}, {c.duration:0.00} s, loop {Yn(c.loop)}, bake rotation {Yn(c.bakeRotation)} height {Yn(c.bakeHeight)} XZ {Yn(c.bakePositionXZ)}");
            foreach (var w in item.warnings) sb.AppendLine($"  warning:  {w}");
            foreach (var e in item.errors) sb.AppendLine($"  ERROR:    {e}");
        }
        if (changedFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Project files changed by this run (git):");
            foreach (var f in changedFiles) sb.AppendLine($"  {f}");
        }
        sb.AppendLine();
        sb.AppendLine($"Unity log: {logPath}");
        return sb.ToString();
    }

    static string Yn(bool b) => b ? "yes" : "no";
}
