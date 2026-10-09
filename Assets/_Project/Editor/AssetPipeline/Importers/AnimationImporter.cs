using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class AnimationImporter
    {
        public static void Import(ImportItem item, ItemResult r)
        {
            var a = item.animation;
            if (Path.GetExtension(item.sourcePath).ToLowerInvariant() != ".fbx")
            {
                r.errors.Add("Humanoid animations must be FBX files.");
                return;
            }
            if (string.IsNullOrWhiteSpace(a.clipName))
            {
                r.errors.Add("Enter a clip name.");
                return;
            }
            if (!SourceCopier.CheckConflicts(item, r)) return;

            var avatar = LoadSharedAvatar(a.sharedAvatarPath, AssetNaming.ModelPath(item), r);
            if (avatar == null) return;
            r.avatar = new AvatarReport { valid = true, isHuman = true, message = "Copies the Avatar of " + a.sharedAvatarPath };

            var modelPath = SourceCopier.CopyAndImport(item, r);
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = avatar;
            importer.importAnimation = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();
            OwnershipLabel.Mark(modelPath);

            importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            var take = PickTake(importer, r);
            if (take == null)
            {
                r.errors.Add("No animation take was found in this FBX. Export it from Mixamo/Blender with the animation included.");
                return;
            }
            var loop = ResolveLoop(a);
            take.name = a.clipName;
            take.loopTime = loop;
            take.loopPose = loop;
            take.lockRootRotation = a.bakeRotation;
            take.lockRootHeightY = a.bakeHeight;
            take.lockRootPositionXZ = a.bakePositionXZ;
            importer.clipAnimations = new[] { take };
            importer.SaveAndReimport();

            Verify(modelPath, a, loop, r);
        }

        /// <summary>Auto means "by category": locomotion loops, everything else plays once.</summary>
        public static bool ResolveLoop(AnimationSettings a)
        {
            if (a.loop == LoopModes.Yes) return true;
            if (a.loop == LoopModes.No) return false;
            return a.category == AnimationCategories.Locomotion;
        }

        static Avatar LoadSharedAvatar(string path, string ownModelPath, ItemResult r)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                r.errors.Add("No shared Avatar was given. Choose a Humanoid model in the project for the clip to target (Settings > Default shared Avatar).");
                return null;
            }
            if (path == ownModelPath)
            {
                r.errors.Add("The shared Avatar cannot be the animation file itself.");
                return null;
            }
            var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            if (avatar == null)
            {
                r.errors.Add("No Avatar was found in '" + path + "'. Choose a model that is imported as Humanoid (Rig tab > Animation Type: Humanoid).");
                return null;
            }
            if (!avatar.isValid || !avatar.isHuman)
            {
                r.errors.Add("The Avatar in '" + path + "' is not a valid Humanoid Avatar. Fix its mapping (Rig tab > Configure) or choose another model.");
                return null;
            }
            return avatar;
        }

        /// <summary>The take to use: Mixamo names it "mixamo.com"; the generic "Take 001" is only used when nothing else exists.</summary>
        static ModelImporterClipAnimation PickTake(ModelImporter importer, ItemResult r)
        {
            var takes = importer.defaultClipAnimations;
            if (takes == null || takes.Length == 0) return null;
            var preferred = takes.Where(t => !t.name.StartsWith("Take ")).ToArray();
            var pool = preferred.Length > 0 ? preferred : takes;
            if (pool.Length > 1)
                r.warnings.Add("The file contains " + pool.Length + " takes; using the first (" + pool[0].name + ").");
            return pool[0];
        }

        static void Verify(string modelPath, AnimationSettings a, bool loop, ItemResult r)
        {
            var all = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToArray();
            var clip = all.FirstOrDefault(c => c.name == a.clipName);
            if (clip == null)
            {
                r.errors.Add("The clip '" + a.clipName + "' was not found after import (found: " + string.Join(", ", all.Select(c => c.name)) + ").");
                return;
            }
            if (clip.length <= 0f) r.errors.Add("The clip '" + a.clipName + "' has no duration.");
            if (!clip.isHumanMotion) r.errors.Add("The clip '" + a.clipName + "' is not Humanoid motion; the file's rig may not match the shared Avatar.");
            var loops = AnimationUtility.GetAnimationClipSettings(clip).loopTime;
            if (loops != loop) r.errors.Add("The clip's Loop Time is " + loops + " but " + loop + " was requested.");
            r.clips.Add(new ClipReport
            {
                name = clip.name, duration = clip.length, loop = loops,
                bakeRotation = a.bakeRotation, bakeHeight = a.bakeHeight, bakePositionXZ = a.bakePositionXZ,
            });
        }
    }
}
