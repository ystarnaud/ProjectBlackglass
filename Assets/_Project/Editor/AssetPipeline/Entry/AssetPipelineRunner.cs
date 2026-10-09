using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    /// <summary>
    /// Batch/menu entry. Reads the manifest Asset Studio wrote, imports every item with Unity's own APIs, and writes a
    /// structured result (even when something fails). One item failing never stops the others.
    /// Command line: -executeMethod Blackglass.AssetPipeline.AssetPipelineRunner.RunFromCommandLine -blackglassManifest m.json -blackglassResult r.json
    /// </summary>
    public static class AssetPipelineRunner
    {
        public const string ManifestArg = "-blackglassManifest";
        public const string ResultArg = "-blackglassResult";
        const string LogPrefix = "[AssetPipeline] ";

        [MenuItem("Blackglass/Asset Pipeline/Run Manifest...")]
        static void RunFromMenu()
        {
            var path = EditorUtility.OpenFilePanel("Choose an Asset Studio manifest", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            var result = Run(path, null);
            EditorUtility.DisplayDialog("Asset Pipeline", result.success ? "Import finished." : "Import finished with errors. See the Console and the result file.", "OK");
        }

        public static void RunFromCommandLine()
        {
            var code = 1;
            try
            {
                var result = Run(ArgValue(ManifestArg), ArgValue(ResultArg));
                code = result.success ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogError(LogPrefix + "ERROR " + e);
            }
            EditorApplication.Exit(code);
        }

        public static ImportResult Run(string manifestPath, string resultPathOverride)
        {
            var result = new ImportResult { unityVersion = Application.unityVersion };
            var resultPath = resultPathOverride;
            try
            {
                if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                {
                    Fatal(result, "The manifest file was not found: " + manifestPath);
                    return Finish(result, resultPath);
                }
                var manifest = JsonUtility.FromJson<ImportManifest>(File.ReadAllText(manifestPath));
                if (manifest == null)
                {
                    Fatal(result, "The manifest file is empty or is not valid JSON: " + manifestPath);
                    return Finish(result, resultPath);
                }
                result.runId = manifest.runId;
                if (string.IsNullOrEmpty(resultPath)) resultPath = manifest.resultPath;
                if (manifest.schemaVersion != ContractInfo.SchemaVersion)
                {
                    Fatal(result, "Unsupported manifest schema version " + manifest.schemaVersion + " (this Unity importer reads version " + ContractInfo.SchemaVersion + ").");
                    return Finish(result, resultPath);
                }
                // An explicit "items": null in the JSON overwrites the default empty array.
                var items = manifest.items ?? new ImportItem[0];
                foreach (var item in items)
                    result.items.Add(ImportOne(item));
            }
            catch (Exception e)
            {
                Fatal(result, e.Message);
                Debug.LogError(LogPrefix + "ERROR " + e);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return Finish(result, resultPath);
        }

        /// <summary>Validates and imports one item; never throws (an exception becomes an item error). Public so tests can drive the importers.</summary>
        public static ItemResult ImportOne(ImportItem item)
        {
            var r = new ItemResult();
            try
            {
                if (item == null)
                {
                    r.errors.Add("The manifest contains an empty item.");
                }
                else
                {
                    r.id = item.id;
                    EnsureBlocks(item);
                    var problem = PathRules.ValidateAssetName(item.name) ?? PathRules.ValidateDestination(item.destinationFolder);
                    if (problem != null)
                        r.errors.Add(problem);
                    else
                        switch (item.profile)
                        {
                            // One case per profile is added by the importer tasks.
                            case ProfileIds.HumanoidCharacter:
                                CharacterImporter.Import(item, r);
                                break;
                            default:
                                r.errors.Add("Unknown or not yet supported import profile '" + item.profile + "'.");
                                break;
                        }
                }
            }
            catch (Exception e)
            {
                r.errors.Add(e.Message);
                Debug.LogError(LogPrefix + "ERROR importing '" + (item != null ? item.name : "(empty item)") + "': " + e);
            }
            r.success = r.errors.Count == 0;
            if (!r.success)
                foreach (var error in r.errors) Debug.LogError(LogPrefix + "ERROR " + (item != null ? item.name : "(empty item)") + ": " + error);
            return r;
        }

        /// <summary>An explicit null block in the manifest (for example "character": null) overwrites the default; put the defaults back so importers never see null.</summary>
        static void EnsureBlocks(ImportItem item)
        {
            if (item.character == null) item.character = new CharacterSettings();
            if (item.animation == null) item.animation = new AnimationSettings();
            if (item.prop == null) item.prop = new PropSettings();
            if (item.environment == null) item.environment = new EnvironmentSettings();
        }

        static void Fatal(ImportResult result, string message)
        {
            result.errors.Add(message);
            Debug.LogError(LogPrefix + "ERROR " + message);
        }

        static ImportResult Finish(ImportResult result, string resultPath)
        {
            result.success = result.errors.Count == 0 && result.items.All(i => i.success);
            if (!string.IsNullOrEmpty(resultPath))
            {
                // System.Text.Json (the app) rejects NaN and Infinity, so no non-finite float may reach the file.
                foreach (var item in result.items) Sanitize(item);
                var directory = Path.GetDirectoryName(resultPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(resultPath, JsonUtility.ToJson(result, true));
            }
            return result;
        }

        /// <summary>Replaces NaN/Infinity (which System.Text.Json refuses to read) with safe values.</summary>
        public static void Sanitize(ItemResult item)
        {
            item.measuredHeight = Finite(item.measuredHeight, 0f);
            item.targetHeight = Finite(item.targetHeight, 0f);
            item.appliedScale = Finite(item.appliedScale, 1f);
            if (item.dimensions != null)
            {
                item.dimensions.x = Finite(item.dimensions.x, 0f);
                item.dimensions.y = Finite(item.dimensions.y, 0f);
                item.dimensions.z = Finite(item.dimensions.z, 0f);
            }
            foreach (var clip in item.clips) clip.duration = Finite(clip.duration, 0f);
        }

        static float Finite(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
        }

        static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
