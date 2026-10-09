# Phase 11.5: Functional Tactical HUD — design

Status: approved in chat 2026-10-09 (five decisions below). Next: implementation plan.

## 1. Goal

A functional player HUD that exposes existing gameplay state so playtesting no longer depends on the IMGUI debug text. It is not the final UI. It consumes state, never owns it, and never leaks what the intelligence layer (decision 037) hides.

Not in scope (owner list): inventory, equipment, loot, character sheet, skill tree, campaign, save/load, menus, final glyph art, minimap, animation polish, sound, third-party UI packages, Phase 12.

## 2. What exists (verified in the repo)

- All current on-screen text is debug IMGUI (`OnGUI`): `PrototypeHud`, `AbilityBarView`, `MissionHud`, `MissionDebugView`, `OperativePanelView` (F9), `IntelligenceDebugView` (F12), `IntelMapView` (M), `TacticalCursorView` (the controller cursor). No Canvas and no EventSystem exist in any scene. `com.unity.ugui 2.0.0` is installed; `Blackglass.asmdef` does not reference it yet.
- World feedback already exists and stays: `SelectionIndicator`, `ActiveCharacterMarker`, `CoverView`, `CommandQueueView` (line renderer), `AbilityTargetingView`, `AttackLineView`, `ObjectiveMarker`, `HostilePresenter` last-known marker.
- Convention: views read state each frame; gameplay never references views (006, 012, 013). All hostile-related displays go through `Knowledge` / `IntelligenceService` (037). `PromptResolver.GetPrompt(action, family)` derives prompts from current bindings (024).
- Squad source: `MissionDirector.Friendlies` (ProceduralMission; dead units stay in the list, deactivated, until regeneration) with `SquadRoster` for rank; `Encounter.Friendlies` in the Prototype scene. The extraction objective is the last entry of `MissionRuntime.Objectives`.
- Free debug keys: F1–F4.

## 3. Decisions (approved)

1. **uGUI, built from code.** Screen-space overlay Canvas, `CanvasScaler` ScaleWithScreenSize 1920×1080, match 0.5. Text uses the built-in legacy `Text` (font `LegacyRuntime.ttf`) through one factory (`HudFactory`) so a later TextMeshPro switch is one class. No TMP essential-resources import, no prefabs, no binary assets. Rejected: UI Toolkit (second UI stack, separate picking, asynchronous layout in tests), TMP (asset import).
2. **Snapshot boundary.** `HudSnapshotBuilder` reads gameplay through `HudSources` (serialized references) into reused snapshot structs each frame (unscaled time, `LateUpdate`). Views compare each value with what they last showed and touch a `Text`/`Image` only on change (cached strings; no per-frame allocation). The HUD subscribes to no gameplay events, so a regenerated mission cannot leave stale references. Gameplay code never references HUD code; the only seams are two small neutral classes (`PointerBlocker`, `DeveloperOverlay`) and one method on `ActiveCharacter`.
3. **Interaction without a focus mode.** The EventSystem runs in pointer-only mode (`InputSystemUIInputModule` with its move/submit/cancel actions left empty), so the HUD never takes stick or button input. Every clickable element sends the request the existing input sends, and each has a controller equivalent that already exists (table in section 8). No new bindings in any pad family. Rejected for now: letting the tactical cursor click HUD elements (changes `PointerTarget` and the input pipeline).
4. **Player HUD versus developer overlay.** The IMGUI views become the developer overlay, hidden by default, toggled by new `Developer/ToggleDebugOverlay` (F1, keyboard only like the other Developer actions). World feedback views stay visible. The drag-select box and the controller cursor are player-facing and are never hidden.
5. **Portraits:** a placeholder badge (initials plus role letter) behind `HudPortraits.Resolve(...)`, which returns `null` today. Real art needs no data change now.

## 4. Architecture

All new runtime code is in `Assets/_Project/Scripts/Hud/` in the existing `Blackglass` assembly (add `Unity.ugui` to its references).

