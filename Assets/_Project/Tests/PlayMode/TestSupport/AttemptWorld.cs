#if UNITY_EDITOR
using System.Collections.Generic;
using Blackglass.Backend.Core;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// A server-like mission world for the attempt tests: the simulation systems the server scene holds, a MissionDirector
    /// with the server's fixture settings and slots, and a SquadInventory holding the shipped catalogue and loot table.
    /// Everything it makes is destroyed by Dispose.
    /// </summary>
    internal sealed class AttemptWorld
    {
        const string Root = "Assets/_Project/";
        public const string CatalogueVersion = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        public const string AttemptId = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";
        public const string SessionId = "0192f1a5-0000-7000-8000-000000000001";
        public const string ReconDefinition = "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02";
        public const string AssaultDefinition = "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301";

        readonly List<Object> made = new List<Object>();

        public MissionDirector Director { get; }
        public SquadInventory Inventory { get; }
        public ItemCatalogue Catalogue { get; }
        public LootTable LootTable { get; }
        public TacticalPause Pause { get; }
        public SquadRoster Roster { get; }

        /// <param name="breakAfterFill">No cover discovery system: the director then throws right after the containers are filled (a fault test).</param>
        public AttemptWorld(bool breakAfterFill = false)
        {
            Catalogue = Load<ItemCatalogue>("Data/Items/ItemCatalogue.asset");
            LootTable = Load<LootTable>("Data/Items/LootTable.asset");
            var systems = Make("Systems");
            Pause = systems.AddComponent<TacticalPause>();
            var encounter = systems.AddComponent<Encounter>();
            var selection = systems.AddComponent<UnitSelection>();
            var active = systems.AddComponent<ActiveCharacter>();
            var registry = systems.AddComponent<CoverRegistry>();
            var discovery = systems.AddComponent<CoverDiscovery>();
            var interactables = systems.AddComponent<InteractableRegistry>();
            var intelligence = systems.AddComponent<IntelligenceService>();
            active.Initialize(null, Pause, selection);
            discovery.Initialize(registry, discoverAtStart: false);
            var squad = Make("Squad");
            squad.SetActive(false); // Awake after Initialize: an inventory without its catalogue logs an error
            Roster = squad.AddComponent<SquadRoster>();
            Inventory = squad.AddComponent<SquadInventory>();
            Inventory.Initialize(Catalogue, null, null, 8, null);
            squad.SetActive(true);
            Director = Make("Mission").AddComponent<MissionDirector>();
            var prefab = Load<GameObject>("Prefabs/FriendlyUnit.prefab");
            Director.Initialize(ServerFixture.Settings(),
                new[]
                {
                    new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Ranged.asset") },
                    new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Melee.asset") },
                },
                new[] { new HostileSlot { prefab = Load<GameObject>("Prefabs/HostileUnit.prefab") } },
                new MissionSystems
                {
                    encounter = encounter, selection = selection, activeCharacter = active, coverRegistry = registry, coverDiscovery = breakAfterFill ? null : discovery,
                    pause = Pause, interactables = interactables, intelligence = intelligence, inventory = Inventory, roster = Roster,
                },
                Load<Material>("Materials/Ground.mat"), Load<Material>("Materials/Obstacle.mat"), generateAtStart: false);
            Director.SetLootTable(LootTable);
        }

        GameObject Make(string name)
        {
            var go = new GameObject(name);
            made.Add(go);
            return go;
        }

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(Root + path);

        /// <summary>A register answer for one account with two operatives (recon at slot 0, assault at slot 1) and starter-like gear.</summary>
        public static SessionRegisterResponse Response(int seed, string sessionId = SessionId)
        {
            SnapshotOperativeDto Operative(int slot, string id, string definition, string rifleId, string rifle) => new SnapshotOperativeDto
            {
                slot = slot, operativeId = id, definitionId = definition, experience = 0, rank = 1, choices = new string[0],
                bag = new SnapshotBagDto
                {
                    instances = new[] { new SnapshotInstanceDto { itemId = rifleId, definitionId = rifle } },
                    stacks = new[] { new SnapshotStackDto { definitionId = "item.medkit", quantity = 2 } },
                },
                equipment = new EquipmentSlotsDto { weapon = rifleId },
            };
            var snapshot = new LoadoutSnapshotDto
            {
                accountId = "JG", inventoryVersion = 1, catalogueVersion = CatalogueVersion,
                operatives = new[]
                {
                    Operative(0, "C", ReconDefinition, "2k", "item.marksman-rifle"),
                    Operative(1, "D", AssaultDefinition, "2m", "item.service-rifle"),
                },
            };
            return new SessionRegisterResponse
            {
                processSessionId = sessionId, seed = seed, missionTemplate = new MissionTemplateDto { id = "procedural-standard", version = 1 },
                catalogueVersion = CatalogueVersion,
                participants = new[] { new RegisterParticipantDto { accountId = "JG", loadout = snapshot } },
                deadlines = new LaunchDeadlineDto { launch = "2026-10-21T12:00:00Z" },
            };
        }

        public AttemptStart Start(int seed, string sessionId = SessionId)
        {
            var ok = AttemptStart.TryCreate(AttemptId, Response(seed, sessionId), CatalogueVersion, id => id == ReconDefinition || id == AssaultDefinition,
                ItemLedgerMapping.Catalogue(Catalogue), out var start, out var refusal);
            NUnit.Framework.Assert.That(ok, NUnit.Framework.Is.True, refusal);
            return start;
        }

        public void Track(Object o) => made.Add(o);

        public static OperativeDefinition[] Definitions() => new[]
        {
            Load<OperativeDefinition>("Data/Operatives/Definitions/Kestrel.asset"),
            Load<OperativeDefinition>("Data/Operatives/Definitions/Darius.asset"),
            Load<OperativeDefinition>("Data/Operatives/Definitions/Sable.asset"),
        };

        public static ProgressionTrack Progression() => Load<ProgressionTrack>("Data/Operatives/Progression.asset");

        /// <summary>The server's snapshot mode: the roster and the inventory are the attempt's (no starter, no settlement, no XP).</summary>
        public void UseSnapshot(AttemptStart start, OperativeDefinition[] definitions = null)
        {
            NUnit.Framework.Assert.That(AttemptSquad.TryMembers(start, definitions ?? Definitions(), Progression(), out var members, out var problem), NUnit.Framework.Is.True, problem);
            Roster.InitializeFromSnapshot(members, Progression());
            NUnit.Framework.Assert.That(Inventory.InitializeFromSnapshot(Catalogue, Roster, 8, start.Snapshot, out problem), NUnit.Framework.Is.True, problem);
        }

        public AttemptSession Session(AttemptStart start)
        {
            var session = AttemptSession.Begin(start, Director, Inventory, ItemLedgerMapping.Catalogue(Catalogue), new ServerClock(), "editor", out var refusal);
            NUnit.Framework.Assert.That(refusal, NUnit.Framework.Is.Null);
            return session;
        }

        public void Dispose()
        {
            if (Director != null)
                Director.Clear();
            foreach (var o in made)
                if (o != null)
                    Object.Destroy(o);
            made.Clear();
        }
    }
}
#endif
