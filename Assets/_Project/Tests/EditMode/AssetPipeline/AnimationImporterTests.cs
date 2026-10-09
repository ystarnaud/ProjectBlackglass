#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests.AssetPipeline
{
    public class AnimationImporterTests
    {
        const string CharPath = ScratchFolder.Path + "/AnimChar.fbx";

        [SetUp]
        public void SetUp()
        {
            // A Humanoid model in the scratch folder is the shared Avatar the clips copy.
            var character = ImporterTestSupport.Item(ProfileIds.HumanoidCharacter, ScratchFolder.Darius("Models/Darius Stand Idle.fbx"), "AnimChar");
            character.character.generatePrefab = false;
            var r = AssetPipelineRunner.ImportOne(character);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
        }

        [TearDown]
        public void TearDown() => ScratchFolder.Clean();

        static ImportItem Clip(string file, string name, string category, Action<AnimationSettings> tweak = null)
        {
            var item = ImporterTestSupport.Item(ProfileIds.HumanoidAnimation, ScratchFolder.Darius("Animations/" + file), name);
            item.animation.category = category;
            item.animation.clipName = name;
            item.animation.sharedAvatarPath = CharPath;
            tweak?.Invoke(item.animation);
            return item;
        }

        static AnimationClip LoadClip(string assetPath, string clipName) =>
            AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<AnimationClip>().First(c => c.name == clipName);

        [Test]
        public void LocomotionLoopsAndIsBakedInPlace()
        {
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion,
                a => { a.loop = LoopModes.Yes; a.bakeRotation = true; a.bakeHeight = true; a.bakePositionXZ = true; }));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));

            var path = ScratchFolder.Path + "/Walk.fbx";
            var clip = LoadClip(path, "Walk");
            Assert.Greater(clip.length, 0.2f);
            Assert.IsTrue(clip.isHumanMotion);
            Assert.IsTrue(AnimationUtility.GetAnimationClipSettings(clip).loopTime);
            var configured = ((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations.Single();
            Assert.AreEqual("Walk", configured.name);
            Assert.IsTrue(configured.lockRootRotation);
            Assert.IsTrue(configured.lockRootHeightY);
            Assert.IsTrue(configured.lockRootPositionXZ);

            Assert.AreEqual(1, r.clips.Count);
            Assert.AreEqual("Walk", r.clips[0].name);
            Assert.IsTrue(r.clips[0].loop);
            Assert.AreEqual(clip.length, r.clips[0].duration, 1e-4f);
            Assert.IsTrue(r.avatar.valid && r.avatar.isHuman);
            Assert.IsEmpty(r.createdPrefabs);
        }

        [Test]
        public void CombatDoesNotLoopAndBakesOnlyHeight()
        {
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Fire.fbx", "Fire", AnimationCategories.Combat,
                a => { a.loop = LoopModes.No; a.bakeHeight = true; }));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            var path = ScratchFolder.Path + "/Fire.fbx";
            Assert.IsFalse(AnimationUtility.GetAnimationClipSettings(LoadClip(path, "Fire")).loopTime);
            var configured = ((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations.Single();
            Assert.IsFalse(configured.lockRootRotation);
            Assert.IsTrue(configured.lockRootHeightY);
            Assert.IsFalse(configured.lockRootPositionXZ);
        }

        [Test]
        public void DeathBakesHeightAndHorizontalPosition()
        {
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Death.fbx", "Death", AnimationCategories.Death,
                a => { a.loop = LoopModes.No; a.bakeHeight = true; a.bakePositionXZ = true; }));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            var configured = ((ModelImporter)AssetImporter.GetAtPath(ScratchFolder.Path + "/Death.fbx")).clipAnimations.Single();
            Assert.IsTrue(configured.lockRootHeightY);
            Assert.IsTrue(configured.lockRootPositionXZ);
        }

        [Test]
        public void AutoLoopFollowsTheCategory()
        {
            Assert.IsTrue(AnimationImporter.ResolveLoop(new AnimationSettings { category = AnimationCategories.Locomotion, loop = LoopModes.Auto }));
            Assert.IsFalse(AnimationImporter.ResolveLoop(new AnimationSettings { category = AnimationCategories.Death, loop = LoopModes.Auto }));
            Assert.IsFalse(AnimationImporter.ResolveLoop(new AnimationSettings { category = AnimationCategories.Locomotion, loop = LoopModes.No }));
            Assert.IsTrue(AnimationImporter.ResolveLoop(new AnimationSettings { category = AnimationCategories.Combat, loop = LoopModes.Yes }));
        }

        [Test]
        public void MissingSharedAvatarFailsBeforeCopyingAnything()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.sharedAvatarPath = ScratchFolder.Path + "/Nope.fbx"));
            Assert.IsFalse(r.success);
            StringAssert.Contains("Avatar", ImporterTestSupport.Errors(r));
            Assert.IsFalse(File.Exists(AssetPaths.Full(ScratchFolder.Path + "/Walk.fbx")), "nothing is copied when the Avatar is unusable");
            Assert.IsEmpty(r.importedAssets);
            Assert.IsEmpty(r.clips);
        }

        [Test]
        public void EmptyAvatarPathAndNonHumanoidAvatarAreActionableErrors()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var empty = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.sharedAvatarPath = ""));
            Assert.IsFalse(empty.success);
            StringAssert.Contains("shared Avatar", ImporterTestSupport.Errors(empty));

            // A Generic (non-Humanoid) rig with an Avatar: the shared Avatar must be refused.
            var generic = ImporterTestSupport.Item(ProfileIds.HumanoidCharacter, ScratchFolder.Darius("Models/Darius Stand Idle.fbx"), "GenericRig");
            generic.character.rigHumanoid = false;
            generic.character.generatePrefab = false;
            Assert.IsTrue(AssetPipelineRunner.ImportOne(generic).success);
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var notHuman = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.sharedAvatarPath = ScratchFolder.Path + "/GenericRig.fbx"));
            Assert.IsFalse(notHuman.success);
            StringAssert.Contains("Humanoid", ImporterTestSupport.Errors(notHuman));
        }

        [Test]
        public void ClipNameIsRequired()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.clipName = " "));
            Assert.IsFalse(r.success);
            StringAssert.Contains("clip name", ImporterTestSupport.Errors(r).ToLowerInvariant());
        }

        [Test]
        public void ReimportUpdatesTheSameAssetAndAppliesNewFlags()
        {
            AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.loop = LoopModes.Yes));
            var guid = AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/Walk.fbx");

            var again = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.loop = LoopModes.No));

            Assert.IsTrue(again.success, ImporterTestSupport.Errors(again));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/Walk.fbx"));
            Assert.IsFalse(AnimationUtility.GetAnimationClipSettings(LoadClip(ScratchFolder.Path + "/Walk.fbx", "Walk")).loopTime);
            Assert.IsTrue(OwnershipLabel.IsOwned(ScratchFolder.Path + "/Walk.fbx"), "the Studio ownership label survives a re-import");
        }

        [Test]
        public void ExistingUnlabelledAnimationIsNotSilentlyReplaced()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            File.Copy(ScratchFolder.Darius("Animations/Darius Walk Gun.fbx"), AssetPaths.Full(ScratchFolder.Path + "/Walk.fbx"));
            AssetDatabase.Refresh();
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion));
            Assert.IsFalse(r.success);
            StringAssert.Contains("already exists", ImporterTestSupport.Errors(r));
        }
    }
}
#endif
