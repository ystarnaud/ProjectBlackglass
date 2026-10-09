using System.Text.RegularExpressions;

namespace Blackglass.AssetStudio;

public static class ProjectLocator
{
    public static bool IsProject(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Directory.Exists(Path.Combine(path, "Assets"))
        && File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"));

    /// <summary>The nearest folder at or above <paramref name="startDir"/> that is a Unity project, or null.</summary>
    public static string? FindFrom(string startDir)
    {
        for (var d = new DirectoryInfo(startDir); d != null; d = d.Parent)
            if (IsProject(d.FullName)) return d.FullName;
        return null;
    }

    public static string? ReadEditorVersion(string projectPath)
    {
        var file = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(file)) return null;
        var m = Regex.Match(File.ReadAllText(file), @"^m_EditorVersion:\s*(\S+)", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }
}
