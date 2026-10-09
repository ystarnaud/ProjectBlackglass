namespace Blackglass.AssetStudio;

/// <summary>Per-user settings (persisted by <see cref="SettingsStore"/>). Nothing here is ever committed.</summary>
public sealed class AppSettings
{
    public string ProjectPath { get; set; } = "";
    public string UnityExePath { get; set; } = "";
    /// <summary>Folder for run files (manifests, staged sources, logs). Empty = %LocalAppData%\BlackglassAssetStudio\runs.</summary>
    public string StagingFolder { get; set; } = "";
    public float DefaultTargetHeight { get; set; } = 1.85f;
    /// <summary>Project-relative path of the Humanoid model whose Avatar animation clips copy.</summary>
    public string DefaultSharedAvatarPath { get; set; } = "";
    public string LastDropFolder { get; set; } = "";
    /// <summary>The last folder files were added from that is outside the project; the Add dialogs start there.</summary>
    public string LastSourceFolder { get; set; } = "";
}
