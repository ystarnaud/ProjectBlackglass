#if UNITY_EDITOR
using Blackglass.AssetPipeline;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests.AssetPipeline
{
    public class PropImporterTests
    {
        [TearDown]
        public void TearDown() => ScratchFolder.Clean();

        static ImportItem Rifle(string name = "TestRifle") =>
            ImporterTestSupport.Item(ProfileIds.GenericProp, ScratchFolder.Darius("Weapons/Rifle.fbx"), name);

        [Test]
        public void ImportsAPropWithAColliderFreePrefabAndReportsSize()
        {
            var r = AssetPipelineRunner.ImportOne(Rifle());
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestRifle.prefab");
            Assert.IsNotNull(prefab);
            Assert.AreEqual(Vector3.one, prefab.transform.localScale);
            Assert.IsEmpty(prefab.GetComponentsInChildren<Collider>(true));
            Assert.AreEqual(Vector3.one, prefab.transform.Find("Model").localScale);
            Assert.Greater(Mathf.Max(r.dimensions.x, r.dimensions.y, r.dimensions.z), 0.1f);
            Assert.IsTrue(OwnershipLabel.IsOwned(ScratchFolder.Path + "/TestRifle.fbx"));
            Assert.IsTrue(OwnershipLabel.IsOwned(ScratchFolder.Path + "/TestRifle.prefab"));
        }

        [Test]
        public void ExplicitScaleIsAppliedToTheModelChildOnly()
        {
            var item = Rifle();
            item.prop.scale = 2f;
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestRifle.prefab");
            Assert.AreEqual(Vector3.one, prefab.transform.localScale);
            Assert.AreEqual(2f, prefab.transform.Find("Model").localScale.x, 1e-5f);
        }

        [Test]
        public void NoPrefabWhenNotRequested()
        {
            var item = Rifle();
            item.prop.generatePrefab = false;
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestRifle.prefab"));
        }

        [Test]
        public void ReimportUpdatesInPlaceAndUnlabelledAssetsAreProtected()
        {
            AssetPipelineRunner.ImportOne(Rifle());
            var guid = AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/TestRifle.prefab");
            Assert.IsTrue(AssetPipelineRunner.ImportOne(Rifle()).success);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/TestRifle.prefab"));

            AssetPaths.EnsureFolder(ScratchFolder.Path);
            System.IO.File.Copy(ScratchFolder.Darius("Weapons/Rifle.fbx"), AssetPaths.Full(ScratchFolder.Path + "/Other.fbx"));
            AssetDatabase.Refresh();
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var refused = AssetPipelineRunner.ImportOne(Rifle("Other"));
            Assert.IsFalse(refused.success);
            StringAssert.Contains("already exists", ImporterTestSupport.Errors(refused));
        }

        [Test]
        public void ANonPositiveScaleIsRefused()
        {
            var item = Rifle();
            item.prop.scale = 0f;
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            Assert.IsFalse(AssetPipelineRunner.ImportOne(item).success);
        }
    }
}
#endif
