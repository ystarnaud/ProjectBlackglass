#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    internal static class EffectPaths
    {
        public const string Root = "Assets/_Project/Presentation";
        public const string Textures = Root + "/Textures";
        public const string Materials = Root + "/Materials";
        public const string Prefabs = Root + "/Prefabs";
        public const string Profiles = Root + "/Profiles";
        public const string Audio = Root + "/Audio";
        public const string Preview = Root + "/Preview";

        public static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                return;
            var parent = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetPath));
        }
    }

    /// <summary>Small original particle masks generated with code: a soft disc, a smoke puff and a thin ring. Replace the PNGs to change the look.</summary>
    internal static class EffectTextures
    {
        public static Texture2D SoftCircle(bool force) => Make(EffectPaths.Textures + "/SoftCircle.png", 64, force,
            (u, v) => Mathf.Pow(1f - Mathf.SmoothStep(0f, 1f, Mathf.Sqrt(u * u + v * v)), 1.5f));

        public static Texture2D Smoke(bool force) => Make(EffectPaths.Textures + "/Smoke.png", 128, force, (u, v) =>
        {
            var disc = Mathf.Pow(1f - Mathf.SmoothStep(0f, 1f, Mathf.Sqrt(u * u + v * v)), 1.2f);
            var noise = 0.5f + 0.5f * Mathf.PerlinNoise(u * 2.3f + 5f, v * 2.3f + 9f);
            return disc * noise;
        });

        public static Texture2D Ring(bool force) => Make(EffectPaths.Textures + "/Ring.png", 128, force, (u, v) =>
        {
            var r = Mathf.Sqrt(u * u + v * v);
            if (r > 1f)
                return 0f;
            var d = (r - 0.92f) / 0.045f;   // the bright band sits at 92% of the quad radius, so a ring sized to a radius ends close to it
            return Mathf.Exp(-d * d);
        });

        static Texture2D Make(string path, int size, bool force, Func<float, float, float> alpha)
        {
            if (!force)
            {
                var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (existing != null)
                    return existing;
            }
            EffectPaths.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size * 2f - 1f;
                    var v = (y + 0.5f) / size * 2f - 1f;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha(u, v)));
                }
            }
            texture.SetPixels32(pixels);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                throw new InvalidOperationException("No texture importer for " + path + ".");
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
#endif
