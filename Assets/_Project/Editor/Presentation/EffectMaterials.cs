#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass.EditorTools
{
    internal enum EffectBlend { Alpha, Additive }

    /// <summary>The shared materials every generated effect uses: URP Particles/Unlit, transparent, no depth writes, no shadows.</summary>
    internal sealed class EffectKit
    {
        public Texture2D SoftCircle;
        public Texture2D SmokeMap;
        public Texture2D RingMap;
        public Material Glow, Smoke, Dust, Ring, Tracer, Spark, Chip;

        public static EffectKit Load(bool force)
        {
            var kit = new EffectKit
            {
                SoftCircle = EffectTextures.SoftCircle(force),
                SmokeMap = EffectTextures.Smoke(force),
                RingMap = EffectTextures.Ring(force),
            };
            kit.Glow = EffectMaterials.Get("FxGlow", kit.SoftCircle, EffectBlend.Additive, Color.white, force);
            kit.Smoke = EffectMaterials.Get("FxSmoke", kit.SmokeMap, EffectBlend.Alpha, Color.white, force);
            kit.Dust = EffectMaterials.Get("FxDust", kit.SmokeMap, EffectBlend.Alpha, Color.white, force);
            kit.Ring = EffectMaterials.Get("FxRing", kit.RingMap, EffectBlend.Additive, Color.white, force);
            kit.Tracer = EffectMaterials.Get("FxTracer", kit.SoftCircle, EffectBlend.Additive, Color.white, force);
            kit.Spark = EffectMaterials.Get("FxSpark", kit.SoftCircle, EffectBlend.Additive, Color.white, force);
            kit.Chip = EffectMaterials.Get("FxChip", kit.SoftCircle, EffectBlend.Alpha, Color.white, force);
            return kit;
        }
    }

    internal static class EffectMaterials
    {
        public static Material Get(string name, Texture2D map, EffectBlend blend, Color tint, bool force)
        {
            var path = EffectPaths.Materials + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null && !force)
                return material;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                throw new InvalidOperationException("The URP Particles/Unlit shader is missing.");
            EffectPaths.EnsureFolder(EffectPaths.Materials);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetTexture("_BaseMap", map);
            material.SetColor("_BaseColor", tint);
            Configure(material, blend);
            EditorUtility.SetDirty(material);
            return material;
        }

        static void Configure(Material m, EffectBlend blend)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", blend == EffectBlend.Additive ? 2f : 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", blend == EffectBlend.Additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_SrcBlendAlpha"))
                m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            if (m.HasProperty("_DstBlendAlpha"))
                m.SetFloat("_DstBlendAlpha", blend == EffectBlend.Additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetShaderPassEnabled("ShadowCaster", false);
        }
    }
}
#endif
