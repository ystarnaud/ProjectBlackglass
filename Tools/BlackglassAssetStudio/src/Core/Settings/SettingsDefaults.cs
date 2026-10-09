namespace Blackglass.AssetStudio;

/// <summary>First-run conveniences. Only fills values that are still empty.</summary>
public static class SettingsDefaults
{
    /// <summary>The current known-good Humanoid model whose Avatar the existing clips copy (decision 034).</summary>
    public const string SharedAvatarCandidate = "Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx";

    public static void Fill(AppSettings s, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(s.ProjectPath))
            s.ProjectPath = ProjectLocator.FindFrom(baseDirectory) ?? "";
        if (string.IsNullOrWhiteSpace(s.DefaultSharedAvatarPath) && ProjectLocator.IsProject(s.ProjectPath)
            && File.Exists(Path.Combine(s.ProjectPath, SharedAvatarCandidate.Replace('/', Path.DirectorySeparatorChar))))
            s.DefaultSharedAvatarPath = SharedAvatarCandidate;
    }
}
