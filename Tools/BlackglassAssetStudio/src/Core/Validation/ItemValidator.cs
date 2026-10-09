using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public enum Severity { Info, Warning, Error }

public sealed record ValidationMessage(Severity Severity, string Text);

/// <summary>Instant local checks (no Unity). Measured values (height, bounds, Avatar, clips) come from the import itself.</summary>
public sealed class ItemValidator
{
    readonly string project;

    public ItemValidator(string projectPath) => project = projectPath;

    public List<ValidationMessage> Validate(AssetItem item)
    {
        var m = new List<ValidationMessage>();
        void Error(string text) => m.Add(new(Severity.Error, text));
        void Warn(string text) => m.Add(new(Severity.Warning, text));
        var i = item.Import;
        var profile = ImportProfiles.Get(item.ProfileId);

        if (item.UnsupportedReason != null) { Error(item.UnsupportedReason); return m; }
        if (!File.Exists(item.OriginalPath)) { Error($"The source file does not exist: {item.OriginalPath}"); return m; }

        var ext = Path.GetExtension(item.OriginalPath).ToLowerInvariant();
        if (!profile.Extensions.Contains(ext))
            Error($"{profile.DisplayName} cannot import '{ext}' files (accepts {string.Join(", ", profile.Extensions)}).");

        var nameError = PathRules.ValidateAssetName(i.name);
        if (nameError != null) Error(nameError);
        var destinationError = PathRules.ValidateDestination(i.destinationFolder);
        if (destinationError != null) Error(destinationError);

        switch (profile.Kind)
        {
            case ProfileIds.HumanoidCharacter:
                var c = i.character;
                if (c.normalizeHeight && c.scaleOverride == 0 && !(c.targetHeight > 0)) Error("The target height must be greater than 0.");
                if (c.scaleOverride < 0 || float.IsNaN(c.scaleOverride)) Error("The manual scale must be positive (or 0 for none).");
                if (c.rigHumanoid && !c.createAvatar) Error("A Humanoid rig needs an Avatar: tick Create Avatar or untick Humanoid.");
                break;
            case ProfileIds.HumanoidAnimation:
                var a = i.animation;
                if (string.IsNullOrWhiteSpace(a.clipName)) Error("Enter a clip name.");
                if (string.IsNullOrWhiteSpace(a.sharedAvatarPath))
                    Error("Pick a shared Avatar: a Humanoid model in the project that this clip should target (Settings > Default shared Avatar).");
                else if (!File.Exists(ProjectFile(a.sharedAvatarPath)))
                    Error($"The shared Avatar model does not exist in the project: {a.sharedAvatarPath}");
                break;
            case ProfileIds.GenericProp:
                if (!(i.prop.scale > 0)) Error("The prop scale must be greater than 0.");
                break;
            case ProfileIds.EnvironmentModule:
                var e = i.environment;
                if (!EnvironmentElements.All.Contains(e.element)) Error("Choose the environment element (wall, floor, cover, ...) this module is.");
                if (!(e.scale > 0)) Error("The module scale must be greater than 0.");
                else if (Math.Abs(e.scale - 1f) > 1e-6f) Warn($"A scale of {e.scale:0.###} will be applied to this module. Environment modules are never rescaled automatically; check it is what you want.");
                if (e.themeMode is ThemeModes.Append or ThemeModes.Replace)
                {
                    if (string.IsNullOrWhiteSpace(e.themePath) || !File.Exists(ProjectFile(e.themePath)))
                        Error($"The theme asset does not exist: {e.themePath}");
                    if (e.themeMode == ThemeModes.Replace && e.replaceIndex < 0) Error("The variant index to replace must be 0 or greater.");
                }
                break;
        }

        if (destinationError == null && nameError == null)
            foreach (var target in Targets(i))
                AddConflict(m, i, target);
        return m;
    }

    public Dictionary<string, List<ValidationMessage>> ValidateAll(IReadOnlyList<AssetItem> items)
    {
        var result = items.ToDictionary(x => x.Id, Validate);
        var owners = new Dictionary<string, List<AssetItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (PathRules.ValidateAssetName(item.Import.name) != null || PathRules.ValidateDestination(item.Import.destinationFolder) != null) continue;
            foreach (var target in Targets(item.Import))
            {
                if (!owners.TryGetValue(target, out var list)) owners[target] = list = new List<AssetItem>();
                list.Add(item);
            }
        }
        foreach (var (target, list) in owners.Where(p => p.Value.Count > 1))
            foreach (var item in list)
                result[item.Id].Add(new(Severity.Error, $"Another entry in this list targets the same asset ({target}). Rename one."));
        return result;
    }

    static IEnumerable<string> Targets(ImportItem i)
    {
        yield return AssetNaming.ModelPath(i);
        var prefab = AssetNaming.PrefabPath(i);
        if (prefab != null) yield return prefab;
    }

    void AddConflict(List<ValidationMessage> m, ImportItem i, string assetPath)
    {
        var full = ProjectFile(assetPath);
        if (!File.Exists(full)) return;
        if (OwnershipProbe.IsStudioOwned(full + ".meta"))
            m.Add(new(Severity.Info, $"{assetPath} exists and was created by Asset Studio: it will be updated."));
        else if (i.allowOverwrite)
            m.Add(new(Severity.Warning, $"{assetPath} exists and was not created by Asset Studio: it will be replaced."));
        else
            m.Add(new(Severity.Error, $"{assetPath} already exists and was not created by Asset Studio. Tick Allow overwrite to replace it."));
    }

    string ProjectFile(string assetPath) => Path.Combine(project, assetPath.Replace('/', Path.DirectorySeparatorChar));
}
