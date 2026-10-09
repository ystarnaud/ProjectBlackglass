using System.Text.Json;

namespace Blackglass.AssetStudio;

public sealed record UnityResolution(string? Path, string Message);

/// <summary>Finds the Unity Editor matching the project's version. Never substitutes a different version.</summary>
public static class UnityLocator
{
    public static string? FindExecutable(string version, IEnumerable<string> hubEditorRoots)
    {
        foreach (var root in hubEditorRoots)
        {
            var exe = Path.Combine(root, version, "Editor", "Unity.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    public static IEnumerable<string> DefaultHubRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Unity", "Hub", "Editor");

        var secondary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "secondaryInstallPath.json");
        string? extra = null;
        try
        {
            if (File.Exists(secondary))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(secondary));
                if (doc.RootElement.ValueKind == JsonValueKind.String) extra = doc.RootElement.GetString();
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        if (!string.IsNullOrWhiteSpace(extra)) yield return extra;
    }

    public static UnityResolution Resolve(AppSettings settings, string projectPath, IEnumerable<string>? hubRoots = null)
    {
        if (!string.IsNullOrWhiteSpace(settings.UnityExePath))
            return File.Exists(settings.UnityExePath)
                ? new UnityResolution(settings.UnityExePath, "Using the Unity executable from settings.")
                : new UnityResolution(null, $"The Unity executable in settings does not exist: {settings.UnityExePath}");

        var version = ProjectLocator.ReadEditorVersion(projectPath);
        if (version == null)
            return new UnityResolution(null, "Could not read the Unity version from ProjectSettings/ProjectVersion.txt. Set the Unity executable in Settings.");
        var exe = FindExecutable(version, hubRoots ?? DefaultHubRoots());
        return exe != null
            ? new UnityResolution(exe, $"Found Unity {version}.")
            : new UnityResolution(null, $"Unity {version} (the version this project uses) was not found in the usual Unity Hub folders. Install it, or set the Unity executable in Settings.");
    }
}
