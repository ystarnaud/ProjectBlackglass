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

            // The texture folder follows the same rule: it is only replaced when the model it belongs to is Studio-owned (or overwrite is allowed).
            var fbmTarget = FbmTargetPath(item);
            if (SourceFbm(item) != null && Directory.Exists(AssetPaths.Full(fbmTarget)))
            {
                var model = AssetNaming.ModelPath(item);
                var modelOwned = File.Exists(AssetPaths.Full(model)) && OwnershipLabel.IsOwned(model);
                if (!modelOwned && !item.allowOverwrite)
                {
                    result.errors.Add(fbmTarget + " already exists and was not created by Asset Studio. Tick Allow overwrite to replace it.");
                    ok = false;
                }
            }
            return ok;
        }

        /// <summary>The source's sibling "&lt;source name&gt;.fbm" texture folder, or null when there is none.</summary>
        static string SourceFbm(ImportItem item)
        {
            var directory = Path.GetDirectoryName(item.sourcePath);
            if (string.IsNullOrEmpty(directory)) return null;
            var fbm = Path.Combine(directory, Path.GetFileNameWithoutExtension(item.sourcePath) + ".fbm");
            return Directory.Exists(fbm) ? fbm : null;
        }

        /// <summary>Unity looks for a model's extracted textures in "&lt;model file name&gt;.fbm", so the folder is named after the target model, not the source.</summary>
        static string FbmTargetPath(ImportItem item)
        {
            return PathRules.Normalize(item.destinationFolder) + "/" + item.name + ".fbm";
        }

        /// <summary>Copies the staged source (and a sibling .fbm texture folder, renamed after the target model) into the destination and imports it synchronously. Returns the model's asset path.</summary>
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

            var fbm = SourceFbm(item);
            if (fbm != null)
                CopyDirectory(fbm, AssetPaths.Full(FbmTargetPath(item)));

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
