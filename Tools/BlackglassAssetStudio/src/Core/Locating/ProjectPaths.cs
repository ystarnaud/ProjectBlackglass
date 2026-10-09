namespace Blackglass.AssetStudio;

/// <summary>Converts between disk paths and the project-relative "Assets/..." paths the manifest uses.</summary>
public static class ProjectPaths
{
    const string OutsideAssets = "Choose a location inside the project's Assets folder.";

    /// <summary>True with an "Assets/..." path when <paramref name="absolutePath"/> is the Assets folder or below it; otherwise false with a reason.</summary>
    public static bool TryToProjectPath(string projectRoot, string absolutePath, out string projectPath, out string error)
    {
        projectPath = "";
        error = OutsideAssets;
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(absolutePath)) return false;
        try
        {
            var assets = Path.GetFullPath(Path.Combine(projectRoot, "Assets")).TrimEnd('\\', '/');
            var full = Path.GetFullPath(absolutePath).TrimEnd('\\', '/');
            if (string.Equals(full, assets, StringComparison.OrdinalIgnoreCase)) { projectPath = "Assets"; error = ""; return true; }
            if (!full.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            projectPath = "Assets/" + full.Substring(assets.Length + 1).Replace('\\', '/');
            error = "";
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "That path cannot be used: " + e.Message;
            return false;
        }
    }

    /// <summary>A typed project path with forward slashes, no surrounding spaces and the exact case of the leading "Assets" (the manifest rules are case-sensitive).</summary>
    public static string Normalize(string? typed)
    {
        var path = (typed ?? "").Trim().Replace('\\', '/');
        if (path.Equals("assets", StringComparison.OrdinalIgnoreCase)) return "Assets";
        return path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? "Assets" + path.Substring(6) : path;
    }

    /// <summary>True for "Assets" or "Assets/..." with no "." or ".." segments.</summary>
    public static bool IsProjectPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var segments = path.Split('/');
        return segments[0] == "Assets" && !segments.Any(s => s is "." or "..");
    }

    /// <summary>The disk path of a project path; an empty project path is the Assets folder.</summary>
    public static string ToAbsolute(string projectRoot, string projectPath) =>
        string.IsNullOrWhiteSpace(projectPath)
            ? Path.Combine(projectRoot, "Assets")
            : Path.Combine(projectRoot, projectPath.Replace('/', Path.DirectorySeparatorChar));
}
