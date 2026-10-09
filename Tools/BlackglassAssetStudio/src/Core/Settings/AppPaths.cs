namespace Blackglass.AssetStudio;

public static class AppPaths
{
    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackglassAssetStudio", "settings.json");

    public static string DefaultRunsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlackglassAssetStudio", "runs");

    public static string RunsRoot(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.StagingFolder) ? DefaultRunsRoot : settings.StagingFolder;
}
