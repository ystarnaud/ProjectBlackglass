#if UNITY_EDITOR
using System.IO;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEngine;
using System.Text.RegularExpressions;
using UnityEngine.TestTools;

namespace Blackglass.Tests.AssetPipeline
{
    public class RunnerTests
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

        // The runner reports failures with Debug.LogError by design (batch logs are scanned for the prefix); the test runner fails on unexpected errors, so each test declares them.
        static void ExpectPipelineErrors(int count)
        {
            for (var i = 0; i < count; i++) LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
        }

        string Write(ImportManifest m)
        {
            var path = System.IO.Path.Combine(dir, "manifest.json");
            File.WriteAllText(path, JsonUtility.ToJson(m));
            return path;
        }

        [Test]
        public void UnknownSchemaVersionFailsWithAMessageAndStillWritesTheResult()
        {
            var resultPath = System.IO.Path.Combine(dir, "result.json");
            ExpectPipelineErrors(1);
            var manifest = Write(new ImportManifest { schemaVersion = 99, resultPath = resultPath });
            var result = AssetPipelineRunner.Run(manifest, null);
            Assert.IsFalse(result.success);
            StringAssert.Contains("schema", string.Join(" ", result.errors).ToLowerInvariant());
            Assert.IsTrue(File.Exists(resultPath));
            Assert.IsFalse(JsonUtility.FromJson<ImportResult>(File.ReadAllText(resultPath)).success);
        }

        [Test]
        public void AFailingSaveOrRefreshBecomesAFatalErrorAndTheResultIsStillWritten()
        {
            var resultPath = System.IO.Path.Combine(dir, "result.json");
            ExpectPipelineErrors(2);
            var manifest = Write(new ImportManifest { runId = "r1", resultPath = resultPath });
            var result = AssetPipelineRunner.Run(manifest, null, () => throw new IOException("disk full"));
            Assert.IsFalse(result.success);
            StringAssert.Contains("disk full", string.Join(" ", result.errors));
            Assert.IsTrue(File.Exists(resultPath));
            var written = JsonUtility.FromJson<ImportResult>(File.ReadAllText(resultPath));
            Assert.IsFalse(written.success);
            Assert.AreEqual("r1", written.runId);
            StringAssert.Contains("disk full", string.Join(" ", written.errors));
        }

        [Test]
        public void MissingManifestIsReportedThroughTheResultOverride()
        {
            var resultPath = System.IO.Path.Combine(dir, "result.json");
            ExpectPipelineErrors(1);
            var result = AssetPipelineRunner.Run(System.IO.Path.Combine(dir, "nope.json"), resultPath);
            Assert.IsFalse(result.success);
            Assert.IsTrue(File.Exists(resultPath));
        }

        [Test]
        public void UnknownProfileAndBadDestinationFailOnlyTheirOwnItem()
        {
            ExpectPipelineErrors(2);
            var m = new ImportManifest
            {
                resultPath = System.IO.Path.Combine(dir, "result.json"),
                items = new[]
                {
                    new ImportItem { id = "a", profile = "Nope", name = "A", destinationFolder = ScratchFolder.Path },
                    new ImportItem { id = "b", profile = ProfileIds.GenericProp, name = "B", destinationFolder = "Packages/x" },
                },
            };
            var result = AssetPipelineRunner.Run(Write(m), null);
            Assert.AreEqual(2, result.items.Count);
            Assert.IsFalse(result.items[0].success);
            StringAssert.Contains("Nope", result.items[0].errors[0]);
            Assert.IsFalse(result.items[1].success);
            StringAssert.Contains("Assets", result.items[1].errors[0]);
            Assert.IsFalse(result.success);
        }

        [Test]
        public void ResultCarriesRunIdAndUnityVersion()
        {
            var result = AssetPipelineRunner.Run(Write(new ImportManifest { runId = "run-1", resultPath = System.IO.Path.Combine(dir, "r.json") }), null);
            Assert.AreEqual("run-1", result.runId);
            Assert.AreEqual(Application.unityVersion, result.unityVersion);
            Assert.IsTrue(result.success); // an empty manifest has nothing to fail
        }
    }
}
#endif
