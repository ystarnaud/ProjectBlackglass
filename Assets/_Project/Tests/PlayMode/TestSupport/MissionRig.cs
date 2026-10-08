#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The persistent systems a MissionDirector needs, plus a director, built from the project's real prefabs and data.</summary>
    internal sealed class MissionRig : IDisposable
    {
        public readonly TestWorld World = new TestWorld();
        public readonly TacticalPause Pause;
        public readonly Encounter Encounter;
        public readonly UnitSelection Selection;
        public readonly ActiveCharacter Active;
        public readonly CoverRegistry Registry;
        public readonly InteractableRegistry Interactables;
        public readonly CoverDiscovery Discovery;
        public readonly TacticalCameraController Camera;
        public readonly MissionDirector Director;
        readonly MissionSettings settings;
        readonly MissionSystems systems;
        readonly Material ground;
        readonly Material obstacle;
        readonly EnvironmentTheme theme;

        public MissionRig(MissionSettings settings = null, bool withCamera = false, EnvironmentTheme theme = null)
        {
            this.theme = theme;
            Pause = World.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            Encounter = World.CreateEncounter();
            Selection = World.Track(new GameObject("Selection")).AddComponent<UnitSelection>();
            Active = World.Track(new GameObject("ActiveCharacter")).AddComponent<ActiveCharacter>();
            Active.Initialize(null, Pause, Selection);
            Registry = World.CreateRegistry();
            Interactables = World.Track(new GameObject("Interactables")).AddComponent<InteractableRegistry>();
            Discovery = World.Track(new GameObject("Discovery")).AddComponent<CoverDiscovery>();
            Discovery.Initialize(Registry, discoverAtStart: false);
            if (withCamera)
            {
                var rig = World.Track(new GameObject("CameraRig"));
                rig.SetActive(false);
                var cameraObject = new GameObject("Camera");
                cameraObject.transform.SetParent(rig.transform, false);
                var viewCamera = cameraObject.AddComponent<UnityEngine.Camera>();
                Camera = rig.AddComponent<TacticalCameraController>();
                Camera.Initialize(viewCamera, null, null, null, null, null, Active);
                rig.SetActive(true);
            }

            this.settings = settings ?? new MissionSettings();
            ground = Load<Material>("Materials/Ground.mat");
            obstacle = Load<Material>("Materials/Obstacle.mat");
            systems = new MissionSystems
            {
                encounter = Encounter, selection = Selection, activeCharacter = Active, coverRegistry = Registry,
                coverDiscovery = Discovery, pause = Pause, camera = Camera, interactables = Interactables,
            };
            Director = World.Track(new GameObject("Director")).AddComponent<MissionDirector>();
            Director.Initialize(this.settings, FriendlySlots(), HostileSlots(), systems, ground, obstacle, generateAtStart: false, theme: theme);
        }

        /// <summary>Gives the director other hostile slots (the rest of its setup is unchanged).</summary>
        public void SetHostileSlots(HostileSlot[] slots) =>
            Director.Initialize(settings, FriendlySlots(), slots, systems, ground, obstacle, generateAtStart: false, theme: theme);

        /// <summary>Gives the director other friendly slots (the rest of its setup is unchanged).</summary>
        public void SetFriendlySlots(FriendlySlot[] slots) =>
            Director.Initialize(settings, slots, HostileSlots(), systems, ground, obstacle, generateAtStart: false, theme: theme);

        /// <summary>
        /// Adds a squad roster to the persistent systems: the director then spawns the squad from it. The roster is built
        /// while its object is inactive, so nothing runs before it is wired.
        /// </summary>
        public SquadRoster AddRoster(OperativeDefinition[] squad, ProgressionTrack track)
        {
            var host = World.Track(new GameObject("Roster"));
            host.SetActive(false);
            var roster = host.AddComponent<SquadRoster>();
            roster.Initialize(squad, track);
            host.SetActive(true);
            systems.roster = roster;
            return roster;
        }

        public const string DariusPlayerPath = "Assets/Art/Characters/Darius/Prefabs/Darius_Player.prefab";

        /// <summary>The usual three friendly slots with Darius (rifle, humanoid visual, Ranged archetype) in slot 0, as in the scene.</summary>
        public static FriendlySlot[] FriendlySlotsWithDarius()
        {
            var slots = FriendlySlots();
            var darius = AssetDatabase.LoadAssetAtPath<GameObject>(DariusPlayerPath);
            if (darius == null)
                throw new InvalidOperationException("Missing asset " + DariusPlayerPath);
            slots[0].prefab = darius;
            slots[0].archetype = Load<CombatArchetype>("Data/Archetypes/Ranged.asset");
            return slots;
        }

        /// <summary>
        /// Makes the director generate when it starts, like the scene's. Call it before the first yield: Start has not
        /// run yet then. `seedSource` replaces the clock seed a new-seed start draws.
        /// </summary>
        public void GenerateAtStart(bool randomSeed, Func<int> seedSource = null)
        {
            Director.Initialize(settings, FriendlySlots(), HostileSlots(), systems, ground, obstacle, generateAtStart: true,
                randomSeedAtStart: randomSeed, theme: theme);
            if (seedSource != null)
                Director.seedSource = seedSource;
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>("Assets/_Project/" + path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path);
            return asset;
        }

        public static FriendlySlot[] FriendlySlots()
        {
            var prefab = Load<GameObject>("Prefabs/FriendlyUnit.prefab");
            var abilities = new[] { Load<AbilityDefinition>("Data/Abilities/AimedShot.asset"),
                Load<AbilityDefinition>("Data/Abilities/Blast.asset"), Load<AbilityDefinition>("Data/Abilities/Mend.asset") };
            return new[]
            {
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Melee.asset"), abilities = abilities },
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Marksman.asset"), abilities = abilities },
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Ranged.asset"), abilities = abilities },
            };
        }

        public static HostileSlot[] HostileSlots()
        {
            var prefab = Load<GameObject>("Prefabs/HostileUnit.prefab");
            return new[]
            {
                new HostileSlot { prefab = prefab },
                new HostileSlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Ranged.asset") },
                new HostileSlot { prefab = prefab },
            };
        }

        /// <summary>Runs one generation to the end (Ready or Failed).</summary>
        public System.Collections.IEnumerator Generate(int seed)
        {
            Director.Generate(seed);
            yield return TestWorld.WaitUntil(() => Director.State == MissionState.Ready || Director.State == MissionState.Failed, 20f);
        }

        public void Dispose()
        {
            Director.Clear();
            World.Dispose();
            Time.timeScale = 1f;
        }
    }
}
#endif