**Data layer (plain C#, EditMode-testable):**
- `HudSnapshot` with `HudSquadCard`, `HudAbilitySlot`, `HudCommandStep`, `HudObjectiveRow`, `HudTarget`, `HudPromptEntry`, `HudWorldMark` structs held in reused lists.
- `HudSources`: `[Serializable]` bag of optional references (`ActiveCharacter`, `UnitSelection`, `TacticalPause`, `Encounter`, `MissionDirector`, `SquadRoster`, `IntelligenceService`, `AbilityTargeting`, `TacticalCursor`, `PlayerCommandInput`, `ActiveInputDevice`, `InputActionAsset`, `Camera`). Every reference is optional; a missing source hides the panel it feeds.
- `HudSnapshotBuilder.Build(sources, snapshot)`: the only place gameplay is read.
- `HudText`: pure wording (health, command step, cover, follow, extraction, objective row, banner). Invariant culture.
- `HudPrompts`: pure context → list of `(label, actionPath)`, resolved per `InputFamily` with `PromptResolver` and cached until the family or context changes.

**View layer (MonoBehaviours):**
- `TacticalHud` (root): owns the Canvas and the EventSystem (creates one only if the scene has none), builds the hierarchy once with `HudFactory`, applies the snapshot.
- Panels: `ObjectivesPanel`, `StatusPanel` (pause, extraction, follow, mission result), `SquadPanel` + `SquadCardView` (pooled), `OperativePanel` (controlled operative, ability slots, command queue), `PromptPanel`, `TargetPanel`, `WorldMarkLayer` (pooled labels).
- `HudRequests`: the only code that turns a click into a gameplay request (see section 8).
- `HudPointerGate`: registers blocking rects and installs a test into `PointerBlocker`.
- `HudTheme`: colours and sizes. Dark, restrained, one cool accent, one warning accent; no glow.

**Seams outside `Hud/`:**
- `Input/PointerBlocker` (MonoBehaviour, no UI types): `bool IsBlocking(Vector2 screen)` backed by a delegate the HUD installs. `PlayerCommandInput` checks it at press time and ignores the whole click or drag gesture if the press began over the HUD.
- `DebugUI/DeveloperOverlay` (MonoBehaviour): `IsVisible`, `Toggle()`. `DeveloperOverlayInput` reads `Developer/ToggleDebugOverlay`. A null overlay reference means "visible", so existing scenes and tests are unchanged.
- `ActiveCharacter.TakeControl(CommandableUnit)`: same hand-over rules as Tab (eligible unit becomes active; selection follows).
- `Input/BlackglassControls.inputactions`: `Developer/ToggleDebugOverlay` on F1.
- Debug IMGUI views gain an optional `DeveloperOverlay` reference and return early when it is hidden: `PrototypeHud` (everything except the drag box), `AbilityBarView`, `MissionHud`, `MissionDebugView`. `OperativePanelView`, `IntelligenceDebugView` and `IntelMapView` keep their own toggles.
- Editor: `HudSceneBuilder` (`Blackglass/HUD/Wire Scenes`, and a `-executeMethod` entry) adds the HUD, EventSystem, `PointerBlocker`, `DeveloperOverlay` and its input to `ProceduralMission` and `Prototype`. Idempotent.

## 5. Layout (1920×1080 reference, 24 px margins)

| Zone | Content |
| --- | --- |
| Top left | Objectives panel (mission phase line, one row per objective). |
| Top centre | `TACTICAL PAUSE` banner (always present while paused); mission result banner (SUCCESS / FAILED) below it. |
| Top right | Extraction state; Follow chip (`FOLLOW: ON/OFF`, clickable). |
| Bottom left | Squad cards, stacked, one per operative. |
| Bottom centre | Controlled operative panel; ability slots; command queue; armed-ability strip. |
| Bottom right | Contextual prompt list. |
| Near the pointer or soft target | Target panel (right of centre, above the prompts) for an observed hostile. |
| World | Pooled labels (objective, extraction, observed hostile tag, last-known marker). |

The centre of the viewport stays empty. Panels use anchors and the scaler, and are checked at 1920×1080, 1280×720, 2560×1440 and 1920×1200.

## 6. Content rules

**Squad card:** portrait badge, display name (never the operative id), role, rank, health bar plus `current/max`, state tags. Tags carry meaning by text and shape, not colour alone: `CONTROLLED` (filled marker), `SELECTED` (outline marker), `ATTACHED` / `PARKED` / `HOLDING` (companions only), `DOWN` (greyed, health 0). Order is the friendly list order, which is the Tab order.

**Controlled operative panel:** name, role, rank, health, follow line (`Following`, `Attached (follow off)`, `Parked`, `Holding cover`), cover line (`Exposed`, `Low cover`, `Corner cover`) from `UnitCover.Status` and `CoverLocation.Height/Placement` only when occupied; no controlled unit shows "No unit in control".

**Abilities:** shown for `AbilityTargeting.Caster` (first eligible selected unit, else the active character, as targeting already decides). Slot count comes from `UnitAbilities.Count` (at most four today); the pool is sized to four and hides unused slots. Each slot: prompt (from `Commands/Ability{n}`, chord text for pads), name, state: ready, cooldown (fill = remaining / `EffectiveCooldown`, number), armed (marker plus text), unusable (caster dead). Cooldown values come from scaled time, so they freeze in tactical pause with no HUD logic. When an ability is armed, a strip shows `AIMING: <name>`, the target (if shown), `distance/range`, and `OK` / `Moving into range` / the failure wording, with Confirm and Cancel prompts. The existing world preview (`AbilityTargetingView`) is unchanged.

**Command queue:** subject is the first selected unit while paused, else the controlled unit. Steps are numbered; step 1 is highlighted as current. Real time shows up to 3 steps, pause up to 8, then `+N`. Wording: `Move`, `Attack <name>`, `Cover`, `Interact <terminal>`, `<ability name>`, plus `Moving into range` for an approaching ability. An `Attack` step names its target only while `Knowledge.IsShown`; otherwise it reads `Attack (target lost)`. A `Clear orders` button issues `StopCommand` (see section 8).

**Objectives:** rows are built from `MissionRuntime.Objectives` (extraction excluded here; it has its own status). Known: `[ ]` active, `[x]` completed (dimmed), `[FAILED]`, `[LOCKED]`, with `Describe()` text. Unknown: listed as `[?] <VagueTitle>` only when `IntelligenceService.ListsUnknownObjectives` and a vague title exists; otherwise omitted. The real title, count and position of an unknown objective are never read into the snapshot. This mirrors `MissionHudText.Panel`, which becomes the shared source of truth (the IMGUI panel and the HUD call the same functions).

**Extraction:** `UNKNOWN` (not `IsKnown`), `LOCKED` (Inactive), `AVAILABLE` (Active, nobody inside), `ACTIVE n/m IN ZONE` (Active, some squad members inside), `EXTRACTED` (success). Its world label obeys `IsKnown`.

**Target panel (observed hostile only):** subject priority: armed-ability preview target, hostile under the mouse (resolved at 10 Hz when the pointer is not over the HUD) or under the controller cursor, the soft target, the controlled unit's current attack target. Every candidate must pass `Knowledge.CanTarget`. Content: `HOSTILE` tag with a diamond, name, archetype or role, health bar and numbers, whether the controlled unit's shot is in cover (`UnitAttacker.IsTargetInCover` → `Cover 65%` / `Exposed`, shown while paused or when it is the attack target), and targeting state (`TARGETED`, `AIMING`).

**World marks:** observed hostile: small filled diamond above the unit. Last-known hostile: hollow diamond with `?` and `LAST KNOWN` at the last seen position only (`IntelligenceService.TryLastKnown`). Neither is drawn for Unknown. Known objective and extraction labels reuse `MissionHudText.MarkerLabel`. Labels are pooled and projected each frame; labels behind the camera are hidden.

**Pause:** `TACTICAL PAUSE — <Resume prompt> to resume` stays visible at top centre. Paused mode additionally expands the queue (8 steps), shows cover detail in the target panel, and shows the cursor-related prompts. It is the same component set, not a second HUD.

**Prompts:** context drives the list: real time (Attack or Order, Pause, Switch character, Follow, Stop); a terminal in reach (`Interact: <name>`, reusing `PlayerCommandInput.PromptInteractable`); paused (Resume, Order, Queue, Cancel, Cursor/Select); ability armed (Confirm, Cancel, Queue). At most six entries. Keyboard/mouse entries use the keyboard actions (`Commands/Command` for the mouse order button, since `Confirm`/`Attack` are pad actions); pad families use the pad actions. All text comes from `PromptResolver`, so Nintendo's swapped Confirm/Cancel, PlayStation and Generic labels follow the bindings. The list refreshes when `ActiveInputDevice.Family` changes (polled), with no restart. Prompt text is plain label text; no glyph art.

## 7. Intelligence and fog rules (critical)

- `HudSnapshotBuilder` is the only reader of hostile data. Hostile name, health, position, cover, count, target-ability preview and existence pass through `Knowledge` (`CanTarget` / `IsShown`) or `IntelligenceService.StateOfEnemy` / `TryLastKnown`.
- No hostile or kill counters. `Encounter.Hostiles` is iterated only to find observed ones and last-known marks; `transform.position` is read only for observed units, otherwise only the last-known point.
- Objective existence, title, count and position follow section 6. Developer truth view (F12) is display-only and does not change the HUD.
- Command queue and target lines never name an unshown target.
- Cursor and target cycling are untouched (they already use `Knowledge`).
- A PlayMode sweep under the Blind preset asserts that no unobserved hostile's name, health or position appears in any HUD text or mark, before and after a hostile is observed, lost and last-known.

## 8. Input: HUD requests and controller equivalents

| HUD element | Mouse | Request (existing path) | Controller equivalent |
| --- | --- | --- | --- |
| Squad card | click / Shift+click | `UnitSelection.Select` / `Toggle` (same as clicking the unit) | LB/RB switches control and selection; LT + Confirm on a unit adds to selection |
| Squad card | double-click | `ActiveCharacter.TakeControl(unit)` (new, Tab's rules) | LB/RB |
| Ability slot | click | `AbilityTargeting.Arm(slot)` (click again disarms) | RT + D-pad direction |
| Clear orders | click | `GroupOrders.Issue([unit], StopCommand)` | Stop (D-pad down) |
| Follow chip | click | `ActiveCharacter.ToggleFollow()` | Follow (D-pad up) |
| Pause chip | click | `TacticalPause.Toggle()` | Start / Menu / Options / + |

`PointerBlocker` keeps HUD panels from letting a click fall through to the world. The EventSystem has no navigation actions, so the HUD takes no focus. A test pins each row (mouse request and that the controller action exists in all four pad groups).

## 9. Debug separation

Developer overlay (hidden by default, F1): unit labels with AI state, cover and LOS, the control hints text, the ability debug lines, the mission debug panel and raw ids. Still keeps its own toggles: operative panel F9, truth view F12, map M. Mission seed and generation details live only in the overlay. World feedback views are not part of the overlay.

## 10. Regeneration

The HUD holds no per-mission reference between frames. Squad cards re-bind when the squad list signature (count plus unit identities) changes; the mission panel re-reads `director.Runtime` each frame (null while generating hides it). Target, queue and ability subjects are re-resolved every frame. `WorldMarkLayer` pools shrink or hide unused labels. Tests cover F6/F7-style regeneration, fog reset (`IntelligenceService.Clear`/`Begin`) and a squad member dying.

## 11. Testing

- EditMode: `HudText`, `HudPrompts` (every family; Nintendo bindings), objective-row and extraction mapping, command-step wording with unknown targets, snapshot builder with fakes where possible, input asset (`ToggleDebugOverlay`), `PointerBlocker`, asmdef reference.
- PlayMode (scene-level): HUD builds with correct panels at 1920×1080 and survives resolution changes; squad cards track health, selection, control and death; cooldown fill freezes under pause; queue reflects Append; follow and parked tags; objectives, vague titles and extraction states; observed, lost, last-known sequence; fog leak sweep; prompts change on device hot-switch (simulated Xbox, PlayStation, Nintendo, keyboard); every HUD request in section 8; a click over the HUD issues no world command and a click elsewhere still does; developer overlay toggle; regeneration leaves no stale card or label; no console errors.
- Existing suites must stay green (baselines: EditMode 992, PlayMode 849) except where a test is deliberately updated.

## 12. Documentation

- Decision 042 in `Docs/Decisions.md`: boundary, squad structure, queue presentation, intelligence restrictions, prompt integration, player versus developer overlay, input without focus mode.
- `Docs/Phase11.5-ManualTests.md`: the owner's manual sequence (mission start, cycle operatives, damage, abilities and cooldowns, pause, queued commands, follow and park, objectives and extraction, hidden objective, observe and lose an enemy, unknown enemy absence, recon, device switching, regeneration, 1920×1080, F1).

## 13. Known limits and risks to report

- Legacy `Text` and the built-in font: no icon glyphs, no portrait art.
- No controller focus mode; controller reaches HUD functions only through existing actions.
- Cover status shows only what `UnitCover` reliably reports.
- Walk/run, hit flash and other unit-art caveats are unrelated to this phase.
- Mouse hover resolution costs one raycast pass at 10 Hz (RaycastAll under fog).
- Edits to debug IMGUI views are early returns only; their text-building functions and tests are untouched.

## 14. Task outline (for the plan)

1. Seams and assembly: `Unity.ugui` reference, `PointerBlocker`, `DeveloperOverlay` + input action, `ActiveCharacter.TakeControl`.
2. Snapshot structs, `HudSources`, `HudText`, `HudPrompts` (pure, with tests).
3. Snapshot builder with intelligence rules.
4. `HudFactory`, `HudTheme`, `TacticalHud` root, canvas, EventSystem.
5. Status and objectives panels.
6. Squad panel.
7. Operative panel: controlled info, abilities, queue.
8. Prompt panel.
9. Target panel and world mark layer.
10. `HudRequests` and `HudPointerGate`, `PlayerCommandInput` gate.
11. Debug-overlay gating of the IMGUI views.
12. `HudSceneBuilder`, scene wiring, PlayMode scene tests.
13. Decision 042, manual tests, full-suite verification.
