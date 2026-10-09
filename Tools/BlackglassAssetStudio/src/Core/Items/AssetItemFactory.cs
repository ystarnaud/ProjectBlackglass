using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public static class AssetItemFactory
{
    public static AssetItem Create(string path, string? dropFolder, AppSettings settings)
    {
        var c = AssetClassifier.Classify(path, dropFolder);
        var profile = ImportProfiles.Get(c.ProfileId);
        var name = AssetNames.FromFileStem(Path.GetFileNameWithoutExtension(path));

        var item = new AssetItem
        {
            OriginalPath = path,
            ProfileId = profile.Id,
            ClassificationReason = c.Reason,
            UnsupportedReason = c.Supported ? null : c.Reason,
            Import = new ImportItem { name = name, sourcePath = path },
        };
        item.Import.id = item.Id;
        item.Import.animation.clipName = c.ClipName;
        item.Import.environment.element = c.Element;
        ImportProfiles.Apply(item.Import, profile, settings);
        return item;
    }
}
