# Tactical HUD (Phase 11.5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A player-facing uGUI tactical HUD (squad, controlled operative, abilities, cooldowns, command queue, objectives, extraction, pause, follow, target info, world marks, contextual prompts) that reads existing gameplay state through a snapshot layer and never leaks fogged information, plus a hidden-by-default developer overlay for the existing IMGUI debug views.

**Architecture:** `HudSnapshotBuilder` reads gameplay (through `HudSources`) into reused snapshot structs each frame; `TacticalHud` and its panels apply the snapshot to a code-built uGUI hierarchy, touching a `Text`/`Image` only when a value changed. Clicks go through `HudRequests`, which call existing semantic APIs. Two tiny neutral seams (`PointerBlocker`, `DeveloperOverlay`) and `ActiveCharacter.TakeControl` are the only changes in gameplay assemblies' surface.

**Tech Stack:** Unity 6000.3.25f1, URP, uGUI 2.0.0 (legacy `Text`), Input System 1.20.0, NUnit EditMode + PlayMode tests.

**Spec:** `Docs/superpowers/specs/2026-10-09-tactical-hud-design.md` (read it first; it is the binding authority).

## Global Constraints

- Work on branch `tactical-hud`. Never touch `Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset` or `.claude/`: stage files explicitly, never `git add -A`.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. LF-to-CRLF warnings are harmless.
- Runtime code is in the `Blackglass` assembly (`Assets/_Project/Scripts`), new HUD code in `Scripts/Hud/`. Tests are `Blackglass.Tests.EditMode` / `Blackglass.Tests.PlayMode`, namespace `Blackglass.Tests`. Components expose `internal Initialize(...)` for tests (assembly internals are visible to the test assemblies).
- `NoDeviceTypesInGameplayTests` forbids `Mouse.current`, `Keyboard.current`, `Pointer.current`, `Gamepad.current`, face-button property names and device class names anywhere outside `Scripts/Input/`. HUD code reads pointer position and modifier state through `InputActionReference`s (`InputActionUtility.Read<Vector2>` / `IsPressed`) or through methods added in `Scripts/Input` / `Scripts/Controls`. It never hard-codes "Press A", "Left Click" or any key name: all prompt text comes from `PromptResolver`.
- Hostile data reaches the HUD only through `Knowledge.CanTarget(intelligence, health)` (observed only) or `IntelligenceService.StateOfEnemy` / `TryLastKnown`. Never `Knowledge.IsShown` / `IsUnitShown` (the F12 truth view widens them). Never read `transform.position`, `name`, `Current` or `Max` of a hostile that fails `CanTarget` (only the last-known point is allowed).
- The HUD subscribes to no gameplay events and caches no per-mission reference between frames except the snapshot it rebuilds every frame.
- Simulation time rules (007/013): the HUD runs on unscaled time (`Time.unscaledTime`/`LateUpdate`) and works while `Time.timeScale == 0`.
- Do not modify the behaviour of existing gameplay, only add the seams listed. Existing debug IMGUI views only gain an early-return gate (Task 10); their text-building statics and tests stay untouched.
- No new third-party package. No TMP. No binary assets or prefabs for the HUD.
- Unity batch runs need the Editor closed (one instance per project): `Tools/run-tests.sh EditMode|PlayMode "<filter>"`. Tasks run serially. Baselines on the branch start: EditMode 992, PlayMode 849 (all green).
- Subagents cannot write report files with the Write tool: return the full report as the final message.
- Code blocks in this plan fix names, signatures and behaviour. If the compiler disagrees with a signature that depends on existing code, match the real signature and keep the name and behaviour, and say so in the report.

## Review Focus

- A hostile that is Unknown/Discovered must not appear by name, health, position, count or target line in any HUD text or mark (also after being lost, and with F12 truth view on).
- A click, drag or box-select that begins over a HUD panel must not issue a world command or change the selection; a click elsewhere still must.
- Cooldown numbers and fill must stay frozen while `Time.timeScale == 0` and not jump on resume.
- Regenerating the mission (F6/F7) or a squad member dying must not leave a stale card, label, queue or target line, nor throw.
- Prompts must change when the input family changes (Xbox → PlayStation → Nintendo → keyboard) without a restart, and Nintendo Confirm/Cancel must follow the bindings.
- Layout must stay inside the screen and not overlap at 16:9, 16:10 and 21:9 canvas sizes.
- Pointer-only EventSystem: pressing a pad stick or button must never move UI focus or click a HUD element.

---

### Task 1: Seams and assembly reference

**Files:**
- Modify: `Assets/_Project/Scripts/Blackglass.asmdef` (add `"Unity.ugui"` to references)
- Create: `Assets/_Project/Scripts/Input/PointerBlocker.cs`
- Modify: `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`
- Create: `Assets/_Project/Scripts/DebugUI/DeveloperOverlay.cs`, `Assets/_Project/Scripts/DebugUI/DeveloperOverlayInput.cs`
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions` (action `Developer/ToggleDebugOverlay`, F1, group `KeyboardMouse`, new GUIDs)
- Modify: `Assets/_Project/Scripts/Controls/ActiveCharacter.cs` (`TakeControl`)
- Test: `Assets/_Project/Tests/EditMode/DeveloperOverlayTests.cs`, `Assets/_Project/Tests/EditMode/ActiveCharacterTakeControlTests.cs`, `Assets/_Project/Tests/EditMode/HudInputAssetTests.cs`, `Assets/_Project/Tests/PlayMode/PointerBlockerPlayModeTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class PointerBlocker : MonoBehaviour { public bool IsBlocking(Vector2 screen); internal void SetTest(System.Func<Vector2,bool> test); }`
  - `PlayerCommandInput`: `[SerializeField] PointerBlocker pointerBlocker;`, `internal void SetPointerBlocker(PointerBlocker b)`, `public Vector2 PointerScreenPosition`, `public PointerTarget ResolveAt(Vector2 screen)` (same resolver call, fields and intelligence the mouse click uses; no side effects), `public bool ModifierIsHeld` (the queue modifier).
  - `public sealed class DeveloperOverlay : MonoBehaviour { public bool IsVisible; public void Toggle(); public void SetVisible(bool); public static bool Shows(DeveloperOverlay overlay) => overlay == null || overlay.IsVisible; }` (visible defaults to `false` when the component exists).
  - `DeveloperOverlayInput` (pattern of `MissionDeveloperInput`): `internal void Initialize(DeveloperOverlay overlay, InputActionReference toggle)`.
  - `public bool ActiveCharacter.TakeControl(CommandableUnit unit)`.

- [ ] **Step 1: Write the failing tests**

`DeveloperOverlayTests.cs` (EditMode):

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class DeveloperOverlayTests
    {
        GameObject go;

        [TearDown] public void TearDown() { if (go != null) Object.DestroyImmediate(go); }

        [Test]
        public void ANullOverlay_MeansVisible_SoExistingScenesAreUnchanged() =>
            Assert.That(DeveloperOverlay.Shows(null), Is.True);

        [Test]
        public void ANewOverlay_IsHidden_AndToggleFlipsIt()
        {
            go = new GameObject("overlay");
            var overlay = go.AddComponent<DeveloperOverlay>();
            Assert.That(DeveloperOverlay.Shows(overlay), Is.False);
            overlay.Toggle();
            Assert.That(overlay.IsVisible, Is.True);
            overlay.SetVisible(false);
            Assert.That(DeveloperOverlay.Shows(overlay), Is.False);
        }
    }
}
```

`ActiveCharacterTakeControlTests.cs` (EditMode; follow the setup style of the existing `ActiveCharacterTests.cs` — read it first and reuse its unit factory): cases:
1. `TakeControl` of an eligible roster unit makes it `Unit`, returns true, and the selection becomes exactly that unit (mirror what Tab does: read `DirectControlInput` for where the selection is replaced after `Cycle`, and put the shared logic where both can call it; do not duplicate it).
2. `TakeControl` of a dead / inactive / non-roster unit returns false and changes nothing.
3. `TakeControl` of the unit already in control returns false.

