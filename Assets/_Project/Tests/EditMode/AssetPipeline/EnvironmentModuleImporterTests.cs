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
        public void TearDown()
        {
            ScratchFolder.Clean();
            if (tempDir != null && System.IO.Directory.Exists(tempDir)) System.IO.Directory.Delete(tempDir, true);
            tempDir = null;
        }

        string tempDir;

        /// <summary>Writes an axis-aligned box OBJ (x and z centred, y from 0 up). OBJ has no unit setting, so its size in metres is exactly what is written.</summary>
        string WriteBoxObj(float width, float height, float depth)
        {
            tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bgas-envtest-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            var path = System.IO.Path.Combine(tempDir, "Box.obj");
            var hx = (width * 0.5f).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            var hz = (depth * 0.5f).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            var h = height.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            var lines = new[]
            {
                "o Box",
                "v -" + hx + " 0 -" + hz, "v " + hx + " 0 -" + hz, "v " + hx + " 0 " + hz, "v -" + hx + " 0 " + hz,
                "v -" + hx + " " + h + " -" + hz, "v " + hx + " " + h + " -" + hz, "v " + hx + " " + h + " " + hz, "v -" + hx + " " + h + " " + hz,
                "f 1 2 3 4", "f 5 8 7 6", "f 1 5 6 2", "f 2 6 7 3", "f 3 7 8 4", "f 4 8 5 1",
            };
            System.IO.File.WriteAllText(path, string.Join("\n", lines) + "\n");
            return path;
        }

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
            Assert.IsTrue(r.warnings.Any(w => w.StartsWith("Y is")), "height mismatch must be reported: " + string.Join("\n", r.warnings));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestWall.prefab");
            Assert.IsNotNull(prefab);
            Assert.AreEqual(Vector3.one, prefab.transform.Find("Model").localScale, "environment modules are never auto-scaled");
            Assert.IsEmpty(prefab.GetComponentsInChildren<Collider>(true));
            // A fresh copy of the cm-declared Darius FBX measures about 0.019 m: it is reported, never silently corrected (note 1).
            Assert.Greater(r.dimensions.y, 0f);
            Assert.IsEmpty(r.changedAssets.Where(p => p.EndsWith(".asset")), "no theme change unless requested");
        }

        static bool IsDimensionOrPivotWarning(string w) =>
            w.StartsWith("X is") || w.StartsWith("Y is") || w.StartsWith("Z is") || w.Contains("pivot");

        [Test]
        public void ARightSizedWallWithTheRightPivotProducesNoDimensionOrPivotWarnings()
        {
            // Wall spec: 1 x 3 x 1 m, pivot at the bottom centre.
            var item = Wall();
            item.sourcePath = WriteBoxObj(1f, 3f, 1f);
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(3f, r.dimensions.y, 0.01f);
            Assert.IsFalse(r.warnings.Any(IsDimensionOrPivotWarning), string.Join("\n", r.warnings));
        }

        [Test]
        public void TheCheckUsesTheScaledDimensions()
        {
            // Authored at twice the wall size; the explicit scale 0.5 brings it to 1 x 3 x 1 in prefab-root space.
            var item = Wall();
            item.sourcePath = WriteBoxObj(2f, 6f, 2f);
            item.environment.scale = 0.5f;
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(3f, r.dimensions.y, 0.01f);
            Assert.IsFalse(r.warnings.Any(IsDimensionOrPivotWarning), string.Join("\n", r.warnings));

            // Unscaled, the same file must be flagged (the check is not blind).
            var unscaled = Wall("TestWall2");
            unscaled.sourcePath = item.sourcePath;
            var u = AssetPipelineRunner.ImportOne(unscaled);
            Assert.IsTrue(u.warnings.Any(w => w.StartsWith("Y is")), string.Join("\n", u.warnings));
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
