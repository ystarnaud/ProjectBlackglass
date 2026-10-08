#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// A small in-memory squad of three differently configured operatives, built from the project's real unit prefab,
    /// archetypes and abilities, with a progression track shaped like the shipped one. Nothing is written to disk.
    /// </summary>
    internal sealed class OperativeKit : IDisposable
    {
        readonly List<UnityEngine.Object> created = new List<UnityEngine.Object>();

        public ProgressionTrack Track { get; private set; }
        public OperativeDefinition[] Definitions { get; private set; }

        public static OperativeKit Build()
        {
            var kit = new OperativeKit();
            var combat = kit.Own(AdvancementChoice.Create("combat", "Combat Training", "+15% attack damage", new StatModifiers { attackDamage = 0.15f }));
            var hull = kit.Own(AdvancementChoice.Create("survivability", "Reinforced", "+25 max health", new StatModifiers { maxHealth = 25 }));
            var speed = kit.Own(AdvancementChoice.Create("mobility", "Fleet-footed", "+0.75 m/s", new StatModifiers { moveSpeed = 0.75f }));
            var focus = kit.Own(AdvancementChoice.Create("ability", "Focus", "+20% ability power, -15% cooldown",
                new StatModifiers { abilityPower = 0.2f, abilityCooldownReduction = 0.15f }));
            kit.Track = kit.Own(ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull, speed, focus }, 150, 50));

            var assault = kit.Own(OperativeRole.Create("Assault", "", new StatModifiers { attackDamage = 0.2f }));
            var recon = kit.Own(OperativeRole.Create("Recon", "", new StatModifiers { moveSpeed = 0.5f }));
            var support = kit.Own(OperativeRole.Create("Support", "", new StatModifiers { abilityPower = 0.15f }));

            var prefab = Load<GameObject>("Prefabs/FriendlyUnit.prefab");
            var ranged = Load<CombatArchetype>("Data/Archetypes/Ranged.asset");
            var marksman = Load<CombatArchetype>("Data/Archetypes/Marksman.asset");
            var aimed = Load<AbilityDefinition>("Data/Abilities/AimedShot.asset");
            var blast = Load<AbilityDefinition>("Data/Abilities/Blast.asset");
            var mend = Load<AbilityDefinition>("Data/Abilities/Mend.asset");

            kit.Definitions = new[]
            {
                kit.Own(OperativeDefinition.Create("test-alpha", "Alpha", assault, prefab, 130, 5f, ranged, new[] { aimed, blast })),
                kit.Own(OperativeDefinition.Create("test-bravo", "Bravo", recon, prefab, 80, 6.5f, marksman, new[] { aimed })),
                kit.Own(OperativeDefinition.Create("test-charlie", "Charlie", support, prefab, 100, 5.5f, ranged, new[] { mend, aimed })),
            };
            return kit;
        }

        T Own<T>(T asset) where T : UnityEngine.Object
        {
            created.Add(asset);
            return asset;
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>("Assets/_Project/" + path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path);
            return asset;
        }

        public void Dispose()
        {
            foreach (var asset in created)
            {
                if (asset != null)
                    UnityEngine.Object.DestroyImmediate(asset);
            }
            created.Clear();
        }
    }
}
#endif