`HudInputAssetTests.cs` (EditMode, pattern of `IntelligenceInputAssetTests`): `Developer/ToggleDebugOverlay` exists, has exactly one binding `<Keyboard>/f1` in group `KeyboardMouse`, and the asset-wide tests (every binding has a group; every non-Developer action has bindings in all four pad groups) still pass.

`PointerBlockerPlayModeTests.cs` (PlayMode; derive from `InputTestFixture`, reuse the rig and helper style of `PlayerCommandInputTests`): with a `PointerBlocker` whose test returns true for `x < 500`:
1. a left press+release at (300,300) over a ground point issues no order and does not clear/alter the selection;
2. a press at (300,300) dragged to (700,500) and released selects nothing (a gesture that began over the HUD is ignored entirely);
3. a press+release at (800,300) issues the normal order (the blocker lets it through);
4. with no blocker assigned, behaviour is unchanged (existing tests cover this; add one explicit case).
Also: `ResolveAt` returns the same `PointerTarget` kind as a click at that point (ground vs friendly).

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.DeveloperOverlayTests"` → compile error / FAIL (types missing). Then proceed.

- [ ] **Step 3: Implement**

`PointerBlocker.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Lets presentation say "this screen point is over my UI" without gameplay knowing about the UI. The HUD installs the
    /// test; input components ask before they treat a press as a world click. No test installed means nothing is blocked.
    /// </summary>
    public sealed class PointerBlocker : MonoBehaviour
    {
        Func<Vector2, bool> test;

        internal void SetTest(Func<Vector2, bool> overHud) => test = overHud;

        public bool IsBlocking(Vector2 screen) => test != null && test(screen);
    }
}
```

`PlayerCommandInput`: in `OnCommandPressed`, before `clickDetector.Press`, `if (pointerBlocker != null && pointerBlocker.IsBlocking(PointerPosition)) { pressBlocked = true; return; }`. In `OnCommandReleased`, first `if (pressBlocked) { pressBlocked = false; return; }`. Also reset `pressBlocked` in `OnDisable`. `Update()` already only tracks while `clickDetector.IsPressed`. Add `ResolveAt`, `PointerScreenPosition`, `ModifierIsHeld` by extracting the resolver call `HandleClick` uses into one private method both call. `ResolveAt` must not mutate selection or issue orders.

`DeveloperOverlay.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Whether the developer (debug) overlay is shown. Hidden by default; a null reference elsewhere means "shown".</summary>
    public sealed class DeveloperOverlay : MonoBehaviour
    {
        [SerializeField] bool visible;

        public bool IsVisible => visible;
        public void SetVisible(bool show) => visible = show;
        public void Toggle() => visible = !visible;
        public static bool Shows(DeveloperOverlay overlay) => overlay == null || overlay.IsVisible;
    }
}
```

`DeveloperOverlayInput.cs`: same shape as `MissionDeveloperInput`, one action, calls `overlay.Toggle()`.

`ActiveCharacter.TakeControl(CommandableUnit unit)`: `if (!IsEligible(unit) || unit == this.unit) return false; SetUnit(unit);` then the same selection step Tab performs. Add the `Developer/ToggleDebugOverlay` action and F1 binding to the inputactions JSON (new random GUIDs; follow the line format of `ToggleTruthView`). Add `"Unity.ugui"` to the asmdef references.

- [ ] **Step 4: Run tests**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.DeveloperOverlayTests"`, `... "Blackglass.Tests.ActiveCharacterTakeControlTests"`, `... "Blackglass.Tests.HudInputAssetTests"`, then `Tools/run-tests.sh PlayMode "Blackglass.Tests.PointerBlockerPlayModeTests"`. All pass. Then the full EditMode suite (992 + new) and `PlayerCommandInputTests`, `ControllerCommandInputTests`, `NoDeviceTypesInGameplayTests`: all green.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Blackglass.asmdef Assets/_Project/Scripts/Input/PointerBlocker.cs Assets/_Project/Scripts/Input/PointerBlocker.cs.meta Assets/_Project/Scripts/Controls/PlayerCommandInput.cs Assets/_Project/Scripts/Controls/ActiveCharacter.cs Assets/_Project/Scripts/DebugUI/DeveloperOverlay.cs Assets/_Project/Scripts/DebugUI/DeveloperOverlay.cs.meta Assets/_Project/Scripts/DebugUI/DeveloperOverlayInput.cs Assets/_Project/Scripts/DebugUI/DeveloperOverlayInput.cs.meta Assets/_Project/Input/BlackglassControls.inputactions Assets/_Project/Tests
git commit -m "HUD seams: pointer blocker, developer overlay flag, TakeControl, F1 action

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```
(Stage any other file this task touched, including every new `.meta`; never the Settings/ProjectSettings files.)

---

### Task 2: Snapshot types, text and prompt rules (pure)

**Files:**
- Create: `Assets/_Project/Scripts/Hud/HudSnapshot.cs`, `HudText.cs`, `HudPrompts.cs`
- Test: `Assets/_Project/Tests/EditMode/HudTextTests.cs`, `HudPromptsTests.cs`

**Interfaces:**
- Produces (used by Tasks 3–9):

```csharp
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

    public sealed class HudSnapshot
    {
        public readonly List<HudSquadCard> Squad = new List<HudSquadCard>();
        public readonly List<HudAbilitySlot> Abilities = new List<HudAbilitySlot>();
        public readonly List<HudCommandStep> Queue = new List<HudCommandStep>();
        public readonly List<HudObjectiveRow> Objectives = new List<HudObjectiveRow>();
        public readonly List<HudPromptEntry> Prompts = new List<HudPromptEntry>();
        public readonly List<HudWorldMark> Marks = new List<HudWorldMark>();
        public bool HasMission; public string PhaseText, BannerText;
        public HudExtractionState Extraction; public int ExtractionInside, ExtractionRequired;
        public bool IsPaused; public string ResumePrompt;
        public bool HasControlled; public string ControlledName, ControlledRole, ControlledCover; public int ControlledRank, ControlledHealth, ControlledMaxHealth;
        public bool HasFollow, FollowOn;
        public string QueueOwner; public int QueueHidden;          // QueueHidden = steps beyond the shown limit
        public CommandableUnit QueueUnit;                          // the queue subject, for the CLEAR request
        public bool CanClearOrders;
        public string CasterName; public bool IsArmed; public string ArmedLine;   // "" when not armed
        public HudTarget Target;
        public void Clear() { /* clear every list and reset every field */ }
    }
}
```

- `HudText` statics: `Health(int, int)`, `Seconds(float)`, `Initials(string)`, `CleanName(string)`, `Step(UnitCommand, System.Func<Health,bool> targetShown)`, `Cover(CoverStatus, CoverHeight, CoverPlacement)`, `CompanionTag(bool parked, bool held, bool followOn)`, `TryObjectiveRow(MissionObjective, bool listUnknown, out HudObjectiveKind, out string)`, `ExtractionOf(MissionObjective extraction, MissionPhase phase, int inside)`, `ExtractionLabel(HudExtractionState, int inside, int required)`, `Pause(string resumePrompt)`.
- `HudPrompts`: `public struct HudPromptContext { public bool Paused, AbilityArmed; public string TerminalName; }`, `public static void Build(HudPromptContext ctx, InputFamily family, InputActionAsset controls, List<HudPromptEntry> into)`, `public static string AbilityPrompt(int slot, InputFamily family, InputActionAsset controls)`.

- [ ] **Step 1: Write the failing tests**

`HudTextTests.cs` (create `Health` components on `new GameObject`; commands are constructed directly; read `MissionObjectivesTests` for how objectives are built in EditMode):

```csharp
[Test] public void Health_IsCurrentOverMax() => Assert.That(HudText.Health(42, 60), Is.EqualTo("42/60"));

[TestCase(0f, "0.0")] [TestCase(0.01f, "0.1")] [TestCase(4.21f, "4.3")] [TestCase(9.99f, "10")] [TestCase(24.2f, "25")]
public void Seconds_RoundsUp_AndDropsDecimalsFromTen(float value, string expected) =>
    Assert.That(HudText.Seconds(value), Is.EqualTo(expected));

