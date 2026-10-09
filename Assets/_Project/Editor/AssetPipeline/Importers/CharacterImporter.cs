using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class CharacterImporter
    {
        public static void Import(ImportItem item, ItemResult r)
        {
            var c = item.character ?? new CharacterSettings();
            r.targetHeight = c.targetHeight;

            if (Path.GetExtension(item.sourcePath).ToLowerInvariant() != ".fbx")
            {
                r.errors.Add("Humanoid characters must be FBX files.");
                return;
            }
            if (c.rigHumanoid && !c.createAvatar)
            {
                r.errors.Add("A Humanoid rig needs an Avatar: enable Create Avatar or untick Humanoid.");
                return;
            }
            if (c.scaleOverride < 0f || float.IsNaN(c.scaleOverride))
            {
                r.errors.Add("The manual scale must be positive (or 0 for none).");
                return;
            }
            if (!SourceCopier.CheckConflicts(item, r)) return;

            var modelPath = SourceCopier.CopyAndImport(item, r);
            Configure(modelPath, c);
            OwnershipLabel.Mark(modelPath);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
            r.avatar = DescribeAvatar(avatar, c);
            if (c.rigHumanoid && !(r.avatar.valid && r.avatar.isHuman))
            {
                r.errors.Add("The model could not be set up as a Humanoid: " + r.avatar.message);
                return;
            }

            var measured = MeasureInstance(model, r);
            if (r.errors.Count > 0) return;
            if (!TryResolveScale(measured, c, r, out var scale)) return;

            if (!c.generatePrefab) return;
            var prefabPath = AssetNaming.PrefabPath(item);
            var existed = File.Exists(AssetPaths.Full(prefabPath));
            ModelPrefabWriter.Write(prefabPath, item.name + "_Visual", model, "ImportedCharacter", scale, avatar);
            OwnershipLabel.Mark(prefabPath);
            (existed ? r.changedAssets : r.createdPrefabs).Add(prefabPath);
        }

        /// <summary>The visual scale for a measured height, applied to the visual child only. Pure apart from filling the result.</summary>
        public static bool TryResolveScale(float measuredHeight, CharacterSettings c, ItemResult r, out float scale)
        {
            var s = HeightScale.Compute(measuredHeight, c.targetHeight, c.normalizeHeight, c.scaleOverride);
            scale = s.scale;
            r.appliedScale = s.scale;
            r.scaleSource = s.source;
            if (!s.Ok)
            {
                r.errors.Add(s.error);
                return false;
            }
            if (s.scale < 0.1f || s.scale > 10f)
                r.warnings.Add("The visual scale " + s.scale.ToString("0.####") + " is unusual. Check the model's units and for stray geometry (hair, coat tails, floating parts) inflating the measured height, or set a manual scale.");
            else if (s.source == ScaleSources.Auto && (s.scale < 0.66f || s.scale > 1.5f))
                r.warnings.Add("The model needed a large correction (x" + s.scale.ToString("0.###") + "). If that is not expected, check for stray geometry or set a manual scale.");
            return true;
        }

        static void Configure(string modelPath, CharacterSettings c)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.animationType = c.rigHumanoid ? ModelImporterAnimationType.Human : ModelImporterAnimationType.Generic;
            importer.avatarSetup = c.createAvatar ? ModelImporterAvatarSetup.CreateFromThisModel : ModelImporterAvatarSetup.NoAvatar;
            importer.importAnimation = false;
            importer.addCollider = false;
            importer.materialLocation = c.materialMode == MaterialModes.External
                ? ModelImporterMaterialLocation.External
                : ModelImporterMaterialLocation.InPrefab;
            importer.SaveAndReimport();
        }

        static AvatarReport DescribeAvatar(Avatar avatar, CharacterSettings c)
        {
            if (!c.rigHumanoid)
                return new AvatarReport { valid = avatar != null && avatar.isValid, isHuman = false, message = "Generic rig." };
            if (avatar == null)
                return new AvatarReport { message = "Unity did not create an Avatar. The model needs a skeleton with a skinned mesh." };
            if (!avatar.isValid)
                return new AvatarReport { message = "The Humanoid bone mapping is invalid. In Unity, select the model > Rig tab > Configure to fix the mapping, or correct the skeleton (bone names, T-pose) in Blender." };
            if (!avatar.isHuman)
                return new AvatarReport { message = "Unity built an Avatar, but it is not humanoid: the model has no humanoid skeleton. Check that it is a rigged, skinned character." };
            return new AvatarReport { valid = true, isHuman = true };
        }

        /// <summary>Instantiates the model at the origin, fills dimensions and checks renderers. Returns the measured height (0 when unusable).</summary>
        static float MeasureInstance(GameObject model, ItemResult r)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                if (instance.GetComponentsInChildren<Renderer>().Length == 0)
                {
                    r.errors.Add("The imported model contains no renderers (no meshes), so there is nothing to display.");
                    return 0f;
                }
                var bounds = ModelMeasure.Measure(instance);
                if (bounds == null) return 0f;
                var b = bounds.Value;
                r.dimensions = new Size3 { x = b.size.x, y = b.size.y, z = b.size.z };
                r.measuredHeight = b.size.y;
                if (Mathf.Abs(b.min.y) > 0.1f)
                    r.warnings.Add("The model's lowest point is " + b.min.y.ToString("0.###") + " m from its origin. Its feet should stand on the origin (check the pivot in Blender).");
                return b.size.y;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
    }
}
