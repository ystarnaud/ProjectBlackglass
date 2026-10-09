using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio.Tests;

/// <summary>Runs the real runner against the real project. Opt-in: set BLACKGLASS_E2E=1 and close the Unity Editor first.</summary>
internal static class E2EFixture
{
    public const string Scratch = "Assets/_AssetStudioScratch";

    public static bool Enabled => Environment.GetEnvironmentVariable("BLACKGLASS_E2E") == "1";

    public static string Project => ProjectLocator.FindFrom(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("The tests must run from inside the Blackglass repository.");

    public static AppSettings Settings() => new()
    {
        ProjectPath = Project,
        StagingFolder = Path.Combine(Path.GetTempPath(), "bgas-e2e-runs"),
        DefaultSharedAvatarPath = "",
    };

    /// <summary>One of the existing Darius art files, used as importer input.</summary>
    public static string Source(string relativeToDarius) =>
        Path.Combine(Project, "Assets", "Art", "Characters", "Darius", relativeToDarius.Replace('/', Path.DirectorySeparatorChar));

    public static AssetItem Item(string sourcePath, string profileId, string name, AppSettings settings, Action<ImportItem>? tweak = null)
    {
        var item = AssetItemFactory.Create(sourcePath, null, settings);
        ImportProfiles.Apply(item.Import, ImportProfiles.Get(profileId), settings);
        item.ProfileId = profileId;
        item.Import.name = name;
        item.Import.destinationFolder = Scratch;
        tweak?.Invoke(item.Import);
        return item;
    }

    public static Task<RunOutcome> Run(AppSettings settings, params AssetItem[] items) =>
        new ImportRunner(settings, new UnityProcess()).RunAsync(items);

    public static void CleanScratch()
    {
        var folder = Path.Combine(Project, "Assets", "_AssetStudioScratch");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
    }
}