[TestCase("Darius", "D")] [TestCase("Kestrel Vale", "KV")] [TestCase("", "?")] [TestCase("  sable ", "S")]
public void Initials(string name, string expected) => Assert.That(HudText.Initials(name), Is.EqualTo(expected));

[TestCase("HostileUnit_3(Clone)", "HostileUnit_3")] [TestCase("Kestrel", "Kestrel")]
public void CleanName_DropsTheCloneSuffix(string raw, string expected) => Assert.That(HudText.CleanName(raw), Is.EqualTo(expected));

[Test] public void Step_NamesTheCommand() { /* Move->"Move", MoveToCover->"Take cover", Interact->"Interact <DisplayName>", Ability->its DisplayName */ }
[Test] public void Step_AttackNamesTheTarget_OnlyWhileItIsShown()
{
    // shown: "Attack HostileUnit_1"; not shown: "Attack (target lost)"; null predicate: shown
}
[TestCase(CoverStatus.None, "Exposed")] [TestCase(CoverStatus.Reserved, "Moving to cover")]
public void Cover_WithoutOccupancy(CoverStatus status, string expected) => Assert.That(HudText.Cover(status, CoverHeight.Low, CoverPlacement.Face), Is.EqualTo(expected));
[Test] public void Cover_Occupied_NamesLowCornerAndTall() { /* Low face -> "Low cover", any Corner placement -> "Corner cover", Tall face/column -> "Tall cover" */ }
[TestCase(true, false, true, "PARKED")] [TestCase(false, true, true, "HOLDING")] [TestCase(false, false, true, "FOLLOWING")] [TestCase(false, false, false, "ATTACHED")]
public void CompanionTag(bool parked, bool held, bool followOn, string expected) => Assert.That(HudText.CompanionTag(parked, held, followOn), Is.EqualTo(expected));
```

Objective rows (these pin the fog rules):
- known active objective → `(Active, Describe())`; known completed → `(Completed, Title)`; failed → `Failed`; known but `Inactive` → `Locked`.
- unknown, `listUnknown: true`, vague title set → `(Unknown, VagueTitle)`; the row text must not contain the real `Title`.
- unknown, `listUnknown: false` → returns false (omitted); unknown with no vague title → false.
- an `EliminateHostilesObjective` with `ShowCounts = false` and 2 of 5 down → text `"<Title> (2 down)"` and never `"/5"`.

Extraction mapping (`ExtractionOf`): `Hidden` for null; `Unknown` when `!IsKnown`; `Locked` for Inactive; `Available` for Active with `inside == 0`; `Active` for Active with `inside > 0`; `Extracted` when completed or `phase == Success`. `ExtractionLabel`: `"EXTRACTION UNKNOWN"`, `"EXTRACTION LOCKED"`, `"EXTRACTION AVAILABLE"`, `"EXTRACTION ACTIVE 1/2 IN ZONE"`, `"EXTRACTED"`, `""` for Hidden.

`Pause("Space")` → `"TACTICAL PAUSE - Space to resume"`.

`HudPromptsTests.cs`: load `BlackglassControls.inputactions` (see `PromptResolverTests`/`ControllerHudTests` for the loader). Cases per family:
1. Real time, no terminal, KeyboardMouse: entries include labels `Order`, `Pause`, `Switch`, `Follow`, `Stop`; none is `Interact`; every `Prompt` is non-empty and not `"-"`; the `Order` prompt equals `PromptResolver.GetPrompt(Commands/Command, KeyboardMouse)`.
2. Real time with `TerminalName = "Terminal"`: first entry label `Interact Terminal`; keyboard prompt is the `Commands/Interact` binding, every pad family's is the `Commands/Confirm` binding.
3. Paused: labels `Resume`, `Order`, `Queue`, `Cancel`, `Switch`; ability armed: `Cast`, `Cancel`, `Queue` (armed wins over paused).
4. Xbox vs PlayStation vs Nintendo: the `Cancel` prompt for Nintendo differs from Xbox (the south/east swap, via the bindings), and equals `PromptResolver.GetPrompt(Commands/Cancel, family)` for each family; the list never has more than 6 entries; same context with a different family yields different prompt text for pad families and keyboard.
5. `AbilityPrompt(0, Xbox, controls)` equals the resolver text for `Commands/Ability1` (a chord such as `RT + D-pad Up`); `AbilityPrompt(1, KeyboardMouse, controls)` is `"2"`.
6. Null `controls` returns no entries and does not throw.

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh EditMode "Blackglass.Tests.HudTextTests"` fails to compile first.

- [ ] **Step 3: Implement**

`HudText.cs` core (invariant culture everywhere):

```csharp
using System;
using System.Globalization;
using UnityEngine;

namespace Blackglass
{
    public static class HudText
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static string Health(int current, int max) => current.ToString(Invariant) + "/" + max.ToString(Invariant);

        public static string Seconds(float seconds)
        {
            if (seconds >= 9.95f) return Mathf.CeilToInt(seconds).ToString(Invariant);
            return (Mathf.Ceil(Mathf.Max(0f, seconds) * 10f) / 10f).ToString("0.0", Invariant);
        }

        public static string CleanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            const string clone = "(Clone)";
            return raw.EndsWith(clone, StringComparison.Ordinal) ? raw.Substring(0, raw.Length - clone.Length).Trim() : raw.Trim();
        }

        public static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var text = parts.Length == 1 ? parts[0].Substring(0, 1) : parts[0].Substring(0, 1) + parts[1].Substring(0, 1);
            return text.ToUpperInvariant();
        }

        public static string Step(UnitCommand command, Func<Health, bool> targetShown)
        {
            switch (command)
            {
                case MoveCommand _: return "Move";
                case MoveToCoverCommand _: return "Take cover";
                case AttackCommand attack:
                    return attack.Target != null && (targetShown == null || targetShown(attack.Target))
                        ? "Attack " + CleanName(attack.Target.name)
                        : "Attack (target lost)";
                case InteractCommand interact:
                    return interact.Target != null ? "Interact " + interact.Target.DisplayName : "Interact";
                case AbilityCommand ability:
                    return ability.Definition != null ? ability.Definition.DisplayName : "Ability";
                case StopCommand _: return "Stop";
                default: return command == null ? string.Empty : command.GetType().Name;
            }
        }

        public static string Cover(CoverStatus status, CoverHeight height, CoverPlacement placement)
        {
            if (status == CoverStatus.Reserved) return "Moving to cover";
            if (status != CoverStatus.Occupied) return "Exposed";
            if (placement == CoverPlacement.Corner) return "Corner cover";
            return height == CoverHeight.Low ? "Low cover" : "Tall cover";
        }

        public static string CompanionTag(bool parked, bool held, bool followOn) =>
            held ? "HOLDING" : parked ? "PARKED" : followOn ? "FOLLOWING" : "ATTACHED";

        public static bool TryObjectiveRow(MissionObjective objective, bool listUnknown, out HudObjectiveKind kind, out string text)
        {
            kind = HudObjectiveKind.Active; text = string.Empty;
            if (objective == null) return false;
            if (!objective.IsKnown)
            {
                if (!listUnknown || string.IsNullOrEmpty(objective.VagueTitle)) return false;
                kind = HudObjectiveKind.Unknown; text = objective.VagueTitle; return true;
            }
            text = objective.Describe();
            switch (objective.State)
            {
                case ObjectiveState.Completed: kind = HudObjectiveKind.Completed; break;
                case ObjectiveState.Failed: kind = HudObjectiveKind.Failed; break;
                case ObjectiveState.Inactive: kind = HudObjectiveKind.Locked; break;
                default: kind = HudObjectiveKind.Active; break;
            }
            return true;
        }

        public static HudExtractionState ExtractionOf(MissionObjective extraction, MissionPhase phase, int inside)
        {
            if (extraction == null) return HudExtractionState.Hidden;
            if (phase == MissionPhase.Success || extraction.State == ObjectiveState.Completed) return HudExtractionState.Extracted;
            if (!extraction.IsKnown) return HudExtractionState.Unknown;
            if (extraction.State == ObjectiveState.Inactive) return HudExtractionState.Locked;
            if (extraction.State == ObjectiveState.Active) return inside > 0 ? HudExtractionState.Active : HudExtractionState.Available;
            return HudExtractionState.Hidden;
        }

        public static string ExtractionLabel(HudExtractionState state, int inside, int required)
        {
            switch (state)
            {
                case HudExtractionState.Unknown: return "EXTRACTION UNKNOWN";
                case HudExtractionState.Locked: return "EXTRACTION LOCKED";
                case HudExtractionState.Available: return "EXTRACTION AVAILABLE";
                case HudExtractionState.Active: return "EXTRACTION ACTIVE " + inside.ToString(Invariant) + "/" + required.ToString(Invariant) + " IN ZONE";
                case HudExtractionState.Extracted: return "EXTRACTED";
                default: return string.Empty;
            }
        }

        public static string Pause(string resumePrompt) => "TACTICAL PAUSE - " + resumePrompt + " to resume";
    }
}
```

