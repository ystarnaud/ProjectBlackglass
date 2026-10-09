using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class EnvironmentModuleImporter
    {
        public static void Import(ImportItem item, ItemResult r)
        {
            var e = item.environment ?? new EnvironmentSettings();
            if (!Enum.TryParse(e.element, out EnvironmentElement element) || Array.IndexOf(EnvironmentElements.All, e.element) < 0)
            {
                r.errors.Add("'" + e.element + "' is not an environment element. Use one of: " + string.Join(", ", EnvironmentElements.All) + ".");
                return;
            }
            if (!(e.scale > 0f))
            {
                r.errors.Add("The module scale must be greater than 0.");
                return;
            }
            if (!SourceCopier.CheckConflicts(item, r)) return;

            var modelPath = SourceCopier.CopyAndImport(item, r);
            PropImporter.ConfigureMeshImport(modelPath);
            OwnershipLabel.Mark(modelPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);

            var size = PropImporter.MeasureInto(model, e.scale, r, out var min, out var max);
            if (size == null) return;
            if (Mathf.Abs(e.scale - 1f) > 1e-6f)
                r.warnings.Add("An explicit scale of " + e.scale.ToString("0.###") + " was applied to the module, as requested.");
            r.warnings.AddRange(EnvironmentSpecs.Check(element, size.Value, min, max));

            var prefabPath = AssetNaming.PrefabPath(item);
            var existed = File.Exists(AssetPaths.Full(prefabPath));
            var prefab = ModelPrefabWriter.Write(prefabPath, item.name, model, "Model", e.scale, null);
            OwnershipLabel.Mark(prefabPath);
            (existed ? r.changedAssets : r.createdPrefabs).Add(prefabPath);

            if (e.themeMode == ThemeModes.Append || e.themeMode == ThemeModes.Replace)
            {
                var problem = Register(e.themePath, element, e.themeMode, e.replaceIndex, prefab);
                if (problem != null)
                {
                    r.errors.Add(problem);
                    return;
                }
                r.registeredInTheme = e.themePath + " (" + element + ", " + e.themeMode + ")";
                if (!r.changedAssets.Contains(e.themePath)) r.changedAssets.Add(e.themePath);
            }
        }

        /// <summary>Adds or replaces a variant in an EnvironmentTheme through SerializedObject (its fields are private). Null on success, otherwise a message.</summary>
        public static string Register(string themePath, EnvironmentElement element, string mode, int replaceIndex, GameObject prefab)
        {
            var theme = AssetDatabase.LoadAssetAtPath<EnvironmentTheme>(themePath);
            if (theme == null) return "The theme asset was not found or is not an EnvironmentTheme: " + themePath;

            var so = new SerializedObject(theme);
            var entries = so.FindProperty("entries");
            SerializedProperty entry = null;
            for (var i = 0; i < entries.arraySize; i++)
            {
                var candidate = entries.GetArrayElementAtIndex(i);
                if (candidate.FindPropertyRelative("element").intValue == (int)element)
                {
                    entry = candidate;
                    break;
                }
            }
            if (entry == null)
            {
                entries.arraySize++;
                entry = entries.GetArrayElementAtIndex(entries.arraySize - 1);
                entry.FindPropertyRelative("element").intValue = (int)element;
                entry.FindPropertyRelative("variants").arraySize = 0;
            }

            var variants = entry.FindPropertyRelative("variants");
            if (mode == ThemeModes.Replace)
            {
                if (replaceIndex < 0 || replaceIndex >= variants.arraySize)
                    return "The theme has " + variants.arraySize + " variant(s) for " + element + "; index " + replaceIndex + " is out of range.";
                variants.GetArrayElementAtIndex(replaceIndex).objectReferenceValue = prefab;
            }
            else
            {
                for (var i = 0; i < variants.arraySize; i++)
                    if (variants.GetArrayElementAtIndex(i).objectReferenceValue == prefab)
                        return null; // already registered
                variants.arraySize++;
                variants.GetArrayElementAtIndex(variants.arraySize - 1).objectReferenceValue = prefab;
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(theme);
            AssetDatabase.SaveAssets();
            return null;
        }
    }
}
