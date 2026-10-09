#if UNITY_EDITOR
using System;
using System.IO;
using System.Text.RegularExpressions;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests.AssetPipeline
{
    public class CharacterImporterTests
    {
        const string Source = "Models/Darius Stand Idle.fbx";

        [SetUp]
        public void SetUp()
        {
            Assert.IsTrue(File.Exists(ScratchFolder.Darius(Source)), "The Darius test model moved: " + ScratchFolder.Darius(Source));
        }

        [TearDown]
        public void TearDown() => ScratchFolder.Clean();

        static ImportItem Character(string name, Action<CharacterSettings> tweak = null)
        {
            var item = ImporterTestSupport.Item(ProfileIds.HumanoidCharacter, ScratchFolder.Darius(Source), name);
            item.character.targetHeight = 1.88f;
            tweak?.Invoke(item.character);
            return item;
        }

        static string Prefab(string name) => ScratchFolder.Path + "/" + name + "_Visual.prefab";

        [Test]
        public void NormalisesTheVisualHeightAndLeavesTheRootAlone()
        {
            var r = AssetPipelineRunner.ImportOne(Character("TestChar"));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            // A fresh import uses Unity's default importer settings. Darius' own .meta compensates a centimetre-declared FBX with
            // globalScale 100, so a plain copy measures 0.0188 m (1.88 cm) and the visual correction is about x100. The spec leaves
            // globalScale/useFileScale untouched, so this is the real behaviour; it is reported with a warning.
            Assert.That(r.measuredHeight, Is.InRange(0.0175f, 0.0195f), "Darius is 1.88 m authored, 1.88 cm under default import settings");
            Assert.IsNotEmpty(r.warnings, "a x100 correction must be called out");
            Assert.AreEqual(ScaleSources.Auto, r.scaleSource);
            Assert.AreEqual(1.88f / r.measuredHeight, r.appliedScale, 1e-4f);
            Assert.AreEqual(1.88f, r.targetHeight);
            CollectionAssert.Contains(r.createdPrefabs, Prefab("TestChar"));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar"));
            Assert.AreEqual(Vector3.one, prefab.transform.localScale, "the visual prefab root is never scaled");
            var child = prefab.transform.Find("ImportedCharacter");
            Assert.IsNotNull(child);
            Assert.AreEqual(r.appliedScale, child.localScale.x, 1e-4f);
            Assert.AreEqual(child.localScale.x, child.localScale.y, 1e-6f);
            Assert.AreEqual(child.localScale.x, child.localScale.z, 1e-6f);
            Assert.IsEmpty(prefab.GetComponentsInChildren<Collider>(true), "visual prefabs carry no colliders");
            var animator = child.GetComponent<Animator>();
            Assert.IsNotNull(animator);
            Assert.IsNotNull(animator.avatar);
            Assert.IsTrue(animator.avatar.isHuman);
            Assert.IsFalse(animator.applyRootMotion);

            // The posed result must be the target height. If this fails with a value near target * scale, BakeMesh(.., true)
            // already includes the transform scale: switch ModelMeasure to BakeMesh(.., false).
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try { Assert.AreEqual(1.88f, ModelMeasure.Measure(instance).Value.size.y, 0.03f); }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        [Test]
        public void ManualScaleOverrideWinsOverTheMeasurement()
        {
            var r = AssetPipelineRunner.ImportOne(Character("TestChar", c => c.scaleOverride = 2f));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(ScaleSources.Manual, r.scaleSource);
            Assert.AreEqual(2f, r.appliedScale);
            var child = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar")).transform.Find("ImportedCharacter");
            Assert.AreEqual(2f, child.localScale.x, 1e-5f);
        }

        [Test]
        public void NormaliseOffKeepsTheModelAtItsImportedSize()
        {
            var r = AssetPipelineRunner.ImportOne(Character("TestChar", c => c.normalizeHeight = false));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(ScaleSources.None, r.scaleSource);
            var child = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar")).transform.Find("ImportedCharacter");
            Assert.AreEqual(Vector3.one, child.localScale);
        }

        [Test]
        public void NoPrefabWhenNotRequested()
        {
            var r = AssetPipelineRunner.ImportOne(Character("TestChar", c => c.generatePrefab = false));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.IsEmpty(r.createdPrefabs);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar")));
        }

        [Test]
        public void ImportedModelAndPrefabAreLabelledAsStudioOwned()
        {
            AssetPipelineRunner.ImportOne(Character("TestChar"));
            Assert.IsTrue(OwnershipLabel.IsOwned(ScratchFolder.Path + "/TestChar.fbx"));
            Assert.IsTrue(OwnershipLabel.IsOwned(Prefab("TestChar")));
        }

        [Test]
        public void ReimportUpdatesInPlaceKeepingTheGuidsAndLabels()
        {
            AssetPipelineRunner.ImportOne(Character("TestChar"));
            var modelGuid = AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/TestChar.fbx");
            var prefabGuid = AssetDatabase.AssetPathToGUID(Prefab("TestChar"));

            var again = AssetPipelineRunner.ImportOne(Character("TestChar", c => c.scaleOverride = 1.5f));

            Assert.IsTrue(again.success, ImporterTestSupport.Errors(again));
            Assert.AreEqual(modelGuid, AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/TestChar.fbx"));
            Assert.AreEqual(prefabGuid, AssetDatabase.AssetPathToGUID(Prefab("TestChar")));
            Assert.IsTrue(OwnershipLabel.IsOwned(ScratchFolder.Path + "/TestChar.fbx"), "the model keeps its label");
            Assert.IsTrue(OwnershipLabel.IsOwned(Prefab("TestChar")), "the prefab keeps its label");
            CollectionAssert.Contains(again.changedAssets, ScratchFolder.Path + "/TestChar.fbx");
            CollectionAssert.Contains(again.changedAssets, Prefab("TestChar"));
            CollectionAssert.DoesNotContain(again.createdPrefabs, Prefab("TestChar"));
            Assert.AreEqual(1.5f, AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar")).transform.Find("ImportedCharacter").localScale.x, 1e-5f);
        }

        [Test]
        public void ExistingUnlabelledAssetIsRefusedUnlessOverwriteIsAllowed()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            File.Copy(ScratchFolder.Darius(Source), AssetPaths.Full(ScratchFolder.Path + "/TestChar.fbx"));
            AssetDatabase.Refresh();

            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var refused = AssetPipelineRunner.ImportOne(Character("TestChar"));
            Assert.IsFalse(refused.success);
            StringAssert.Contains("already exists", ImporterTestSupport.Errors(refused));
            Assert.IsFalse(OwnershipLabel.IsOwned(ScratchFolder.Path + "/TestChar.fbx"), "a refused item must not claim the asset");

            var item = Character("TestChar");
            item.allowOverwrite = true;
            var ok = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(ok.success, ImporterTestSupport.Errors(ok));
        }

        [Test]
        public void ARigidMeshThatCannotBeHumanoidFailsWithoutAPrefab()
        {
            var item = ImporterTestSupport.Item(ProfileIds.HumanoidCharacter, ScratchFolder.Darius("Weapons/Rifle.fbx"), "NotAPerson");
            // Unity reports the failed mapping itself on every (re)import of the model: after the settings change and after labelling.
            LogAssert.Expect(LogType.Error, new Regex("Invalid Avatar Rig Configuration"));
            LogAssert.Expect(LogType.Error, new Regex("Invalid Avatar Rig Configuration"));
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsFalse(r.success);
            StringAssert.Contains("Humanoid", ImporterTestSupport.Errors(r));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("NotAPerson")), "no prefab after a failed Humanoid mapping");
            Assert.IsFalse(r.avatar.valid);
        }

        [Test]
        public void ModelWithNoGeometryGivesAnErrorNeverANanScale()
        {
            var empty = new GameObject("empty");
            try { Assert.IsNull(ModelMeasure.Measure(empty)); }
            finally { UnityEngine.Object.DestroyImmediate(empty); }
            var r = new ItemResult();
            Assert.IsFalse(CharacterImporter.TryResolveScale(0f, new CharacterSettings(), r, out _));
            Assert.IsNotEmpty(r.errors);
            var manual = new ItemResult();
            Assert.IsTrue(CharacterImporter.TryResolveScale(0f, new CharacterSettings { scaleOverride = 0.5f }, manual, out var scale));
            Assert.AreEqual(0.5f, scale);
        }

        [Test]
        public void UnusualScalesAreWarnedAbout()
        {
            var r = new ItemResult();
            Assert.IsTrue(CharacterImporter.TryResolveScale(0.05f, new CharacterSettings(), r, out _)); // would scale x37
            Assert.IsNotEmpty(r.warnings);
        }

        [Test]
        public void ObjIsRefusedForCharacters()
        {
            var item = ImporterTestSupport.Item(ProfileIds.HumanoidCharacter, "C:/x/body.obj", "Body");
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsFalse(r.success);
            StringAssert.Contains("FBX", ImporterTestSupport.Errors(r));
        }

        [Test]
        public void ANullCharacterBlockFallsBackToDefaults()
        {
            var item = Character("TestChar");
            item.character = null;
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(1.85f, r.targetHeight, 1e-6f, "default target height");
        }
    }
}
#endif
