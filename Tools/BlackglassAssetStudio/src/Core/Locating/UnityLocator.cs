using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    static readonly Regex VersionPattern = new(@"^\d{4}\.\d+\.\d+[abfpc]\d+$", RegexOptions.Compiled);
    static readonly Regex VersionInProduct = new(@"\d{4}\.\d+\.\d+[abfpc]\d+", RegexOptions.Compiled);

    /// <summary>
    /// Best-effort editor version of a Unity.exe: the Unity Hub folder name (<c>&lt;version&gt;\Editor\Unity.exe</c>) when it looks like a
    /// version, else the executable's product version resource. Null when neither is known.
    /// </summary>
    public static string? DetectEditorVersion(string exePath)
    {
        try
        {
            var editorDir = Path.GetDirectoryName(exePath);
            var versionDir = editorDir == null ? null : Path.GetFileName(Path.GetDirectoryName(editorDir));
            if (versionDir != null && VersionPattern.IsMatch(versionDir)) return versionDir;

            var product = FileVersionInfo.GetVersionInfo(exePath).ProductVersion;
            if (!string.IsNullOrEmpty(product))
            {
                var m = VersionInProduct.Match(product);
                if (m.Success) return m.Value;
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { }
        return null;
    }

    public static UnityResolution Resolve(AppSettings settings, string projectPath, IEnumerable<string>? hubRoots = null)
    {
        var version = ProjectLocator.ReadEditorVersion(projectPath);

        if (!string.IsNullOrWhiteSpace(settings.UnityExePath))
        {
            var chosen = settings.UnityExePath;
            if (!File.Exists(chosen))
                return new UnityResolution(null, $"The Unity executable in settings does not exist: {chosen}");
            var chosenVersion = DetectEditorVersion(chosen);
            if (chosenVersion != null && version != null && !string.Equals(chosenVersion, version, StringComparison.OrdinalIgnoreCase))
                return new UnityResolution(null, $"Unity {chosenVersion} is selected in Settings, but the project uses Unity {version}. Browse to the Unity.exe of Unity {version}, or clear the Unity executable setting so it is found automatically.");
            if (chosenVersion != null)
                return new UnityResolution(chosen, $"Using Unity {chosenVersion} from settings.");
            return new UnityResolution(chosen, version == null
                ? "Using the Unity executable from settings (its version could not be verified)."
                : $"Using the Unity executable from settings; its version could not be verified against the project's Unity {version}.");
        }

        if (version == null)
            return new UnityResolution(null, "Could not read the Unity version from ProjectSettings/ProjectVersion.txt. Set the Unity executable in Settings.");
        var exe = FindExecutable(version, hubRoots ?? DefaultHubRoots());
        return exe != null
            ? new UnityResolution(exe, $"Found Unity {version}.")
            : new UnityResolution(null, $"Unity {version} (the version this project uses) was not found in the usual Unity Hub folders. Install it, or set the Unity executable in Settings.");
    }
}
