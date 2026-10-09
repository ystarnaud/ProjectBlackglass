using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class PropImporter
    {
        public static void Import(ImportItem item, ItemResult r)
        {
            var p = item.prop ?? new PropSettings();
            if (!(p.scale > 0f))
            {
                r.errors.Add("The prop scale must be greater than 0.");
                return;
            }
            if (!SourceCopier.CheckConflicts(item, r)) return;

            var modelPath = SourceCopier.CopyAndImport(item, r);
            ConfigureMeshImport(modelPath);
            OwnershipLabel.Mark(modelPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);

            var size = MeasureInto(model, p.scale, r, out _, out _);
            if (size.HasValue)
            {
                var largest = Mathf.Max(size.Value.x, size.Value.y, size.Value.z);
                if (largest < 0.05f || largest > 10f)
                    r.warnings.Add("The prop's largest side is " + largest.ToString("0.###") + " m. Check the model's units or set a scale.");
            }
            if (r.errors.Count > 0 || !p.generatePrefab) return;

            var prefabPath = AssetNaming.PrefabPath(item);
            var existed = File.Exists(AssetPaths.Full(prefabPath));
            ModelPrefabWriter.Write(prefabPath, item.name, model, "Model", p.scale, null);
            OwnershipLabel.Mark(prefabPath);
            (existed ? r.changedAssets : r.createdPrefabs).Add(prefabPath);
        }

        /// <summary>Mesh-only import: no animation, no colliders, materials inside the model.</summary>
        public static void ConfigureMeshImport(string modelPath)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.animationType = ModelImporterAnimationType.None;
            importer.importAnimation = false;
            importer.addCollider = false;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// Measures the model at the origin and fills dimensions (already multiplied by the explicit scale). Returns the scaled size,
        /// or null with an error when there is no geometry. min/max are the scaled bounds corners.
        /// </summary>
        public static Vector3? MeasureInto(GameObject model, float scale, ItemResult r, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var bounds = ModelMeasure.Measure(instance);
                if (bounds == null)
                {
                    r.errors.Add("The imported model contains no meshes.");
                    return null;
                }
                var b = bounds.Value;
                min = b.min * scale;
                max = b.max * scale;
                var size = b.size * scale;
                r.dimensions = new Size3 { x = size.x, y = size.y, z = size.z };
                return size;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
    }
}
