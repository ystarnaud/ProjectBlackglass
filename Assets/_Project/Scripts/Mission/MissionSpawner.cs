using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    public sealed class MissionSpawnResult
    {
        public List<CommandableUnit> Friendlies { get; } = new List<CommandableUnit>();
        public List<CommandableUnit> Hostiles { get; } = new List<CommandableUnit>();
    }

    /// <summary>
    /// Puts the squad and the hostiles into a built mission. Units are instantiated under the (inactive) Actors object
    /// and wired before it is activated, so no component's OnEnable ever sees a half-wired unit. Every spawn point is
    /// snapped to the NavMesh and checked for geometry first; one bad point fails the whole spawn.
    /// </summary>
    public static class MissionSpawner
    {
        const float UnitRadius = 0.45f;

        public static bool TrySpawn(GeneratedMission mission, IReadOnlyList<FriendlySlot> friendlySlots,
            IReadOnlyList<HostileSlot> hostileSlots, MissionSystems systems, out MissionSpawnResult result, out string failure)
        {
            result = new MissionSpawnResult();
            failure = null;
            var layout = mission.Layout;
            if (friendlySlots.Count == 0 || hostileSlots.Count == 0)
            {
                failure = "the director has no friendly or no hostile slots";
                return false;
            }
            var actors = mission.Actors.gameObject;
            actors.SetActive(false);

            var friendlyCentre = Centre(layout, layout.FriendlySpawns);
            var hostileCentre = Centre(layout, layout.HostileSpawns);
            var grounds = new List<(CommandableUnit unit, Vector3 ground)>();

            for (var i = 0; i < layout.FriendlySpawns.Count; i++)
            {
                var slot = friendlySlots[i % friendlySlots.Count];
                if (!TryCreate(mission, slot.prefab, $"FriendlyUnit_{i + 1}", layout.TileCenter(layout.FriendlySpawns[i]),
                        hostileCentre - friendlyCentre, out var unit, out var ground, out failure))
                    return false;
                if (slot.archetype != null)
                    unit.GetComponent<UnitAttacker>().ApplyArchetype(slot.archetype);
                if (slot.abilities != null && slot.abilities.Length > 0)
                    unit.gameObject.AddComponent<UnitAbilities>().Initialize(systems.encounter, slot.abilities);
                unit.GetComponent<CompanionAI>().Wire(systems.activeCharacter, systems.encounter);
                unit.GetComponent<CompanionAI>().HoldUntilLeaderMoves();   // no squad-up walk at spawn (decision 028)
                unit.GetComponent<UnitCover>().Wire(systems.coverRegistry);
                result.Friendlies.Add(unit);
                grounds.Add((unit, ground));
            }
            for (var i = 0; i < layout.HostileSpawns.Count; i++)
            {
                var slot = hostileSlots[i % hostileSlots.Count];
                if (!TryCreate(mission, slot.prefab, $"HostileUnit_{i + 1}", layout.TileCenter(layout.HostileSpawns[i]),
                        friendlyCentre - hostileCentre, out var unit, out var ground, out failure))
                    return false;
                if (slot.archetype != null)
                    unit.GetComponent<UnitAttacker>().ApplyArchetype(slot.archetype);
                unit.GetComponent<EnemyAI>().Wire(systems.encounter, systems.coverRegistry);
                unit.GetComponent<UnitCover>().Wire(systems.coverRegistry);
                result.Hostiles.Add(unit);
                grounds.Add((unit, ground));
            }

            var friendlyHealths = new List<Health>();
            var selectables = new List<SelectableUnit>();
            foreach (var unit in result.Friendlies)
            {
                friendlyHealths.Add(unit.GetComponent<Health>());
                selectables.Add(unit.GetComponent<SelectableUnit>());
            }
            var hostileHealths = new List<Health>();
            foreach (var unit in result.Hostiles)
                hostileHealths.Add(unit.GetComponent<Health>());

            systems.encounter.Initialize(friendlyHealths, hostileHealths);
            systems.selection.Clear();
            systems.selection.Initialize(selectables.ToArray());
            systems.activeCharacter.SetUnit(result.Friendlies[0]);
            systems.activeCharacter.SetTakeover(false);
            systems.activeCharacter.SetFollow(true);

            actors.SetActive(true);

            foreach (var (unit, ground) in grounds)
            {
                var agent = unit.GetComponent<NavMeshAgent>();
                if (!agent.isOnNavMesh)
                    agent.Warp(ground);
                if (!agent.isOnNavMesh)
                {
                    failure = $"{unit.name} could not be placed on the NavMesh at {ground}";
                    return false;
                }
            }
            return true;
        }

        static bool TryCreate(GeneratedMission mission, GameObject prefab, string name, Vector3 world, Vector3 facing,
            out CommandableUnit unit, out Vector3 ground, out string failure)
        {
            unit = null;
            failure = null;
            ground = world;
            if (prefab == null)
            {
                failure = $"{name} has no prefab";
                return false;
            }
            if (!NavMesh.SamplePosition(world, out var hit, 1f, NavMesh.AllAreas))
            {
                failure = $"{name}'s spawn at {world} is not on the NavMesh";
                return false;
            }
            ground = hit.position;
            var bottom = ground + Vector3.up * (UnitRadius + 0.1f);
            var top = ground + Vector3.up * (2f - UnitRadius - 0.1f);
            if (Physics.CheckCapsule(bottom, top, UnitRadius, ~0, QueryTriggerInteraction.Ignore))
            {
                failure = $"{name}'s spawn at {ground} is inside geometry";
                return false;
            }
            var instance = Object.Instantiate(prefab, mission.Actors);
            instance.name = name;
            if (!instance.TryGetComponent<NavMeshAgent>(out var agent) || !instance.TryGetComponent(out unit))
            {
                failure = $"{name}'s prefab needs a NavMeshAgent and a CommandableUnit";
                return false;
            }
            facing.y = 0f;
            var rotation = facing.sqrMagnitude > 0.01f ? Quaternion.LookRotation(facing) : Quaternion.identity;
            instance.transform.SetPositionAndRotation(ground + Vector3.up * agent.baseOffset, rotation);
            return true;
        }

        static Vector3 Centre(MissionLayout layout, IReadOnlyList<Vector2Int> tiles)
        {
            var sum = Vector3.zero;
            foreach (var tile in tiles)
                sum += layout.TileCenter(tile);
            return sum / Mathf.Max(1, tiles.Count);
        }
    }
}
