#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Generates the combat presentation assets once: textures, materials, effect prefabs, profiles, placeholder sounds, and
    /// the references on the rifle weapon visuals and the ability assets. Idempotent: it creates only what is missing and
    /// fills only empty references, so hand tuning survives; "Rebuild" regenerates prefabs and materials in place (GUIDs kept).
    /// Nothing here runs when the game starts.
    /// </summary>
    public static class CombatPresentationAssetBuilder
    {
        const string VisualsRoot = "Assets/_Project/Data/Items/Visuals/";

        [MenuItem("Blackglass/Presentation/Build Effects")]
        public static void BuildMenu() => Build(false);

        [MenuItem("Blackglass/Presentation/Rebuild Effects (overwrite prefabs and materials)")]
        public static void RebuildMenu() => Build(true);

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build(false);

        /// <summary>Unity -batchmode -executeMethod entry for the overwrite rebuild (prefabs and materials regenerated, GUIDs kept).</summary>
        public static void RebuildFromCommandLine() => Build(true);

        public static void Build(bool force)
        {
            var kit = EffectKit.Load(force);
            BuildRifleStage(kit, force);
            BuildExplosionStage(kit, force);
            BuildAudioStage(force);
            BuildPulseStage(kit, force);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Combat presentation assets built (force=" + force + ").");
        }

        static CombatPresentationProfile BuildRifleStage(EffectKit kit, bool force)
        {
            var muzzle = EffectPrefabs.MuzzleFlashRifle(kit, force);
            var tracer = EffectPrefabs.TracerRifle(kit, force);
            var concrete = EffectPrefabs.ImpactConcrete(kit, force);
            var metal = EffectPrefabs.ImpactMetal(kit, force);
            var character = EffectPrefabs.ImpactCharacter(kit, force);
            var fallback = EffectPrefabs.ImpactDefault(kit, force);

            var rifle = LoadOrCreateProfile("RifleCombatPresentation");
            if (rifle.MuzzleEffect == null)
                rifle.SetMuzzle(muzzle, 1f);
            if (!rifle.Tracer.IsUsable)
                rifle.SetTracer(TracerStyle.Create(tracer, 0.035f, 6f, 300f));
            rifle.EnsureImpact(ImpactSurfaceKind.Concrete, concrete, null);
            rifle.EnsureImpact(ImpactSurfaceKind.Metal, metal, null);
            rifle.EnsureImpact(ImpactSurfaceKind.Character, character, null);
            rifle.EnsureImpact(ImpactSurfaceKind.Default, fallback, null);
            EditorUtility.SetDirty(rifle);

            var defaults = LoadOrCreateProfile("DefaultCombatPresentation");
            defaults.EnsureImpact(ImpactSurfaceKind.Character, character, null);
            defaults.EnsureImpact(ImpactSurfaceKind.Default, fallback, null);
            EditorUtility.SetDirty(defaults);

            GiveRifleProfile("RifleVisual", rifle);
            GiveRifleProfile("MarksmanRifleVisual", rifle);
            return rifle;
        }

        const string AbilitiesRoot = "Assets/_Project/Data/Abilities/";

        static void BuildExplosionStage(EffectKit kit, bool force)
        {
            var explosion = EffectPrefabs.ExplosionGrenade(kit, force);
            var profile = LoadOrCreateProfile("BlastCombatPresentation");
            if (profile.AreaEffect == null)
                profile.SetArea(explosion, EffectPrefabs.ExplosionReferenceRadius, profile.AreaCue, 0.7f);
            EditorUtility.SetDirty(profile);

            var blast = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(AbilitiesRoot + "Blast.asset");
            if (blast == null)
                throw new System.InvalidOperationException("Missing the Blast ability asset.");
            if (blast.Presentation == null)
            {
                blast.SetPresentation(profile);
                EditorUtility.SetDirty(blast);
            }
        }

        static void BuildAudioStage(bool force)
        {
            PlaceholderSounds.WriteReadme();
            var shot = AudioCue.Create(0.8f, 100, 0.05f, PlaceholderSounds.RifleShot(force)).WithFalloff(5f, 55f);
            var concrete = AudioCue.Create(0.6f, 160, 0.04f, PlaceholderSounds.ImpactConcrete(force)).WithFalloff(3f, 35f);
            var metal = AudioCue.Create(0.6f, 160, 0.04f, PlaceholderSounds.ImpactMetal(force)).WithFalloff(3f, 40f);
            var body = AudioCue.Create(0.5f, 160, 0.04f, PlaceholderSounds.ImpactBody(force)).WithFalloff(3f, 30f);
            var fall = AudioCue.Create(0.6f, 150, 0.1f, PlaceholderSounds.BodyFall(force)).WithFalloff(3f, 35f);
            var chime = AudioCue.Create(0.6f, 140, 0.1f, PlaceholderSounds.HealChime(force)).WithFalloff(3f, 30f);
            var boom = AudioCue.Create(1f, 40, 0.1f, PlaceholderSounds.Explosion(force)).WithFalloff(8f, 80f);

            var rifle = LoadOrCreateProfile("RifleCombatPresentation");
            if (rifle.FireCue.IsEmpty)
                rifle.SetFireCue(shot);
            rifle.EnsureImpact(ImpactSurfaceKind.Concrete, null, concrete);
            rifle.EnsureImpact(ImpactSurfaceKind.Metal, null, metal);
            rifle.EnsureImpact(ImpactSurfaceKind.Character, null, body);
            rifle.EnsureImpact(ImpactSurfaceKind.Default, null, concrete);
            EditorUtility.SetDirty(rifle);

            var defaults = LoadOrCreateProfile("DefaultCombatPresentation");
            defaults.EnsureImpact(ImpactSurfaceKind.Character, null, body);
            defaults.EnsureImpact(ImpactSurfaceKind.Default, null, concrete);
            if (defaults.DeathCue.IsEmpty)
                defaults.SetDeathCue(fall);
            if (defaults.PulseCue.IsEmpty)
                defaults.SetPulse(defaults.PulseEffect, chime);
            EditorUtility.SetDirty(defaults);

            var blast = LoadOrCreateProfile("BlastCombatPresentation");
            if (blast.AreaCue.IsEmpty)
                blast.SetArea(blast.AreaEffect, blast.ReferenceRadius, boom, blast.CameraImpulse);
            EditorUtility.SetDirty(blast);
        }

        // Fills only an empty pulse prefab; the chime cue (set by the audio stage) is kept.
        static void BuildPulseStage(EffectKit kit, bool force)
        {
            var pulse = EffectPrefabs.HealPulse(kit, force);
            var defaults = LoadOrCreateProfile("DefaultCombatPresentation");
            if (defaults.PulseEffect == null)
                defaults.SetPulse(pulse, defaults.PulseCue);
            EditorUtility.SetDirty(defaults);
        }

        static void GiveRifleProfile(string visualName, CombatPresentationProfile profile)
        {
            var visual = AssetDatabase.LoadAssetAtPath<WeaponVisualAsset>(VisualsRoot + visualName + ".asset");
            if (visual == null)
                throw new System.InvalidOperationException("Missing weapon visual " + visualName + ".");
            if (visual.Presentation != null)
                return;
            visual.SetPresentation(profile);
            EditorUtility.SetDirty(visual);
        }

        internal static CombatPresentationProfile LoadOrCreateProfile(string name)
        {
            var path = EffectPaths.Profiles + "/" + name + ".asset";
            var profile = AssetDatabase.LoadAssetAtPath<CombatPresentationProfile>(path);
            if (profile != null)
                return profile;
            EffectPaths.EnsureFolder(EffectPaths.Profiles);
            profile = ScriptableObject.CreateInstance<CombatPresentationProfile>();
            AssetDatabase.CreateAsset(profile, path);
            return profile;
        }
    }
}
#endif
