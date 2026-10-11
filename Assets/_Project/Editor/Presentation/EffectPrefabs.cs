#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>Builds the effect prefabs: small particle systems, an optional capped light, a PooledEffect on the root. No colliders, ever.</summary>
    internal static class EffectPrefabs
    {
        public static GameObject MuzzleFlashRifle(EffectKit kit, bool force) => Build("MuzzleFlashRifle", force, root =>
        {
            SetLightSeconds(root, 0.06f);
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Flash", Material = kit.Glow, Burst = 2, Duration = 0.1f, ShapeEnabled = false,
                Lifetime = new Vector2(0.04f, 0.06f), Size = new Vector2(0.28f, 0.4f),
                StartColor = new Color(1f, 0.85f, 0.5f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.3f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Cone", Material = kit.Spark, Render = ParticleSystemRenderMode.Stretch, Burst = 1, Duration = 0.1f,
                Lifetime = new Vector2(0.05f, 0.05f), Size = new Vector2(0.18f, 0.22f), Speed = new Vector2(12f, 16f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 6f, LengthScale = 3f,
                StartColor = new Color(1f, 0.75f, 0.35f, 0.9f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.2f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Smoke", Material = kit.Smoke, Burst = 3, Duration = 0.2f,
                Lifetime = new Vector2(0.7f, 1.2f), Size = new Vector2(0.08f, 0.12f), Speed = new Vector2(0.4f, 0.9f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 12f, Gravity = -0.05f,
                StartColor = new Color(0.72f, 0.72f, 0.72f, 0.28f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.1f),
                SizeOverLifetime = ParticleAuthoring.Grow(1f, 4f),
            });
            AddLight(root.transform, new Color(1f, 0.78f, 0.45f), range: 4f, intensity: 3f, localPosition: new Vector3(0f, 0f, 0.15f));
        });

        public static GameObject TracerRifle(EffectKit kit, bool force) => Build("TracerRifle", force, root =>
        {
            var line = root.AddComponent<LineRenderer>();
            line.sharedMaterial = kit.Tracer;
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.widthMultiplier = 0.035f;
            line.numCapVertices = 2;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            root.AddComponent<TracerView>();
        });

        public static GameObject ImpactConcrete(EffectKit kit, bool force) => Build("ImpactConcrete", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Dust", Material = kit.Dust, Burst = 6, Duration = 0.2f,
                Lifetime = new Vector2(0.5f, 0.9f), Size = new Vector2(0.15f, 0.25f), Speed = new Vector2(0.4f, 1.2f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 35f, ShapeRadius = 0.02f, Gravity = -0.02f,
                StartColor = new Color(0.62f, 0.6f, 0.55f, 0.35f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.1f),
                SizeOverLifetime = ParticleAuthoring.Grow(1f, 3f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Chips", Material = kit.Chip, Burst = 6, Duration = 0.2f,
                Lifetime = new Vector2(0.4f, 0.7f), Size = new Vector2(0.025f, 0.05f), Speed = new Vector2(2f, 4.5f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 40f, Gravity = 1.5f,
                StartColor = new Color(0.45f, 0.43f, 0.4f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.5f),
            });
        });

        public static GameObject ImpactMetal(EffectKit kit, bool force) => Build("ImpactMetal", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Sparks", Material = kit.Spark, Render = ParticleSystemRenderMode.Stretch, Burst = 9, Duration = 0.2f,
                Lifetime = new Vector2(0.2f, 0.4f), Size = new Vector2(0.03f, 0.05f), Speed = new Vector2(3f, 7f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 50f, LengthScale = 4f, Gravity = 1f,
                StartColor = new Color(1f, 0.8f, 0.4f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.3f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Flash", Material = kit.Glow, Burst = 1, Duration = 0.1f, ShapeEnabled = false,
                Lifetime = new Vector2(0.05f, 0.05f), Size = new Vector2(0.18f, 0.18f),
                StartColor = new Color(1f, 0.9f, 0.6f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.2f),
            });
        });

        public static GameObject ImpactCharacter(EffectKit kit, bool force) => Build("ImpactCharacter", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Puff", Material = kit.Dust, Burst = 4, Duration = 0.2f,
                Lifetime = new Vector2(0.3f, 0.5f), Size = new Vector2(0.1f, 0.16f), Speed = new Vector2(0.5f, 1.4f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 40f,
                StartColor = new Color(0.8f, 0.78f, 0.76f, 0.5f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.1f),
                SizeOverLifetime = ParticleAuthoring.Grow(1f, 2.5f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Specks", Material = kit.Chip, Burst = 4, Duration = 0.2f,
                Lifetime = new Vector2(0.25f, 0.45f), Size = new Vector2(0.02f, 0.035f), Speed = new Vector2(1.5f, 3.5f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 45f, Gravity = 1.2f,
                StartColor = new Color(0.8f, 0.78f, 0.75f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.4f),
            });
        });

        public static GameObject ImpactDefault(EffectKit kit, bool force) => Build("ImpactDefault", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Puff", Material = kit.Dust, Burst = 3, Duration = 0.2f,
                Lifetime = new Vector2(0.4f, 0.7f), Size = new Vector2(0.1f, 0.16f), Speed = new Vector2(0.3f, 0.9f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 35f,
                StartColor = new Color(0.7f, 0.7f, 0.68f, 0.3f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.1f),
                SizeOverLifetime = ParticleAuthoring.Grow(1f, 3f),
            });
        });

        // Authored for the shipped Blast radius of 3 m; the presenter scales the instance by gameplay radius / 3.
        public const float ExplosionReferenceRadius = 3f;

        public static GameObject ExplosionGrenade(EffectKit kit, bool force) => Build("ExplosionGrenade", force, root =>
        {
            SetLightSeconds(root, 0.3f);
            var t = root.transform;
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Flash", LocalPosition = new Vector3(0f, 0.6f, 0f), MaxParticleSize = 1f, Material = kit.Glow, Burst = 1, Duration = 0.2f, ShapeEnabled = false,
                Lifetime = new Vector2(0.1f, 0.12f), Size = new Vector2(2.6f, 2.6f),
                StartColor = new Color(1f, 0.9f, 0.6f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.2f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Fireball", LocalPosition = new Vector3(0f, 0.6f, 0f), MaxParticleSize = 1f, Material = kit.Glow, Burst = 14, Duration = 0.2f,
                Lifetime = new Vector2(0.3f, 0.5f), Size = new Vector2(1.7f, 2.4f), Speed = new Vector2(1f, 2.6f),
                Shape = ParticleSystemShapeType.Sphere, ShapeRadius = 0.5f,
                StartColor = new Color(1f, 0.58f, 0.2f, 0.95f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.3f),
                SizeOverLifetime = ParticleAuthoring.Grow(0.5f, 1.4f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Dust", LocalPosition = new Vector3(0f, 0.4f, 0f), MaxParticleSize = 1f, Material = kit.Dust, Burst = 16, Duration = 0.3f,
                Lifetime = new Vector2(0.9f, 1.5f), Size = new Vector2(1.2f, 1.9f), Speed = new Vector2(2.5f, 5f),
                Shape = ParticleSystemShapeType.Sphere, ShapeRadius = 0.5f, Gravity = -0.03f,
                StartColor = new Color(0.52f, 0.46f, 0.4f, 0.6f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.25f),
                SizeOverLifetime = ParticleAuthoring.Grow(0.6f, 2f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Sparks", Material = kit.Spark, Render = ParticleSystemRenderMode.Stretch, Burst = 20, Duration = 0.2f,
                Lifetime = new Vector2(0.45f, 0.9f), Size = new Vector2(0.16f, 0.26f), Speed = new Vector2(6f, 12f),
                Shape = ParticleSystemShapeType.Sphere, ShapeRadius = 0.2f, LengthScale = 5f, Gravity = 1f,
                StartColor = new Color(1f, 0.8f, 0.4f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.4f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Fragments", Material = kit.Chip, Burst = 14, Duration = 0.2f,
                Lifetime = new Vector2(0.7f, 1f), Size = new Vector2(0.16f, 0.3f), Speed = new Vector2(5f, 10f),
                Shape = ParticleSystemShapeType.Hemisphere, ShapeRadius = 0.3f, Gravity = 2f,
                StartColor = new Color(0.1f, 0.09f, 0.08f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.6f),
            });
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Smoke", LocalPosition = new Vector3(0f, 0.7f, 0f), MaxParticleSize = 1f, Material = kit.Smoke, Burst = 18, Duration = 0.4f,
                Lifetime = new Vector2(2.5f, 3.5f), Size = new Vector2(1.8f, 2.6f), Speed = new Vector2(0.5f, 1.4f),
                Shape = ParticleSystemShapeType.Sphere, ShapeRadius = 0.7f, Gravity = -0.04f,
                StartColor = new Color(0.3f, 0.3f, 0.32f, 0.6f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.5f),
                SizeOverLifetime = ParticleAuthoring.Grow(0.8f, 2.2f),
            });
            // A thin ring that ends at the gameplay radius (start size is the diameter at the end of the grow curve). A flat mesh quad, not a
            // HorizontalBillboard: Unity draws horizontal billboards at about 1/sqrt(2) of their size, so the ring would stop short.
            ParticleAuthoring.Build(t, new PsSpec
            {
                Name = "Ring", MaxParticleSize = 1f, Material = kit.Ring, Render = ParticleSystemRenderMode.Mesh, Mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx"),
                LocalEuler = new Vector3(90f, 0f, 0f), Burst = 1, Duration = 0.2f,
                ShapeEnabled = false, RandomRotation = false,
                Lifetime = new Vector2(0.45f, 0.45f), Size = new Vector2(ExplosionReferenceRadius * 2f, ExplosionReferenceRadius * 2f),
                StartColor = new Color(1f, 0.85f, 0.6f, 0.7f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.6f),
                SizeOverLifetime = new AnimationCurve(new Keyframe(0f, 0.1f, 0f, 2.5f), new Keyframe(1f, 1f, 0f, 0f)),   // fast out, settling at the boundary
            });
            AddLight(t, new Color(1f, 0.7f, 0.4f), range: 9f, intensity: 6f, localPosition: new Vector3(0f, 0.8f, 0f));
        });

        // The supportive-action cue: a green ring spreading on the ground (ends at 0.9 m radius) and a few motes rising. Same flat mesh
        // quad as the explosion ring (a HorizontalBillboard would draw at about 0.71 of its size).
        public static GameObject HealPulse(EffectKit kit, bool force) => Build("HealPulse", force, root =>
        {
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Ring", LocalPosition = new Vector3(0f, 0.05f, 0f), LocalEuler = new Vector3(90f, 0f, 0f), MaxParticleSize = 1f,
                Material = kit.Ring, Render = ParticleSystemRenderMode.Mesh, Mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx"),
                Burst = 1, Duration = 0.2f, ShapeEnabled = false, RandomRotation = false,
                Lifetime = new Vector2(0.6f, 0.6f), Size = new Vector2(1.8f, 1.8f),
                StartColor = new Color(0.25f, 1f, 0.5f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.4f),
                SizeOverLifetime = ParticleAuthoring.Grow(0.35f, 1f),
            });
            ParticleAuthoring.Build(root.transform, new PsSpec
            {
                Name = "Motes", Material = kit.Chip, Burst = 14, Duration = 0.3f,
                Lifetime = new Vector2(0.6f, 0.9f), Size = new Vector2(0.12f, 0.2f), Speed = new Vector2(0.6f, 1.4f),
                Shape = ParticleSystemShapeType.Cone, ShapeAngle = 10f, ShapeRadius = 0.6f, Gravity = -0.1f,
                LocalEuler = new Vector3(-90f, 0f, 0f),
                StartColor = new Color(0.3f, 1f, 0.5f, 1f), ColorOverLifetime = ParticleAuthoring.FadeOut(0.4f),
            });
        });

        // ---- shared helpers (also used by the explosion and heal-pulse stages) ----

        public static GameObject Build(string name, bool force, Action<GameObject> populate)
        {
            var path = EffectPaths.Prefabs + "/" + name + ".prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null && !force)
                return existing;
            EffectPaths.EnsureFolder(EffectPaths.Prefabs);
            var root = new GameObject(name);
            try
            {
                root.AddComponent<PooledEffect>();
                populate(root);
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        public static void SetLightSeconds(GameObject root, float seconds)
        {
            var serialized = new SerializedObject(root.GetComponent<PooledEffect>());
            serialized.FindProperty("lightSeconds").floatValue = seconds;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        public static Light AddLight(Transform parent, Color color, float range, float intensity, Vector3 localPosition)
        {
            var child = new GameObject("Light");
            child.transform.SetParent(parent, false);
            child.transform.localPosition = localPosition;
            var light = child.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            light.enabled = false;
            return light;
        }
    }
}
#endif
