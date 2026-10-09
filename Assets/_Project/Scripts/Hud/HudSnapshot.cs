using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public enum HudAbilityState { Ready, Cooldown, Armed, Unavailable }
    public enum HudObjectiveKind { Active, Completed, Failed, Locked, Unknown }
    public enum HudExtractionState { Hidden, Unknown, Locked, Available, Active, Extracted }
    public enum HudMarkKind { Hostile, LastKnown, Objective, Extraction }

    public struct HudSquadCard
    {
        public CommandableUnit Unit;      // for requests; rebuilt every frame
        public string Name, Role, Initials, Tag;   // Tag: "", FOLLOWING, ATTACHED, PARKED, HOLDING (companions only)
        public int Rank, Health, MaxHealth;        // Rank 0 = not shown
        public bool IsDown, IsControlled, IsSelected;
    }

    public struct HudAbilitySlot
    {
        public int Slot; public string Prompt, Name; public HudAbilityState State;
        public float Remaining, Fraction;          // Fraction = Remaining / total cooldown, 0..1
    }

    public struct HudCommandStep { public int Number; public string Text; public bool IsCurrent; }
    public struct HudObjectiveRow { public HudObjectiveKind Kind; public string Text; }
    public struct HudPromptEntry { public string Label, Prompt; }
    public struct HudWorldMark { public HudMarkKind Kind; public Vector3 World; public string Text; }

    public struct HudTarget
    {
        public bool Visible; public Health Unit; public string Name, Detail, Tag, CoverText; public int Health, MaxHealth;
    }

    /// <summary>
    /// Everything the tactical HUD shows, as plain data rebuilt by the presenter each frame. The views read it and draw it;
    /// they never query gameplay. Hostile data only ever enters it through the knowledge filter.
    /// </summary>
    public sealed class HudSnapshot
    {
        public readonly List<HudSquadCard> Squad = new List<HudSquadCard>();
        public readonly List<HudAbilitySlot> Abilities = new List<HudAbilitySlot>();
        public readonly List<HudCommandStep> Queue = new List<HudCommandStep>();
        public readonly List<HudObjectiveRow> Objectives = new List<HudObjectiveRow>();
        public readonly List<HudPromptEntry> Prompts = new List<HudPromptEntry>();
        public readonly List<HudWorldMark> Marks = new List<HudWorldMark>();
        public bool HasMission; public string PhaseText = string.Empty, BannerText = string.Empty;
        public HudExtractionState Extraction; public int ExtractionInside, ExtractionRequired;
        public bool IsPaused; public string ResumePrompt = string.Empty;
        public bool HasControlled; public string ControlledName = string.Empty, ControlledRole = string.Empty, ControlledCover = string.Empty; public int ControlledRank, ControlledHealth, ControlledMaxHealth;
        public bool HasFollow, FollowOn;
        public string QueueOwner = string.Empty; public int QueueHidden;          // QueueHidden = steps beyond the shown limit
        public CommandableUnit QueueUnit;                          // the queue subject, for the CLEAR request
        public bool CanClearOrders;
        public string CasterName = string.Empty; public bool IsArmed; public string ArmedLine = string.Empty;   // "" when not armed
        public HudTarget Target;

        public void Clear()
        {
            Squad.Clear();
            Abilities.Clear();
            Queue.Clear();
            Objectives.Clear();
            Prompts.Clear();
            Marks.Clear();
            HasMission = false; PhaseText = string.Empty; BannerText = string.Empty;
            Extraction = HudExtractionState.Hidden; ExtractionInside = 0; ExtractionRequired = 0;
            IsPaused = false; ResumePrompt = string.Empty;
            HasControlled = false; ControlledName = string.Empty; ControlledRole = string.Empty; ControlledCover = string.Empty;
            ControlledRank = 0; ControlledHealth = 0; ControlledMaxHealth = 0;
            HasFollow = false; FollowOn = false;
            QueueOwner = string.Empty; QueueHidden = 0; QueueUnit = null;
            CanClearOrders = false;
            CasterName = string.Empty; IsArmed = false; ArmedLine = string.Empty;
            Target = default;
        }
    }
}
