using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The scene-level owner of the squad's items (decision 043): wraps an InventorySession, builds it from the roster,
    /// seeds the starter items once, and settles the running mission when the director reports its result. Lives next to
    /// the SquadRoster so it outlasts every generated mission. In memory only: nothing is written to disk.
    /// </summary>
    [DefaultExecutionOrder(5)]   // after SquadRoster.Awake (order 0) has built its members
    public sealed class SquadInventory : MonoBehaviour
    {
        [SerializeField] ItemCatalogue catalogue;
        [SerializeField] StarterLoadout starter;
        [SerializeField] SquadRoster roster;
        [SerializeField] MissionDirector director;
        [SerializeField, Min(1)] int bagCapacity = 8;
        // The scene opens on the loadout panel and waits for Deploy instead of generating a mission at once.
        [SerializeField] bool startInLoadout = true;

        InventorySession core;
        // Roster members the session has been seeded for. Core can be read before SquadRoster.Awake builds the members
        // (scene order); the next read after they appear seeds them then. SeedStarter grants each operative only once.
        int seededMembers;
        Func<string> missionIdSource;
        bool subscribed;

        public ItemCatalogue Catalogue => catalogue;
        public bool StartInLoadout => startInLoadout;
        public SquadRoster Roster => roster;

        /// <summary>The rules and state. Built on first use; roster members that appear later are seeded on the next read.</summary>
        public InventorySession Core
        {
            get
            {
                if (core == null)
                    Build();
                else if (roster != null && roster.Count != seededMembers)
                    SeedLate();
                return core;
            }
        }

        void Awake()
        {
            if (core == null)
                Build();
            else if (roster != null && roster.Count != seededMembers)
                SeedLate();
        }

        void OnEnable() => Subscribe();

        void OnDisable() => Unsubscribe();

        internal void Initialize(ItemCatalogue itemCatalogue, StarterLoadout starterLoadout, SquadRoster squad, int bag,
            Func<string> currentMissionId, MissionDirector missionDirector = null)
        {
            Unsubscribe();
            catalogue = itemCatalogue;
            starter = starterLoadout;
            roster = squad;
            director = missionDirector;
            bagCapacity = Mathf.Max(1, bag);
            missionIdSource = currentMissionId;
            core = null;
            Build();
            if (isActiveAndEnabled)
                Subscribe();
        }

        void Build()
        {
            var errors = new List<string>();
            if (catalogue == null)
                errors.Add($"{name}: the squad inventory has no item catalogue.");
            else
                catalogue.Validate(errors);
            core = new InventorySession(catalogue, bagCapacity);
            seededMembers = 0;
            SeedFromRoster(errors);
            foreach (var error in errors)
                Debug.LogError(error, this);
        }

        void SeedLate()
        {
            var errors = new List<string>();
            SeedFromRoster(errors);
            foreach (var error in errors)
                Debug.LogError(error, this);
        }

        void SeedFromRoster(List<string> errors)
        {
            if (roster == null)
                return;
            var ids = new List<string>();
            foreach (var member in roster.Members)
                ids.Add(member.Id);
            seededMembers = ids.Count;   // first, so a Changed handler reading Core does not seed again
            foreach (var id in ids)
                core.EnsureOperative(id);
            core.SeedStarter(starter, ids, errors);
        }

        void Subscribe()
        {
            if (subscribed || director == null)
                return;
            director.MissionFinished += HandleMissionFinished;
            subscribed = true;
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            if (director != null)
                director.MissionFinished -= HandleMissionFinished;
            subscribed = false;
        }

        /// <summary>Starts a mission for this instance id: makes sure every roster member has a loadout, then clones the session.</summary>
        public void BeginMission(string missionId)
        {
            if (roster != null)
            {
                foreach (var member in roster.Members)
                    Core.EnsureOperative(member.Id);
            }
            Core.BeginMission(missionId);
        }

        public void AbortMission() => Core.AbortMission();

        internal void HandleMissionFinished(MissionPhase phase)
        {
            var id = missionIdSource != null ? missionIdSource() : director != null ? director.InstanceId : null;
            Core.Settle(id, phase == MissionPhase.Success);
        }

        /// <summary>The operative's working loadout during a mission, else null.</summary>
        public OperativeLoadout WorkingLoadout(string operativeId) =>
            core != null && core.Working != null ? core.Working.Loadout(operativeId) : null;

        /// <summary>What the operative has equipped in the active state (working during a mission, else the session); default when unknown.</summary>
        public EquippedItems EquippedFor(string operativeId) => Core.EquippedFor(Core.Active, operativeId);
    }
}
