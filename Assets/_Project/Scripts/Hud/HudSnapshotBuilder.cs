using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// The only place the tactical HUD reads gameplay: each frame it fills a HudSnapshot from the optional HudSources. Every
    /// hostile fact (name, health, position, cover, target lines) passes Knowledge.CanTarget, and an unobserved hostile can
    /// only appear as its last-known point (IntelligenceService.StateOfEnemy / TryLastKnown). The developer truth view
    /// widens Knowledge.IsShown, so the HUD never uses it. Subscribes to nothing; the strings it can reuse (prompts, names,
    /// queue steps, the aiming line) are cached so a steady frame does no string work.
    /// The small caches `hovered`, `stepCommands` and `armedTarget` keep per-mission references between frames only to
    /// compare them by reference: every Build re-validates them, and nothing is ever read from them for display without
    /// passing the CanTarget guard first (a lost hostile in the hover cache or a stale aim drops out on the next Build).
    /// </summary>
    public sealed class HudSnapshotBuilder
    {
        const int RealTimeSteps = 3;
        const int PausedSteps = 8;
        const float HoverInterval = 0.1f;
        const float HostileMarkHeight = 2.3f;
        const int NameCacheLimit = 256;
        const string NoPrompt = "-";
        const string PauseActionPath = "Commands/ToggleTacticalPause";
        const string AttackingTag = "ATTACKING";
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly string[] CoverPercentTexts = new string[101];

        // The knowledge filter. `intelligence` is set for the duration of one Build only.
        IntelligenceService intelligence;
        readonly Func<Health, bool> canTarget;

        // Prompt list and resume prompt, rebuilt only when (family, paused, armed, terminal, controls) changes.
        // It does not notice a live rebind (there is no rebind UI yet); add a binding version to the key when there is.
        readonly List<HudPromptEntry> cachedPrompts = new List<HudPromptEntry>();
        bool hasPromptKey;
        InputFamily promptFamily;
        bool promptPaused;
        bool promptArmed;
        string promptTerminal;
        InputActionAsset promptControls;
        string cachedResume = NoPrompt;

        // Ability slot prompts, rebuilt when the family or the controls change.
        string[] abilityPrompts = new string[4];
        bool hasAbilityPrompts;
        InputFamily abilityFamily;
        InputActionAsset abilityControls;

        // The hostile under the mouse, resolved at most every HoverInterval of unscaled time.
        Health hovered;
        bool hasHover;
        float hoverAt;

        // Clean object names by instance id, and initials by name.
        readonly Dictionary<int, string> names = new Dictionary<int, string>();
        readonly Dictionary<string, string> initials = new Dictionary<string, string>();

        // Queue step texts per shown position, reused while the command and its wording inputs are unchanged.
        readonly UnitCommand[] stepCommands = new UnitCommand[PausedSteps];
        readonly int[] stepVariants = new int[PausedSteps];
        readonly string[] stepTexts = new string[PausedSteps];

        // The aiming line, reused while what it says is unchanged.
        string armedLine = string.Empty;
        AbilityDefinition armedAbility;
        Health armedTarget;
        bool armedHasAim;
        int armedTenths;
        int armedStatus = -1;

        public HudSnapshotBuilder() => canTarget = unit => Knowledge.CanTarget(intelligence, unit);

        /// <summary>
        /// Optional: true when the screen point is over a HUD panel. While it is, the mouse hover is dropped (the HUD, not
        /// the world, is under the pointer).
        /// </summary>
        public Func<Vector2, bool> PointerOverHud { get; set; }

        /// <summary>How many times the aiming line was rebuilt (tests).</summary>
        internal int ArmedLineBuilds { get; private set; }

        /// <summary>How many times the prompt list was rebuilt (tests).</summary>
        internal int PromptRebuilds { get; private set; }

        /// <summary>Replaces the contents of `into` with what the HUD shows now. A null source leaves its part empty.</summary>
        public void Build(HudSources sources, HudSnapshot into, float unscaledTime)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            into.Clear();
            if (sources == null)
                return;

            intelligence = sources.intelligence;
            try
            {
                var family = sources.inputDevice != null ? sources.inputDevice.Family : InputFamily.KeyboardMouse;
                var active = sources.activeCharacter;
                var controlled = active != null && active.HasUnit ? active.Unit : null;
                var runtime = sources.director != null ? sources.director.Runtime : null;

                into.IsPaused = sources.tacticalPause != null && sources.tacticalPause.IsPaused;
                into.HasPause = sources.tacticalPause != null;
                into.HasFollow = active != null;
                into.FollowOn = active != null && active.IsFollowOn;

                BuildMission(runtime, into);
                BuildSquad(sources, controlled, into);
                BuildControlled(sources.roster, controlled, into);
                BuildAbilities(sources, family, into);
                BuildQueue(sources, controlled, into);
                BuildPrompts(sources, family, into);
                BuildTarget(sources, family, controlled, unscaledTime, into);
                BuildMarks(sources.encounter, runtime, into);
            }
            finally
            {
                intelligence = null;
            }
        }

        // ---- mission ----

        void BuildMission(MissionRuntime runtime, HudSnapshot into)
        {
            if (runtime == null)
                return;
            into.HasMission = true;
            into.PhaseText = MissionHudText.PhaseLabel(runtime.Phase);
            into.BannerText = MissionHudText.Banner(runtime.Phase);

            var extraction = runtime.Extraction;
            var listUnknown = intelligence != null && intelligence.ListsUnknownObjectives;
            var objectives = runtime.Objectives;
            for (var i = 0; i < objectives.Count; i++)
            {
                var objective = objectives[i];
                if (objective == null || objective == extraction)
                    continue;
                if (HudText.TryObjectiveRow(objective, listUnknown, out var kind, out var text))
                    into.Objectives.Add(new HudObjectiveRow { Kind = kind, Text = text });
            }

            // Who stands in the zone is counted only once the zone is known: the count would locate an unknown one.
            var inside = 0;
            if (extraction is ReachZoneObjective zone)
            {
                into.ExtractionRequired = zone.RequiredUnits;
                if (zone.IsKnown)
                    inside = zone.Inside;
            }
            into.Extraction = HudText.ExtractionOf(extraction, runtime.Phase, inside);
            into.ExtractionInside = inside;
        }

        // ---- squad and controlled operative ----

        void BuildSquad(HudSources sources, CommandableUnit controlled, HudSnapshot into)
        {
            var director = sources.director;
            if (director != null && director.Friendlies.Count > 0)
            {
                var friendlies = director.Friendlies;
                for (var i = 0; i < friendlies.Count; i++)
                    AddCard(sources, friendlies[i], controlled, into);
                return;
            }
            if (sources.selection == null)
                return;
            var roster = sources.selection.Roster;
            for (var i = 0; i < roster.Count; i++)
            {
                if (roster[i] != null)
                    AddCard(sources, roster[i].Unit, controlled, into);
            }
        }

        void AddCard(HudSources sources, CommandableUnit unit, CommandableUnit controlled, HudSnapshot into)
        {
            if (unit == null)
                return;
            Identify(unit, sources.roster, out var name, out var role, out var rank);
            ReadHealth(unit, out var current, out var max, out var down);
            var isControlled = unit == controlled;
            var tag = string.Empty;
            if (!down && !isControlled && unit.TryGetComponent<CompanionAI>(out var companion))
            {
                var followOn = sources.activeCharacter != null && sources.activeCharacter.IsFollowOn;
                // Held at spawn or holding ordered cover: either way it is not following, by design.
                var cover = unit.Cover;
                var coverOrdered = cover != null && cover.Status == CoverStatus.Occupied && cover.OccupiedByOrder;
                tag = HudText.CompanionTag(companion.IsParked, companion.IsHeld || coverOrdered, followOn);
            }
            into.Squad.Add(new HudSquadCard
            {
                Unit = unit, Name = name, Role = role, Initials = InitialsOf(name), Tag = tag,
                Rank = rank, Health = current, MaxHealth = max,
                IsDown = down, IsControlled = isControlled, IsSelected = IsSelected(sources.selection, unit),
            });
        }

        void BuildControlled(SquadRoster roster, CommandableUnit controlled, HudSnapshot into)
        {
            if (controlled == null)
                return;
            into.HasControlled = true;
            Identify(controlled, roster, out var name, out var role, out var rank);
            ReadHealth(controlled, out var current, out var max, out _);
            into.ControlledName = name;
            into.ControlledRole = role;
            into.ControlledRank = rank;
            into.ControlledHealth = current;
            into.ControlledMaxHealth = max;

            var cover = controlled.Cover;
            var point = cover != null ? cover.Point : null;
            if (point != null && point.IsValid)
                into.ControlledCover = HudText.Cover(cover.Status, point.Height, point.Placement);
            else
                into.ControlledCover = HudText.Cover(cover != null && cover.Status == CoverStatus.Reserved ? CoverStatus.Reserved : CoverStatus.None,
                    CoverHeight.Low, CoverPlacement.Face);
        }

        // Name, role and rank: the operative's when the unit is bound to one, else the object name and the combat role.
        void Identify(CommandableUnit unit, SquadRoster roster, out string name, out string role, out int rank)
        {
            name = null;
            role = null;
            rank = 0;
            if (unit.TryGetComponent<UnitIdentity>(out var identity) && !string.IsNullOrEmpty(identity.OperativeId))
            {
                name = identity.DisplayName;
                role = identity.RoleName;
                if (roster != null && roster.Track != null)
                {
                    var member = roster.Find(identity.OperativeId);
                    if (member != null)
                        rank = roster.Rank(member);
                }
            }
            if (string.IsNullOrEmpty(name))
                name = NameOf(unit);
            if (string.IsNullOrEmpty(role))
                role = unit.TryGetComponent<UnitAttacker>(out var attacker) ? RoleName(attacker.Role) : string.Empty;
        }

        static void ReadHealth(CommandableUnit unit, out int current, out int max, out bool down)
        {
            if (unit.TryGetComponent<Health>(out var health))
            {
                current = health.Current;
                max = health.Max;
                down = !health.IsAlive;
                return;
            }
            current = 0;
            max = 0;
            down = !unit.IsAlive;
        }

        static bool IsSelected(UnitSelection selection, CommandableUnit unit)
        {
            if (selection == null || !unit.TryGetComponent<SelectableUnit>(out var selectable))
                return false;
            var selected = selection.Selected;
            for (var i = 0; i < selected.Count; i++)
            {
                if (selected[i] == selectable)
                    return true;
            }
            return false;
        }

        // ---- abilities ----

        void BuildAbilities(HudSources sources, InputFamily family, HudSnapshot into)
        {
            var targeting = sources.abilityTargeting;
            if (targeting == null)
                return;
            var caster = targeting.Caster;
            var abilities = targeting.CasterAbilities;
            if (caster == null || abilities == null)
                return;

            Identify(caster, sources.roster, out var casterName, out _, out _);
            into.CasterName = casterName;
            RefreshAbilityPrompts(family, sources.controls, abilities.Count);
            var alive = abilities.IsAlive;
            for (var i = 0; i < abilities.Count; i++)
            {
                var definition = abilities.Definition(i);
                if (definition == null)
                    continue;
                var remaining = abilities.CooldownRemaining(i);
                var total = abilities.EffectiveCooldown(definition);
                var state = !alive ? HudAbilityState.Unavailable
                    : targeting.ArmedSlot == i ? HudAbilityState.Armed
                    : remaining > 0f ? HudAbilityState.Cooldown
                    : HudAbilityState.Ready;
                into.Abilities.Add(new HudAbilitySlot
                {
                    Slot = i, Prompt = abilityPrompts[i], Name = definition.DisplayName, State = state,
                    Remaining = remaining, Fraction = total > 0f ? Mathf.Clamp01(remaining / total) : 0f,
                });
            }

            if (targeting.IsArmed && targeting.ArmedAbility != null)
            {
                into.IsArmed = true;
                into.ArmedLine = ArmedLine(targeting.ArmedAbility, targeting.Preview);
            }
        }

        void RefreshAbilityPrompts(InputFamily family, InputActionAsset controls, int count)
        {
            var stale = !hasAbilityPrompts || abilityFamily != family || abilityControls != controls;
            if (count > abilityPrompts.Length)
            {
                Array.Resize(ref abilityPrompts, count);
                stale = true;
            }
            if (!stale)
                return;
            for (var i = 0; i < abilityPrompts.Length; i++)
                abilityPrompts[i] = HudPrompts.AbilityPrompt(i, family, controls);
            hasAbilityPrompts = true;
            abilityFamily = family;
            abilityControls = controls;
        }

        // "AIMING: <ability>", then, with an aim: the target's name (observed units only), distance/range and the verdict.
        // An aim on a unit the player has not observed is treated as no aim, so its distance is never shown. The distance is
        // keyed and written by tenths, so mouse movement that does not change the shown number reuses the cached line.
        string ArmedLine(AbilityDefinition ability, AbilityPreview preview)
        {
            var target = preview.Target;
            var hasAim = preview.HasAim && (target == null || canTarget(target));
            if (!hasAim)
                target = null;
            var tenths = hasAim ? Mathf.RoundToInt(preview.Check.Distance * 10f) : 0;
            var status = !hasAim ? 0 : preview.IsValid ? 1 : preview.WillApproach ? 2 : 3 + (int)preview.Failure;
            if (ability == armedAbility && target == armedTarget && hasAim == armedHasAim && tenths == armedTenths && status == armedStatus)
                return armedLine;

            var text = "AIMING: " + ability.DisplayName;
            if (hasAim)
            {
                if (target != null)
                    text += " | " + NameOf(target);
                var verdict = status == 1 ? "OK" : status == 2 ? "Moving into range" : preview.Failure.Describe();
                text += " | " + (tenths / 10f).ToString("0.0", Invariant) + "/" + ability.Range.ToString("0.0", Invariant) + " m | " + verdict;
            }
            armedAbility = ability;
            armedTarget = target;
            armedHasAim = hasAim;
            armedTenths = tenths;
            ArmedLineBuilds++;
            armedStatus = status;
            armedLine = text;
            return text;
        }

        // ---- command queue ----

        void BuildQueue(HudSources sources, CommandableUnit controlled, HudSnapshot into)
        {
            var paused = into.IsPaused;
            var subject = paused ? FirstSelectedAlive(sources.selection) : null;
            if (subject == null)
                subject = controlled;
            if (subject == null)
                return;

            Identify(subject, sources.roster, out var owner, out _, out _);
            into.QueueOwner = owner;
            into.QueueUnit = subject;
            var current = subject.CurrentCommand;
            var pending = subject.PendingCommands;
            var total = (current != null ? 1 : 0) + pending.Count;
            into.CanClearOrders = total > 0;

            var limit = paused ? PausedSteps : RealTimeSteps;
            var shown = 0;
            if (current != null)
            {
                var approaching = current is AbilityCommand && subject.AbilityPhase != AttackPhase.None;
                into.Queue.Add(new HudCommandStep { Number = 1, Text = StepText(0, current, approaching), IsCurrent = true });
                shown = 1;
            }
            for (var i = 0; i < pending.Count && shown < limit; i++)
            {
                into.Queue.Add(new HudCommandStep { Number = shown + 1, Text = StepText(shown, pending[i], false), IsCurrent = false });
                shown++;
            }
            into.QueueHidden = total - shown;
            for (var i = shown; i < stepCommands.Length; i++)
            {
                stepCommands[i] = null;
                stepTexts[i] = null;
            }
        }

        static CommandableUnit FirstSelectedAlive(UnitSelection selection)
        {
            if (selection == null)
                return null;
            var selected = selection.Selected;
            for (var i = 0; i < selected.Count; i++)
            {
                var unit = selected[i] != null ? selected[i].Unit : null;
                if (unit != null && unit.IsAlive)
                    return unit;
            }
            return null;
        }

        string StepText(int index, UnitCommand command, bool approaching)
        {
            // What the wording depends on besides the command itself: whether an attack target may be named, and the approach.
            var named = command is AttackCommand attack && attack.Target != null && canTarget(attack.Target);
            var variant = (named ? 1 : 0) | (approaching ? 2 : 0);
            if (stepCommands[index] == command && stepVariants[index] == variant && stepTexts[index] != null)
                return stepTexts[index];
            var text = HudText.Step(command, canTarget);
            if (approaching)
                text += " (moving into range)";
            stepCommands[index] = command;
            stepVariants[index] = variant;
            stepTexts[index] = text;
            return text;
        }

        // ---- prompts ----

        void BuildPrompts(HudSources sources, InputFamily family, HudSnapshot into)
        {
            string terminal = null;
            if (sources.commandInput != null)
            {
                var interactable = sources.commandInput.PromptInteractable(family.IsController());
                if (interactable != null)
                    terminal = interactable.DisplayName;
            }
            var controls = sources.controls;
            if (!hasPromptKey || promptFamily != family || promptPaused != into.IsPaused || promptArmed != into.IsArmed
                || !string.Equals(promptTerminal, terminal, StringComparison.Ordinal) || promptControls != controls)
            {
                HudPrompts.Build(new HudPromptContext { Paused = into.IsPaused, AbilityArmed = into.IsArmed, TerminalName = terminal },
                    family, controls, cachedPrompts);
                cachedResume = controls == null ? NoPrompt : PromptResolver.GetPrompt(controls.FindAction(PauseActionPath), family);
                hasPromptKey = true;
                promptFamily = family;
                promptPaused = into.IsPaused;
                promptArmed = into.IsArmed;
                promptTerminal = terminal;
                promptControls = controls;
                PromptRebuilds++;
            }
            for (var i = 0; i < cachedPrompts.Count; i++)
                into.Prompts.Add(cachedPrompts[i]);
            into.ResumePrompt = cachedResume;
        }

        // ---- target ----

        void BuildTarget(HudSources sources, InputFamily family, CommandableUnit controlled, float unscaledTime, HudSnapshot into)
        {
            var encounter = sources.encounter;
            Health target = null;
            string tag = null;

            var targeting = sources.abilityTargeting;
            if (targeting != null && targeting.IsArmed && IsObservedHostile(targeting.Preview.Target, encounter))
            {
                target = targeting.Preview.Target;
                tag = "AIMING";
            }
            if (target == null)
            {
                var hover = Hover(sources, family, unscaledTime);
                if (IsObservedHostile(hover, encounter))
                {
                    target = hover;
                    tag = "HOVERED";
                }
            }
            if (target == null && sources.cursor != null)
            {
                var soft = sources.cursor.SoftTarget;
                if (IsObservedHostile(soft, encounter))
                {
                    target = soft;
                    tag = "TARGETED";
                }
            }
            if (target == null && controlled != null)
            {
                var attacking = controlled.AttackTarget;
                if (IsObservedHostile(attacking, encounter))
                {
                    target = attacking;
                    tag = AttackingTag;
                }
            }
            if (target == null)
                return;

            into.Target = new HudTarget
            {
                Visible = true, Unit = target, Name = NameOf(target), Detail = DetailOf(target), Tag = tag,
                Health = target.Current, MaxHealth = target.Max,
                // The target panel shows the cover line only while paused or for the attack target: evaluate it only then.
                CoverText = into.IsPaused || ReferenceEquals(tag, AttackingTag) ? CoverTextFor(controlled, target) : string.Empty,
            };
        }

        // The hostile the mouse is on (keyboard and mouse), or the controller cursor's hostile while the cursor is in use.
        Health Hover(HudSources sources, InputFamily family, float unscaledTime)
        {
            if (family == InputFamily.KeyboardMouse)
            {
                var input = sources.commandInput;
                if (input == null)
                {
                    hovered = null;
                    hasHover = false;
                    return null;
                }
                var screen = input.PointerScreenPosition;
                if (PointerOverHud != null && PointerOverHud(screen))
                {
                    hovered = null;
                    hasHover = true;
                    hoverAt = unscaledTime;
                    return null;
                }
                if (!hasHover || unscaledTime - hoverAt >= HoverInterval || unscaledTime < hoverAt)
                {
                    var pointed = input.ResolveAt(screen);
                    hovered = pointed.Kind == PointerTargetKind.Hostile ? pointed.Hostile : null;
                    hasHover = true;
                    hoverAt = unscaledTime;
                }
                return hovered;
            }

            hovered = null;
            hasHover = false;
            var cursor = sources.cursor;
            if (cursor != null && cursor.IsActive && cursor.Target.Kind == PointerTargetKind.Hostile)
                return cursor.Target.Hostile;
            return null;
        }

        // A living hostile the player observes. With an encounter, it must be one of its hostiles (an aimed heal targets a
        // friendly, which is not a target-panel subject).
        bool IsObservedHostile(Health unit, Encounter encounter)
        {
            if (unit == null || !unit.IsAlive || !canTarget(unit))
                return false;
            if (encounter == null)
                return !unit.TryGetComponent<SelectableUnit>(out _);
            var hostiles = encounter.Hostiles;
            for (var i = 0; i < hostiles.Count; i++)
            {
                if (hostiles[i] == unit)
                    return true;
            }
            return false;
        }

        static string DetailOf(Health target)
        {
            if (!target.TryGetComponent<UnitAttacker>(out var attacker))
                return string.Empty;
            return attacker.Archetype != null ? attacker.Archetype.DisplayName : RoleName(attacker.Role);
        }

        // Whether the controlled unit's shot at the target would be into cover; empty unless it fights at range.
        static string CoverTextFor(CommandableUnit controlled, Health target)
        {
            if (controlled == null || !controlled.TryGetComponent<UnitAttacker>(out var attacker) || !attacker.NeedsLineOfSight)
                return string.Empty;
            return attacker.IsTargetInCover(target, out var hitChance) ? CoverPercent(hitChance) : "Exposed";
        }

        static string CoverPercent(float hitChance)
        {
            var percent = Mathf.Clamp(Mathf.RoundToInt(hitChance * 100f), 0, 100);
            return CoverPercentTexts[percent] ??= "Cover " + percent.ToString(Invariant) + "%";
        }

        // ---- world marks ----

        void BuildMarks(Encounter encounter, MissionRuntime runtime, HudSnapshot into)
        {
            if (encounter != null)
            {
                var hostiles = encounter.Hostiles;
                for (var i = 0; i < hostiles.Count; i++)
                {
                    var hostile = hostiles[i];
                    if (hostile == null || !hostile.IsAlive)
                        continue;
                    if (canTarget(hostile))
                        into.Marks.Add(new HudWorldMark
                        {
                            Kind = HudMarkKind.Hostile, World = hostile.transform.position + Vector3.up * HostileMarkHeight, Text = string.Empty,
                        });
                    else if (intelligence != null && intelligence.StateOfEnemy(hostile) == KnowledgeState.Discovered
                             && intelligence.TryLastKnown(hostile, out var lastKnown))
                        into.Marks.Add(new HudWorldMark { Kind = HudMarkKind.LastKnown, World = lastKnown, Text = "LAST KNOWN" });
                }
            }

            if (runtime == null)
                return;
            var objectives = runtime.Objectives;
            for (var i = 0; i < objectives.Count; i++)
            {
                var objective = objectives[i];
                if (objective == null || !objective.IsKnown || !objective.HasTarget || objective.State == ObjectiveState.Completed)
                    continue;
                var label = MissionHudText.MarkerLabel(objective);
                if (string.IsNullOrEmpty(label))
                    continue;
                into.Marks.Add(new HudWorldMark
                {
                    Kind = objective == runtime.Extraction ? HudMarkKind.Extraction : HudMarkKind.Objective,
                    World = objective.TargetPosition, Text = label,
                });
            }
        }

        // ---- names ----

        // The object's name without "(Clone)", read once per object: Object.name allocates a new string on every read.
        string NameOf(UnityEngine.Object item)
        {
            var id = item.GetInstanceID();
            if (names.TryGetValue(id, out var name))
                return name;
            if (names.Count >= NameCacheLimit)
                names.Clear();
            name = HudText.CleanName(item.name);
            names[id] = name;
            return name;
        }

        string InitialsOf(string name)
        {
            if (string.IsNullOrEmpty(name))
                return HudText.Initials(name);
            if (initials.TryGetValue(name, out var text))
                return text;
            if (initials.Count >= NameCacheLimit)
                initials.Clear();
            text = HudText.Initials(name);
            initials[name] = text;
            return text;
        }

        static string RoleName(CombatRole role)
        {
            switch (role)
            {
                case CombatRole.Melee: return nameof(CombatRole.Melee);
                case CombatRole.Ranged: return nameof(CombatRole.Ranged);
                default: return role.ToString();
            }
        }
    }
}
