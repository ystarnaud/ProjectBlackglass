using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One friendly of the squad: the unit prefab, its weapon archetype and its abilities.</summary>
    [Serializable]
    public sealed class FriendlySlot
    {
        public GameObject prefab;
        public CombatArchetype archetype;
        public AbilityDefinition[] abilities = new AbilityDefinition[0];
    }

    /// <summary>One kind of hostile: the unit prefab and, optionally, an archetype (none keeps the prefab's own stats).</summary>
    [Serializable]
    public sealed class HostileSlot
    {
        public GameObject prefab;
        public CombatArchetype archetype;
    }

    /// <summary>The persistent systems a mission is generated into. `camera`, `abilityTargeting` and `interactables` are optional.</summary>
    [Serializable]
    public sealed class MissionSystems
    {
        public Encounter encounter;
        public UnitSelection selection;
        public ActiveCharacter activeCharacter;
        public CoverRegistry coverRegistry;
        public CoverDiscovery coverDiscovery;
        public TacticalPause pause;
        public TacticalCameraController camera;
        public AbilityTargeting abilityTargeting;
        public InteractableRegistry interactables;
    }
}
