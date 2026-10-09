#if UNITY_EDITOR
using System.IO;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using System.Text.RegularExpressions;
using UnityEngine.TestTools;

namespace Blackglass.Tests.AssetPipeline
{
    public class SourceCopierTests
    {
        string dir;

        [SetUp]
        public void SetUp()
        {
            dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bgas-unity-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(dir, true); } catch { }
            ScratchFolder.Clean();
        }

        static ImportItem Item(bool allowOverwrite)
        {
            return new ImportItem
            {
                id = "x",
                profile = ProfileIds.GenericProp,
                name = "Copied Model",
                sourcePath = ScratchFolder.Darius("Models/Darius Stand Idle.fbx"),
                destinationFolder = ScratchFolder.Path + "/Sub",
                allowOverwrite = allowOverwrite,
                prop = new PropSettings { generatePrefab = false },
            };
        }

        [Test]
        public void CopyAndImportCreatesTheModelAndLabelsSurviveASave()
        {
            Assume.That(File.Exists(ScratchFolder.Darius("Models/Darius Stand Idle.fbx")), "Darius test model is missing");
            var item = Item(false);
            var result = new ItemResult();
            Assert.IsTrue(SourceCopier.CheckConflicts(item, result));
            var path = SourceCopier.CopyAndImport(item, result);
            Assert.AreEqual(ScratchFolder.Path + "/Sub/Copied Model.fbx", path);
            Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(path));
            CollectionAssert.Contains(result.importedAssets, path);
            CollectionAssert.DoesNotContain(result.changedAssets, path);

            Assert.IsFalse(OwnershipLabel.IsOwned(path));
            OwnershipLabel.Mark(path);
            OwnershipLabel.Mark(path); // idempotent
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Assert.IsTrue(OwnershipLabel.IsOwned(path));
            Assert.AreEqual(1, System.Array.FindAll(AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(path)), l => l == ContractInfo.OwnershipLabel).Length);
        }

        [Test]
        public void UnlabelledExistingAssetIsRefusedUnlessOverwriteIsAllowed()
        {
            Assume.That(File.Exists(ScratchFolder.Darius("Models/Darius Stand Idle.fbx")), "Darius test model is missing");
            var first = new ItemResult();
            var path = SourceCopier.CopyAndImport(Item(false), first);

            var refused = new ItemResult();
            Assert.IsFalse(SourceCopier.CheckConflicts(Item(false), refused));
            StringAssert.Contains("Allow overwrite", refused.errors[0]);

            Assert.IsTrue(SourceCopier.CheckConflicts(Item(true), new ItemResult()));

            OwnershipLabel.Mark(path);
            AssetDatabase.SaveAssets();
            Assert.IsTrue(SourceCopier.CheckConflicts(Item(false), new ItemResult()));

            var again = new ItemResult();
            SourceCopier.CopyAndImport(Item(false), again);
            CollectionAssert.Contains(again.changedAssets, path);
        }

        [Test]
        public void SourceInsideTheDestinationIsRefused()
        {
            Assume.That(File.Exists(ScratchFolder.Darius("Models/Darius Stand Idle.fbx")), "Darius test model is missing");
            var path = SourceCopier.CopyAndImport(Item(false), new ItemResult());
            var self = Item(true);
            self.sourcePath = AssetPaths.Full(path);
            Assert.Throws<IOException>(() => SourceCopier.CopyAndImport(self, new ItemResult()));
        }

        [Test]
        public void ExplicitNullBlocksAndItemsInTheManifestDoNotCrashTheRunner()
        {
            var resultPath = System.IO.Path.Combine(dir, "result.json");
            var manifestPath = System.IO.Path.Combine(dir, "m.json");
            var json = "{\"schemaVersion\":1,\"runId\":\"n\",\"resultPath\":\"" + resultPath.Replace("\\", "\\\\") + "\",\"items\":[" +
                   "{\"id\":\"a\",\"profile\":\"GenericProp\",\"name\":\"A\",\"destinationFolder\":\"Packages/x\",\"character\":null,\"animation\":null,\"prop\":null,\"environment\":null}]}";
            File.WriteAllText(manifestPath, json);
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var result = AssetPipelineRunner.Run(manifestPath, null);
            Assert.AreEqual(1, result.items.Count);
            Assert.IsFalse(result.items[0].success);
            StringAssert.Contains("Assets", result.items[0].errors[0]);

            File.WriteAllText(manifestPath, "{\"schemaVersion\":1,\"resultPath\":\"" + resultPath.Replace("\\", "\\\\") + "\",\"items\":null}");
            var empty = AssetPipelineRunner.Run(manifestPath, null);
            Assert.IsTrue(empty.success);
        }

        [Test]
        public void NonFiniteNumbersAreSanitizedBeforeTheResultIsWritten()
        {
            var item = new ItemResult { measuredHeight = float.NaN, targetHeight = float.PositiveInfinity, appliedScale = float.NaN };
            item.dimensions.y = float.NegativeInfinity;
            item.clips.Add(new ClipReport { name = "c", duration = float.NaN });
            AssetPipelineRunner.Sanitize(item);
            Assert.AreEqual(0f, item.measuredHeight);
            Assert.AreEqual(0f, item.targetHeight);
            Assert.AreEqual(1f, item.appliedScale);
            Assert.AreEqual(0f, item.dimensions.y);
            Assert.AreEqual(0f, item.clips[0].duration);
        }
    }
}
#endif
