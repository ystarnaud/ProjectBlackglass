using System.Text.Json;

namespace Blackglass.AssetStudio;

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    readonly string path;

    public SettingsStore(string path) => this.path = path;

    /// <summary>Never throws: a missing or unreadable file gives the defaults.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            if (!(s.DefaultTargetHeight > 0f) || float.IsInfinity(s.DefaultTargetHeight)) s.DefaultTargetHeight = 1.85f;
            return s;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }
}
