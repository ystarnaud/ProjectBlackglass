#if UNITY_EDITOR
using System;
using FishNet.Managing.Object;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the dedicated server's scene (Checkpoint A, decision 045): the simulation systems a mission needs and a
    /// MissionDirector holding the fixture, nothing else (no camera, light, audio, UI, input or debug view). Rebuilt from
    /// scratch every run, so it is idempotent; the client scenes are never opened.
    /// </summary>
    public static class ServerSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/ServerMission.unity";
        /// <summary>The fixture seed (ServerFixture.Seed).</summary>
        public const int FixtureSeed = ServerFixture.Seed;
        const string Root = "Assets/_Project/";

        [MenuItem("Blackglass/Server/Build Server Scene")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        /// <summary>The small fixture mission (ServerFixture.Settings).</summary>
        public static MissionSettings FixtureSettings() => ServerFixture.Settings();

        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var server = new GameObject("Server");
            var systems = new GameObject("Systems");
            var mission = new GameObject("Mission");

            var pause = systems.AddComponent<TacticalPause>();
            var encounter = systems.AddComponent<Encounter>();
            var selection = systems.AddComponent<UnitSelection>();
            var active = systems.AddComponent<ActiveCharacter>();
            var registry = systems.AddComponent<CoverRegistry>();
            var discovery = systems.AddComponent<CoverDiscovery>();
            var interactables = systems.AddComponent<InteractableRegistry>();
            var intelligence = systems.AddComponent<IntelligenceService>();
            active.Initialize(null, pause, selection);
            discovery.Initialize(registry, discoverAtStart: false);

            var director = mission.AddComponent<MissionDirector>();
            director.Initialize(FixtureSettings(), FriendlySlots(), HostileSlots(), new MissionSystems
                {
                    encounter = encounter,
                    selection = selection,
                    activeCharacter = active,
                    coverRegistry = registry,
                    coverDiscovery = discovery,
                    pause = pause,
                    interactables = interactables,
                    intelligence = intelligence,
                },
                Load<Material>("Materials/Ground.mat"), Load<Material>("Materials/Obstacle.mat"),
                generateAtStart: false, randomSeedAtStart: false, theme: null);

            server.AddComponent<ServerBootstrap>().Initialize(director, pause);
            // The networking adapter is in the scene but inert: it builds FishNet's manager only when -net is given.
            server.AddComponent<NetServer>().Initialize(
                Load<GameObject>("Prefabs/Net/NetUnitAvatar.prefab"),
                AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset"),
                intelligence);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Could not save " + ScenePath + ".");
            Debug.Log($"ServerSceneBuilder: saved {ScenePath} (fixture seed {FixtureSeed}).");
        }

        // Ranged first: the diagnostic's mover is friendly 0 and must need line of sight. No abilities: the fixture keeps
        // its asset references (and so the server build's content) to the simulation.
        static FriendlySlot[] FriendlySlots()
        {
            var prefab = Load<GameObject>("Prefabs/FriendlyUnit.prefab");
            return new[]
            {
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Ranged.asset") },
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Melee.asset") },
            };
        }

        static HostileSlot[] HostileSlots() => new[] { new HostileSlot { prefab = Load<GameObject>("Prefabs/HostileUnit.prefab") } };

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(Root + path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + Root + path);
            return asset;
        }
    }
}
#endif