`HudPrompts.cs`: a private `Spec { string labelKbm, labelPad; string[] kbmPaths; string[] padPaths; }`, with `Resolve(spec, family, controls)` joining `PromptResolver.GetPrompt(controls.FindAction(path), family)` of each path with `" / "`, dropping `"-"` results; entries with no resolved prompt are skipped. Lists (paths are action paths in the asset):
- Interact (only with a terminal): label `"Interact " + terminal`; kbm `Commands/Interact`; pad `Commands/Confirm`.
- Real time: `Order`/`Attack` (kbm `Commands/Command`; pad `Commands/Attack`), `Pause` (`Commands/ToggleTacticalPause`), `Switch` (kbm `Character/NextCharacter`; pad `Character/PreviousCharacter` + `Character/NextCharacter`), `Follow` (`Character/ToggleFollow`), `Stop` (`Commands/Stop`).
- Paused: `Resume` (`Commands/ToggleTacticalPause`), `Order` (kbm `Commands/Command`; pad `Commands/Confirm`), `Queue` (`Commands/QueueModifier`), `Cancel` (`Commands/Cancel`), `Switch` (as above).
- Armed (wins over paused): `Cast` (kbm `Commands/Command`; pad `Commands/Confirm`), `Cancel`, `Queue`.
- Cap at 6 entries; the Interact entry is first when present.
`AbilityPrompt`: `Commands/Ability{slot+1}`; if the resolver returns `"-"`, fall back to `(slot + 1).ToString()`.

- [ ] **Step 4: Run tests** — both new classes green; full EditMode suite still green (992 + Task 1 + new).

- [ ] **Step 5: Commit** — `git add Assets/_Project/Scripts/Hud Assets/_Project/Tests/EditMode/HudTextTests.cs* Assets/_Project/Tests/EditMode/HudPromptsTests.cs*` (include `.meta`), message `HUD: snapshot types, wording and prompt rules`.

---

### Task 3: Snapshot builder with the intelligence rules

**Files:**
- Create: `Assets/_Project/Scripts/Hud/HudSources.cs`, `HudSnapshotBuilder.cs`
- Test: `Assets/_Project/Tests/PlayMode/HudSnapshotBuilderTests.cs`

