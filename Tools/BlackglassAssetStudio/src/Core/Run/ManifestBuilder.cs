using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public static class ManifestBuilder
{
    public static ImportManifest Build(StagedRun run, IReadOnlyList<AssetItem> items)
    {
        var manifest = new ImportManifest { runId = run.RunId, resultPath = run.ResultPath };
        manifest.items = items.Select(item =>
        {
            var profile = ImportProfiles.Get(item.ProfileId);
            var copy = ManifestJson.Clone(item.Import);
            copy.id = item.Id;
            copy.profile = profile.Kind;
            if (profile.IsAnimation) copy.animation.category = profile.Category;
            copy.destinationFolder = PathRules.Normalize(copy.destinationFolder);
            copy.sourcePath = run.StagedPaths[item.Id];
            return copy;
        }).ToArray();
        return manifest;
    }
}
