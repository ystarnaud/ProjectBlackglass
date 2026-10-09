using System.IO;
using UnityEditor;

namespace Blackglass.AssetPipeline
{
    public static class SourceCopier
    {
        /// <summary>False (with errors added) when a target already exists, is not Studio-owned, and overwrite was not allowed.</summary>
        public static bool CheckConflicts(ImportItem item, ItemResult result)
        {
            var ok = true;
            foreach (var target in new[] { AssetNaming.ModelPath(item), AssetNaming.PrefabPath(item) })
            {
                if (target == null || !File.Exists(AssetPaths.Full(target))) continue;
                if (OwnershipLabel.IsOwned(target) || item.allowOverwrite) continue;
                result.errors.Add(target + " already exists and was not created by Asset Studio. Tick Allow overwrite to replace it.");
                ok = false;
            }
            return ok;
        }

        /// <summary>Copies the staged source (and a sibling .fbm texture folder) into the destination and imports it synchronously. Returns the model's asset path.</summary>
        public static string CopyAndImport(ImportItem item, ItemResult result)
        {
            var folder = PathRules.Normalize(item.destinationFolder);
            AssetPaths.EnsureFolder(folder);
            var modelPath = AssetNaming.ModelPath(item);
            var target = AssetPaths.Full(modelPath);

            if (Path.GetFullPath(item.sourcePath) == Path.GetFullPath(target))
                throw new IOException("The source file is the destination file: " + modelPath);

            var existed = File.Exists(target);
            File.Copy(item.sourcePath, target, true);

            var fbm = Path.Combine(Path.GetDirectoryName(item.sourcePath), Path.GetFileNameWithoutExtension(item.sourcePath) + ".fbm");
            if (Directory.Exists(fbm))
                CopyDirectory(fbm, Path.Combine(AssetPaths.Full(folder), Path.GetFileName(fbm)));

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            result.importedAssets.Add(modelPath);
            if (existed) result.changedAssets.Add(modelPath);
            return modelPath;
        }

        static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