**Interfaces:**
- Consumes: Task 2 types; existing `ActiveCharacter`, `UnitSelection`, `TacticalPause`, `Encounter`, `MissionDirector`, `SquadRoster`, `IntelligenceService`, `AbilityTargeting`, `TacticalCursor`, `PlayerCommandInput` (including Task 1's `ResolveAt`, `PointerScreenPosition`), `ActiveInputDevice`, `UnitAbilities`, `CompanionAI`, `UnitCover`, `UnitAttacker`, `UnitIdentity`.
- Produces:

```csharp
[Serializable]
public sealed class HudSources
{
    public ActiveCharacter activeCharacter; public UnitSelection selection; public TacticalPause tacticalPause;
    public Encounter encounter; public MissionDirector director; public SquadRoster roster;
    public IntelligenceService intelligence; public AbilityTargeting abilityTargeting; public TacticalCursor cursor;
    public PlayerCommandInput commandInput; public ActiveInputDevice inputDevice; public InputActionAsset controls;
    public Camera camera;
}

public sealed class HudSnapshotBuilder
{
    public void Build(HudSources sources, HudSnapshot into, float unscaledTime);
}
```

Behaviour of `Build` (everything optional; a null source yields the empty value for that part; `into.Clear()` first):

1. **Status:** `IsPaused`, `ResumePrompt` (`PromptResolver.GetPrompt` on `Commands/ToggleTacticalPause` for the active family; `"-"` when `controls` is null; tests assert the resolver value when controls exist), `HasFollow = activeCharacter != null`, `FollowOn`.
2. **Mission:** `HasMission = director?.Runtime != null`; `PhaseText = MissionHudText.PhaseLabel(phase)`; `BannerText = MissionHudText.Banner(phase)`; objective rows from every `runtime.Objectives` entry except `runtime.Extraction`, through `HudText.TryObjectiveRow(o, intelligence != null && intelligence.ListsUnknownObjectives, ...)`; extraction via `HudText.ExtractionOf(runtime.Extraction, runtime.Phase, inside)` where `inside` is `((ReachZoneObjective)extraction).Inside` when it is one, else 0, and `ExtractionRequired` its `RequiredUnits`.
3. **Squad:** units = `director.Friendlies` when `director != null && director.Friendlies.Count > 0`, else `selection.Roster` units. One `HudSquadCard` per unit, same order. Name/role/rank from `UnitIdentity` + `roster.Find(identity.OperativeId)` + `roster.Rank(member)` (rank 0 when unavailable); fallback name `HudText.CleanName(unit.name)`, role = the attacker's `Role.ToString()`. Health from the unit's `Health` (`IsDown = !IsAlive`). `IsControlled = unit == activeCharacter.Unit`; `IsSelected` = the unit's `SelectableUnit` is in `selection.Selected`. `Tag` only for a living, non-controlled unit with a `CompanionAI`: `HudText.CompanionTag(companion.IsParked, companion.IsHeld, activeCharacter.IsFollowOn)`.
4. **Controlled:** `HasControlled = activeCharacter.HasUnit`; name, role, rank, health as for cards; `ControlledCover` = `HudText.Cover(unit.Cover.Status, point.Height, point.Placement)` using the point only when `Point != null && Point.IsValid`.
5. **Abilities:** caster = `abilityTargeting.Caster`, `abilities = abilityTargeting.CasterAbilities`; `CasterName`; for each slot `i < abilities.Count` with a non-null definition one `HudAbilitySlot`: prompt `HudPrompts.AbilityPrompt`, name, `Remaining = abilities.CooldownRemaining(i)`, `Fraction = Remaining / abilities.EffectiveCooldown(def)` clamped 0..1, state `Armed` if `abilityTargeting.ArmedSlot == i`, else `Cooldown` if `Remaining > 0`, else `Ready`; every slot `Unavailable` when `!abilities.IsAlive`. `IsArmed`, and `ArmedLine`: `"AIMING: <name>"` plus, when `Preview.HasAim`, `" | <target name if CanTarget> | <dist>/<range> m | OK / Moving into range / <failure.Describe()>"` (invariant numbers, one decimal). Armed-ability target names pass `Knowledge.CanTarget`.
6. **Queue:** subject = the first selected, alive, `CommandableUnit` when paused and `selection.Selected.Count > 0`, else the controlled unit. `QueueOwner` = its display name. Steps: current command (`IsCurrent`) then pending, numbered from 1, at most 3 in real time and 8 paused; `QueueHidden` = the remainder; text via `HudText.Step(command, shown)` with a cached delegate `h => Knowledge.CanTarget(intelligence, h)`; when the current command is an `AbilityCommand` and `unit.AbilityPhase != AttackPhase.None`, append `" (moving into range)"`. `QueueUnit = subject`; `CanClearOrders = subject has any command`.
7. **Prompts:** `HudPrompts.Build(ctx, family, controls, into.Prompts)` with `Paused`, `AbilityArmed`, and `TerminalName = commandInput.PromptInteractable(family.IsController())?.DisplayName` (null when no commandInput or no terminal in reach). Cache by `(family, paused, armed, terminalName)`: rebuild only when that key changes (so no string work per frame); the cached list is copied into `into.Prompts`.
8. **Target:** candidates in order, first that passes `Knowledge.CanTarget(intelligence, h)` and is alive wins: (a) `abilityTargeting.Preview.Target` while armed (tag `AIMING`); (b) pointer hover — for `KeyboardMouse` family `commandInput.ResolveAt(commandInput.PointerScreenPosition)` hostile, refreshed at most every 0.1 s of `unscaledTime` (cache the last result; clear it when the HUD says the pointer is over the HUD — see `HudSources`-free rule: the builder takes an optional `Func<Vector2,bool> overHud` property `PointerOverHud`, default null), for pad families `cursor.Target` hostile only when `cursor.IsActive` (tag `HOVERED`); (c) `cursor.SoftTarget` (tag `TARGETED`); (d) the controlled unit's `AttackTarget` (tag `ATTACKING`). Fill name (`HudText.CleanName`), detail (attacker archetype `DisplayName` else role), health, and `CoverText` = for the controlled unit's attacker only: `IsTargetInCover(target, out hit) ? "Cover " + percent + "%" : "Exposed"` when the controlled attacker `NeedsLineOfSight`, else empty.
9. **World marks:** for each `encounter.Hostiles` health that is alive: if `Knowledge.CanTarget` → `Hostile` mark at `position + up * 2.3` with empty text; else if `intelligence != null && intelligence.StateOfEnemy(h) == KnowledgeState.Discovered && intelligence.TryLastKnown(h, out p)` → `LastKnown` mark at `p` with text `"LAST KNOWN"`. For each runtime objective with `IsKnown && HasTarget && State != Completed`: mark of kind `Extraction` (the extraction objective) or `Objective`, text `MissionHudText.MarkerLabel(o)`, skipping empty labels. No other hostile data is read.

- [ ] **Step 1: Write the failing tests** (PlayMode; read `TestSupport/MissionRig.cs`, `IntelRig.cs`, `AbilityRig.cs` and `ObjectiveKnowledgeMissionTests`/`ReconScanPlayModeTests` first and reuse their rigs; create `HudSources` from the rig's components):

1. `Squad_HasOneCardPerFriendly_WithNameRoleRankAndHealth`: names equal the `UnitIdentity` display names; damaging a unit changes its card's `Health` on the next `Build`; killing it sets `IsDown` and keeps the card.
2. `Controlled_And_Selected_AreDistinct`: `activeCharacter.Unit` card has `IsControlled`; selecting another sets `IsSelected` only there; `Tag` is empty for the controlled unit and non-empty for companions.
3. `Tags_FollowParkedAndFollowOff`: with follow on a companion reads `FOLLOWING`; `SetFollow(false)` → `ATTACHED`; after a Tab switch away from a distant companion → `PARKED` (use the existing companion rig).
4. `Abilities_ShowCasterSlotsAndCooldownFill`: after `TryUse`, the slot state is `Cooldown`, `Fraction` is in (0,1], `Remaining > 0`.
5. `Cooldown_IsFrozenWhilePaused`: `TacticalPause.Pause()`, build twice across several `yield return null` frames (unscaled): `Remaining` identical to the float; after `Resume()` it decreases.
6. `ArmedAbility_ShowsAimingLine_AndMarksTheSlotArmed`.
7. `Queue_ListsCurrentThenPending_AndCapsByMode`: issue Move, then Append Attack and ability; steps are numbered 1.., step 1 `IsCurrent`; real-time cap 3 with `QueueHidden`, paused cap 8.
8. `Queue_NeverNamesAnUnobservedTarget`: fog Blind preset (see `IntelRig`), the unit holds an `AttackCommand` on an unobserved hostile → step text is `Attack (target lost)` and does not contain the hostile's name.
9. `Objectives_RespectKnowledge`: unknown objective is omitted when `ListsUnknownObjectives` is false, appears by `VagueTitle` only when true; real title absent in both; extraction row is not in `Objectives` and `Extraction` is `Unknown`/`Locked`/`Available` per state.
10. `Extraction_Active_CountsSquadInsideTheZone`.
11. `Target_OnlyForObservedHostiles`: hostile unobserved → `Target.Visible == false` even when it is the controlled unit's `AttackTarget`, the soft target, or under the pointer; after `service.ObserveEnemy`-equivalent (use the rig's way of observing) → visible with name/health.
12. `Marks_ObservedLastKnownAndUnknown`: observed → one `Hostile` mark; after losing it → one `LastKnown` mark at the last seen point and no `Hostile`; a never-seen hostile → no mark and no entry anywhere in the snapshot (assert by stringifying every text field and every mark position against the hostile's name and position).
13. `TruthView_DoesNotWidenTheHud`: `service.SetTruthView(true)` (use the real toggle) and a Build: unobserved hostiles still produce no `Hostile` mark, no target, no queue name.
14. `Prompts_RebuildOnlyWhenTheKeyChanges` (reference-equals the cached strings across two builds) and `Prompts_FollowTheFamily` (set the family through `ActiveInputDevice` the way `ActiveInputDeviceTests` does; Xbox → Nintendo changes `Cancel`).
15. `NullSources_BuildAnEmptySnapshotWithoutThrowing`.

- [ ] **Step 2: Run to verify failure** — `Tools/run-tests.sh PlayMode "Blackglass.Tests.HudSnapshotBuilderTests"` (compile failure first).

- [ ] **Step 3: Implement** `HudSources` and `HudSnapshotBuilder` per the behaviour list. Keep helper state (cached prompt key/list, last hover result and time, the `Func<Health,bool>` delegate) in builder fields; use `TryGetComponent`; no LINQ; reuse lists. Add `public Func<Vector2, bool> PointerOverHud { get; set; }` for step 8(b).

- [ ] **Step 4: Run tests** — `HudSnapshotBuilderTests` green; `Blackglass.Tests.EditMode` full suite green.

- [ ] **Step 5: Commit** — `HUD: snapshot builder with intelligence-safe reads`.

---

### Task 4: Factory, theme, root and the status and objectives panels

**Files:**
- Create: `Hud/HudTheme.cs`, `Hud/HudFactory.cs`, `Hud/TacticalHud.cs`, `Hud/ObjectivesPanel.cs`, `Hud/StatusPanel.cs`
- Test: `Assets/_Project/Tests/PlayMode/TacticalHudBuildTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class HudTheme
{
    public static readonly Color Panel = new Color(0.04f, 0.05f, 0.07f, 0.78f);
    public static readonly Color PanelEdge = new Color(0.22f, 0.27f, 0.33f, 1f);
    public static readonly Color Text = new Color(0.86f, 0.89f, 0.92f, 1f);
    public static readonly Color TextDim = new Color(0.52f, 0.57f, 0.62f, 1f);
    public static readonly Color Accent = new Color(0.36f, 0.78f, 0.92f, 1f);     // friendly / selected / ready
    public static readonly Color Warn = new Color(0.96f, 0.72f, 0.28f, 1f);      // cooldown, pause, last known
    public static readonly Color Bad = new Color(0.92f, 0.36f, 0.34f, 1f);       // hostile, failed, low health
    public static readonly Color Good = new Color(0.46f, 0.82f, 0.52f, 1f);      // completed
    public const int Margin = 24, FontSmall = 14, FontBody = 16, FontLarge = 22, FontBanner = 34;
}

public static class HudFactory
{
    public static Font Font { get; }                                   // Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
    public static RectTransform Rect(string name, Transform parent);   // RectTransform, no graphic
    public static RectTransform Box(string name, Transform parent, Color color, bool blocksWorld);  // Image; raycastTarget=false unless asked
    public static Text Label(string name, Transform parent, int size, Color color, TextAnchor anchor);   // raycastTarget=false, no best-fit, horizontal overflow wrap
    public static void Anchor(RectTransform r, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 offsetMin, Vector2 offsetMax);
    public static Image Bar(string name, Transform parent, Color back, Color fill, out Image fillImage);   // Image.Type.Filled horizontal for the fill (sprite: a 1x1 white sprite created once)
    public static void SetText(Text label, string value);              // assigns only when different (string compare)
}
```

`TacticalHud : MonoBehaviour`:
- `[SerializeField] HudSources sources;` `[SerializeField] PointerBlocker pointerBlocker;`
- `internal void Initialize(HudSources s, PointerBlocker blocker)`; `internal HudSnapshot Snapshot`; `internal RectTransform BuildInto(RectTransform root)` builds the whole hierarchy once under any rect (so layout tests can pass their own rect); `OnEnable` creates the `Canvas` (`ScreenSpaceOverlay`, sortingOrder 10), `CanvasScaler` (`ScaleWithScreenSize`, reference 1920×1080, `matchWidthOrHeight` 0.5), `GraphicRaycaster`, calls `BuildInto`, creates an `EventSystem` + `InputSystemUIInputModule` if `EventSystem.current == null && FindFirstObjectByType<EventSystem>() == null` (call `AssignDefaultActions()` and then set `move`, `submit`, `cancel` and `navigate`-type references to `null`/empty so only pointer actions remain); `LateUpdate` runs `builder.Build(sources, Snapshot, Time.unscaledTime)` and calls each panel's `Apply(Snapshot)`.
- Panel contract: `abstract class HudPanel : MonoBehaviour`-free plain classes with `void Apply(HudSnapshot s)` are fine; each panel keeps its `Text`/`Image` refs and compares with the last value it set (`HudFactory.SetText`, fill/colour/active only on change).

Layout (reference 1920×1080; anchors are corners/edges with `Margin`):
- `ObjectivesPanel`: anchor top-left, width 420, height by content (vertical layout group, content size fitter) — header `MISSION  <PhaseText>`; up to 8 rows (pool of 8 `Text`+marker boxes). Marker text per kind: Active `[ ]`, Completed `[x]` (dim + Good), Failed `[!]` (Bad), Locked `[-]` (dim), Unknown `[?]` (Warn). Hidden when `!HasMission`.
- `StatusPanel`: (a) top-centre banner `HudText.Pause(ResumePrompt)` shown while paused (Warn, FontLarge, framed box with a double-bar glyph made of two small Images so it is not colour-only); (b) mission result banner below it from `BannerText` (FontBanner); (c) top-right block: `HudText.ExtractionLabel(...)` (Hidden → panel off) and a `FOLLOW: ON/OFF` chip (button added in Task 9) — hidden when `!HasFollow`.

- [ ] **Step 1: Write the failing tests** (PlayMode, no scene load; create `new GameObject("hud")`, add `TacticalHud`, call `Initialize(new HudSources(), null)` with a plain `RectTransform` root of `Rect(0,0,1920,1080)` via `BuildInto`; push values straight into `hud.Snapshot` and call the panels' `Apply` through an internal `hud.ApplySnapshot()`):
1. `Build_CreatesTheExpectedPanels` (names `Objectives`, `Status`, `Squad`, `Operative`, `Prompts`, `Target`, `WorldMarks` exist after `BuildInto`; later tasks fill the last five — this task asserts the first two plus that the others exist as empty rects).
2. `Objectives_ShowRowsWithKindMarkers_AndHideWithoutAMission`.
3. `PauseBanner_ShowsWhilePausedAndNamesTheResumePrompt` and is off when not paused.
4. `ExtractionBlock_ShowsTheStateLabel_AndHidesWhenHidden`.
5. `ResultBanner_ShowsSuccessAndFailure`.
6. `Apply_DoesNotReassignUnchangedText`: apply the same snapshot twice and assert via a test hook (`HudFactory.TextWrites` counter incremented only when `SetText` actually assigns) that the second apply performs zero writes.
7. `Layout_StaysInsideAndDoesNotOverlap_At16x9_16x10_21x9`: build under `RectTransform`s sized 1920×1080, 1920×1200, 2560×1080; force layout (`Canvas.ForceUpdateCanvases`, `LayoutRebuilder.ForceRebuildLayoutImmediate`); every panel's `GetWorldCorners` lies inside the root rect and the objectives, status, squad, operative, prompts and target rects pairwise do not overlap (write `AssertNoOverlap(params RectTransform[])` in `TestSupport/HudLayout.cs` and reuse it in later tasks; populate the snapshot with maximum content first: 8 objectives, 6 squad cards, 4 abilities, 8 queue steps, 6 prompts, a visible target).
8. `TheEventSystem_IsPointerOnly`: after `OnEnable` with no EventSystem present, one exists, its module's `move`, `submit` and `cancel` references are empty, and none of the HUD's Graphics other than explicit buttons has `raycastTarget == true`.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement** the factory, theme, root, `ObjectivesPanel`, `StatusPanel`; create empty panel rects for Squad/Operative/Prompts/Target/WorldMarks with the final anchors so the layout test is meaningful from now on: Squad bottom-left (width 460, up to 6 cards of 72 px + 8 gap), Operative bottom-centre (width 760, height 250), Prompts bottom-right (width 380), Target right of centre above Prompts (width 340, height 140), WorldMarks full-screen non-blocking.

- [ ] **Step 4: Run** `TacticalHudBuildTests` green; PlayMode suite still passes (no scene contains the HUD yet).

- [ ] **Step 5: Commit** — `HUD: uGUI factory, theme, root canvas, objectives and status panels`.

---

### Task 5: Squad panel

**Files:** Create `Hud/SquadPanel.cs`, `Hud/SquadCardView.cs`, `Hud/HudPortraits.cs`; Modify `TacticalHud.cs`; Test `PlayMode/HudSquadPanelTests.cs`.

**Interfaces:** `HudPortraits.Resolve(CommandableUnit unit)` returns `Sprite` (always `null` for now). `SquadCardView` fields: portrait box + initials, name, `Role · Rank n` line, health bar + `HudText.Health`, state tag text, marker boxes (controlled: filled square left edge + the word `CONTROL`; selected: outlined frame + the word `SELECTED`; down: card dimmed + text `DOWN`). Pool of 6 cards, hidden when unused; card order = snapshot order. A card keeps `HudSquadCard` of the last apply so Task 9 can read `Unit`.

Card height 72, width 460; each card is a `Button` with `Image` target (raycast on) — the click handlers are added in Task 9; here the card exposes `internal event Action<CommandableUnit, bool /*double*/> Clicked` that is never invoked yet.

- [ ] **Step 1: Failing tests** — snapshot-driven (as in Task 4): cards count/visibility for 0, 3 and 7 entries (extras beyond 6 are dropped, not thrown); name/role/rank/health text; rank 0 hides the rank; controlled card shows `CONTROL`, selected `SELECTED`, down shows `DOWN` and the health bar at 0; a companion tag is shown only when non-empty; initials come from `HudText.Initials`; neither the operative id nor any text outside the card fields appears (assert the whole panel's text equals the concatenation of the expected parts); no colour-only state: for each of controlled / selected / down / companion tag, assert there is visible text for it (not just a colour change); layout no-overlap test with 6 cards.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** panel and card views with change-only writes.
- [ ] **Step 4: Run** `HudSquadPanelTests` and `TacticalHudBuildTests`.
- [ ] **Step 5: Commit** — `HUD: squad panel`.

---

### Task 6: Operative panel (controlled operative, abilities, command queue)

**Files:** Create `Hud/OperativePanel.cs`, `Hud/AbilitySlotView.cs`; Modify `TacticalHud.cs`; Test `PlayMode/HudOperativePanelTests.cs`.

Layout inside the 760×250 panel: row 1: `ControlledName` (FontLarge), `ControlledRole · Rank n`, health bar with `HudText.Health`, cover line (`ControlledCover`); row 2: ability slots (pool of 4, each 170×64: prompt chip, name, state; a `Filled` overlay `Image` (vertical fill) darkens by `Fraction` while on cooldown with the number `HudText.Seconds(Remaining)` over it; Armed: bright frame plus `>` prefix in the name text; Unavailable: dim); an `AIMING` strip line below the slots showing `ArmedLine` (hidden when `!IsArmed`); row 3: queue header `ORDERS: <QueueOwner>` and the numbered steps (pool of 8 `Text`; step 1 prefixed with `> `, others with their number; `+N more` line when `QueueHidden > 0`) and a `CLEAR` button (shown when `CanClearOrders`; click handler added in Task 9). When `!HasControlled`: show `No unit in control` and hide abilities/queue.

- [ ] **Step 1: Failing tests** (snapshot-driven): controlled info text; `No unit in control` state; slot count follows the snapshot (0–4); each state renders a distinct text marker (`READY`, the seconds, `ARMED`, `—`) so state is never colour-only; the cooldown overlay fill equals `Fraction` and the number equals `HudText.Seconds`; armed strip visible only while armed; queue renders N numbered lines with the `>` mark on the first, `+N more` when hidden > 0; `CLEAR` button visible only with `CanClearOrders`; applying a snapshot with 4 abilities + 8 steps keeps every rect inside the panel (use `HudLayout`); and a pause-freeze integration check using the real rig from Task 3: `Time.timeScale = 0` (through `TacticalPause.Pause()`), build+apply for several frames, assert the displayed cooldown text never changes.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `HudOperativePanelTests`, `TacticalHudBuildTests`.
- [ ] **Step 5: Commit** — `HUD: controlled operative, ability bar and command queue`.

---

### Task 7: Prompt panel

**Files:** Create `Hud/PromptPanel.cs`; Modify `TacticalHud.cs`; Test `PlayMode/HudPromptPanelTests.cs`.

Bottom-right panel, up to 6 rows, each row = prompt chip (boxed `Prompt` text) + `Label`. Rows hidden when unused. A small footer shows the active family name? No: do not show the family name (debug only).

- [ ] **Step 1: Failing tests**: rows match the snapshot (count, label, prompt); unused rows hidden; changing the snapshot prompts (simulate a family switch by building prompts with `HudPrompts.Build` for Xbox then PlayStation then Nintendo then KeyboardMouse and applying) changes the chips without recreating rows (same `Text` instances); hot-switch integration with the real `ActiveInputDevice` and `TacticalHud` in a small rig: after a simulated Xbox → PlayStation switch (follow `ActiveInputDeviceTests`), the next `LateUpdate` shows PlayStation chips; Nintendo `Cancel` chip equals the Nintendo binding text and differs from Xbox; no hard-coded strings: a source scan test over `Scripts/Hud/*.cs` that fails if any file contains `"Press "`, `"Left Click"`, `"Space"` (outside comments), `"Tab"` as a prompt literal, or the forbidden device tokens from `NoDeviceTypesInGameplayTests` (comments allowed).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the new class and `NoDeviceTypesInGameplayTests`.
- [ ] **Step 5: Commit** — `HUD: contextual prompt panel`.

---

### Task 8: Target panel and world marks

**Files:** Create `Hud/TargetPanel.cs`, `Hud/WorldMarkLayer.cs`; Modify `TacticalHud.cs`; Test `PlayMode/HudTargetAndMarksTests.cs`.

`TargetPanel` (340×140): hidden unless `Target.Visible`; `HOSTILE` tag with a diamond shape (a 45°-rotated filled `Image`), name, detail, health bar + numbers, `Tag` (`AIMING`/`HOVERED`/`TARGETED`/`ATTACKING`), `CoverText` line shown only while `IsPaused` or `Tag == "ATTACKING"`.

`WorldMarkLayer`: pool of `Text` + marker shapes projected with `camera.WorldToScreenPoint` each `Apply` (use the overlay canvas scale factor: convert via `RectTransformUtility.ScreenPointToLocalPointInRectangle` on the canvas root with `null` camera). Kinds: `Hostile` = small filled red diamond (12 px) above the unit; `LastKnown` = hollow diamond (frame only, built from four thin Images or an outlined Image) with `?` and the text `LAST KNOWN`, Warn colour; `Objective`/`Extraction` = text label with a small square. Marks with `z <= 0` (behind the camera) are hidden. The pool grows on demand (cap 32) and hides unused entries; it never creates or destroys per frame.

- [ ] **Step 1: Failing tests**: panel hidden/visible; fields; cover line rules; marks: 3 observed hostiles → 3 diamonds; a `LastKnown` mark uses a different shape set (hollow, `?`) and different text than `Hostile` (the shape/text difference is asserted, not just colour); marks behind the camera hidden; positions follow the camera (move the camera, the screen position changes); pool reuse (same `GameObject` instances across 100 applies; instance count constant); the fog integration with the Task 3 rig: only observed hostiles produce `Hostile` marks and the label text contains no hostile name.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the new class.
- [ ] **Step 5: Commit** — `HUD: target panel and world marks`.

---

### Task 9: Requests, buttons and the pointer gate

**Files:** Create `Hud/HudRequests.cs`, `Hud/HudPointerGate.cs`; Modify `SquadCardView.cs`, `AbilitySlotView.cs`, `OperativePanel.cs`, `StatusPanel.cs`, `TacticalHud.cs`; Test `PlayMode/HudRequestsTests.cs`.

**Interfaces:**

```csharp
public sealed class HudRequests
{
    public HudRequests(HudSources sources, InputActionReference queueModifier);
    public void SelectUnit(CommandableUnit unit, bool additive);   // UnitSelection.Select / Toggle on the unit's SelectableUnit
    public bool TakeControl(CommandableUnit unit);                 // ActiveCharacter.TakeControl
    public void ToggleAbility(int slot);                           // abilityTargeting.IsArmed && ArmedSlot == slot ? Disarm() : Arm(slot)
    public void ClearOrders(CommandableUnit unit);                 // GroupOrders.Issue(new[] { unit }, new StopCommand(), IssueMode.Replace)
    public void ToggleFollow();                                    // ActiveCharacter.ToggleFollow
    public void TogglePause();                                     // TacticalPause.Toggle
    public bool AdditiveHeld { get; }                              // InputActionUtility.IsPressed(queueModifier)
}

public sealed class HudPointerGate
{
    public void Register(RectTransform r);                         // blocking panels and buttons
    public bool IsOver(Vector2 screen);                            // any registered, active-in-hierarchy rect contains the point
}
```

Wiring: `TacticalHud.OnEnable` creates the gate, registers the blocking rects (Objectives, Status blocks, Squad, Operative, Prompts, Target when visible — not WorldMarks, not the full-screen root, not the pause banner), installs `pointerBlocker.SetTest(gate.IsOver)` (if present) and sets `builder.PointerOverHud = gate.IsOver`; `OnDisable` clears the test. Cards: `IPointerClickHandler`; `clickCount == 2` → `TakeControl`, otherwise `SelectUnit(unit, additive: requests.AdditiveHeld)`. Ability slot click → `ToggleAbility(slot)`. `CLEAR` → `ClearOrders(queue subject)` (the snapshot carries the subject unit in `QueueUnit`). Follow chip → `ToggleFollow`. A pause chip beside the banner/extraction block → `TogglePause`. All other graphics keep `raycastTarget = false`.

- [ ] **Step 1: Failing tests**: each request calls the right API and nothing else (select, additive via a held modifier action in an `InputTestFixture`, take control follows Task 1 rules, arm/disarm toggling, clear orders issues a Stop that empties the queue and does not park a companion differently than the Stop action — compare against the `Commands/Stop` action result on the same rig, follow flips, pause flips); card `Clicked` wiring: invoking the click handler with `clickCount 1` selects, with `2` takes control; the table in the spec section 8: for every mouse request assert the controller equivalent action exists with bindings in all four pad groups (`Character/PreviousCharacter`, `Character/NextCharacter`, `Commands/Ability1..4` chords, `Commands/Stop`, `Character/ToggleFollow`, `Commands/ToggleTacticalPause`, `Commands/QueueModifier`) — read the asset; gate: `IsOver` is true inside registered rects and false outside, false for inactive rects; end-to-end with `PlayerCommandInput`: a left press+release inside the squad panel rect issues no order, outside issues one (reuse Task 1's rig plus the real `TacticalHud`); pad input never moves UI focus: with the EventSystem present, send a simulated stick/button press on a pad and assert `EventSystem.current.currentSelectedGameObject == null` and no card `Clicked` event fired.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the new class plus `PointerBlockerPlayModeTests`, `PlayerCommandInputTests`.
- [ ] **Step 5: Commit** — `HUD: requests, buttons and the pointer gate`.

---

### Task 10: Developer overlay gating of the IMGUI views

**Files:** Modify `DebugUI/PrototypeHud.cs`, `DebugUI/AbilityBarView.cs`, `Mission/MissionHud.cs`, `Mission/MissionDebugView.cs`; Test `PlayMode/DeveloperOverlayGatingTests.cs`, `EditMode` additions only if a pure helper is added.

Each gets `[SerializeField] DeveloperOverlay overlay;` and `internal void SetOverlay(DeveloperOverlay o)`. `OnGUI`: `PrototypeHud` draws the drag box (`commandInput.IsDragging`) first, then returns if `!DeveloperOverlay.Shows(overlay)`; the other three return immediately. Nothing else changes in these files. `TacticalCursorView`, `OperativePanelView`, `IntelligenceDebugView`, `IntelMapView`, all world views and `CoverView` are untouched.

- [ ] **Step 1: Failing tests**: a hidden overlay makes the gated views report "not drawing" via an `internal bool IsDrawing => DeveloperOverlay.Shows(overlay)` property on each of the four views (add it; `OnGUI` uses it) — assert false when hidden, true after `Toggle`, true with no overlay; `DeveloperOverlayInput` toggles on F1 with a simulated keyboard (pattern of `MissionDeveloperInputTests`); all existing tests of the four views' static text functions still pass unchanged (run `PrototypeHudTests`, `AbilityViewsTests`, `MissionHudTextTests`, `MissionDebugTextTests`, `LeakTextTests`, `ControllerHudTests`).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** those test classes.
- [ ] **Step 5: Commit** — `Developer overlay: hide the IMGUI debug views unless toggled (F1)`.

---

### Task 11: Scene wiring, scene-level tests, fog sweep and regeneration

**Files:** Create `Assets/_Project/Editor/HudSceneBuilder.cs`; Modify scenes `ProceduralMission.unity`, `Prototype.unity` (through the builder only); Test `PlayMode/HudSceneTests.cs`, `PlayMode/HudFogSweepTests.cs`, `PlayMode/HudRegenerationTests.cs`.

`HudSceneBuilder` (pattern of `IntelligenceSceneBuilder`): menu `Blackglass/HUD/Wire Scenes` and `public static void WireFromCommandLine()`. For each of the two scenes: open it, find the existing components (`FindFirstObjectByType`), add (if absent) a `Systems`-sibling root `TacticalHud` GameObject with `TacticalHud`, `PointerBlocker`, and a `DeveloperOverlay` + `DeveloperOverlayInput` (action reference `Developer/ToggleDebugOverlay` from the project's controls asset), fill `HudSources` through `SerializedObject` (director, roster and intelligence only where they exist in that scene), set `PlayerCommandInput.pointerBlocker`, set `overlay` on the four IMGUI views, mark dirty, save, close. Idempotent: running twice changes nothing. Run it once through Unity batch (`-executeMethod Blackglass.EditorTools.HudSceneBuilder.WireFromCommandLine -quit`) after writing it, then commit the changed scenes.

- [ ] **Step 1: Failing tests** (scene tests load the scene the way `IntelligenceSceneTests` does and wait for `MissionState.Ready`):
  - `HudSceneTests`: both scenes contain exactly one `TacticalHud`, one `PointerBlocker`, one `DeveloperOverlay` (hidden); every serialized `HudSources` reference that exists in the scene is non-null; ProceduralMission squad panel shows 3 cards with the operatives' display names; the objectives panel lists the known objectives and `Extraction` matches the runtime; selecting via Tab updates `IsControlled` within one frame; pausing shows the banner; the Prototype scene (no director, roster or intelligence) builds and shows squad cards from the selection roster with no errors; `LogAssert.NoUnexpectedReceived()` in both.
  - `HudFogSweepTests` (Blind preset scene): over a scripted sequence — start, advance until a hostile becomes observed (use the rig/service API the existing `ObjectiveKnowledgeMissionTests`/`ReconScanPlayModeTests` use), lose it, `Recon Scan` with the scan — after each step collect every `Text.text` under the HUD root plus every mark position and assert: no unobserved hostile's `name`, health string (`"x/y"` of that hostile) or position (compare world marks to within 0.01) appears; the number of `Hostile` marks equals the number of `CanTarget` hostiles; `LastKnown` marks exist only for `Discovered` hostiles and sit at `TryLastKnown`; with `service.SetTruthView(true)` the same assertions hold; the objectives panel never shows a real title of an unknown objective (set `showUnknownObjectives` both ways).
  - `HudRegenerationTests`: `director.RegenerateSame()` then `GenerateNew()` (F6/F7 paths): the snapshot's cards reference only the new units (`card.Unit` not destroyed, in `director.Friendlies`), mark/label pools do not grow beyond cap, objective rows equal the new runtime, no exception and no `MissingReferenceException` in the log, `TacticalHud` still enabled; mid-fight regeneration; killing the controlled unit shows `DOWN` on its card, control passes and the controlled panel updates; fog reset: `service.Clear()` then `Begin` leaves no `Hostile`/`LastKnown` mark from the old mission.
  - Layout at runtime: with the HUD populated, the Task 4 layout assertion on the live scene's panels.
- [ ] **Step 2: Run to verify failure** (scenes not wired yet).
- [ ] **Step 3: Implement** the builder, run it in batch, verify both scenes open without errors.
- [ ] **Step 4: Run** the three classes; then the full PlayMode suite.
- [ ] **Step 5: Commit** — `HUD: wire the HUD into ProceduralMission and Prototype; scene, fog and regeneration tests`.

---

### Task 12: Documentation and full verification

**Files:** Modify `Docs/Decisions.md` (append `## 042 — The tactical HUD: a snapshot boundary, no focus mode, a developer overlay`); Create `Docs/Phase11.5-ManualTests.md`; update `Docs/superpowers/specs/2026-10-09-tactical-hud-design.md` only if behaviour deviated.

Decision 042 (concise, house style: Decided / Why / Rejected / Implications / Known limitations) must record: the gameplay→snapshot→view boundary and that the HUD subscribes to no events; the squad structure (source order, down cards, tags); command queue presentation and the unknown-target rule; the intelligence restrictions (`CanTarget` only, truth view excluded, vague titles, extraction rules, no counters); prompt integration (`PromptResolver`, family poll, no hard-coded text); input without a focus mode (pointer-only EventSystem, request table, `PointerBlocker`, `TakeControl`); player HUD versus developer overlay (F1, hidden by default, which views are gated); uGUI with legacy `Text` and why not TMP/UI Toolkit; known limitations (placeholder portraits, no icon glyphs, cover status granularity, hover raycast at 10 Hz, no controller focus mode, cursor cannot click the HUD).

`Phase11.5-ManualTests.md`: the owner's sequence from the phase brief (start a mission; cycle all operatives; compare cards; take damage; use abilities and watch cooldowns; pause and confirm cooldowns freeze; queue several commands and read the queue; toggle Follow; park and re-attach a companion; complete objectives; activate extraction; find a hidden objective; observe an enemy; lose sight and read the last-known mark; confirm unknown enemies never appear; hack cameras and use Recon Scan; switch keyboard/mouse ↔ controller and read the prompts; click squad cards, ability slots, CLEAR and Follow with the mouse and do the same with a controller's own actions; regenerate with F6/F7; 1920×1080; F1 overlay), each with an expected result.

- [ ] **Step 1:** Run the complete suites: `Tools/run-tests.sh EditMode` and `Tools/run-tests.sh PlayMode`. All green (record exact totals; baselines 992/849 plus the new tests).
- [ ] **Step 2:** Run the Unity console check: open nothing; grep `Logs/TestRun-PlayMode.log` for `Exception` / `error CS` not attributable to an expected assertion; none unexpected.
- [ ] **Step 3:** Write the two documents.
- [ ] **Step 4:** Confirm `git status` shows only intended files (never the Settings/ProjectSettings files).
- [ ] **Step 5: Commit** — `HUD docs: decision 042 and the manual test sequence`.
- [ ] **Step 6:** Whole-branch review by a fresh reviewer on the most capable model; fix findings (one fix round), re-run both suites, then report to the owner. Do not merge or push: the owner decides.
