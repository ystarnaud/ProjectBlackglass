using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// The gameplay systems the tactical HUD reads, as optional references (wired in the scene or by the scene builder). A
    /// missing reference hides the part of the HUD it feeds; nothing here is required.
    /// </summary>
    [Serializable]
    public sealed class HudSources
    {
        public ActiveCharacter activeCharacter;
        public UnitSelection selection;
        public TacticalPause tacticalPause;
        public Encounter encounter;
        public MissionDirector director;
        public SquadRoster roster;
        public IntelligenceService intelligence;
        public AbilityTargeting abilityTargeting;
        public TacticalCursor cursor;
        public PlayerCommandInput commandInput;
        public ActiveInputDevice inputDevice;
        public InputActionAsset controls;
        public Camera camera;
        // Optional: the prototype intel map; the HUD places it above the squad roster.
        public IntelMapView intelMap;
    }
}
