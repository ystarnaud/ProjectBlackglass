#if UNITY_EDITOR
using System;
using System.IO;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests.AssetPipeline
{
    public class ContractUnityTests
    {
        static string FixturePath() => System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..",
            "Tools", "BlackglassAssetStudio", "tests", "Core.Tests", "Fixtures", "manifest-v1.json"));

        [Test]
        public void GoldenFixtureParsesWithJsonUtility()
        {
            var m = JsonUtility.FromJson<ImportManifest>(File.ReadAllText(FixturePath()));
            Assert.AreEqual(1, m.schemaVersion);
            Assert.AreEqual(3, m.items.Length);
            Assert.AreEqual(ProfileIds.HumanoidCharacter, m.items[0].profile);
            Assert.AreEqual(1.85f, m.items[0].character.targetHeight);
            StringAssert.Contains(" ", m.items[0].sourcePath);
            Assert.AreEqual("Walk", m.items[1].animation.clipName);
            Assert.IsTrue(m.items[1].allowOverwrite);
            Assert.AreEqual("WallStraight", m.items[2].environment.element);
            Assert.AreEqual(ThemeModes.Append, m.items[2].environment.themeMode);
        }

        [Test]
        public void ResultWrittenByJsonUtilityKeepsItsShape()
        {
            var r = new ImportResult { runId = "r", success = true };
            var item = new ItemResult { id = "i", success = true, appliedScale = 0.5f };
            item.clips.Add(new ClipReport { name = "Walk", loop = true });
            r.items.Add(item);
            var back = JsonUtility.FromJson<ImportResult>(JsonUtility.ToJson(r, true));
            Assert.AreEqual("Walk", back.items[0].clips[0].name);
            Assert.AreEqual(0.5f, back.items[0].appliedScale);
        }

        [Test]
        public void HeightScaleMatchesTheSpecRules()
        {
            var auto = HeightScale.Compute(2.4f, 1.85f, true, 0f);
            Assert.IsTrue(auto.Ok);
            Assert.AreEqual(1.85f / 2.4f, auto.scale, 1e-5f);
            Assert.AreEqual(ScaleSources.Manual, HeightScale.Compute(0f, 1.85f, true, 0.5f).source);
            Assert.IsFalse(HeightScale.Compute(0f, 1.85f, true, 0f).Ok);
            Assert.IsFalse(HeightScale.Compute(float.NaN, 1.85f, true, 0f).Ok);
        }

        [Test]
        public void EnvironmentElementListMatchesTheEnum()
        {
            CollectionAssert.AreEquivalent(Enum.GetNames(typeof(EnvironmentElement)), EnvironmentElements.All);
        }

        [Test]
        public void PathRulesRefuseEscapesFromAssets()
        {
            Assert.IsNull(PathRules.ValidateDestination("Assets/Art/Props"));
            Assert.IsNotNull(PathRules.ValidateDestination("Assets/../Library"));
            Assert.IsNotNull(PathRules.ValidateDestination("Packages/x"));
        }

        [Test]
        public void PathRulesRefuseNamesUnityIgnoresAndWindowsReserves()
        {
            foreach (var folder in new[] { "Assets/.hidden/x", "Assets/Art~/x", "Assets/CON", "Assets/aux/x", "Assets/Com1", "Assets/NUL.v2" })
                Assert.IsNotNull(PathRules.ValidateDestination(folder), folder);
            foreach (var name in new[] { "hidden~", "CON", "prn", "COM9", "lpt1", "NUL.txt" })
                Assert.IsNotNull(PathRules.ValidateAssetName(name), name);
            Assert.IsNull(PathRules.ValidateDestination("Assets/Console/COM10"));
            Assert.IsNull(PathRules.ValidateAssetName("Console"));
            Assert.IsNull(PathRules.ValidateAssetName("a~b"));
        }

        [Test]
        public void EntryMethodNameMatchesWhatTheAppLaunches()
        {
            Assert.AreEqual("Blackglass.AssetPipeline.AssetPipelineRunner.RunFromCommandLine",
                typeof(AssetPipelineRunner).FullName + "." + nameof(AssetPipelineRunner.RunFromCommandLine));
        }
    }
}
#endif
