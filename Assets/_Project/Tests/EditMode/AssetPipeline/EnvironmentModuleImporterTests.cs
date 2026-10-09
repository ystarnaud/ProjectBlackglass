#if UNITY_EDITOR
using System.Linq;
using Blackglass.AssetPipeline;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests.AssetPipeline
{
    public class EnvironmentModuleImporterTests
    {
        [TearDown]
        public void TearDown() => ScratchFolder.Clean();

        // Darius is about 1.8 m tall, so as a "wall" he deliberately mismatches the 3 m module height.
        static ImportItem Wall(string name = "TestWall", string element = "WallStraight")
        {
            var item = ImporterTestSupport.Item(ProfileIds.EnvironmentModule, ScratchFolder.Darius("Models/Darius Stand Idle.fbx"), name);
            item.environment.element = element;
            return item;
        }

        static GameObject MakePrefab(string path)
        {
            var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(path));
            var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return saved;
        }

        static EnvironmentTheme MakeTheme(string path)
        {
            var theme = EnvironmentTheme.Create(7, new EnvironmentTheme.Entry[0]);
            AssetDatabase.CreateAsset(theme, path);
            return theme;
        }

        static int VariantCount(EnvironmentTheme theme, EnvironmentElement element)
        {
            var entries = new SerializedObject(theme).FindProperty("entries");
            for (var i = 0; i < entries.arraySize; i++)
            {
                var entry = entries.GetArrayElementAtIndex(i);
                if (entry.FindPropertyRelative("element").intValue == (int)element)
                    return entry.FindPropertyRelative("variants").arraySize;
            }
            return 0;
        }

        [Test]
        public void ImportsAModuleAndWarnsAboutDimensionMismatchesWithoutRescaling()
        {
            var r = AssetPipelineRunner.ImportOne(Wall());
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.IsTrue(r.warnings.Any(w => w.Contains("Y")), "height mismatch must be reported: " + string.Join("\n", r.warnings));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestWall.prefab");
            Assert.IsNotNull(prefab);
            Assert.AreEqual(Vector3.one, prefab.transform.Find("Model").localScale, "environment modules are never auto-scaled");
            Assert.IsEmpty(prefab.GetComponentsInChildren<Collider>(true));
            // A fresh copy of the cm-declared Darius FBX measures about 0.019 m: it is reported, never silently corrected (note 1).
            Assert.Greater(r.dimensions.y, 0f);
            Assert.IsEmpty(r.changedAssets.Where(p => p.EndsWith(".asset")), "no theme change unless requested");
        }

        [Test]
        public void ExplicitScaleChangesTheModelChildAndTheReportedSize()
        {
            var baseline = AssetPipelineRunner.ImportOne(Wall());
            Assert.IsTrue(baseline.success, ImporterTestSupport.Errors(baseline));

            var item = Wall();
            item.environment.scale = 2f;
            var r = AssetPipelineRunner.ImportOne(item); // re-import of the owned item at a different scale
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(2f, AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestWall.prefab").transform.Find("Model").localScale.x, 1e-5f);
            // The absolute size depends on the file's unit (cm FBXs measure about 0.019 m), so compare against the unscaled import.
            Assert.AreEqual(baseline.dimensions.y * 2f, r.dimensions.y, baseline.dimensions.y * 0.01f);
            Assert.IsTrue(r.warnings.Any(w => w.Contains("explicit scale")), string.Join("\n", r.warnings));
        }

        [Test]
        public void UnknownElementIsAnError()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var r = AssetPipelineRunner.ImportOne(Wall("Bad", "Banana"));
            Assert.IsFalse(r.success);
            StringAssert.Contains("Banana", ImporterTestSupport.Errors(r));
        }

        [Test]
        public void ThemeAppendAddsOnceAndCreatesTheEntryWhenMissing()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            var theme = MakeTheme(ScratchFolder.Path + "/Theme.asset");
            var prefab = MakePrefab(ScratchFolder.Path + "/P1.prefab");

            Assert.IsNull(EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.WallStraight, ThemeModes.Append, 0, prefab));
            Assert.AreEqual(1, VariantCount(theme, EnvironmentElement.WallStraight));
            Assert.IsNull(EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.WallStraight, ThemeModes.Append, 0, prefab));
            Assert.AreEqual(1, VariantCount(theme, EnvironmentElement.WallStraight), "appending the same prefab twice adds it once");

            var second = MakePrefab(ScratchFolder.Path + "/P2.prefab");
            Assert.IsNull(EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.WallStraight, ThemeModes.Append, 0, second));
            Assert.AreEqual(2, VariantCount(theme, EnvironmentElement.WallStraight));
            Assert.AreEqual(0, VariantCount(theme, EnvironmentElement.Floor), "other elements are untouched");
        }

        [Test]
        public void ThemeReplaceChecksTheIndexAndMissingThemesAreReported()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            var theme = MakeTheme(ScratchFolder.Path + "/Theme.asset");
            var p1 = MakePrefab(ScratchFolder.Path + "/P1.prefab");
            var p2 = MakePrefab(ScratchFolder.Path + "/P2.prefab");
            EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.Crate, ThemeModes.Append, 0, p1);

            Assert.IsNull(EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.Crate, ThemeModes.Replace, 0, p2));
            Assert.AreEqual(1, VariantCount(theme, EnvironmentElement.Crate));
            StringAssert.Contains("out of range", EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.Crate, ThemeModes.Replace, 5, p2));
            StringAssert.Contains("out of range", EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.Floor, ThemeModes.Replace, 0, p2));
            Assert.AreEqual(0, VariantCount(theme, EnvironmentElement.Floor), "a failed replace must not leave an empty entry behind");
            StringAssert.Contains("not found", EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Nope.asset", EnvironmentElement.Crate, ThemeModes.Append, 0, p1));
        }

        [Test]
        public void ImportCanRegisterTheNewPrefabInATheme()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            var theme = MakeTheme(ScratchFolder.Path + "/Theme.asset");
            var item = Wall();
            item.environment.themePath = ScratchFolder.Path + "/Theme.asset";
            item.environment.themeMode = ThemeModes.Append;

            var r = AssetPipelineRunner.ImportOne(item);

            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(1, VariantCount(theme, EnvironmentElement.WallStraight));
            StringAssert.Contains("WallStraight", r.registeredInTheme);
            CollectionAssert.Contains(r.changedAssets, ScratchFolder.Path + "/Theme.asset");
        }

        [Test]
        public void ARegistrationProblemFailsTheItemClearly()
        {
            var item = Wall();
            item.environment.themePath = ScratchFolder.Path + "/Missing.asset";
            item.environment.themeMode = ThemeModes.Append;
            LogAssert.Expect(LogType.Error, new Regex(@"^\[AssetPipeline\] ERROR"));
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsFalse(r.success);
            StringAssert.Contains("not found", ImporterTestSupport.Errors(r));
        }
    }
}
#endif
