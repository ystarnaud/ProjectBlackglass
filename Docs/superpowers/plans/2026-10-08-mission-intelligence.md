# Mission Intelligence (Phase 11) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Separate what a mission contains from what the player knows: a knowledge layer fed by exploration, friendly sight, a hackable camera network, a Recon Scan ability and hostile fire, that gates targeting, labels, cover markers, objectives and world presentation, with a truth-view for developers.

**Architecture:** A pure `MissionIntelligence` model (regions, enemies, devices, objectives) is fed through a small reveal API by an `IntelligenceService` (a scene MonoBehaviour that samples sources every 0.2 s of simulation time). Everything else (pointer resolver, cursor, abilities, companions, HUD, cover view, fog, hostile presenters) asks the service through optional serialized references; a null service or a fog-off mission means "everything known", so Phase 10 behaviour is unchanged. Regions come from the existing `MissionLayout` (rooms + corridor strips); security devices come from a new pure `SecurityPlan` drawn from its own salted RNG stream.

**Tech Stack:** Unity 6000.3.25f1, C#, Unity Input System, NavMesh (`Unity.AI.Navigation`), NUnit/Unity Test Framework. No new packages.

**Spec:** `Docs/superpowers/specs/2026-10-08-mission-intelligence-design.md` (read it first; this plan implements it).

## Global Constraints

- Flat namespace `Blackglass`; new runtime code in `Assets/_Project/Scripts/Intelligence/`; Editor-only code in `Assets/_Project/Editor/`; tests in `Assets/_Project/Tests/EditMode` and `Tests/PlayMode` (namespace `Blackglass.Tests`; PlayMode files wrapped in `#if UNITY_EDITOR ... #endif`, as the existing ones are).
- Simple, modular, readable C#; composition over inheritance; no giant managers; no global mutable state; no singletons; no new packages; tactical pause stays a first-class service; unit commands stay data; no game rules in input callbacks (CLAUDE.md).
- **Fog off is the code default and changes nothing.** `IntelligenceSettings.fogEnabled == false` and a `null` `IntelligenceService` reference both mean "everything known". Every consumer takes the service as an optional serialized reference (`[SerializeField] IntelligenceService intelligence;` plus an `internal SetIntelligence`) and goes through the static `Knowledge` helper.
- Intel never uses randomness. `SecurityPlacer` has its own salted stream (`SeededRandom.ForSecurity`); `MissionLayout.Hash` and `ObjectivePlan.Hash` must stay byte-identical. Pinned objective hash for seed 12345 is `18368419039347052969UL` (`ObjectivePlacerTests.Golden12345`).
- Never use `??` or `?.` on a `UnityEngine.Object` (fake-null); use `x != null`. Plain C# classes (`IntelligenceSettings`, `SecurityPlan`) may use them.
- EditMode has no `Awake`/`OnEnable`/`Update` on MonoBehaviours. Runtime colliders are invisible to raycasts until `Physics.SyncTransforms()`. NUnit enum collections use `Has.Member`.
- No friendly fire: area abilities still hit only the caster's hostiles. Enemy AI is not fogged and keeps its own perception.
- Defaults (exact values): `observationRange` 14 m, `cameraRange` 12 m, `cameraFov` 90 degrees (full angle), `cameraCount` 0, `cameraStaysLive` true, `exposureSeconds` 3, pass interval 0.2 s, camera mount height 2.6 m, Recon Scan range 18 m / radius 12 m / cooldown 25 s / `revealSeconds` 6 / no LOS needed / ground target.
- Ground abilities (including Recon Scan) may target any point within normal range rules, including undiscovered ground (spec decision).
- Do not begin Phase 12. Do not modify unrelated code. Do not commit or push anything except the per-task commits below, on branch `phase-11-intelligence`.
- Commit trailer on every commit: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

### Running tests (read before the first task)

Unity allows one instance per project: **the Unity Editor must be closed and only one test run may be active at a time.** Never run two `Tools/run-tests.sh` at once, and a reviewer must not run Unity while an implementer does.

- `Tools/run-tests.sh EditMode [filter]` / `Tools/run-tests.sh PlayMode [filter]` (from the repo root, bash). `EXIT=1` means a compile error (the script prints `error CS` lines), `EXIT=2` means test failures (it prints the failing names and messages), `EXIT=0` means everything passed. The filter is a Unity `-testFilter` string, e.g. `Blackglass.Tests.RegionMapTests`.
- Baselines measured on 2026-10-08 before any change (branch `phase-11-intelligence` at the spec commit): **EditMode 788/788 and PlayMode 729/729**, both `EXIT=0` (records in `.claude/scratchpad/baseline-edit.txt` and `baseline-play.txt`). Every task must keep earlier tests green; Task 14 compares the final totals with these.
- New `.cs` files need their `.meta` files: Unity generates them on the next import (the test run does it). Commit the `.meta` files together with the sources (`git add -A <paths>` includes them).

## Review Focus

Failure modes the spec implies but the obvious tests would not exercise; each has a test in the owning task.

1. **A closed wall must not reveal the room behind it, an open door must reveal only what the opening allows** (Task 6, `IntelligenceServiceGeometryTests`): range alone must never discover a region.
2. **A hostile that fires from beyond observation range is exposed, then becomes a last-known marker and stops being a target** (Task 6 `ExposureTests`; Task 8 for targeting).
3. **An unobserved hostile must leak through no channel**: pointer click, cursor snap, soft target, target cycling, Attack fallback, ability preview/`areaHits`, `TargetHealth`, companion assist, labels, hostile counts, attack lines, cover markers, debug text (Tasks 8, 9, 11).
4. **Regeneration (F6/F7) mid-mission, including during tactical pause, must leave no stale state**: no event subscribed to a destroyed hostile, no presenter calling a destroyed `Health`, no knowledge carried over; the new mission starts from its briefing (Task 6 `RegenerationTests`, Task 10).
5. **An enemy that dies while Observed or last-known must drop out cleanly** (no marker, no label, no exception) and a destroyed `Health` key in the exposure table must not throw (Task 3 and Task 6).
6. **Fog off must be byte-for-byte Phase 10 behaviour** (Task 13 runs the full suites with the original scene settings restored by the opt-out helper).

## File Structure

**Create** (`Assets/_Project/Scripts/Intelligence/`):

| File | Responsibility |
|---|---|
| `KnowledgeState.cs` | `enum KnowledgeState { Unknown, Discovered, Observed }` |
| `IntelligenceSettings.cs` | per-mission configuration, validation, presets, `MapKnowledge`, `ObjectiveKnowledge` |
| `RegionMap.cs` | pure: regions (rooms + corridors) from a `MissionLayout`, tile lookup, adjacency, sample points |
| `MissionIntelligence.cs` | pure model + reveal API (regions, enemies, devices, objectives) |
| `ObservationRules.cs` | pure range / cone / circle tests |
| `SecurityPlan.cs`, `SecurityPlacer.cs` | pure plan (hash) + deterministic placement |
| `CameraNetwork.cs` | `CameraSpec`, `CameraNetwork` (plain) |
| `IntelligenceService.cs` | the scene service: sources, passes, queries, `Begin`/`Clear`, `Scan` |
| `Knowledge.cs` | null-safe static helpers for consumers |
| `FogPresenter.cs`, `HostilePresenter.cs`, `DevicePresenter.cs` | world presentation |
| `IntelMapView.cs`, `IntelligenceDebugView.cs`, `IntelligenceDeveloperInput.cs` | overlay, truth/debug view, developer keys |

**Create** (other): `Assets/_Project/Editor/IntelligenceSceneBuilder.cs`; `Assets/_Project/Data/Abilities/ReconScan.asset` and three materials (made by the builder); `Docs/Phase11-ManualTests.md`; tests listed per task; `Tests/PlayMode/TestSupport/IntelRig.cs`, `IntelLayouts.cs`.

**Modify** (small, mechanical edits): `MissionSettings.cs`, `SeededRandom.cs`, `ObjectivePlacer.cs` (three helpers `static` -> `internal static`), `MissionNavigation.cs`, `MissionInteractable.cs`, `MissionContent.cs`, `MissionBuilder.cs` (`GeneratedMission.Security/CameraTerminal/Network`), `MissionDirector.cs`, `MissionSlots.cs` (`MissionSystems.intelligence`), `MissionSpawner.cs`, `MissionObjective.cs`, `EliminateHostilesObjective.cs`, `MissionHud.cs`, `MissionDebugView.cs`, `MissionGizmoView.cs`, `InteractableRegistry.cs`, `HostileTargets.cs`, `PointerTargetResolver.cs`, `TacticalCursor.cs`, `AbilityDefinition.cs`, `AbilityDescriptions.cs`, `AbilityTargeting.cs`, `UnitAbilities.cs`, `CompanionAI.cs`, `PlayerCommandInput.cs`, `PrototypeHud.cs`, `AttackLineView.cs`, `CoverView.cs`, `BlackglassControls.inputactions`, `Docs/Decisions.md`, the two procedural scene test files, and `Tests/PlayMode/TestSupport/MissionRig.cs`.

---

### Task 1: Knowledge state and intelligence settings

**Files:**
- Create: `Assets/_Project/Scripts/Intelligence/KnowledgeState.cs`, `Assets/_Project/Scripts/Intelligence/IntelligenceSettings.cs`
- Modify: `Assets/_Project/Scripts/Mission/MissionSettings.cs`
- Test: `Assets/_Project/Tests/EditMode/IntelligenceSettingsTests.cs`

**Interfaces:**
- Produces:
  - `enum KnowledgeState { Unknown, Discovered, Observed }`
  - `enum MapKnowledge { Full, Partial, None }`
  - `enum ObjectiveKnowledge { All, ExtractionAndTerminal, ExtractionOnly, None }`
  - `[Serializable] sealed class IntelligenceSettings` with public fields `bool fogEnabled; MapKnowledge map; ObjectiveKnowledge objectives; bool showUnknownObjectives; int enemyMarkersAtStart; float observationRange; int cameraCount; float cameraRange; float cameraFov; bool cameraStaysLive; float exposureSeconds`; `IntelligenceSettings Validated()`; `string Describe()`; statics `Full()`, `Blind()`, `LayoutKnown()`, `ObjectivesKnown()`, `Briefed()`, `const int PresetCount`, `Preset(int index)`, `PresetName(int index)`.
  - `MissionSettings.intelligence` (public field, `new IntelligenceSettings()` by default), deep-cloned by `Validated()`.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/IntelligenceSettingsTests.cs`:

```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class IntelligenceSettingsTests
    {
        [Test]
        public void Default_IsFogOffFullKnowledgeAndNoCameras()
        {
            var settings = new IntelligenceSettings();
            Assert.That(settings.fogEnabled, Is.False);
            Assert.That(settings.map, Is.EqualTo(MapKnowledge.Full));
            Assert.That(settings.objectives, Is.EqualTo(ObjectiveKnowledge.All));
            Assert.That(settings.cameraCount, Is.EqualTo(0));
            Assert.That(settings.observationRange, Is.EqualTo(14f));
            Assert.That(settings.cameraRange, Is.EqualTo(12f));
            Assert.That(settings.cameraFov, Is.EqualTo(90f));
            Assert.That(settings.cameraStaysLive, Is.True);
            Assert.That(settings.exposureSeconds, Is.EqualTo(3f));
        }

        [Test]
        public void Validated_ClampsEveryNumber_AndLeavesTheOriginalAlone()
        {
            var original = new IntelligenceSettings
            {
                observationRange = 1000f, cameraCount = 99, cameraRange = 0f, cameraFov = 500f, exposureSeconds = -4f, enemyMarkersAtStart = 99,
            };
            var copy = original.Validated();
            Assert.That(copy, Is.Not.SameAs(original));
            Assert.That(copy.observationRange, Is.EqualTo(40f));
            Assert.That(copy.cameraCount, Is.EqualTo(6));
            Assert.That(copy.cameraRange, Is.EqualTo(4f));
            Assert.That(copy.cameraFov, Is.EqualTo(180f));
            Assert.That(copy.exposureSeconds, Is.EqualTo(0f));
            Assert.That(copy.enemyMarkersAtStart, Is.EqualTo(8));
            Assert.That(original.cameraCount, Is.EqualTo(99), "the original is untouched");

            var low = new IntelligenceSettings { observationRange = 0f, cameraFov = 1f }.Validated();
            Assert.That(low.observationRange, Is.EqualTo(4f));
            Assert.That(low.cameraFov, Is.EqualTo(30f));
        }

        [Test]
        public void Presets_AreFiveAndNamed_AndOnlyFullHasFogOff()
        {
            Assert.That(IntelligenceSettings.PresetCount, Is.EqualTo(5));
            for (var i = 0; i < IntelligenceSettings.PresetCount; i++)
            {
                Assert.That(IntelligenceSettings.PresetName(i), Is.Not.Empty);
                Assert.That(IntelligenceSettings.Preset(i).fogEnabled, Is.EqualTo(i != 0), IntelligenceSettings.PresetName(i));
            }
            Assert.That(IntelligenceSettings.Preset(0).Describe(), Is.EqualTo(new IntelligenceSettings().Describe()));
        }

        [Test]
        public void Preset_WrapsAroundAndBlindIsFullFogWithCameras()
        {
            Assert.That(IntelligenceSettings.Preset(5).fogEnabled, Is.False, "index 5 wraps to 0");
            var blind = IntelligenceSettings.Blind();
            Assert.That(blind.map, Is.EqualTo(MapKnowledge.None));
            Assert.That(blind.objectives, Is.EqualTo(ObjectiveKnowledge.ExtractionOnly));
            Assert.That(blind.cameraCount, Is.EqualTo(3));
            Assert.That(IntelligenceSettings.LayoutKnown().map, Is.EqualTo(MapKnowledge.Full));
            Assert.That(IntelligenceSettings.LayoutKnown().objectives, Is.EqualTo(ObjectiveKnowledge.None));
            Assert.That(IntelligenceSettings.ObjectivesKnown().objectives, Is.EqualTo(ObjectiveKnowledge.All));
            Assert.That(IntelligenceSettings.ObjectivesKnown().map, Is.EqualTo(MapKnowledge.None));
            var briefed = IntelligenceSettings.Briefed();
            Assert.That(briefed.map, Is.EqualTo(MapKnowledge.Partial));
            Assert.That(briefed.objectives, Is.EqualTo(ObjectiveKnowledge.ExtractionAndTerminal));
            Assert.That(briefed.enemyMarkersAtStart, Is.EqualTo(1));
        }

        [Test]
        public void MissionSettingsValidated_DeepClonesTheIntelligenceSettings()
        {
            var original = new MissionSettings { intelligence = IntelligenceSettings.Blind() };
            var copy = original.Validated();
            Assert.That(copy.intelligence, Is.Not.SameAs(original.intelligence));
            copy.intelligence.cameraCount = 1;
            Assert.That(original.intelligence.cameraCount, Is.EqualTo(3));
        }

        [Test]
        public void MissionSettingsValidated_ReplacesANullIntelligenceWithDefaults()
        {
            var original = new MissionSettings { intelligence = null };
            Assert.That(original.Validated().intelligence.fogEnabled, Is.False);
        }

        [Test]
        public void MissionSettingsDescribe_IsUnchangedWhileFogAndCamerasAreOff_AndNamesThemWhenOn()
        {
            Assert.That(new MissionSettings().Describe(), Does.Not.Contain("intel"));
            var text = new MissionSettings { intelligence = IntelligenceSettings.Blind() }.Describe();
            Assert.That(text, Does.Contain("intel=").And.Contain("Blind").Or.Contain("fog=True"));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.IntelligenceSettingsTests`
Expected: `EXIT=1` with `error CS0246 ... IntelligenceSettings` (compile error: the types do not exist yet).

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/Intelligence/KnowledgeState.cs`:

```csharp
namespace Blackglass
{
    /// <summary>
    /// What the player knows about one thing. Regions and devices: Discovered is known and permanent, Observed is in sight
    /// right now. Enemies: Discovered means last known (a marker at the last seen position), Observed is live. One enum for
    /// all three, so every consumer asks the same question.
    /// </summary>
    public enum KnowledgeState
    {
        Unknown,
        Discovered,
        Observed,
    }
}
```

Create `Assets/_Project/Scripts/Intelligence/IntelligenceSettings.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>How much of the layout the player starts with.</summary>
    public enum MapKnowledge
    {
        /// <summary>Every region is known from the start.</summary>
        Full,
        /// <summary>The friendly room and its corridors, plus the extraction room.</summary>
        Partial,
        /// <summary>Nothing but what the squad can see.</summary>
        None,
    }

    /// <summary>Which objectives the player starts knowing about (an unknown objective still exists and still completes).</summary>
    public enum ObjectiveKnowledge
    {
        All,
        /// <summary>Extraction and the Interact objective; the elimination objective is learned in play.</summary>
        ExtractionAndTerminal,
        ExtractionOnly,
        None,
    }

    /// <summary>
    /// Per-mission battlefield-uncertainty configuration. Plain serializable data inside MissionSettings. The default is
    /// "nothing hidden" (fog off), so existing scenes and tests behave exactly as before; a preset opts a mission in.
    /// </summary>
    [Serializable]
    public sealed class IntelligenceSettings
    {
        public const int PresetCount = 5;

        /// <summary>False: everything is known and nothing is hidden; the rest of the settings (except cameras) is ignored.</summary>
        public bool fogEnabled;
        public MapKnowledge map = MapKnowledge.Full;
        public ObjectiveKnowledge objectives = ObjectiveKnowledge.All;
        /// <summary>List an unknown objective by its vague title ("Locate the data terminal") instead of hiding it.</summary>
        public bool showUnknownObjectives = true;
        /// <summary>This many hostiles (spawn order) start as last-known markers.</summary>
        public int enemyMarkersAtStart;
        /// <summary>How far a friendly sees (360 degrees, needs line of sight). Above the 12 m enemy detection range by default.</summary>
        public float observationRange = 14f;
        /// <summary>Security cameras (and one camera-control terminal when above zero).</summary>
        public int cameraCount;
        public float cameraRange = 12f;
        /// <summary>The full field-of-view angle of a camera, degrees.</summary>
        public float cameraFov = 90f;
        /// <summary>True: hacked cameras keep observing live; false: the hack reveals what they cover once.</summary>
        public bool cameraStaysLive = true;
        /// <summary>How long a hostile that fires stays observed, scaled seconds.</summary>
        public float exposureSeconds = 3f;

        /// <summary>A clamped copy; the original is untouched.</summary>
        public IntelligenceSettings Validated()
        {
            var copy = (IntelligenceSettings)MemberwiseClone();
            copy.enemyMarkersAtStart = Mathf.Clamp(enemyMarkersAtStart, 0, 8);
            copy.observationRange = Mathf.Clamp(observationRange, 4f, 40f);
            copy.cameraCount = Mathf.Clamp(cameraCount, 0, 6);
            copy.cameraRange = Mathf.Clamp(cameraRange, 4f, 40f);
            copy.cameraFov = Mathf.Clamp(cameraFov, 30f, 180f);
            copy.exposureSeconds = Mathf.Clamp(exposureSeconds, 0f, 10f);
            return copy;
        }

        /// <summary>One line with every setting.</summary>
        public string Describe() =>
            FormattableString.Invariant($"fog={fogEnabled} map={map} objectives={objectives} listUnknown={showUnknownObjectives} ") +
            FormattableString.Invariant($"markers={enemyMarkersAtStart} sight={observationRange:0.#} cameras={cameraCount} camRange={cameraRange:0.#} ") +
            FormattableString.Invariant($"camFov={cameraFov:0.#} live={cameraStaysLive} expose={exposureSeconds:0.#}");

        public static IntelligenceSettings Full() => new IntelligenceSettings();

        /// <summary>No map, only the extraction known, hostiles hidden, three cameras and their control terminal.</summary>
        public static IntelligenceSettings Blind() => new IntelligenceSettings
        {
            fogEnabled = true, map = MapKnowledge.None, objectives = ObjectiveKnowledge.ExtractionOnly, cameraCount = 3,
        };

        /// <summary>The whole layout known; the objectives and hostiles are not.</summary>
        public static IntelligenceSettings LayoutKnown() => new IntelligenceSettings
        {
            fogEnabled = true, map = MapKnowledge.Full, objectives = ObjectiveKnowledge.None,
        };

        /// <summary>The objectives known; the layout and hostiles are not.</summary>
        public static IntelligenceSettings ObjectivesKnown() => new IntelligenceSettings
        {
            fogEnabled = true, map = MapKnowledge.None, objectives = ObjectiveKnowledge.All,
        };

        /// <summary>A briefing: partial layout, extraction and the terminal known, one enemy marker.</summary>
        public static IntelligenceSettings Briefed() => new IntelligenceSettings
        {
            fogEnabled = true, map = MapKnowledge.Partial, objectives = ObjectiveKnowledge.ExtractionAndTerminal, enemyMarkersAtStart = 1,
        };

        /// <summary>Preset by index; any index wraps around.</summary>
        public static IntelligenceSettings Preset(int index)
        {
            switch (((index % PresetCount) + PresetCount) % PresetCount)
            {
                case 1: return Blind();
                case 2: return LayoutKnown();
                case 3: return ObjectivesKnown();
                case 4: return Briefed();
                default: return Full();
            }
        }

        public static string PresetName(int index)
        {
            switch (((index % PresetCount) + PresetCount) % PresetCount)
            {
                case 1: return "Blind";
                case 2: return "Layout known";
                case 3: return "Objectives known";
                case 4: return "Briefed";
                default: return "Full knowledge";
            }
        }
    }
}
```

Edit `Assets/_Project/Scripts/Mission/MissionSettings.cs`:

(a) after the `hackTerminal` field add:

```csharp
        /// <summary>Battlefield uncertainty and security cameras (decision 037). The default hides nothing.</summary>
        public IntelligenceSettings intelligence = new IntelligenceSettings();
```

(b) in `Validated()` before `return copy;` add (the line after the `hackTerminal` fix-up):

```csharp
            // MemberwiseClone shares nested objects: clone the intelligence settings so a validated copy is independent.
            copy.intelligence = (intelligence ?? new IntelligenceSettings()).Validated();
```

(c) replace the `Describe()` body so it appends the intelligence line only when something is on (`Describe_NamesTheSeedAndEverySetting` and `Describe_AlsoNamesTheObjectiveSettings` stay valid):

```csharp
        public string Describe()
        {
            var text =
                FormattableString.Invariant($"seed={seed} grid={gridColumns}x{gridRows} cell={cellSize} rooms={roomCount} corridor={corridorWidth} ") +
                FormattableString.Invariant($"loops={extraLoops} baffles={bafflesPerRoom} lowCover={lowCoverDensity:0.##} friendlies={friendlyCount} ") +
                FormattableString.Invariant($"hostiles={hostileCount} separation={minTeamSeparation:0.##} attempts={maxAttempts} ") +
                FormattableString.Invariant($"guards={guardCount} interact={interactionSeconds:0.##} extractionUnits={extractionUnits} eliminate={eliminateHostiles} hack={hackTerminal}");
            if (intelligence != null && (intelligence.fogEnabled || intelligence.cameraCount > 0))
                text += " intel=[" + intelligence.Describe() + "]";
            return text;
        }
```

Adjust the new test's last assertion to match: replace `Does.Contain("intel=").And.Contain("Blind").Or.Contain("fog=True")` with `Does.Contain("intel=[").And.Contain("fog=True")`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.IntelligenceSettingsTests` then `Tools/run-tests.sh EditMode Blackglass.Tests.MissionSettingsTests`
Expected: both `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Intelligence Assets/_Project/Scripts/Mission/MissionSettings.cs Assets/_Project/Tests/EditMode/IntelligenceSettingsTests.cs Assets/_Project/Tests/EditMode/IntelligenceSettingsTests.cs.meta
git commit -m "Add KnowledgeState and per-mission IntelligenceSettings with presets" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Region map

**Files:**
- Create: `Assets/_Project/Scripts/Intelligence/RegionMap.cs`
- Test: `Assets/_Project/Tests/EditMode/RegionMapTests.cs`

**Interfaces:**
- Consumes: `MissionLayout` (`Rooms`, `Connections`, `Width`, `Height`, `IsFloor`, `ToWorld`, `TileCenter`), `MissionConnection.Strip`.
- Produces:
  - `enum RegionKind { Room, Corridor }`; `readonly struct MapRegion { int Index; RegionKind Kind; RectInt Rect; }`
  - `sealed class RegionMap` with: `RegionMap(MissionLayout layout)`, `int Count`, `int RoomCount`, `MapRegion this[int i]`, `int RegionOfTile(int x, int y)` (-1 none), `int RegionAt(Vector3 world)` (-1 none), `IReadOnlyList<int> Neighbours(int region)`, `IReadOnlyList<Vector3> SamplePoints(int region)`, `void RegionsInCircle(Vector3 centre, float radius, List<int> into)` (clears `into` first), `void RegionsAdjacentTo(RectInt footprint, List<int> into)` (clears first; regions whose rect inflated by one tile overlaps the footprint), `Rect WorldRect(int region)` (x/y = world x/z), `const float SampleHeight = 0.5f`.
  - Region ids: rooms keep their layout index (`0..RoomCount-1`), corridors follow (`RoomCount + connectionIndex`).

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/RegionMapTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class RegionMapTests
    {
        // Two 6x6 rooms and a 6x3 corridor between them on a 20x10 grid. Tile (x, y) is world (x - 10, y - 5).
        static readonly RectInt RoomA = new RectInt(1, 1, 6, 6);
        static readonly RectInt RoomB = new RectInt(13, 1, 6, 6);
        static readonly RectInt Strip = new RectInt(7, 2, 6, 3);

        static MissionLayout TwoRooms()
        {
            const int width = 20;
            const int height = 10;
            var floor = new bool[width * height];
            foreach (var rect in new[] { RoomA, RoomB, Strip })
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        floor[y * width + x] = true;
            return new MissionLayout(1, 1, width, height, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), RoomA), new MissionRoom(1, new Vector2Int(1, 0), RoomB) },
                Connections = new[] { new MissionConnection(0, 1, Strip) },
                FloorRects = new[] { RoomA, RoomB, Strip },
                FriendlyRoom = 0,
            };
        }

        [Test]
        public void Regions_AreRoomsThenCorridors_WithRoomIdsEqualToLayoutIndices()
        {
            var map = new RegionMap(TwoRooms());
            Assert.That(map.Count, Is.EqualTo(3));
            Assert.That(map.RoomCount, Is.EqualTo(2));
            Assert.That(map[0].Kind, Is.EqualTo(RegionKind.Room));
            Assert.That(map[1].Rect, Is.EqualTo(RoomB));
            Assert.That(map[2].Kind, Is.EqualTo(RegionKind.Corridor));
            Assert.That(map[2].Rect, Is.EqualTo(Strip));
        }

        [Test]
        public void RegionOfTile_FindsTheRegion_AndMinusOneForVoidAndOutOfBounds()
        {
            var map = new RegionMap(TwoRooms());
            Assert.That(map.RegionOfTile(3, 3), Is.EqualTo(0));
            Assert.That(map.RegionOfTile(15, 3), Is.EqualTo(1));
            Assert.That(map.RegionOfTile(9, 3), Is.EqualTo(2));
            Assert.That(map.RegionOfTile(9, 8), Is.EqualTo(-1), "void tile");
            Assert.That(map.RegionOfTile(-1, 0), Is.EqualTo(-1));
            Assert.That(map.RegionOfTile(0, 10), Is.EqualTo(-1));
        }

        [Test]
        public void RegionAt_ConvertsWorldToTiles()
        {
            var layout = TwoRooms();
            var map = new RegionMap(layout);
            Assert.That(map.RegionAt(layout.TileCenter(new Vector2Int(3, 3))), Is.EqualTo(0));
            Assert.That(map.RegionAt(layout.TileCenter(new Vector2Int(16, 4))), Is.EqualTo(1));
            Assert.That(map.RegionAt(new Vector3(0.2f, 1f, -1.5f)), Is.EqualTo(2), "world (0.2, -1.5) is tile (10, 3)");
            Assert.That(map.RegionAt(new Vector3(-50f, 0f, 0f)), Is.EqualTo(-1));
        }

        [Test]
        public void Neighbours_JoinARoomToItsCorridors_AndACorridorToItsTwoRooms()
        {
            var map = new RegionMap(TwoRooms());
            Assert.That(map.Neighbours(0), Is.EquivalentTo(new[] { 2 }));
            Assert.That(map.Neighbours(1), Is.EquivalentTo(new[] { 2 }));
            Assert.That(map.Neighbours(2), Is.EquivalentTo(new[] { 0, 1 }));
        }

        [Test]
        public void SamplePoints_AreFloorTilesAtSampleHeight_AndEveryRegionHasSome()
        {
            var layout = TwoRooms();
            var map = new RegionMap(layout);
            for (var region = 0; region < map.Count; region++)
            {
                var samples = map.SamplePoints(region);
                Assert.That(samples.Count, Is.InRange(1, 16), $"region {region}");
                foreach (var point in samples)
                {
                    Assert.That(point.y, Is.EqualTo(RegionMap.SampleHeight));
                    Assert.That(map.RegionAt(point), Is.EqualTo(region));
                }
            }
        }

        [Test]
        public void SamplePoints_OfA6x6Room_AreFourOnAThreeTileGrid()
        {
            var map = new RegionMap(TwoRooms());
            Assert.That(map.SamplePoints(0).Count, Is.EqualTo(4));
        }

        [Test]
        public void RegionsInCircle_ReturnsRegionsWhoseRectTouchesTheCircle()
        {
            var layout = TwoRooms();
            var map = new RegionMap(layout);
            var found = new List<int>();
            map.RegionsInCircle(layout.TileCenter(new Vector2Int(3, 3)), 2f, found);
            Assert.That(found, Is.EquivalentTo(new[] { 0 }));
            map.RegionsInCircle(layout.TileCenter(new Vector2Int(3, 3)), 8f, found);
            Assert.That(found, Is.EquivalentTo(new[] { 0, 2 }), "the corridor starts 4 m east of the room's far wall; room B is 10 m away");
            map.RegionsInCircle(layout.TileCenter(new Vector2Int(3, 3)), 30f, found);
            Assert.That(found, Is.EquivalentTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void RegionsAdjacentTo_FindsTheRegionsAWallFootprintStandsBeside()
        {
            var map = new RegionMap(TwoRooms());
            var found = new List<int>();
            map.RegionsAdjacentTo(new RectInt(7, 1, 6, 1), found);   // the wall row above... below the corridor, between the rooms
            Assert.That(found, Is.EquivalentTo(new[] { 0, 1, 2 }));
            map.RegionsAdjacentTo(new RectInt(0, 0, 20, 1), found);   // the grid's bottom row: only room tiles in row 1 touch it
            Assert.That(found, Is.EquivalentTo(new[] { 0, 1 }));
            map.RegionsAdjacentTo(new RectInt(9, 8, 2, 1), found);
            Assert.That(found, Is.Empty);
        }

        [Test]
        public void WorldRect_IsTheRegionRectInWorldXz()
        {
            var layout = TwoRooms();
            var map = new RegionMap(layout);
            var rect = map.WorldRect(0);
            Assert.That(rect.xMin, Is.EqualTo(-9f));
            Assert.That(rect.xMax, Is.EqualTo(-3f));
            Assert.That(rect.yMin, Is.EqualTo(-4f));
            Assert.That(rect.yMax, Is.EqualTo(2f));
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(12345)]
        [TestCase(31)]
        public void OnGeneratedLayouts_EveryFloorTileBelongsToExactlyOneRegion_AndEveryRegionHasSamples(int seed)
        {
            var settings = new MissionSettings { seed = seed }.Validated();
            MissionLayout layout = null;
            for (var attempt = 1; attempt <= settings.maxAttempts && layout == null; attempt++)
                if (MissionGenerator.TryAttempt(settings, attempt, out var candidate, out _))
                    layout = candidate;
            Assert.That(layout, Is.Not.Null, $"seed {seed} produced no layout");

            var map = new RegionMap(layout);
            for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                    if (layout.IsFloor(x, y))
                        Assert.That(map.RegionOfTile(x, y), Is.GreaterThanOrEqualTo(0), $"floor tile ({x},{y}) has no region");
            for (var region = 0; region < map.Count; region++)
                Assert.That(map.SamplePoints(region), Is.Not.Empty, $"region {region}");
            for (var i = 0; i < layout.Rooms.Count; i++)
                Assert.That(layout.Rooms[i].Index, Is.EqualTo(i), "region ids rely on Rooms[i].Index == i");
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.RegionMapTests`
Expected: `EXIT=1`, `error CS0246 ... RegionMap`.

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/Intelligence/RegionMap.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public enum RegionKind
    {
        Room,
        Corridor,
    }

    /// <summary>One discovery unit: a room, or the corridor strip joining two rooms.</summary>
    public readonly struct MapRegion
    {
        public MapRegion(int index, RegionKind kind, RectInt rect)
        {
            Index = index;
            Kind = kind;
            Rect = rect;
        }

        public int Index { get; }
        public RegionKind Kind { get; }
        public RectInt Rect { get; }
    }

    /// <summary>
    /// The regions of a generated mission, derived from its MissionLayout (rooms first, ids equal to the layout's room
    /// indices, then one corridor per connection): the unit of discovery. Pure and immutable; it answers "which region is this
    /// tile or point in", "which regions touch this circle or wall footprint", "which regions border this one" and "where do I
    /// aim sight rays to see this region" (a coarse grid of floor sample points). It is not a second level representation: it
    /// only indexes the rectangles the layout already has.
    /// </summary>
    public sealed class RegionMap
    {
        /// <summary>Sample points float this high above the floor, so a ray to one ends at about a prone unit's height.</summary>
        public const float SampleHeight = 0.5f;
        const int SampleStep = 3;

        readonly MissionLayout layout;
        readonly MapRegion[] regions;
        readonly int[] tileRegion;
        readonly List<int>[] neighbours;
        readonly Vector3[][] samples;

        public RegionMap(MissionLayout layout)
        {
            this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
            var rooms = layout.Rooms;
            var connections = layout.Connections;
            RoomCount = rooms.Count;
            regions = new MapRegion[rooms.Count + connections.Count];
            for (var i = 0; i < rooms.Count; i++)
                regions[i] = new MapRegion(i, RegionKind.Room, rooms[i].Rect);
            for (var j = 0; j < connections.Count; j++)
                regions[rooms.Count + j] = new MapRegion(rooms.Count + j, RegionKind.Corridor, connections[j].Strip);

            tileRegion = new int[layout.Width * layout.Height];
            for (var i = 0; i < tileRegion.Length; i++)
                tileRegion[i] = -1;
            // Corridors first, rooms second: a tile in both belongs to the room.
            for (var i = regions.Length - 1; i >= 0; i--)
                Paint(regions[i]);

            neighbours = new List<int>[regions.Length];
            for (var i = 0; i < neighbours.Length; i++)
                neighbours[i] = new List<int>();
            for (var j = 0; j < connections.Count; j++)
            {
                var corridor = rooms.Count + j;
                Link(corridor, connections[j].RoomA);
                Link(corridor, connections[j].RoomB);
            }

            samples = new Vector3[regions.Length][];
            for (var i = 0; i < regions.Length; i++)
                samples[i] = BuildSamples(regions[i].Rect);
        }

        public int Count => regions.Length;
        public int RoomCount { get; }
        public MapRegion this[int index] => regions[index];

        public int RegionOfTile(int x, int y) =>
            x < 0 || y < 0 || x >= layout.Width || y >= layout.Height ? -1 : tileRegion[y * layout.Width + x];

        public int RegionAt(Vector3 world) =>
            RegionOfTile(Mathf.FloorToInt(world.x + layout.Width * 0.5f), Mathf.FloorToInt(world.z + layout.Height * 0.5f));

        public IReadOnlyList<int> Neighbours(int region) => neighbours[region];

        public IReadOnlyList<Vector3> SamplePoints(int region) => samples[region];

        /// <summary>The region's rectangle in world space (x and y of the Rect are world x and z).</summary>
        public Rect WorldRect(int region)
        {
            var rect = regions[region].Rect;
            var min = layout.ToWorld(rect.xMin, rect.yMin);
            var max = layout.ToWorld(rect.xMax, rect.yMax);
            return Rect.MinMaxRect(min.x, min.z, max.x, max.z);
        }

        /// <summary>Clears `into` and fills it with every region whose rectangle touches the flat circle.</summary>
        public void RegionsInCircle(Vector3 centre, float radius, List<int> into)
        {
            into.Clear();
            for (var i = 0; i < regions.Length; i++)
            {
                var rect = WorldRect(i);
                var dx = centre.x - Mathf.Clamp(centre.x, rect.xMin, rect.xMax);
                var dz = centre.z - Mathf.Clamp(centre.z, rect.yMin, rect.yMax);
                if (dx * dx + dz * dz <= radius * radius)
                    into.Add(i);
            }
        }

        /// <summary>Clears `into` and fills it with every region whose rectangle, grown by one tile, overlaps the footprint (a wall box).</summary>
        public void RegionsAdjacentTo(RectInt footprint, List<int> into)
        {
            into.Clear();
            for (var i = 0; i < regions.Length; i++)
            {
                var r = regions[i].Rect;
                var grown = new RectInt(r.x - 1, r.y - 1, r.width + 2, r.height + 2);
                if (grown.Overlaps(footprint))
                    into.Add(i);
            }
        }

        void Paint(MapRegion region)
        {
            var rect = region.Rect;
            for (var y = Mathf.Max(0, rect.yMin); y < Mathf.Min(layout.Height, rect.yMax); y++)
                for (var x = Mathf.Max(0, rect.xMin); x < Mathf.Min(layout.Width, rect.xMax); x++)
                    tileRegion[y * layout.Width + x] = region.Index;
        }

        void Link(int a, int b)
        {
            if (b < 0 || b >= neighbours.Length)
                return;
            if (!neighbours[a].Contains(b))
                neighbours[a].Add(b);
            if (!neighbours[b].Contains(a))
                neighbours[b].Add(a);
        }

        // Floor tile centres on a three-tile grid inside the rect (offset one tile from the edge), or the centre tile for a
        // rect too small for the grid.
        Vector3[] BuildSamples(RectInt rect)
        {
            var list = new List<Vector3>();
            for (var y = rect.yMin + SampleStep / 2; y < rect.yMax; y += SampleStep)
                for (var x = rect.xMin + SampleStep / 2; x < rect.xMax; x += SampleStep)
                    AddSample(list, x, y);
            if (list.Count == 0)
                AddSample(list, rect.xMin + rect.width / 2, rect.yMin + rect.height / 2);
            return list.ToArray();
        }

        void AddSample(List<Vector3> list, int x, int y)
        {
            if (layout.IsFloor(x, y))
                list.Add(layout.TileCenter(new Vector2Int(x, y)) + Vector3.up * SampleHeight);
        }
    }
}
```

Note for the implementer: `Overlaps` on `RectInt` is exclusive-edge (rects that only share an edge do not overlap), which is what `RegionsAdjacentTo` relies on together with the one-tile growth. If the 6x6-room sample count test (`4`) fails because of the offset arithmetic (`rect.xMin + 1`, then `+3`: tiles 2 and 5 for a rect starting at 1 and 6 wide → 2 columns x 2 rows = 4), the code above is the intended behaviour; fix the test, not the grid, only if the arithmetic in the test comment is wrong.

- [ ] **Step 4: Run to verify pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.RegionMapTests`
Expected: `EXIT=0`. If `OnGeneratedLayouts_EveryFloorTileBelongsToExactlyOneRegion...` fails with a floor tile that has no region, the layout has floor outside rooms and strips: stop and report it (the region model needs a third region kind); do not weaken the test.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Intelligence/RegionMap.cs Assets/_Project/Scripts/Intelligence/RegionMap.cs.meta Assets/_Project/Tests/EditMode/RegionMapTests.cs Assets/_Project/Tests/EditMode/RegionMapTests.cs.meta
git commit -m "Add RegionMap: rooms and corridors of a layout as discovery regions" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Knowledge model and observation rules

**Files:**
- Create: `Assets/_Project/Scripts/Intelligence/MissionIntelligence.cs`, `Assets/_Project/Scripts/Intelligence/ObservationRules.cs`
- Test: `Assets/_Project/Tests/EditMode/MissionIntelligenceTests.cs`, `Assets/_Project/Tests/EditMode/ObservationRulesTests.cs`

**Interfaces:**
- Consumes: `RegionMap` (Task 2), `KnowledgeState`, `ObjectiveKnowledge` (Task 1), `MissionObjective` (`Type`, `IsKnown`, `SetKnown`, `HasTarget`, `TargetPosition`), `EliminateHostilesObjective` (Task 7 adds `ShowCounts`; this task refers to it, so Task 3 adds the property too, see Step 3).
- Produces: `sealed class MissionIntelligence`:
  - `MissionIntelligence(RegionMap map, int deviceCount, bool open = false)`; `bool IsOpen`; `RegionMap Map`; `event Action Changed`; `int Version`.
  - Regions: `KnowledgeState StateOfRegion(int)`, `KnowledgeState StateOfPoint(Vector3)` (Unknown outside every region), `bool RevealRegion(int)` (true when it changed anything), `int RevealArea(Vector3 centre, float radius)` (regions newly discovered).
  - Pass: `BeginPass()`, `MarkRegionObserved(int)`, `ObserveEnemy(Health, Vector3)`, `EndPass()`.
  - Enemies: `TrackEnemy(Health)`, `KnowledgeState StateOfEnemy(Health)`, `bool IsObserved(Health)` (true for open models and untracked units), `bool TryGetLastKnown(Health, out Vector3)`, `MarkLastKnown(Health, Vector3)`, `bool AnyEnemyEverObserved`.
  - Devices: `int DeviceCount`, `KnowledgeState StateOfDevice(int)`, `bool RevealDevice(int)`.
  - Objectives: `BindObjectives(IReadOnlyList<MissionObjective>, ObjectiveKnowledge)`, `RevealObjective(MissionObjective)`.
- Produces: `static class ObservationRules`: `bool InCircle(Vector3 centre, float radius, Vector3 point)`, `bool InCone(Vector3 origin, Vector3 forward, float halfAngleDegrees, float range, Vector3 point)`; both use flat (XZ) distance and a flat angle.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/ObservationRulesTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ObservationRulesTests
    {
        [Test]
        public void InCircle_IsFlatAndInclusive()
        {
            Assert.That(ObservationRules.InCircle(Vector3.zero, 5f, new Vector3(3f, 9f, 4f)), Is.True, "height is ignored; 5 m exactly is in");
            Assert.That(ObservationRules.InCircle(Vector3.zero, 5f, new Vector3(3f, 0f, 4.1f)), Is.False);
        }

        [TestCase(0f, 5f, true)]
        [TestCase(44f, 5f, true)]
        [TestCase(46f, 5f, false)]
        [TestCase(0f, 12.5f, false)]
        [TestCase(180f, 5f, false)]
        public void InCone_NeedsRangeAndAnAngleWithinTheHalfAngle(float degreesOffAxis, float distance, bool expected)
        {
            var direction = Quaternion.Euler(0f, degreesOffAxis, 0f) * Vector3.forward;
            var point = new Vector3(1f, 2.6f, 1f) + direction * distance;
            Assert.That(ObservationRules.InCone(new Vector3(1f, 2.6f, 1f), Vector3.forward, 45f, 12f, point), Is.EqualTo(expected));
        }

        [Test]
        public void InCone_ForwardWithAHeightComponentIsFlattened()
        {
            Assert.That(ObservationRules.InCone(Vector3.zero, new Vector3(0f, -0.5f, 1f), 45f, 12f, new Vector3(0f, 0f, 6f)), Is.True);
        }

        [Test]
        public void InCone_AtTheOriginItselfIsIn()
        {
            Assert.That(ObservationRules.InCone(Vector3.zero, Vector3.forward, 45f, 12f, new Vector3(0f, 1f, 0f)), Is.True);
        }
    }
}
```

Create `Assets/_Project/Tests/EditMode/MissionIntelligenceTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionIntelligenceTests
    {
        // Same two-room layout as RegionMapTests: rooms 0 and 1, corridor 2. Tile (x, y) is world (x - 10, y - 5).
        static MissionLayout TwoRooms()
        {
            const int width = 20;
            const int height = 10;
            var floor = new bool[width * height];
            var a = new RectInt(1, 1, 6, 6);
            var b = new RectInt(13, 1, 6, 6);
            var strip = new RectInt(7, 2, 6, 3);
            foreach (var rect in new[] { a, b, strip })
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        floor[y * width + x] = true;
            return new MissionLayout(1, 1, width, height, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), a), new MissionRoom(1, new Vector2Int(1, 0), b) },
                Connections = new[] { new MissionConnection(0, 1, strip) },
                FriendlyRoom = 0,
            };
        }

        readonly List<GameObject> created = new List<GameObject>();
        MissionLayout layout;
        RegionMap map;
        MissionIntelligence intel;

        [SetUp]
        public void SetUp()
        {
            layout = TwoRooms();
            map = new RegionMap(layout);
            intel = new MissionIntelligence(map, deviceCount: 2);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                Object.DestroyImmediate(go);
            created.Clear();
        }

        Health Enemy(Vector3 at)
        {
            var go = new GameObject("Enemy");
            go.transform.position = at;
            created.Add(go);
            var health = go.AddComponent<Health>();
            health.Initialize(50);
            return health;
        }

        [Test]
        public void EverythingStartsUnknown()
        {
            Assert.That(intel.IsOpen, Is.False);
            for (var r = 0; r < map.Count; r++)
                Assert.That(intel.StateOfRegion(r), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.StateOfDevice(0), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.StateOfPoint(layout.TileCenter(new Vector2Int(3, 3))), Is.EqualTo(KnowledgeState.Unknown));
        }

        [Test]
        public void RevealRegion_IsPermanent_AndReportsWhetherItChangedAnything()
        {
            Assert.That(intel.RevealRegion(1), Is.True);
            Assert.That(intel.RevealRegion(1), Is.False);
            Assert.That(intel.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered));
            intel.BeginPass();
            intel.EndPass();
            Assert.That(intel.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "a pass that does not see it leaves it discovered");
        }

        [Test]
        public void ARegionSeenInAPass_IsObservedThenFallsBackToDiscovered()
        {
            intel.BeginPass();
            intel.MarkRegionObserved(0);
            intel.EndPass();
            Assert.That(intel.StateOfRegion(0), Is.EqualTo(KnowledgeState.Observed));
            intel.BeginPass();
            intel.EndPass();
            Assert.That(intel.StateOfRegion(0), Is.EqualTo(KnowledgeState.Discovered), "discovery persists, live sight does not");
        }

        [Test]
        public void StateOfPoint_UsesTheRegionOfThePoint_AndIsUnknownOutsideEveryRegion()
        {
            intel.RevealRegion(0);
            Assert.That(intel.StateOfPoint(layout.TileCenter(new Vector2Int(3, 3))), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.StateOfPoint(layout.TileCenter(new Vector2Int(16, 3))), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.StateOfPoint(new Vector3(500f, 0f, 500f)), Is.EqualTo(KnowledgeState.Unknown));
        }

        [Test]
        public void RevealArea_DiscoversEveryRegionTheCircleTouches_AndCountsTheNewOnes()
        {
            intel.RevealRegion(0);
            var newly = intel.RevealArea(layout.TileCenter(new Vector2Int(9, 3)), 1f);
            Assert.That(newly, Is.EqualTo(1), "only the corridor is new");
            Assert.That(intel.StateOfRegion(2), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
        }

        [Test]
        public void ObservedEnemy_IsLiveThenLastKnownAtItsLastPosition_AndStopsTrackingItsHiddenMovement()
        {
            var enemy = Enemy(new Vector3(5f, 1f, 0f));
            intel.TrackEnemy(enemy);
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.IsObserved(enemy), Is.False);

            intel.BeginPass();
            intel.ObserveEnemy(enemy, enemy.transform.position);
            intel.EndPass();
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(intel.IsObserved(enemy), Is.True);

            enemy.transform.position = new Vector3(8f, 1f, 3f);   // moves while unseen
            intel.BeginPass();
            intel.EndPass();
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.IsObserved(enemy), Is.False, "a last-known marker is not a live target");
            Assert.That(intel.TryGetLastKnown(enemy, out var at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(5f, 1f, 0f)), "the marker stays where it was last seen");

            intel.BeginPass();
            intel.ObserveEnemy(enemy, enemy.transform.position);
            intel.EndPass();
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(intel.TryGetLastKnown(enemy, out at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(8f, 1f, 3f)));
        }

        [Test]
        public void AnEnemyNeverSeen_HasNoLastKnownPosition()
        {
            var enemy = Enemy(Vector3.zero);
            intel.TrackEnemy(enemy);
            Assert.That(intel.TryGetLastKnown(enemy, out _), Is.False);
        }

        [Test]
        public void MarkLastKnown_ModelsABriefingMarkerWithoutSeeingTheEnemy()
        {
            var enemy = Enemy(new Vector3(2f, 1f, 2f));
            intel.TrackEnemy(enemy);
            intel.MarkLastKnown(enemy, new Vector3(2f, 1f, 2f));
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.IsObserved(enemy), Is.False);
            Assert.That(intel.TryGetLastKnown(enemy, out var at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(2f, 1f, 2f)));
        }

        [Test]
        public void ADeadEnemy_IsUnknownAndNotObserved_EvenWhileSeenInThePass()
        {
            var enemy = Enemy(Vector3.zero);
            intel.TrackEnemy(enemy);
            intel.BeginPass();
            intel.ObserveEnemy(enemy, Vector3.zero);
            intel.EndPass();
            enemy.TakeDamage(1000);
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.IsObserved(enemy), Is.False);
            Assert.That(intel.TryGetLastKnown(enemy, out _), Is.False);
            intel.BeginPass();
            Assert.DoesNotThrow(() => intel.EndPass());
        }

        [Test]
        public void ADestroyedEnemy_DoesNotThrowInAPass()
        {
            var enemy = Enemy(Vector3.zero);
            intel.TrackEnemy(enemy);
            intel.BeginPass();
            intel.ObserveEnemy(enemy, Vector3.zero);
            intel.EndPass();
            Object.DestroyImmediate(enemy.gameObject);
            Assert.DoesNotThrow(() =>
            {
                intel.BeginPass();
                intel.EndPass();
            });
        }

        [Test]
        public void AnUntrackedUnit_CountsAsObserved_SoFriendliesAreNeverGated()
        {
            var friendly = Enemy(Vector3.zero);
            Assert.That(intel.IsObserved(friendly), Is.True);
            Assert.That(intel.StateOfEnemy(friendly), Is.EqualTo(KnowledgeState.Unknown), "but it has no enemy intel");
        }

        [Test]
        public void AnOpenModel_KnowsEverything_AndIgnoresPasses()
        {
            var open = new MissionIntelligence(map, 1, open: true);
            var enemy = Enemy(Vector3.zero);
            open.TrackEnemy(enemy);
            Assert.That(open.IsOpen, Is.True);
            Assert.That(open.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(open.StateOfPoint(layout.TileCenter(new Vector2Int(16, 3))), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(open.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(open.IsObserved(enemy), Is.True);
            Assert.That(open.StateOfDevice(0), Is.EqualTo(KnowledgeState.Discovered));
        }

        [Test]
        public void Devices_AreDiscoveredOnlyByRevealDevice()
        {
            Assert.That(intel.DeviceCount, Is.EqualTo(2));
            Assert.That(intel.RevealDevice(1), Is.True);
            Assert.That(intel.RevealDevice(1), Is.False);
            Assert.That(intel.StateOfDevice(1), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.StateOfDevice(0), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.StateOfDevice(7), Is.EqualTo(KnowledgeState.Unknown), "an unknown id is unknown, not an exception");
            Assert.That(intel.RevealDevice(7), Is.False);
        }

        [Test]
        public void Changed_FiresOnRealChangesOnly()
        {
            var count = 0;
            intel.Changed += () => count++;
            intel.RevealRegion(0);
            Assert.That(count, Is.EqualTo(1));
            intel.RevealRegion(0);
            Assert.That(count, Is.EqualTo(1));
            intel.BeginPass();
            intel.EndPass();
            Assert.That(count, Is.EqualTo(1), "a pass that changes nothing is silent");
            intel.BeginPass();
            intel.MarkRegionObserved(0);
            intel.EndPass();
            Assert.That(count, Is.EqualTo(2));
        }

        // ---- objectives ----

        sealed class StubObjective : MissionObjective
        {
            readonly bool hasTarget;
            readonly Vector3 position;

            public StubObjective(string id, ObjectiveType type, bool hasTarget, Vector3 position)
                : base(id, type, id, true)
            {
                this.hasTarget = hasTarget;
                this.position = position;
            }

            public override bool HasTarget => hasTarget;
            public override Vector3 TargetPosition => position;
            public override string Describe() => Title;
        }

        List<MissionObjective> Goals() => new List<MissionObjective>
        {
            new StubObjective("eliminate", ObjectiveType.EliminateHostiles, false, Vector3.zero),
            new StubObjective("hack", ObjectiveType.Interact, true, new Vector3(6.5f, 0f, -1.5f)),     // room 1
            new StubObjective("extract", ObjectiveType.ReachZone, true, new Vector3(-6.5f, 0f, -1.5f)),  // room 0
        };

        [TestCase(ObjectiveKnowledge.All, true, true, true)]
        [TestCase(ObjectiveKnowledge.ExtractionAndTerminal, false, true, true)]
        [TestCase(ObjectiveKnowledge.ExtractionOnly, false, false, true)]
        [TestCase(ObjectiveKnowledge.None, false, false, false)]
        public void BindObjectives_SetsTheStartingKnowledge(ObjectiveKnowledge knowledge, bool eliminate, bool hack, bool extract)
        {
            var goals = Goals();
            intel.BindObjectives(goals, knowledge);
            Assert.That(goals[0].IsKnown, Is.EqualTo(eliminate));
            Assert.That(goals[1].IsKnown, Is.EqualTo(hack));
            Assert.That(goals[2].IsKnown, Is.EqualTo(extract));
        }

        [Test]
        public void AnObjectiveWithATarget_BecomesKnownWhenItsRegionIsDiscovered()
        {
            var goals = Goals();
            intel.BindObjectives(goals, ObjectiveKnowledge.None);
            Assert.That(goals[1].IsKnown, Is.False);
            intel.RevealRegion(1);
            Assert.That(goals[1].IsKnown, Is.True);
            Assert.That(goals[2].IsKnown, Is.False, "room 0 is still unknown");
        }

        [Test]
        public void TheEliminateObjective_BecomesKnownWhenAnyEnemyIsFirstObserved()
        {
            var goals = Goals();
            intel.BindObjectives(goals, ObjectiveKnowledge.None);
            var enemy = Enemy(Vector3.zero);
            intel.TrackEnemy(enemy);
            Assert.That(goals[0].IsKnown, Is.False);
            intel.BeginPass();
            intel.ObserveEnemy(enemy, Vector3.zero);
            intel.EndPass();
            Assert.That(goals[0].IsKnown, Is.True);
            Assert.That(intel.AnyEnemyEverObserved, Is.True);
        }

        [Test]
        public void RevealObjective_MakesItKnown()
        {
            var goals = Goals();
            intel.BindObjectives(goals, ObjectiveKnowledge.None);
            intel.RevealObjective(goals[1]);
            Assert.That(goals[1].IsKnown, Is.True);
        }

        [Test]
        public void AnOpenModel_LeavesObjectivesAlone()
        {
            var open = new MissionIntelligence(map, 0, open: true);
            var goals = Goals();
            open.BindObjectives(goals, ObjectiveKnowledge.None);
            Assert.That(goals[1].IsKnown, Is.True);
        }

        [Test]
        public void BindObjectives_HidesTheEliminateCounts()
        {
            var group = new List<Health> { Enemy(Vector3.zero), Enemy(Vector3.one) };
            var eliminate = new EliminateHostilesObjective("e", "Eliminate", group);
            intel.BindObjectives(new List<MissionObjective> { eliminate }, ObjectiveKnowledge.All);
            Assert.That(eliminate.ShowCounts, Is.False);
            var open = new MissionIntelligence(map, 0, open: true);
            var second = new EliminateHostilesObjective("e2", "Eliminate", group);
            open.BindObjectives(new List<MissionObjective> { second }, ObjectiveKnowledge.All);
            Assert.That(second.ShowCounts, Is.True);
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.MissionIntelligenceTests`
Expected: `EXIT=1` (`MissionIntelligence`, `ObservationRules`, `ShowCounts` do not exist).

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/Intelligence/ObservationRules.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Pure geometry for who can see what: all tests are flat (XZ), like the rest of the game's range rules.</summary>
    public static class ObservationRules
    {
        /// <summary>Inclusive.</summary>
        public static bool InCircle(Vector3 centre, float radius, Vector3 point) =>
            CoverRules.FlatDistance(centre, point) <= radius;

        /// <summary>Within `range` of the origin and within `halfAngleDegrees` of `forward` (both flattened). The origin itself is in.</summary>
        public static bool InCone(Vector3 origin, Vector3 forward, float halfAngleDegrees, float range, Vector3 point)
        {
            var offset = point - origin;
            offset.y = 0f;
            if (offset.sqrMagnitude <= 1e-6f)
                return true;
            if (offset.magnitude > range)
                return false;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 1e-6f)
                return false;
            return Vector3.Angle(forward, offset) <= halfAngleDegrees;
        }
    }
}
```

Add to `Assets/_Project/Scripts/Mission/EliminateHostilesObjective.cs` (the property used by the model; the HUD wording is Task 7): after the `Living` property add

```csharp
        /// <summary>False hides the living/total count: the player may not know how many hostiles there are (decision 037).</summary>
        public bool ShowCounts { get; set; } = true;
```

Create `Assets/_Project/Scripts/Intelligence/MissionIntelligence.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What the player knows about one mission. Plain C#, no randomness, no scene access: sources (friendly sight, cameras,
    /// scans, exposure, briefing) call the reveal API and everything else reads the queries. Permanent knowledge (regions,
    /// devices, objectives) only ever goes up. Live knowledge (an enemy being observed, a region being in sight) is rebuilt
    /// by each pass: BeginPass, then sources mark what they see, then EndPass demotes whatever nobody marked (an enemy to
    /// last known at its last seen position, a region to Discovered). A mission whose fog is off is "open": every query
    /// answers "known" and passes do nothing.
    /// </summary>
    public sealed class MissionIntelligence
    {
        sealed class EnemyIntel
        {
            public KnowledgeState State;
            public Vector3 LastPosition;
            public bool HasPosition;
            public bool SeenThisPass;
            public Vector3 PassPosition;
        }

        readonly RegionMap map;
        readonly KnowledgeState[] regions;
        readonly bool[] observedNow;
        readonly bool[] observedNext;
        readonly KnowledgeState[] devices;
        readonly Dictionary<Health, EnemyIntel> enemies = new Dictionary<Health, EnemyIntel>();
        readonly List<Health> order = new List<Health>();
        readonly List<int> circleBuffer = new List<int>();
        IReadOnlyList<MissionObjective> goals = Array.Empty<MissionObjective>();
        bool anyEnemyEverObserved;

        public MissionIntelligence(RegionMap map, int deviceCount, bool open = false)
        {
            this.map = map ?? throw new ArgumentNullException(nameof(map));
            IsOpen = open;
            regions = new KnowledgeState[map.Count];
            observedNow = new bool[map.Count];
            observedNext = new bool[map.Count];
            devices = new KnowledgeState[Math.Max(0, deviceCount)];
        }

        public bool IsOpen { get; }
        public RegionMap Map => map;
        public int DeviceCount => devices.Length;
        /// <summary>Bumped by every real change.</summary>
        public int Version { get; private set; }
        public bool AnyEnemyEverObserved => anyEnemyEverObserved;

        /// <summary>Raised after any real change to what is known.</summary>
        public event Action Changed;

        // ---- regions ----

        public KnowledgeState StateOfRegion(int region)
        {
            if (IsOpen)
                return KnowledgeState.Observed;
            if (region < 0 || region >= regions.Length)
                return KnowledgeState.Unknown;
            return observedNow[region] ? KnowledgeState.Observed : regions[region];
        }

        /// <summary>The region the point is in; Unknown for a point outside every region (a wall, the void).</summary>
        public KnowledgeState StateOfPoint(Vector3 world) =>
            IsOpen ? KnowledgeState.Observed : StateOfRegion(map.RegionAt(world));

        /// <summary>Discovers the region for good. True when it was unknown.</summary>
        public bool RevealRegion(int region)
        {
            if (IsOpen || region < 0 || region >= regions.Length || regions[region] != KnowledgeState.Unknown)
                return false;
            regions[region] = KnowledgeState.Discovered;
            UpdateObjectives();
            Touch();
            return true;
        }

        /// <summary>Discovers every region the flat circle touches (a scan ignores walls). Returns how many were unknown.</summary>
        public int RevealArea(Vector3 centre, float radius)
        {
            if (IsOpen)
                return 0;
            map.RegionsInCircle(centre, radius, circleBuffer);
            var newly = 0;
            foreach (var region in circleBuffer)
                if (RevealRegion(region))
                    newly++;
            return newly;
        }

        // ---- passes ----

        public void BeginPass()
        {
            if (IsOpen)
                return;
            Array.Clear(observedNext, 0, observedNext.Length);
            foreach (var enemy in enemies.Values)
                enemy.SeenThisPass = false;
        }

        /// <summary>The region is in sight this pass; it is also discovered for good.</summary>
        public void MarkRegionObserved(int region)
        {
            if (IsOpen || region < 0 || region >= regions.Length)
                return;
            observedNext[region] = true;
            if (regions[region] == KnowledgeState.Unknown)
            {
                regions[region] = KnowledgeState.Discovered;
                Touch();
            }
        }

        public void ObserveEnemy(Health enemy, Vector3 position)
        {
            if (IsOpen || enemy == null)
                return;
            var intel = Track(enemy);
            intel.SeenThisPass = true;
            intel.PassPosition = position;
        }

        public void EndPass()
        {
            if (IsOpen)
                return;
            var changed = false;
            for (var r = 0; r < observedNow.Length; r++)
            {
                if (observedNow[r] == observedNext[r])
                    continue;
                observedNow[r] = observedNext[r];
                changed = true;
            }
            foreach (var health in order)
            {
                var enemy = enemies[health];
                // A destroyed or dead enemy takes no part: a dead one reads Unknown and never moves a marker.
                if (health == null || !health.IsAlive)
                    continue;
                if (enemy.SeenThisPass)
                {
                    if (enemy.State != KnowledgeState.Observed || enemy.LastPosition != enemy.PassPosition)
                        changed = true;
                    enemy.State = KnowledgeState.Observed;
                    enemy.LastPosition = enemy.PassPosition;
                    enemy.HasPosition = true;
                    anyEnemyEverObserved = true;
                }
                else if (enemy.State == KnowledgeState.Observed)
                {
                    enemy.State = KnowledgeState.Discovered;   // last known: the position is left where it was
                    changed = true;
                }
            }
            if (UpdateObjectives())
                changed = true;
            if (changed)
                Touch();
        }

        // ---- enemies ----

        /// <summary>Starts keeping intel on a hostile. Every hostile of a mission is tracked at its start.</summary>
        public void TrackEnemy(Health enemy)
        {
            if (enemy != null)
                Track(enemy);
        }

        public KnowledgeState StateOfEnemy(Health enemy)
        {
            if (IsOpen)
                return KnowledgeState.Observed;
            if (enemy == null || !enemy.IsAlive || !enemies.TryGetValue(enemy, out var intel))
                return KnowledgeState.Unknown;
            return intel.State;
        }

        /// <summary>
        /// Whether the unit is in live sight, so it may be targeted and shown. True for every unit in an open model and for
        /// units the model does not track (friendlies); a tracked hostile must be Observed.
        /// </summary>
        public bool IsObserved(Health unit)
        {
            if (IsOpen)
                return true;
            if (unit == null)
                return false;
            if (!enemies.TryGetValue(unit, out var intel))
                return true;
            return unit.IsAlive && intel.State == KnowledgeState.Observed;
        }

        public bool TryGetLastKnown(Health enemy, out Vector3 position)
        {
            position = default;
            if (enemy == null || !enemy.IsAlive || !enemies.TryGetValue(enemy, out var intel) || !intel.HasPosition)
                return false;
            position = intel.LastPosition;
            return true;
        }

        /// <summary>A briefing marker: the enemy is known to have been here, without having been seen.</summary>
        public void MarkLastKnown(Health enemy, Vector3 position)
        {
            if (IsOpen || enemy == null)
                return;
            var intel = Track(enemy);
            if (intel.State == KnowledgeState.Observed)
                return;
            intel.State = KnowledgeState.Discovered;
            intel.LastPosition = position;
            intel.HasPosition = true;
            Touch();
        }

        // ---- devices ----

        public KnowledgeState StateOfDevice(int id)
        {
            if (IsOpen)
                return KnowledgeState.Discovered;
            return id < 0 || id >= devices.Length ? KnowledgeState.Unknown : devices[id];
        }

        public bool RevealDevice(int id)
        {
            if (IsOpen || id < 0 || id >= devices.Length || devices[id] != KnowledgeState.Unknown)
                return false;
            devices[id] = KnowledgeState.Discovered;
            Touch();
            return true;
        }

        // ---- objectives ----

        /// <summary>
        /// Sets each objective's starting knowledge from the settings and keeps it up to date: an objective with a place is
        /// learned when its region is discovered, the elimination objective when the first hostile is observed. Hides the
        /// elimination objective's living/total count. An open model changes nothing.
        /// </summary>
        public void BindObjectives(IReadOnlyList<MissionObjective> objectives, ObjectiveKnowledge knowledge)
        {
            goals = objectives ?? Array.Empty<MissionObjective>();
            if (IsOpen)
                return;
            foreach (var goal in goals)
            {
                goal.SetKnown(KnownAtStart(goal.Type, knowledge));
                if (goal is EliminateHostilesObjective eliminate)
                    eliminate.ShowCounts = false;
            }
            UpdateObjectives();
        }

        public void RevealObjective(MissionObjective objective)
        {
            if (objective == null || objective.IsKnown)
                return;
            objective.SetKnown(true);
            Touch();
        }

        static bool KnownAtStart(ObjectiveType type, ObjectiveKnowledge knowledge)
        {
            switch (knowledge)
            {
                case ObjectiveKnowledge.All:
                    return true;
                case ObjectiveKnowledge.ExtractionAndTerminal:
                    return type == ObjectiveType.ReachZone || type == ObjectiveType.Interact;
                case ObjectiveKnowledge.ExtractionOnly:
                    return type == ObjectiveType.ReachZone;
                default:
                    return false;
            }
        }

        bool UpdateObjectives()
        {
            var changed = false;
            foreach (var goal in goals)
            {
                if (goal.IsKnown)
                    continue;
                var learned = goal.HasTarget ? StateOfPoint(goal.TargetPosition) != KnowledgeState.Unknown : anyEnemyEverObserved;
                if (!learned)
                    continue;
                goal.SetKnown(true);
                changed = true;
            }
            return changed;
        }

        EnemyIntel Track(Health enemy)
        {
            if (enemies.TryGetValue(enemy, out var intel))
                return intel;
            intel = new EnemyIntel();
            enemies[enemy] = intel;
            order.Add(enemy);
            return intel;
        }

        void Touch()
        {
            Version++;
            Changed?.Invoke();
        }
    }
}
```

Note: `RevealArea` goes through `RevealRegion`, so a scan also teaches the objectives whose place it covers; `MarkRegionObserved` leaves that to `EndPass`. `ADeadEnemy...` expects `enemy.TakeDamage(1000)` to kill a 50-HP `Health`; `Health` deactivates the GameObject on death, which is fine in EditMode.

- [ ] **Step 4: Run to verify pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.MissionIntelligenceTests` and `Tools/run-tests.sh EditMode Blackglass.Tests.ObservationRulesTests`
Expected: both `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Intelligence Assets/_Project/Scripts/Mission/EliminateHostilesObjective.cs Assets/_Project/Tests/EditMode/MissionIntelligenceTests.cs Assets/_Project/Tests/EditMode/MissionIntelligenceTests.cs.meta Assets/_Project/Tests/EditMode/ObservationRulesTests.cs Assets/_Project/Tests/EditMode/ObservationRulesTests.cs.meta
git commit -m "Add MissionIntelligence (knowledge model and reveal API) and observation rules" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```


### Task 4: Security plan and placement

**Files:**
- Create: `Assets/_Project/Scripts/Intelligence/SecurityPlan.cs`, `Assets/_Project/Scripts/Intelligence/SecurityPlacer.cs`
- Modify: `Assets/_Project/Scripts/Mission/SeededRandom.cs`, `Assets/_Project/Scripts/Mission/ObjectivePlacer.cs`, `Assets/_Project/Scripts/Mission/MissionNavigation.cs`
- Test: `Assets/_Project/Tests/EditMode/SecurityPlacerTests.cs`

**Interfaces:**
- Consumes: `MissionLayout`, `ObjectivePlan` (`GuardTiles`, `TerminalTile`, `ExtractionTile`), `MissionSettings.intelligence.cameraCount`, `ObjectivePlacer` helpers (made `internal`).
- Produces:
  - `SeededRandom.ForSecurity(int seed, int attempt)`.
  - `readonly struct CameraMount { CameraMount(Vector2Int tile, Vector2Int wallSide, int room); Vector2Int Tile; Vector2Int WallSide /* cardinal step from the tile to the wall it hangs on */; int Room; }`
  - `sealed class SecurityPlan`: `static SecurityPlan Empty`; `bool HasTerminal`; `int TerminalRoom`; `Vector2Int TerminalTile`; `IReadOnlyList<CameraMount> Cameras`; `ulong Hash`; `int DeviceCount` (`HasTerminal ? 1 + Cameras.Count : 0`); `const int TerminalDeviceId = 0`; `int CameraDeviceId(int index)` (`1 + index`); `Vector3 TerminalPosition(MissionLayout)`; `Vector3 CameraPosition(MissionLayout, int index)`; `Vector3 CameraForward(int index)`; `const float CameraHeight = 2.6f`; `internal SecurityPlan(bool hasTerminal, int terminalRoom, Vector2Int terminalTile, IReadOnlyList<CameraMount> cameras)`.
  - `static class SecurityPlacer { static bool TryPlace(MissionLayout, ObjectivePlan, MissionSettings, out SecurityPlan plan, out string reason) }`. With `cameraCount == 0` it returns `true` and `SecurityPlan.Empty`.
  - `MissionNavigation.ValidateSecurity(MissionLayout, SecurityPlan, out string reason)`.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/SecurityPlacerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class SecurityPlacerTests
    {
        // The pipeline's loop in miniature: the first attempt whose layout takes objectives and (when cameras are on) security.
        static (MissionLayout layout, ObjectivePlan objectives, SecurityPlan security, MissionSettings settings) Build(int seed, int cameras = 3)
        {
            var settings = new MissionSettings { seed = seed, intelligence = new IntelligenceSettings { cameraCount = cameras } }.Validated();
            for (var attempt = 1; attempt <= settings.maxAttempts; attempt++)
            {
                if (MissionGenerator.TryAttempt(settings, attempt, out var layout, out _)
                    && ObjectivePlacer.TryPlace(layout, settings, out var objectives, out _)
                    && SecurityPlacer.TryPlace(layout, objectives, settings, out var security, out _))
                    return (layout, objectives, security, settings);
            }
            Assert.Fail($"seed {seed}: no attempt produced layout + objectives + security");
            return default;
        }

        [Test]
        public void WithNoCameras_ThePlanIsEmpty()
        {
            var built = Build(12345, cameras: 0);
            Assert.That(built.security, Is.SameAs(SecurityPlan.Empty));
            Assert.That(built.security.HasTerminal, Is.False);
            Assert.That(built.security.Cameras, Is.Empty);
            Assert.That(built.security.DeviceCount, Is.EqualTo(0));
        }

        [Test]
        public void TheSameSeed_GivesTheSamePlan_AndDifferentSeedsMostlyDiffer()
        {
            Assert.That(Build(12345).security.Hash, Is.EqualTo(Build(12345).security.Hash));
            var hashes = new HashSet<ulong>();
            for (var seed = 1; seed <= 12; seed++)
                hashes.Add(Build(seed).security.Hash);
            Assert.That(hashes.Count, Is.GreaterThan(6), "twelve seeds should give mostly different plans");
        }

        [Test]
        public void ThePlacementNeverMovesTheLayoutOrTheObjectivePlan()
        {
            foreach (var seed in new[] { 12345, 1, 2 })
            {
                var without = Build(seed, cameras: 0);
                var with = Build(seed, cameras: 3);
                Assert.That(with.layout.Hash, Is.EqualTo(without.layout.Hash), $"seed {seed} layout");
                Assert.That(with.objectives.Hash, Is.EqualTo(without.objectives.Hash), $"seed {seed} objectives");
            }
        }

        [Test]
        public void TheSecurityStream_IsIndependentOfTheLayoutAndObjectiveStreams()
        {
            ulong First(SeededRandom r) => r.NextULong();
            var security = First(SeededRandom.ForSecurity(12345, 1));
            Assert.That(security, Is.Not.EqualTo(First(SeededRandom.ForObjectives(12345, 1))));
            Assert.That(security, Is.Not.EqualTo(First(SeededRandom.ForAttempt(12345, 1))));
            Assert.That(First(SeededRandom.ForSecurity(12345, 1)), Is.EqualTo(security));
        }

        [TestCase(12345)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(31)]
        public void TheTerminal_IsNearTheStart_OnAFreeTile_AwayFromSpawnsAndObjectives(int seed)
        {
            var (layout, objectives, security, _) = Build(seed);
            Assert.That(security.HasTerminal, Is.True);

            var allowed = new List<int> { layout.FriendlyRoom };
            foreach (var connection in layout.Connections)
            {
                if (connection.RoomA == layout.FriendlyRoom) allowed.Add(connection.RoomB);
                if (connection.RoomB == layout.FriendlyRoom) allowed.Add(connection.RoomA);
            }
            Assert.That(allowed, Has.Member(security.TerminalRoom), "the friendly room or one joined to it");

            var tile = security.TerminalTile;
            Assert.That(layout.Rooms[security.TerminalRoom].Rect.Contains(tile), Is.True);
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    Assert.That(layout.IsFloor(tile.x + dx, tile.y + dy), Is.True, "a free 3x3 block");
                    Assert.That(layout.Boxes.Any(b => b.Kind != MissionBoxKind.Wall && b.Footprint.Contains(new Vector2Int(tile.x + dx, tile.y + dy))), Is.False);
                }
            foreach (var spawn in layout.FriendlySpawns.Concat(layout.HostileSpawns).Concat(objectives.GuardTiles))
                Assert.That(Vector2.Distance(tile, spawn), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing));
            Assert.That(tile, Is.Not.EqualTo(objectives.TerminalTile));
            Assert.That(Vector2.Distance(tile, objectives.ExtractionTile), Is.GreaterThanOrEqualTo(ObjectivePlacer.ExtractionClearance));
        }

        [TestCase(12345)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(31)]
        public void TheCameras_HangOnAWall_FaceIntoADifferentNonStartRoomEach(int seed)
        {
            var (layout, _, security, settings) = Build(seed);
            Assert.That(security.Cameras.Count, Is.InRange(1, settings.intelligence.cameraCount));
            var rooms = new HashSet<int>();
            foreach (var mount in security.Cameras)
            {
                Assert.That(mount.Room, Is.Not.EqualTo(layout.FriendlyRoom));
                Assert.That(rooms.Add(mount.Room), Is.True, "one camera per room");
                Assert.That(layout.Rooms[mount.Room].Rect.Contains(mount.Tile), Is.True);
                Assert.That(layout.IsFloor(mount.Tile.x, mount.Tile.y), Is.True);
                var wall = mount.Tile + mount.WallSide;
                Assert.That(layout.IsFloor(wall.x, wall.y), Is.False, "the wall side is not floor");
                Assert.That(Mathf.Abs(mount.WallSide.x) + Mathf.Abs(mount.WallSide.y), Is.EqualTo(1), "a cardinal step");
                var front = mount.Tile - mount.WallSide;
                Assert.That(layout.IsFloor(front.x, front.y), Is.True, "it faces into the room");
            }
        }

        [Test]
        public void DeviceIds_AreTheTerminalThenTheCamerasInOrder()
        {
            var (layout, _, security, _) = Build(12345);
            Assert.That(SecurityPlan.TerminalDeviceId, Is.EqualTo(0));
            Assert.That(security.DeviceCount, Is.EqualTo(1 + security.Cameras.Count));
            for (var i = 0; i < security.Cameras.Count; i++)
                Assert.That(security.CameraDeviceId(i), Is.EqualTo(1 + i));
            var position = security.CameraPosition(layout, 0);
            Assert.That(position.y, Is.EqualTo(SecurityPlan.CameraHeight));
            var tileCentre = layout.TileCenter(security.Cameras[0].Tile);
            Assert.That(Vector3.Distance(new Vector3(position.x, 0f, position.z), tileCentre), Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(security.CameraForward(0), Is.EqualTo(new Vector3(-security.Cameras[0].WallSide.x, 0f, -security.Cameras[0].WallSide.y)));
        }

        [Test]
        public void ThePinnedHash_ForSeed12345_DoesNotDrift()
        {
            // Pinned the first time this test ran (see the plan, Task 4 Step 4). A change here means the placement changed.
            Assert.That(Build(12345).security.Hash, Is.EqualTo(PinnedHash12345));
        }

        const ulong PinnedHash12345 = 0UL;

        [Test]
        public void NotEnoughRooms_StillPlacesTheCamerasThereAre()
        {
            var built = Build(12345, cameras: 6);
            Assert.That(built.security.Cameras.Count, Is.LessThanOrEqualTo(built.layout.Rooms.Count - 1));
            Assert.That(built.security.Cameras.Count, Is.GreaterThan(0));
        }

        [Test]
        public void ADifferentCameraCount_ChangesNothingAboutTheTerminal()
        {
            var few = Build(12345, cameras: 1).security;
            var many = Build(12345, cameras: 3).security;
            Assert.That(few.TerminalTile, Is.EqualTo(many.TerminalTile), "the terminal is drawn before the cameras");
            Assert.That(few.Cameras.Count, Is.LessThanOrEqualTo(many.Cameras.Count));
        }

        [Test]
        public void ARoomListWithNoWallToHangOn_FailsTheAttemptWithAReason()
        {
            // A layout whose only non-start room is a single floor tile with floor all around cannot take a camera.
            var settings = new MissionSettings { intelligence = new IntelligenceSettings { cameraCount = 1 } }.Validated();
            var floor = new bool[9 * 9];
            for (var i = 0; i < floor.Length; i++) floor[i] = true;
            var layout = new MissionLayout(1, 1, 9, 9, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), new RectInt(0, 0, 4, 9)), new MissionRoom(1, new Vector2Int(1, 0), new RectInt(4, 3, 3, 3)) },
                FriendlyRoom = 0,
                FriendlySpawns = new[] { new Vector2Int(1, 1) },
                HostileSpawns = new[] { new Vector2Int(8, 8) },
            };
            var ok = SecurityPlacer.TryPlace(layout, null, settings, out var plan, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Is.Not.Empty);
            Assert.That(plan, Is.SameAs(SecurityPlan.Empty));
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.SecurityPlacerTests`
Expected: `EXIT=1` (`SecurityPlan`, `SecurityPlacer`, `ForSecurity` missing).

- [ ] **Step 3: Implement**

Edit `Assets/_Project/Scripts/Mission/SeededRandom.cs`: after `ForObjectives` add

```csharp
        /// <summary>
        /// The security placer's stream for one attempt of one seed: independent of the layout and objective streams, so
        /// cameras never move a wall or an objective and a layout never depends on how many cameras a mission asks for.
        /// </summary>
        public static SeededRandom ForSecurity(int seed, int attempt) => ForStream(seed, attempt, 0x5EC17F4A0C0D3B5DUL);
```

Edit `Assets/_Project/Scripts/Mission/ObjectivePlacer.cs`: change the visibility of exactly three helpers so the security placer reuses them instead of copying them: `static bool[] ObstacleMask(` -> `internal static bool[] ObstacleMask(`, `static List<Vector2Int> FreeTiles(` -> `internal static List<Vector2Int> FreeTiles(`, `static RectInt Shrink(` -> `internal static RectInt Shrink(`. Nothing else in that file changes.

Create `Assets/_Project/Scripts/Intelligence/SecurityPlan.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>A camera's place: the floor tile it hangs over and the cardinal step from that tile to the wall it hangs on.</summary>
    public readonly struct CameraMount
    {
        public CameraMount(Vector2Int tile, Vector2Int wallSide, int room)
        {
            Tile = tile;
            WallSide = wallSide;
            Room = room;
        }

        public Vector2Int Tile { get; }
        public Vector2Int WallSide { get; }
        public int Room { get; }
    }

    /// <summary>
    /// Where the mission's security devices go, as integer tiles (pure data, like the layout and the objective plan): one
    /// camera-control terminal and the camera mounts. Device ids are the terminal (0) then the cameras in order, the key the
    /// knowledge model uses. Hash is FNV-1a over everything, so tests can pin and compare placements.
    /// </summary>
    public sealed class SecurityPlan
    {
        public const int TerminalDeviceId = 0;
        public const float CameraHeight = 2.6f;
        // How far from the tile centre toward the wall the camera hangs: the wall face is 0.5 away.
        const float WallInset = 0.4f;
        const float TerminalAimHeight = 0.6f;

        public static readonly SecurityPlan Empty = new SecurityPlan(false, -1, default, Array.Empty<CameraMount>());

        internal SecurityPlan(bool hasTerminal, int terminalRoom, Vector2Int terminalTile, IReadOnlyList<CameraMount> cameras)
        {
            HasTerminal = hasTerminal;
            TerminalRoom = terminalRoom;
            TerminalTile = terminalTile;
            Cameras = new List<CameraMount>(cameras).AsReadOnly();
            Hash = ComputeHash();
        }

        public bool HasTerminal { get; }
        public int TerminalRoom { get; }
        public Vector2Int TerminalTile { get; }
        public IReadOnlyList<CameraMount> Cameras { get; }
        public ulong Hash { get; }
        public int DeviceCount => HasTerminal ? 1 + Cameras.Count : 0;

        public int CameraDeviceId(int index) => 1 + index;

        public Vector3 TerminalPosition(MissionLayout layout) =>
            layout.TileCenter(TerminalTile) + Vector3.up * TerminalAimHeight;

        public Vector3 CameraPosition(MissionLayout layout, int index)
        {
            var side = Cameras[index].WallSide;
            return layout.TileCenter(Cameras[index].Tile) + new Vector3(side.x, 0f, side.y) * WallInset + Vector3.up * CameraHeight;
        }

        public Vector3 CameraForward(int index)
        {
            var side = Cameras[index].WallSide;
            return new Vector3(-side.x, 0f, -side.y);
        }

        ulong ComputeHash()
        {
            var h = 14695981039346656037UL;
            void Add(int value)
            {
                unchecked
                {
                    h ^= (uint)value;
                    h *= 1099511628211UL;
                }
            }
            Add(HasTerminal ? 1 : 0);
            Add(TerminalRoom);
            Add(TerminalTile.x);
            Add(TerminalTile.y);
            Add(Cameras.Count);
            foreach (var mount in Cameras)
            {
                Add(mount.Tile.x);
                Add(mount.Tile.y);
                Add(mount.WallSide.x);
                Add(mount.WallSide.y);
                Add(mount.Room);
            }
            return h;
        }
    }
}
```

Create `Assets/_Project/Scripts/Intelligence/SecurityPlacer.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Chooses where the security devices go, from the generated layout and the objective plan alone: pure integer tile
    /// work on its own seed stream (SeededRandom.ForSecurity), after the objectives, so it never moves a wall or an
    /// objective. With no cameras asked for it places nothing. Rules (decision 037): the camera-control terminal goes in the
    /// friendly room, else in a room joined to it, on a tile with a free 3x3 block, clear of every spawn, guard and the
    /// objective terminal and at least ExtractionClearance from the extraction tile; each camera hangs on a wall of its own
    /// non-friendly room, facing into it. A layout that cannot take the terminal or any camera fails the attempt.
    /// </summary>
    public static class SecurityPlacer
    {
        static readonly Vector2Int[] Directions =
        {
            new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0),
        };

        public static bool TryPlace(MissionLayout layout, ObjectivePlan objectives, MissionSettings settings,
            out SecurityPlan plan, out string reason)
        {
            plan = SecurityPlan.Empty;
            reason = null;
            var wanted = settings.intelligence != null ? settings.intelligence.cameraCount : 0;
            if (wanted <= 0)
                return true;

            var rng = SeededRandom.ForSecurity(layout.Seed, layout.Attempt);
            var blocked = ObjectivePlacer.ObstacleMask(layout);
            var avoid = new List<Vector2Int>(layout.FriendlySpawns);
            avoid.AddRange(layout.HostileSpawns);
            if (objectives != null)
            {
                avoid.AddRange(objectives.GuardTiles);
                avoid.Add(objectives.TerminalTile);
            }

            // The terminal: the friendly room first, then the rooms joined to it.
            var terminalRoom = -1;
            var terminalTile = default(Vector2Int);
            foreach (var room in TerminalRooms(layout))
            {
                var area = ObjectivePlacer.Shrink(layout.Rooms[room].Rect, ObjectivePlacer.FreeRadius);
                var tiles = ObjectivePlacer.FreeTiles(layout, blocked, area, avoid, MissionConstants.SpawnSpacing);
                if (objectives != null)
                    tiles.RemoveAll(t => Vector2.Distance(t, objectives.ExtractionTile) < ObjectivePlacer.ExtractionClearance);
                if (tiles.Count == 0)
                    continue;
                terminalRoom = room;
                terminalTile = tiles[rng.NextInt(tiles.Count)];
                break;
            }
            if (terminalRoom < 0)
            {
                reason = "no room near the start has a free camera-terminal tile";
                return false;
            }

            // The cameras: one per room, in a shuffled order of the rooms other than the friendly one.
            var rooms = new List<int>();
            for (var r = 0; r < layout.Rooms.Count; r++)
            {
                if (r != layout.FriendlyRoom)
                    rooms.Add(r);
            }
            rng.Shuffle(rooms);
            var cameras = new List<CameraMount>();
            foreach (var room in rooms)
            {
                if (cameras.Count >= wanted)
                    break;
                var mounts = MountsIn(layout, blocked, room);
                if (mounts.Count > 0)
                    cameras.Add(mounts[rng.NextInt(mounts.Count)]);
            }
            if (cameras.Count == 0)
            {
                reason = "no room has a wall to hang a camera on";
                return false;
            }

            plan = new SecurityPlan(true, terminalRoom, terminalTile, cameras);
            return true;
        }

        static List<int> TerminalRooms(MissionLayout layout)
        {
            var rooms = new List<int>();
            if (layout.FriendlyRoom >= 0 && layout.FriendlyRoom < layout.Rooms.Count)
                rooms.Add(layout.FriendlyRoom);
            foreach (var connection in layout.Connections)
            {
                if (connection.RoomA == layout.FriendlyRoom && !rooms.Contains(connection.RoomB))
                    rooms.Add(connection.RoomB);
                else if (connection.RoomB == layout.FriendlyRoom && !rooms.Contains(connection.RoomA))
                    rooms.Add(connection.RoomA);
            }
            return rooms;
        }

        // Every floor tile of the room, free of obstacles, with a wall (a non-floor tile) on one cardinal side.
        static List<CameraMount> MountsIn(MissionLayout layout, bool[] blocked, int room)
        {
            var mounts = new List<CameraMount>();
            var rect = layout.Rooms[room].Rect;
            for (var y = rect.yMin; y < rect.yMax; y++)
            {
                for (var x = rect.xMin; x < rect.xMax; x++)
                {
                    if (!layout.IsFloor(x, y) || blocked[y * layout.Width + x])
                        continue;
                    foreach (var side in Directions)
                    {
                        if (!layout.IsFloor(x + side.x, y + side.y) && layout.IsFloor(x - side.x, y - side.y))
                            mounts.Add(new CameraMount(new Vector2Int(x, y), side, room));
                    }
                }
            }
            return mounts;
        }
    }
}
```

Edit `Assets/_Project/Scripts/Mission/MissionNavigation.cs`: after `ValidateObjectives` add

```csharp
        /// <summary>
        /// The camera-control terminal must be usable like the data terminal: somewhere to stand within the interaction range
        /// and a complete path from the first friendly spawn. A plan without a terminal is always valid.
        /// </summary>
        public static bool ValidateSecurity(MissionLayout layout, SecurityPlan plan, out string reason)
        {
            reason = null;
            if (plan == null || !plan.HasTerminal)
                return true;
            if (!NavMesh.SamplePosition(layout.TileCenter(layout.FriendlySpawns[0]), out var start, 1f, NavMesh.AllAreas))
            {
                reason = "the first friendly spawn is not on the NavMesh";
                return false;
            }
            var terminal = layout.TileCenter(plan.TerminalTile);
            if (!NavMesh.SamplePosition(terminal, out var stand, SnapRadius, NavMesh.AllAreas)
                || Vector3.Distance(new Vector3(stand.position.x, 0f, stand.position.z), terminal) > MissionContent.InteractionRange - 0.2f)
            {
                reason = "the camera terminal has no standing place within reach";
                return false;
            }
            return PathExists(start.position, terminal, new NavMeshPath(), out reason, "the camera terminal");
        }
```

- [ ] **Step 4: Pin the hash, run to verify pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.SecurityPlacerTests`
Expected: everything passes except `ThePinnedHash_ForSeed12345_DoesNotDrift`, which fails with `Expected: 0 But was: <N>`. Copy `<N>` (with the `UL` suffix) into `PinnedHash12345`, run again. Expected: `EXIT=0`. Then run `Tools/run-tests.sh EditMode Blackglass.Tests.ObjectivePlacerTests` and `Tools/run-tests.sh EditMode Blackglass.Tests.MissionGeneratorTests`: both `EXIT=0` (nothing about the layout or the objective plan moved).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Intelligence Assets/_Project/Scripts/Mission/SeededRandom.cs Assets/_Project/Scripts/Mission/ObjectivePlacer.cs Assets/_Project/Scripts/Mission/MissionNavigation.cs Assets/_Project/Tests/EditMode/SecurityPlacerTests.cs Assets/_Project/Tests/EditMode/SecurityPlacerTests.cs.meta
git commit -m "Add SecurityPlan and SecurityPlacer on their own seed stream" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Security content in the mission (camera terminal, camera mounts, network)

**Files:**
- Create: `Assets/_Project/Scripts/Intelligence/CameraNetwork.cs`
- Modify: `Assets/_Project/Scripts/Mission/MissionInteractable.cs`, `MissionContent.cs`, `MissionBuilder.cs` (`GeneratedMission`), `MissionDirector.cs`
- Test: `Assets/_Project/Tests/EditMode/MissionInteractableCompletedTests.cs`, `Assets/_Project/Tests/EditMode/CameraNetworkTests.cs`, `Assets/_Project/Tests/PlayMode/MissionSecurityPlayModeTests.cs`

**Interfaces:**
- Consumes: `SecurityPlan` / `SecurityPlacer` / `MissionNavigation.ValidateSecurity` (Task 4), `ObservationRules.InCone` (Task 3), `IntelligenceSettings` (Task 1).
- Produces:
  - `MissionInteractable.Completed` (`event Action<MissionInteractable>`), raised once when `Advance` finishes the work.
  - `readonly struct CameraSpec { CameraSpec(int deviceId, Vector3 position, Vector3 forward, float range, float halfAngleDegrees, GameObject visual = null); int DeviceId; Vector3 Position; Vector3 Forward; float Range; float HalfAngle; GameObject Visual; bool Covers(Vector3 point) /* InCone, no line of sight */ }`
  - `sealed class CameraNetwork { CameraNetwork(IReadOnlyList<CameraSpec> cameras); IReadOnlyList<CameraSpec> Cameras; bool Compromised; void Compromise(); }`
  - `MissionContent.AddCameraTerminal(Transform geometry, SecurityPlan security, MissionLayout layout, MissionSettings settings, Material material, EnvironmentTheme theme = null)` (available from the start, label "Camera control") and `MissionContent.CreateCameras(SecurityPlan, MissionLayout, IntelligenceSettings, Transform root, Material material)` returning a `CameraNetwork`.
  - `GeneratedMission.Security`, `.CameraTerminal`, `.Network`; `MissionReport.SecurityHash`; `InteractableRegistry` holds the data terminal and the camera terminal.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/MissionInteractableCompletedTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionInteractableCompletedTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                if (host != null)
                    Object.DestroyImmediate(host);
            hosts.Clear();
        }

        MissionInteractable Terminal(float duration)
        {
            var host = new GameObject("Terminal");
            hosts.Add(host);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, duration);
            terminal.SetAvailable(true);
            return terminal;
        }

        CommandableUnit Unit()
        {
            var host = new GameObject("Unit");
            hosts.Add(host);
            host.AddComponent<Health>();
            host.AddComponent<UnitInteractor>();
            return host.AddComponent<CommandableUnit>();
        }

        [Test]
        public void Completed_FiresOnce_WhenTheWorkFinishes()
        {
            var terminal = Terminal(2f);
            var unit = Unit();
            var count = 0;
            MissionInteractable from = null;
            terminal.Completed += item =>
            {
                count++;
                from = item;
            };
            terminal.TryBegin(unit);
            terminal.Advance(unit, 1f);
            Assert.That(count, Is.EqualTo(0));
            terminal.Advance(unit, 1.5f);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(from, Is.SameAs(terminal));
            terminal.Advance(unit, 1f);
            Assert.That(count, Is.EqualTo(1), "a completed terminal is used up and does not fire again");
        }

        [Test]
        public void Completed_DoesNotFire_ForACancelledInteraction()
        {
            var terminal = Terminal(2f);
            var unit = Unit();
            var fired = false;
            terminal.Completed += _ => fired = true;
            terminal.TryBegin(unit);
            terminal.Advance(unit, 1f);
            terminal.Release(unit);
            Assert.That(fired, Is.False);
        }
    }
}
```

Create `Assets/_Project/Tests/EditMode/CameraNetworkTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CameraNetworkTests
    {
        static CameraSpec Spec(int id = 1) => new CameraSpec(id, new Vector3(8.9f, 2.6f, -1.5f), Vector3.left, 12f, 45f);

        [Test]
        public void ANewNetwork_IsNotCompromised_UntilCompromiseIsCalled()
        {
            var network = new CameraNetwork(new[] { Spec() });
            Assert.That(network.Compromised, Is.False);
            network.Compromise();
            Assert.That(network.Compromised, Is.True);
            network.Compromise();
            Assert.That(network.Compromised, Is.True, "idempotent");
            Assert.That(network.Cameras.Count, Is.EqualTo(1));
        }

        [Test]
        public void Covers_IsTheConeInFrontOfTheCamera()
        {
            var camera = Spec();
            Assert.That(camera.Covers(new Vector3(4f, 1f, -1.5f)), Is.True);
            Assert.That(camera.Covers(new Vector3(4f, 1f, -5.5f)), Is.False, "too far off axis");
            Assert.That(camera.Covers(new Vector3(-6f, 1f, -1.5f)), Is.False, "out of range");
            Assert.That(camera.Covers(new Vector3(12f, 1f, -1.5f)), Is.False, "behind the camera");
        }

        [Test]
        public void ACameraWithoutAVisual_IsFine()
        {
            Assert.That(Spec().Visual == null, Is.True);
        }
    }
}
```

Create `Assets/_Project/Tests/PlayMode/MissionSecurityPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace Blackglass.Tests
{
    public class MissionSecurityPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        static MissionSettings WithCameras(int cameras) =>
            new MissionSettings { intelligence = new IntelligenceSettings { cameraCount = cameras } };

        [UnityTest]
        public IEnumerator WithCameras_TheMissionHasACameraTerminalAndCameras_AndTheRegistryHoldsBothTerminals()
        {
            rig = new MissionRig(WithCameras(3));
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            var mission = rig.Director.Current;

            Assert.That(mission.Security.HasTerminal, Is.True);
            Assert.That(mission.CameraTerminal, Is.Not.Null);
            Assert.That(mission.CameraTerminal.IsAvailable, Is.True, "usable from the start");
            Assert.That(mission.CameraTerminal.DisplayName, Is.EqualTo("Camera control"));
            Assert.That(mission.CameraTerminal.GetComponent<Collider>(), Is.Not.Null, "solid like the data terminal");
            Assert.That(mission.Network, Is.Not.Null);
            Assert.That(mission.Network.Cameras.Count, Is.EqualTo(mission.Security.Cameras.Count));
            Assert.That(mission.Network.Compromised, Is.False);
            Assert.That(rig.Interactables.Items, Is.EquivalentTo(new[] { mission.Terminal, mission.CameraTerminal }));
            Assert.That(rig.Director.Report.SecurityHash, Is.EqualTo(mission.Security.Hash));
        }

        [UnityTest]
        public IEnumerator TheCameraMounts_HaveNoColliders_AndHangAboveTheFloor()
        {
            rig = new MissionRig(WithCameras(3));
            yield return rig.Generate(12345);
            var network = rig.Director.Current.Network;
            foreach (var camera in network.Cameras)
            {
                Assert.That(camera.Visual, Is.Not.Null);
                Assert.That(camera.Visual.GetComponentsInChildren<Collider>(), Is.Empty, "a camera never blocks a ray or a click");
                Assert.That(camera.Position.y, Is.EqualTo(SecurityPlan.CameraHeight));
                Assert.That(camera.Visual.transform.IsChildOf(rig.Director.Current.Root.transform), Is.True, "destroyed with the mission");
            }
        }

        [UnityTest]
        public IEnumerator WithoutCameras_NothingIsAdded()
        {
            rig = new MissionRig(WithCameras(0));
            yield return rig.Generate(12345);
            var mission = rig.Director.Current;
            Assert.That(mission.Security, Is.SameAs(SecurityPlan.Empty));
            Assert.That(mission.CameraTerminal == null, Is.True);
            Assert.That(mission.Network, Is.Null);
            Assert.That(rig.Interactables.Items, Is.EqualTo(new[] { mission.Terminal }));
        }

        [UnityTest]
        public IEnumerator CamerasDoNotMoveTheLayoutOrTheObjectives()
        {
            foreach (var seed in new[] { 12345, 31 })
            {
                rig = new MissionRig(WithCameras(0));
                yield return rig.Generate(seed);
                var layoutHash = rig.Director.Report.LayoutHash;
                var objectiveHash = rig.Director.Report.ObjectiveHash;
                var hostile = rig.Director.Hostiles[0].transform.position;
                rig.Dispose();

                rig = new MissionRig(WithCameras(3));
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
                Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(layoutHash), $"seed {seed}");
                Assert.That(rig.Director.Report.ObjectiveHash, Is.EqualTo(objectiveHash), $"seed {seed}");
                Assert.That(rig.Director.Hostiles[0].transform.position, Is.EqualTo(hostile).Using(Vector3EqualityComparer.Instance), $"seed {seed}");
                rig.Dispose();
                rig = null;
            }
        }

        [UnityTest]
        public IEnumerator TheCameraTerminal_IsReachableAndAUnitCanStandInRange()
        {
            rig = new MissionRig(WithCameras(3));
            foreach (var seed in new[] { 12345, 1, 2, 3 })
            {
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}: " + string.Join("\n", rig.Director.Report.Failures));
                var mover = rig.Director.Friendlies[0].GetComponent<UnitMover>();
                var terminal = rig.Director.Current.CameraTerminal;
                Assert.That(mover.CanReach(terminal.Position), Is.True, $"seed {seed}");
                Assert.That(mover.TrySnap(terminal.Position, out var stand), Is.True);
                Assert.That(TestWorld.HorizontalDistance(stand, terminal.Position), Is.LessThanOrEqualTo(terminal.Range - 0.2f), $"seed {seed}");
            }
        }

        [UnityTest]
        public IEnumerator WorkingTheCameraTerminal_RaisesCompleted()
        {
            rig = new MissionRig(WithCameras(3));
            yield return rig.Generate(12345);
            var terminal = rig.Director.Current.CameraTerminal;
            var unit = rig.Director.Friendlies[0];
            var fired = 0;
            terminal.Completed += _ => fired++;
            Assert.That(terminal.TryBegin(unit), Is.True);
            terminal.Advance(unit, 100f);
            Assert.That(fired, Is.EqualTo(1));
            Assert.That(terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator Regenerating_ReplacesTheCamerasAndLeavesNoneBehind()
        {
            rig = new MissionRig(WithCameras(3));
            yield return rig.Generate(12345);
            var oldRoot = rig.Director.Current.Root;
            var oldCameras = rig.Director.Current.Network.Cameras.Select(c => c.Visual).ToArray();
            yield return rig.Generate(31);
            Assert.That(oldRoot == null, Is.True);
            Assert.That(oldCameras.All(c => c == null), Is.True);
            Assert.That(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name.StartsWith("SecurityCamera_")),
                Is.EqualTo(rig.Director.Current.Network.Cameras.Count));
        }
    }
}
#endif
```


- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CameraNetworkTests` -> `EXIT=1`.

- [ ] **Step 3: Implement**

Edit `Assets/_Project/Scripts/Mission/MissionInteractable.cs`: add `using System;` at the top; add the event after the `Position` property and raise it in `Advance`:

```csharp
        /// <summary>Raised once, when the work is finished (not when it is cancelled).</summary>
        public event Action<MissionInteractable> Completed;
```

In `Advance`, replace

```csharp
                progress = duration;
                completed = true;
                user = null;
            }
            return true;
```

with

```csharp
                progress = duration;
                completed = true;
                user = null;
                Completed?.Invoke(this);
            }
            return true;
```

(`?.` on a plain `Action`, not on a Unity object: fine.)

Create `Assets/_Project/Scripts/Intelligence/CameraNetwork.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One security camera: where it hangs, where it faces, how far and how wide it sees. Coverage also needs line of sight, which the service tests.</summary>
    public readonly struct CameraSpec
    {
        public CameraSpec(int deviceId, Vector3 position, Vector3 forward, float range, float halfAngleDegrees, GameObject visual = null)
        {
            DeviceId = deviceId;
            Position = position;
            Forward = forward;
            Range = range;
            HalfAngle = halfAngleDegrees;
            Visual = visual;
        }

        public int DeviceId { get; }
        public Vector3 Position { get; }
        public Vector3 Forward { get; }
        public float Range { get; }
        /// <summary>Half of the field of view, degrees.</summary>
        public float HalfAngle { get; }
        /// <summary>The scene object (no collider), or null in tests.</summary>
        public GameObject Visual { get; }

        /// <summary>Inside the range and the cone. Sight is a separate test (a wall between still blocks it).</summary>
        public bool Covers(Vector3 point) => ObservationRules.InCone(Position, Forward, HalfAngle, Range, point);
    }

    /// <summary>The mission's cameras and whether the player has hacked them. Plain state; the service reads it every pass.</summary>
    public sealed class CameraNetwork
    {
        readonly List<CameraSpec> cameras;

        public CameraNetwork(IReadOnlyList<CameraSpec> cameras)
        {
            this.cameras = new List<CameraSpec>(cameras ?? throw new ArgumentNullException(nameof(cameras)));
        }

        public IReadOnlyList<CameraSpec> Cameras => cameras;
        public bool Compromised { get; private set; }

        public void Compromise() => Compromised = true;
    }
}
```

Edit `Assets/_Project/Scripts/Mission/MissionContent.cs`:

(a) Replace the whole `AddTerminal` method and `AddTerminalVisual` signature use so the terminal builder takes a tile, a name, a label and a duration. New code for the region from `public static MissionInteractable AddTerminal(` to the end of `AddTerminal`:

```csharp
        public static MissionInteractable AddTerminal(Transform geometry, ObjectivePlan plan, MissionLayout layout,
            MissionSettings settings, Material material, EnvironmentTheme theme = null) =>
            BuildTerminal(geometry, "Terminal", "Terminal", plan.TerminalTile, layout, settings.interactionSeconds, material, theme);

        /// <summary>
        /// The camera-control terminal: built like the data terminal (solid, Not Walkable, marker) but available from the
        /// start, because using it is the player's choice and no objective opens it. Call it from the builder's pre-bake hook.
        /// </summary>
        public static MissionInteractable AddCameraTerminal(Transform geometry, SecurityPlan security, MissionLayout layout,
            MissionSettings settings, Material material, EnvironmentTheme theme = null)
        {
            var interactable = BuildTerminal(geometry, "CameraTerminal", "Camera control", security.TerminalTile, layout,
                settings.interactionSeconds, material, theme);
            interactable.SetAvailable(true);
            return interactable;
        }

        static MissionInteractable BuildTerminal(Transform geometry, string objectName, string label, Vector2Int tile,
            MissionLayout layout, float seconds, Material material, EnvironmentTheme theme)
        {
            var terminal = new GameObject(objectName);
            terminal.transform.SetParent(geometry, false);
            terminal.transform.position = layout.TileCenter(tile) + Vector3.up * (TerminalHeight * 0.5f);
            var box = terminal.AddComponent<BoxCollider>();
            box.size = new Vector3(TerminalSize, TerminalHeight, TerminalSize);
            // Not walkable, like every obstacle: no island forms on top and the mesh has a hole around it.
            var modifier = terminal.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = NotWalkableArea;
            var interactable = terminal.AddComponent<MissionInteractable>();
            interactable.Initialize(InteractionRange, seconds, label);

            var visualRoot = new GameObject(VisualRootName).transform;
            visualRoot.SetParent(terminal.transform, false);
            visualRoot.localPosition = new Vector3(0f, -TerminalHeight * 0.5f, 0f);
            var tint = AddTerminalVisual(visualRoot, tile, layout, theme, material);
            terminal.AddComponent<ObjectiveMarker>().Bind(() => ObjectiveMarker.TerminalColour(interactable), tint);
            return interactable;
        }
```

and change `AddTerminalVisual`'s signature and its one use of the plan: `static Renderer[] AddTerminalVisual(Transform visualRoot, Vector2Int tile, MissionLayout layout, EnvironmentTheme theme, Material material)` with `theme.Resolve(EnvironmentElement.Terminal, tile, layout.Seed)`.

(b) Add after `CreateZone`:

```csharp
        /// <summary>
        /// The security cameras under `root`: a small housing and lens per mount (renderers only, never a collider), tinted
        /// grey until the network is hacked and green after. Returns the network the intelligence service reads.
        /// </summary>
        public static CameraNetwork CreateCameras(SecurityPlan security, MissionLayout layout, IntelligenceSettings intelligence,
            Transform root, Material material)
        {
            var specs = new List<CameraSpec>();
            var lenses = new List<Renderer[]>();
            for (var i = 0; i < security.Cameras.Count; i++)
            {
                var position = security.CameraPosition(layout, i);
                var forward = security.CameraForward(i);
                var camera = new GameObject($"SecurityCamera_{i + 1}");
                camera.transform.SetParent(root, false);
                camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward));
                CameraPart(camera.transform, "Housing", Vector3.zero, new Vector3(0.3f, 0.2f, 0.45f), material);
                var lens = CameraPart(camera.transform, "Lens", new Vector3(0f, 0f, 0.27f), new Vector3(0.12f, 0.12f, 0.1f), material);
                lenses.Add(new[] { lens });
                specs.Add(new CameraSpec(security.CameraDeviceId(i), position, forward, intelligence.cameraRange,
                    intelligence.cameraFov * 0.5f, camera));
            }
            var network = new CameraNetwork(specs);
            for (var i = 0; i < specs.Count; i++)
            {
                specs[i].Visual.AddComponent<ObjectiveMarker>().Bind(
                    () => ObjectiveMarker.ZoneColour(network.Compromised ? ObjectiveState.Completed : ObjectiveState.Inactive), lenses[i]);
            }
            return network;
        }

        static Renderer CameraPart(Transform parent, string name, Vector3 localPosition, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            if (material != null)
                renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }
```

(The `for` closure over `i` in the second loop is safe because `i` is used as an index into already-built lists inside the lambda arguments evaluated immediately: `lenses[i]` is evaluated at call time, and the lambda captures `network` only. Keep it exactly like this.)

Edit `Assets/_Project/Scripts/Mission/MissionBuilder.cs` (`GeneratedMission`): after `Terminal` add

```csharp
        public SecurityPlan Security { get; internal set; } = SecurityPlan.Empty;
        /// <summary>The camera-control terminal, or null when the mission has no cameras.</summary>
        public MissionInteractable CameraTerminal { get; internal set; }
        public CameraNetwork Network { get; internal set; }
```

Edit `Assets/_Project/Scripts/Mission/MissionDirector.cs`:

1. `MissionReport`: after `ObjectiveHash` add `public ulong SecurityHash;`
2. In `TryAttempt`, replace the block from `MissionInteractable terminal = null;` through `mission = MissionBuilder.Build(...)` with:

```csharp
            if (!SecurityPlacer.TryPlace(layout, plan, request, out var security, out reason))
            {
                Report.Failures.Add($"attempt {attempt}: security: {reason}");
                return false;
            }
            MissionInteractable terminal = null;
            MissionInteractable cameraTerminal = null;
            Action<Transform> addContent = null;
            if (request.hackTerminal || security.HasTerminal)
            {
                addContent = geometry =>
                {
                    if (request.hackTerminal)
                        terminal = MissionContent.AddTerminal(geometry, plan, layout, request, obstacleMaterial, environmentTheme);
                    if (security.HasTerminal)
                        cameraTerminal = MissionContent.AddCameraTerminal(geometry, security, layout, request, obstacleMaterial, environmentTheme);
                };
            }
            mission = MissionBuilder.Build(layout, groundMaterial, obstacleMaterial, addContent, environmentTheme);
```

3. After the `ValidateObjectives` block add:

```csharp
            if (!MissionNavigation.ValidateSecurity(layout, security, out reason))
            {
                Report.Failures.Add($"attempt {attempt}: security: {reason}");
                DestroyMission(mission, true);
                return false;
            }
```

4. After `mission.Terminal = terminal;` add:

```csharp
            mission.Security = security;
            mission.CameraTerminal = cameraTerminal;
            mission.Network = security.HasTerminal
                ? MissionContent.CreateCameras(security, layout, request.intelligence, mission.Root.transform, obstacleMaterial)
                : null;
```

5. Replace the `systems.interactables.Rebuild(...)` statement with:

```csharp
            if (systems.interactables != null)
            {
                var interactables = new List<MissionInteractable>();
                if (terminal != null)
                    interactables.Add(terminal);
                if (cameraTerminal != null)
                    interactables.Add(cameraTerminal);
                systems.interactables.Rebuild(interactables);
            }
```

6. After `Fill(Report, layout, navigation, plan);` add `Report.SecurityHash = security.Hash;`.

- [ ] **Step 4: Run to verify pass**

Run, one after another: `Tools/run-tests.sh EditMode Blackglass.Tests.CameraNetworkTests`, `Tools/run-tests.sh EditMode Blackglass.Tests.MissionInteractableCompletedTests`, `Tools/run-tests.sh EditMode Blackglass.Tests.MissionInteractableTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.MissionSecurityPlayModeTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.MissionContentPlayModeTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.MissionDirectorPlayModeTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.MissionBuilderPlayModeTests`. Expected: all `EXIT=0`.

If `CamerasDoNotMoveTheLayoutOrTheObjectives` fails because the cameras run needed a later attempt (the report's `Attempt` differs), the failing seed's security placement or navigation validation rejected attempt 1; print `rig.Director.Report.Failures`, and if the reason is a real placement shortfall report it; do not change the assertion.

- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Scripts/Intelligence Assets/_Project/Scripts/Mission Assets/_Project/Tests/EditMode/MissionInteractableCompletedTests.cs Assets/_Project/Tests/EditMode/MissionInteractableCompletedTests.cs.meta Assets/_Project/Tests/EditMode/CameraNetworkTests.cs Assets/_Project/Tests/EditMode/CameraNetworkTests.cs.meta Assets/_Project/Tests/PlayMode/MissionSecurityPlayModeTests.cs Assets/_Project/Tests/PlayMode/MissionSecurityPlayModeTests.cs.meta
git commit -m "Build the camera-control terminal, camera mounts and network into the mission" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The intelligence service (sources, passes, queries) and its pipeline integration

**Files:**
- Create: `Assets/_Project/Scripts/Intelligence/IntelligenceService.cs`, `Assets/_Project/Scripts/Intelligence/Knowledge.cs`, `Assets/_Project/Tests/PlayMode/TestSupport/IntelLayouts.cs`, `Assets/_Project/Tests/PlayMode/TestSupport/IntelRig.cs`
- Modify: `Assets/_Project/Scripts/Mission/MissionSlots.cs` (`MissionSystems.intelligence`), `MissionDirector.cs`, `Assets/_Project/Tests/PlayMode/TestSupport/MissionRig.cs`
- Test: `Assets/_Project/Tests/PlayMode/IntelligenceServiceGeometryTests.cs`, `IntelligenceServiceSourcesTests.cs`, `IntelligenceServiceMissionTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 5; `LineOfSight.IsClear(Vector3 eye, Vector3 point, LayerMask, RaycastHit[])`, `LineOfSight.EyeHeight`, `SimulationTime.IsRunning`, `Encounter` (`Friendlies`, `Hostiles`), `UnitAttacker.Attacked/Missed` (`Action<Health>`).
- Produces:
  - `sealed class IntelligenceMission { MissionLayout Layout; ObjectivePlan Objectives; SecurityPlan Security; CameraNetwork Network; MissionInteractable CameraTerminal; IReadOnlyList<MissionObjective> Goals; Transform Root; }` (every field but `Layout` optional).
  - `sealed class IntelligenceService : MonoBehaviour` with: `void Begin(IntelligenceMission, IntelligenceSettings, Encounter)`; `void Clear()`; `void RunPass()`; `void Scan(Vector3 centre, float radius, float seconds)`; `MissionIntelligence Model`; `IntelligenceSettings Settings`; `RegionMap Map`; `bool HasMission`; `bool IsFogActive`; `bool TruthView {get;set;}`; `bool ListsUnknownObjectives`; `Material MarkerMaterial`; queries `bool CanTarget(Health)`, `bool IsUnitShown(Health)`, `KnowledgeState StateOfEnemy(Health)`, `bool TryLastKnown(Health, out Vector3)`, `KnowledgeState StateOfRegion(int)`, `bool CanInteract(MissionInteractable)`, `bool CanSeeCover(CoverLocation)`, `bool IsCoverShown(CoverLocation)` (known ground or truth view), `bool IsDeviceShown(int deviceId)`; read-only `IReadOnlyList<IntelDevice> Devices`, `IReadOnlyList<IntelPulse> Pulses`, `CameraNetwork Network`; events `Changed`, `MissionBegun(MissionLayout, RegionMap, Transform)`. `IntelDevice { int Id; Vector3 Position }` and `IntelPulse { Vector3 Centre; float Radius; float Until }` are public readonly structs in the same file.
  - `static class Knowledge`: `CanTarget`, `IsShown`, `CanInteract`, `CanSeeCover`, `IsCoverShown` taking the (possibly null) service first; all return `true` for a null service.
  - `MissionSystems.intelligence` (optional `IntelligenceService`); the director calls `Begin` after the runtime starts and `Clear` in `ResetSystems()`.
  - Test support: `IntelLayouts.TwoRooms(bool corridor)`; `IntelRig` (see Step 1).

- [ ] **Step 1: Write the test support and the failing tests**

Create `Assets/_Project/Tests/PlayMode/TestSupport/IntelLayouts.cs`:

```csharp
#if UNITY_EDITOR
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// A hand-made layout for the intelligence tests: two 6x6 rooms on a 20x10 grid (tile (x, y) is world (x - 10, y - 5)),
    /// room A at world x -9..-3, z -4..2 and room B at x 3..9, z -4..2, with an optional 6x3 corridor between them
    /// (world x -3..3, z -3..0). Region ids: A 0, B 1, corridor 2.
    /// </summary>
    internal static class IntelLayouts
    {
        public static readonly RectInt RoomA = new RectInt(1, 1, 6, 6);
        public static readonly RectInt RoomB = new RectInt(13, 1, 6, 6);
        public static readonly RectInt Strip = new RectInt(7, 2, 6, 3);

        public static MissionLayout TwoRooms(bool corridor)
        {
            const int width = 20;
            const int height = 10;
            var floor = new bool[width * height];
            void Fill(RectInt rect)
            {
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        floor[y * width + x] = true;
            }
            Fill(RoomA);
            Fill(RoomB);
            if (corridor)
                Fill(Strip);
            return new MissionLayout(1, 1, width, height, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), RoomA), new MissionRoom(1, new Vector2Int(1, 0), RoomB) },
                Connections = corridor ? new[] { new MissionConnection(0, 1, Strip) } : new MissionConnection[0],
                FriendlyRoom = 0,
                FriendlySpawns = new[] { new Vector2Int(3, 3) },
                HostileSpawns = new[] { new Vector2Int(16, 3) },
            };
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/TestSupport/IntelRig.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// A small world for the intelligence tests: the IntelLayouts rooms with real wall colliders (a closed 6 m wall between
    /// them, or two wall pieces leaving the corridor open), a NavMesh, a friendly in room A at world (-6.5, -1.5), an
    /// Encounter and an IntelligenceService. Hostiles are added before Begin, which hands everything to the service.
    /// </summary>
    internal sealed class IntelRig : IDisposable
    {
        public static readonly Vector3 FriendlyGround = new Vector3(-6.5f, 0f, -1.5f);
        /// <summary>In room B, on the corridor's axis, 13 m from the friendly.</summary>
        public static readonly Vector3 InLineGround = new Vector3(6.5f, 0f, -1.5f);
        /// <summary>In room B, off the corridor's axis: the corridor walls hide it from the friendly.</summary>
        public static readonly Vector3 OffAxisGround = new Vector3(6.5f, 0f, 2.0f);

        public readonly TestWorld World = new TestWorld();
        public readonly MissionLayout Layout;
        public readonly Encounter Encounter;
        public readonly IntelligenceService Service;
        public readonly SelectableUnit Friendly;
        public readonly Health FriendlyHealth;
        public readonly List<Health> Hostiles = new List<Health>();

        public IntelRig(bool corridor)
        {
            var walls = corridor
                ? new[]
                {
                    (new Vector3(0f, 1.5f, -4f), new Vector3(6f, 3f, 2f)),     // below the corridor, z -5..-3
                    (new Vector3(0f, 1.5f, 2.5f), new Vector3(6f, 3f, 5f)),    // above the corridor, z 0..5
                }
                : new[] { (new Vector3(0f, 1.5f, 0f), new Vector3(6f, 3f, 10f)) };   // closed: the whole gap
            World.CreateEnvironment(walls);
            Layout = IntelLayouts.TwoRooms(corridor);
            Encounter = World.CreateEncounter();
            Friendly = World.CreateFriendlyFighter(FriendlyGround);
            FriendlyHealth = Friendly.GetComponent<Health>();
            Service = World.Track(new GameObject("Intelligence")).AddComponent<IntelligenceService>();
        }

        /// <summary>A hostile with a collider and no weapon (a cylinder dummy).</summary>
        public Health AddHostile(Vector3 ground)
        {
            var health = World.CreateDummy(ground);
            Hostiles.Add(health);
            return health;
        }

        /// <summary>A hostile that can shoot: a ranged fighter. It stands at `ground` and never moves on its own.</summary>
        public (Health health, UnitAttacker attacker) AddShooter(Vector3 ground, float range = 40f)
        {
            var unit = World.CreateFighter(ground, 60, 10, 1f, CombatRole.Ranged, range);
            unit.name = "TestShooter";
            Hostiles.Add(unit.GetComponent<Health>());
            return (unit.GetComponent<Health>(), unit.GetComponent<UnitAttacker>());
        }

        /// <summary>
        /// A 0.9 m wall at world x -5 (z -3..0) and the friendly occupying a cover point behind it facing east: a shooter on
        /// the corridor's axis sees over the wall (its eye line stays above 0.9 m) but its eye-to-feet ray crosses it, so the
        /// friendly is protected from it (hit chance 0.5).
        /// </summary>
        public CoverLocation PutFriendlyInCover()
        {
            var wall = World.CreateObstacle(new Vector3(-5f, 0.45f, -1.5f), new Vector3(0.5f, 0.9f, 3f));
            var point = World.CreateCoverPoint(FriendlyGround, Vector3.right, wall.GetComponent<Collider>());
            var registry = World.CreateRegistry(point);
            var cover = Friendly.GetComponent<UnitCover>();
            cover.Initialize(registry);
            cover.TryReserve(point);
            cover.TryOccupy();
            return point;
        }

        public void Begin(IntelligenceSettings settings, IntelligenceMission extra = null)
        {
            Encounter.Initialize(new[] { FriendlyHealth }, Hostiles);
            var mission = extra ?? new IntelligenceMission();
            mission.Layout = Layout;
            Service.Begin(mission, settings, Encounter);
        }

        public static IntelligenceSettings Fog(Action<IntelligenceSettings> tweak = null)
        {
            var settings = new IntelligenceSettings { fogEnabled = true, map = MapKnowledge.None, objectives = ObjectiveKnowledge.None };
            tweak?.Invoke(settings);
            return settings;
        }

        public void Dispose()
        {
            Service.Clear();
            World.Dispose();
            Time.timeScale = 1f;
        }
    }
}
#endif
```

Edit `Assets/_Project/Tests/PlayMode/TestSupport/MissionRig.cs`: after `AddRoster(...)` add

```csharp
        /// <summary>
        /// Adds an intelligence service to the persistent systems and gives the director these intelligence settings: the next
        /// generation then builds its knowledge model. Call before the first Generate.
        /// </summary>
        public IntelligenceService AddIntelligence(IntelligenceSettings intelligence)
        {
            var service = World.Track(new GameObject("Intelligence")).AddComponent<IntelligenceService>();
            systems.intelligence = service;
            Director.Settings.intelligence = intelligence;
            return service;
        }
```

Create `Assets/_Project/Tests/PlayMode/IntelligenceServiceGeometryTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The rule the whole feature rests on: sight needs a line, range alone never discovers anything.</summary>
    public class IntelligenceServiceGeometryTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator AClosedWall_DoesNotRevealTheRoomBehindIt_EvenInRange()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);   // 13 m away, inside the 14 m observation range
            rig.Begin(IntelRig.Fog());
            yield return null;

            Assert.That(rig.Service.StateOfRegion(0), Is.EqualTo(KnowledgeState.Observed), "the room the squad stands in");
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown), "the room behind the wall");
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
            Assert.That(rig.Service.IsUnitShown(hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator ALongerRange_StillDoesNotSeeThroughAClosedWall()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.observationRange = 40f));
            yield return null;
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator AnOpenDoor_RevealsTheCorridorAndTheEnemyInLine()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;

            Assert.That(rig.Service.StateOfRegion(2), Is.EqualTo(KnowledgeState.Observed), "the corridor");
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed), "part of room B is visible down the corridor");
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(rig.Service.CanTarget(hostile), Is.True);
        }

        [UnityTest]
        public IEnumerator AnOpenDoor_ShowsOnlyTheEnemiesTheOpeningAllows()
        {
            rig = new IntelRig(corridor: true);
            var inLine = rig.AddHostile(IntelRig.InLineGround);
            var hidden = rig.AddHostile(IntelRig.OffAxisGround);   // in the same, partly seen room, behind the corridor wall
            rig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(rig.Service.CanTarget(inLine), Is.True);
            Assert.That(rig.Service.CanTarget(hidden), Is.False, "room B is partly seen but this enemy is not");
        }

        [UnityTest]
        public IEnumerator DiscoveryPersists_WhenTheSquadLooksAway_ButLiveSightDoesNot()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Observed));

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);   // north end of room A: the corridor is out of line
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "the layout stays discovered");
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Discovered), "the enemy is only last known now");
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
            Assert.That(rig.Service.TryLastKnown(hostile, out var at), Is.True);
            Assert.That(Vector3.Distance(at, hostile.transform.position), Is.LessThan(0.01f));
        }

        [UnityTest]
        public IEnumerator ALastKnownMarker_DoesNotFollowTheHiddenEnemy()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            var seenAt = hostile.transform.position;

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            hostile.transform.position = new Vector3(8f, 1f, 1.5f);   // walks off while unseen
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(rig.Service.TryLastKnown(hostile, out var at), Is.True);
            Assert.That(Vector3.Distance(at, seenAt), Is.LessThan(0.01f));
        }

        [UnityTest]
        public IEnumerator ADeadEnemy_DropsOutWithoutAMarkerOrAnException()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            hostile.TakeDamage(1000);
            Assert.DoesNotThrow(() => rig.Service.RunPass());
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.TryLastKnown(hostile, out _), Is.False);
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator FogOff_MakesEverythingKnown_AndPassesDoNothing()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());   // fog off
            yield return null;
            Assert.That(rig.Service.IsFogActive, Is.False);
            Assert.That(rig.Service.CanTarget(hostile), Is.True);
            Assert.That(rig.Service.IsUnitShown(hostile), Is.True);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed));
        }

        [Test]
        public void NoMissionYet_MeansEverythingKnown()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            Assert.That(rig.Service.HasMission, Is.False);
            Assert.That(rig.Service.CanTarget(hostile), Is.True);
            Assert.That(Knowledge.CanTarget(null, hostile), Is.True, "a null service is everything-known too");
            Assert.That(Knowledge.IsShown(null, hostile), Is.True);
            Assert.That(Knowledge.CanInteract(null, null), Is.True);
            Assert.That(Knowledge.CanSeeCover(null, null), Is.True);
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/IntelligenceServiceSourcesTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The non-sight sources: hostile fire, the camera network, scans and the briefing.</summary>
    public class IntelligenceServiceSourcesTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        // ---- exposure (a hostile that fires reveals itself) ----

        [UnityTest]
        public IEnumerator AHostileThatFiresFromBeyondObservationRange_IsExposed_ThenBecomesLastKnown()
        {
            rig = new IntelRig(corridor: true);
            var (shooter, attacker) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s =>
            {
                s.observationRange = 8f;   // the shooter is 13 m away
                s.exposureSeconds = 1f;
            }));
            yield return null;
            Assert.That(rig.Service.CanTarget(shooter), Is.False, "out of observation range");

            Assert.That(attacker.TryAttack(rig.FriendlyHealth), Is.True, "the shot is fired");
            Assert.That(rig.Service.CanTarget(shooter), Is.True, "exposed the moment it fires");
            Assert.That(rig.Service.StateOfEnemy(shooter), Is.EqualTo(KnowledgeState.Observed));

            yield return new WaitForSeconds(1.4f);
            Assert.That(rig.Service.StateOfEnemy(shooter), Is.EqualTo(KnowledgeState.Discovered), "exposure ran out: last known");
            Assert.That(rig.Service.CanTarget(shooter), Is.False);
            Assert.That(rig.Service.TryLastKnown(shooter, out var at), Is.True);
            Assert.That(Vector3.Distance(at, shooter.transform.position), Is.LessThan(0.01f));
        }

        [UnityTest]
        public IEnumerator AMissedShot_ExposesTheShooterToo()
        {
            rig = new IntelRig(corridor: true);
            var (shooter, attacker) = rig.AddShooter(IntelRig.InLineGround);
            rig.PutFriendlyInCover();   // a low wall between the two: the shooter sees over it, but the friendly is protected
            rig.Begin(IntelRig.Fog(s => s.observationRange = 8f));
            yield return null;
            Assert.That(rig.Service.CanTarget(shooter), Is.False, "out of observation range");

            var missed = 0;
            var hit = 0;
            attacker.Missed += _ => missed++;
            attacker.Attacked += _ => hit++;
            attacker.HitRoll = () => 0.99f;   // above the cover's 0.5 hit chance: a miss
            Assert.That(attacker.TryAttack(rig.FriendlyHealth), Is.True);
            Assert.That(missed, Is.EqualTo(1), "Precondition: the shot was turned away by cover");
            Assert.That(hit, Is.EqualTo(0));
            Assert.That(rig.Service.CanTarget(shooter), Is.True, "a miss exposes the shooter as much as a hit");
        }

        [UnityTest]
        public IEnumerator Exposure_DoesNotExpireWhileTheGameIsPaused()
        {
            rig = new IntelRig(corridor: true);
            var (shooter, attacker) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s =>
            {
                s.observationRange = 8f;
                s.exposureSeconds = 1f;
            }));
            yield return null;
            attacker.TryAttack(rig.FriendlyHealth);

            Time.timeScale = 0f;
            yield return new WaitForSecondsRealtime(1.5f);
            Assert.That(rig.Service.StateOfEnemy(shooter), Is.EqualTo(KnowledgeState.Observed), "nothing hidden advances while paused");
            Time.timeScale = 1f;
            yield return new WaitForSeconds(1.4f);
            Assert.That(rig.Service.StateOfEnemy(shooter), Is.EqualTo(KnowledgeState.Discovered));
        }

        [UnityTest]
        public IEnumerator AShooterNotYetFiring_IsNotExposed()
        {
            rig = new IntelRig(corridor: true);
            var (shooter, _) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.observationRange = 8f));
            yield return null;
            Assert.That(rig.Service.CanTarget(shooter), Is.False);
        }

        // ---- scans ----

        [UnityTest]
        public IEnumerator AScan_DiscoversTheRegionsItTouches_AndObservesEnemiesForAWhile_ThroughWalls()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(rig.Service.CanTarget(hostile), Is.False);

            rig.Service.Scan(IntelRig.InLineGround, 4f, 1f);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed), "scanned room B, behind a closed wall");
            Assert.That(rig.Service.CanTarget(hostile), Is.True, "a scan is not sight: walls do not matter");

            yield return new WaitForSeconds(1.4f);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "the layout stays");
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Discovered), "the enemy is a marker now");
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator AScan_OnlyObservesEnemiesInsideItsRadius()
        {
            rig = new IntelRig(corridor: false);
            var near = rig.AddHostile(IntelRig.InLineGround);
            var far = rig.AddHostile(new Vector3(6.5f, 0f, 1.5f));
            rig.Begin(IntelRig.Fog());
            yield return null;
            rig.Service.Scan(IntelRig.InLineGround, 1.5f, 5f);
            Assert.That(rig.Service.CanTarget(near), Is.True);
            Assert.That(rig.Service.CanTarget(far), Is.False, "3 m from the scan centre");
        }

        [UnityTest]
        public IEnumerator AScan_WhenFogIsOff_DoesNothingAndDoesNotThrow()
        {
            rig = new IntelRig(corridor: false);
            rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());
            yield return null;
            Assert.DoesNotThrow(() => rig.Service.Scan(IntelRig.InLineGround, 4f, 1f));
        }

        // ---- the camera network ----

        // A camera on room B's east wall, looking west across the room (and, with the corridor shut, only across room B).
        static IntelligenceMission CameraMission(MissionInteractable terminal, out CameraNetwork network, float range = 12f,
            Vector2Int? terminalTile = null)
        {
            var mount = new CameraMount(new Vector2Int(18, 3), new Vector2Int(1, 0), 1);
            var security = new SecurityPlan(true, 0, terminalTile ?? new Vector2Int(2, 2), new[] { mount });
            var layout = IntelLayouts.TwoRooms(false);
            var spec = new CameraSpec(security.CameraDeviceId(0), security.CameraPosition(layout, 0), security.CameraForward(0), range, 45f);
            network = new CameraNetwork(new[] { spec });
            return new IntelligenceMission { Security = security, Network = network, CameraTerminal = terminal };
        }

        MissionInteractable CameraTerminal()
        {
            var host = rig.World.Track(new GameObject("CameraTerminal"));
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, 2f, "Camera control");
            terminal.SetAvailable(true);
            return terminal;
        }

        [UnityTest]
        public IEnumerator TheCameras_GiveNothingUntilTheTerminalIsUsed_ThenCoverTheirRooms_AndStayLive()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            var terminal = CameraTerminal();
            rig.Begin(IntelRig.Fog(), CameraMission(terminal, out var network));
            yield return null;
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.CanTarget(hostile), Is.False);

            Assert.That(terminal.TryBegin(rig.Friendly.Unit), Is.True);
            terminal.Advance(rig.Friendly.Unit, 10f);
            Assert.That(network.Compromised, Is.True);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed), "the camera sees room B");
            Assert.That(rig.Service.CanTarget(hostile), Is.True, "and the enemy in it, live");

            // Still inside room B, but 0.4 m in front of and 3 m to the side of the camera (at x 8.9, facing west): 82 degrees off axis.
            hostile.transform.position = new Vector3(8.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "outside the camera's cone");
        }

        [UnityTest]
        public IEnumerator ACamera_CannotSeeThroughTheWallIntoRoomA()
        {
            rig = new IntelRig(corridor: false);
            var inA = rig.AddHostile(new Vector3(-4f, 0f, -1.5f));   // in the squad's room, 2.5 m from the friendly
            var terminal = CameraTerminal();
            rig.Begin(IntelRig.Fog(s => s.observationRange = 4f), CameraMission(terminal, out var network, range: 30f));   // long camera range
            yield return null;
            Assert.That(rig.Service.CanTarget(inA), Is.True, "Precondition: the friendly sees it from 2.5 m");

            terminal.TryBegin(rig.Friendly.Unit);
            terminal.Advance(rig.Friendly.Unit, 10f);
            Assert.That(network.Compromised, Is.True);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed), "room B is the camera's own room");

            // The friendly walks 5.4 m away from the hostile, out of its 4 m sight. The camera, 12.9 m away to the east with
            // range 30 and the hostile dead ahead in its cone, must still not see it: the closed wall is in the ray.
            rig.Friendly.transform.position = new Vector3(-8.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(rig.Service.CanTarget(inA), Is.False);
        }

        [UnityTest]
        public IEnumerator WithSnapshotCameras_TheHackRevealsTheRoomOnce_AndEnemiesAreNotObservedAfterwards()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            var terminal = CameraTerminal();
            rig.Begin(IntelRig.Fog(s => s.cameraStaysLive = false), CameraMission(terminal, out _));
            yield return null;
            terminal.TryBegin(rig.Friendly.Unit);
            terminal.Advance(rig.Friendly.Unit, 10f);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "the layout is revealed for good");
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "but nothing is watched live");
        }

        [UnityTest]
        public IEnumerator ACamera_IsADeviceTheSquadDiscoversBySeeingIt()
        {
            rig = new IntelRig(corridor: true);
            var terminal = CameraTerminal();
            rig.Begin(IntelRig.Fog(s => s.observationRange = 30f), CameraMission(terminal, out _));
            yield return null;
            var cameraDevice = 1;
            Assert.That(rig.Service.Model.StateOfDevice(cameraDevice), Is.EqualTo(KnowledgeState.Discovered),
                "visible down the open corridor from the friendly");
            Assert.That(rig.Service.IsDeviceShown(cameraDevice), Is.True);
        }

        // ---- the briefing ----

        static IntelligenceMission BriefingMission() => new IntelligenceMission
        {
            Objectives = new ObjectivePlan(1, new Vector2Int(16, 4), new Vector2Int[0], 1, new Vector2Int(16, 2)),
        };

        [UnityTest]
        public IEnumerator FullMapKnowledge_DiscoversEveryRegionAtTheStart()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog(s => s.map = MapKnowledge.Full));
            yield return null;
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered));
        }

        [UnityTest]
        public IEnumerator PartialMapKnowledge_IsTheStartRoomItsCorridorsAndTheExtractionRoom()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog(s => s.map = MapKnowledge.Partial), BriefingMission());
            yield return null;
            Assert.That(rig.Service.StateOfRegion(0), Is.Not.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "room 1 holds the extraction");
        }

        [UnityTest]
        public IEnumerator EnemyMarkersAtStart_MakeTheFirstHostilesLastKnown()
        {
            rig = new IntelRig(corridor: false);
            var first = rig.AddHostile(IntelRig.InLineGround);
            var second = rig.AddHostile(IntelRig.OffAxisGround);
            rig.Begin(IntelRig.Fog(s => s.enemyMarkersAtStart = 1));
            yield return null;
            Assert.That(rig.Service.StateOfEnemy(first), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(rig.Service.CanTarget(first), Is.False, "a marker is not a target");
            Assert.That(rig.Service.TryLastKnown(first, out _), Is.True);
            Assert.That(rig.Service.StateOfEnemy(second), Is.EqualTo(KnowledgeState.Unknown));
        }

        // ---- queries used by the other systems ----

        [UnityTest]
        public IEnumerator CanInteract_FollowsTheRegionOfADataTerminal_AndTheDeviceOfTheCameraTerminal()
        {
            rig = new IntelRig(corridor: false);
            var cameraTerminal = CameraTerminal();
            cameraTerminal.transform.position = rig.Layout.TileCenter(new Vector2Int(2, 2));
            var dataHost = rig.World.Track(new GameObject("DataTerminal"));
            dataHost.transform.position = rig.Layout.TileCenter(new Vector2Int(16, 4));   // room B
            var data = dataHost.AddComponent<MissionInteractable>();
            data.Initialize(1.8f, 2f);
            // The plan's terminal tile is the grid's far corner, out of the friendly's range and sight: its device stays undiscovered.
            rig.Begin(IntelRig.Fog(), CameraMission(cameraTerminal, out _, terminalTile: new Vector2Int(19, 9)));
            yield return null;
            Assert.That(rig.Service.CanInteract(data), Is.False, "room B is unknown");
            Assert.That(rig.Service.CanInteract(cameraTerminal), Is.False, "the camera terminal device is not discovered yet");
            rig.Service.Model.RevealRegion(1);
            rig.Service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            Assert.That(rig.Service.CanInteract(data), Is.True);
            Assert.That(rig.Service.CanInteract(cameraTerminal), Is.True);
        }

        [UnityTest]
        public IEnumerator CanSeeCover_NeedsAKnownRegion()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog());
            yield return null;
            var inA = new CoverLocation("A", rig.Layout.TileCenter(new Vector2Int(3, 3)), Vector3.forward, null);
            var inB = new CoverLocation("B", rig.Layout.TileCenter(new Vector2Int(16, 3)), Vector3.forward, null);
            Assert.That(rig.Service.CanSeeCover(inA), Is.True);
            Assert.That(rig.Service.CanSeeCover(inB), Is.False);
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/IntelligenceServiceMissionTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The service inside the real pipeline: briefing, hidden hostiles, determinism and regeneration.</summary>
    public class IntelligenceServiceMissionTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator ABlindMission_StartsWithOnlyTheStartRoomAndTheExtractionKnown()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            var layout = rig.Director.Current.Layout;

            Assert.That(service.IsFogActive, Is.True);
            Assert.That(service.StateOfRegion(layout.FriendlyRoom), Is.EqualTo(KnowledgeState.Observed));
            var unknown = Enumerable.Range(0, service.Map.Count).Count(r => service.StateOfRegion(r) == KnowledgeState.Unknown);
            Assert.That(unknown, Is.GreaterThan(service.Map.Count / 2), "most of the map is unknown");
            foreach (var hostile in rig.Encounter.Hostiles)
                Assert.That(service.CanTarget(hostile), Is.False, hostile.name);

            var goals = rig.Director.Runtime.Objectives;
            Assert.That(goals.Single(g => g.Type == ObjectiveType.ReachZone).IsKnown, Is.True, "extraction stays known");
            Assert.That(goals.Single(g => g.Type == ObjectiveType.Interact).IsKnown, Is.False);
            Assert.That(goals.Single(g => g.Type == ObjectiveType.EliminateHostiles).IsKnown, Is.False);
        }

        [UnityTest]
        public IEnumerator AFullKnowledgeMission_HidesNothing()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Full());
            yield return rig.Generate(12345);
            Assert.That(service.IsFogActive, Is.False);
            foreach (var hostile in rig.Encounter.Hostiles)
                Assert.That(service.CanTarget(hostile), Is.True);
            Assert.That(rig.Director.Runtime.Objectives.All(g => g.IsKnown), Is.True);
        }

        [UnityTest]
        public IEnumerator Fog_DoesNotChangeTheMission_ForTheSameSeed()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var layoutHash = rig.Director.Report.LayoutHash;
            var objectiveHash = rig.Director.Report.ObjectiveHash;
            var spawns = rig.Director.Hostiles.Select(h => h.transform.position).ToArray();
            rig.Dispose();

            rig = new MissionRig();
            rig.AddIntelligence(IntelligenceSettings.ObjectivesKnown());   // fog on, no cameras: the same geometry
            yield return rig.Generate(12345);
            Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(layoutHash));
            Assert.That(rig.Director.Report.ObjectiveHash, Is.EqualTo(objectiveHash));
            var fogged = rig.Director.Hostiles.Select(h => h.transform.position).ToArray();
            Assert.That(fogged.Length, Is.EqualTo(spawns.Length));
            for (var i = 0; i < spawns.Length; i++)
                Assert.That(Vector3.Distance(fogged[i], spawns[i]), Is.LessThan(0.01f), $"hostile {i}");
            rig = null;
        }

        [UnityTest]
        public IEnumerator Regenerating_StartsFromTheBriefingAgain_AndNothingStaleIsLeft()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            var oldHostile = rig.Encounter.Hostiles[0];
            service.Scan(Vector3.zero, 200f, 100f);   // learn the whole map and every enemy
            Assert.That(service.CanTarget(oldHostile), Is.True);

            yield return rig.Generate(12345);
            yield return null;
            Assert.That(oldHostile == null, Is.True, "the old hostile is destroyed");
            Assert.That(() => service.CanTarget(oldHostile), Throws.Nothing);
            Assert.That(service.CanTarget(oldHostile), Is.False);
            foreach (var hostile in rig.Encounter.Hostiles)
                Assert.That(service.CanTarget(hostile), Is.False, "knowledge from the first mission did not carry over");
            var unknown = Enumerable.Range(0, service.Map.Count).Count(r => service.StateOfRegion(r) == KnowledgeState.Unknown);
            Assert.That(unknown, Is.GreaterThan(service.Map.Count / 2));
        }

        [UnityTest]
        public IEnumerator Regenerating_WhilePaused_IsClean()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            rig.Pause.Pause();
            yield return rig.Generate(31);   // an error log fails the test: no stale subscriptions fire into a dead mission
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(service.HasMission, Is.True);
        }

        [UnityTest]
        public IEnumerator ClearingTheDirector_ClearsTheServiceToo()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            Assert.That(service.HasMission, Is.True);
            rig.Director.Clear();
            Assert.That(service.HasMission, Is.False);
            Assert.That(service.CanTarget(null), Is.True, "no mission: nothing is hidden, and a null unit is no exception");
        }

        [UnityTest]
        public IEnumerator ANewMissionWithCameras_GivesTheServiceItsNetwork()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            var mission = rig.Director.Current;
            var terminal = mission.CameraTerminal;
            Assert.That(mission.Network.Compromised, Is.False);
            terminal.TryBegin(rig.Director.Friendlies[0]);
            terminal.Advance(rig.Director.Friendlies[0], 100f);
            Assert.That(mission.Network.Compromised, Is.True, "using the terminal compromises the network through the service");
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.IntelligenceServiceGeometryTests`
Expected: `EXIT=1` (`IntelligenceService`, `IntelligenceMission` and `Knowledge` do not exist yet).

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/Intelligence/Knowledge.cs`:

```csharp
namespace Blackglass
{
    /// <summary>
    /// What a consumer asks about the player's knowledge when it holds an optional IntelligenceService. A null service (a
    /// scene without the feature) answers "known" to everything, so a consumer needs one call and no branch of its own.
    /// </summary>
    public static class Knowledge
    {
        /// <summary>The unit may be targeted: true for friendlies and for hostiles in live sight.</summary>
        public static bool CanTarget(IntelligenceService intelligence, Health unit) =>
            intelligence == null || intelligence.CanTarget(unit);

        /// <summary>The unit may be drawn, labelled or counted.</summary>
        public static bool IsShown(IntelligenceService intelligence, Health unit) =>
            intelligence == null || intelligence.IsUnitShown(unit);

        public static bool CanInteract(IntelligenceService intelligence, MissionInteractable item) =>
            intelligence == null || intelligence.CanInteract(item);

        public static bool CanSeeCover(IntelligenceService intelligence, CoverLocation cover) =>
            intelligence == null || intelligence.CanSeeCover(cover);

        /// <summary>The cover marker may be drawn (known ground, or the developer truth view).</summary>
        public static bool IsCoverShown(IntelligenceService intelligence, CoverLocation cover) =>
            intelligence == null || intelligence.IsCoverShown(cover);
    }
}
```

Create `Assets/_Project/Scripts/Intelligence/IntelligenceService.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Everything the service needs to know about one built mission. Only Layout is required.</summary>
    public sealed class IntelligenceMission
    {
        public MissionLayout Layout;
        public ObjectivePlan Objectives;
        public SecurityPlan Security;
        public CameraNetwork Network;
        public MissionInteractable CameraTerminal;
        public IReadOnlyList<MissionObjective> Goals;
        /// <summary>Where presenters parent what they build (the mission root, so regeneration destroys it).</summary>
        public Transform Root;
    }

    /// <summary>A security device the model knows by id: the camera-control terminal (id 0) or a camera, at a world position.</summary>
    public readonly struct IntelDevice
    {
        public IntelDevice(int id, Vector3 position)
        {
            Id = id;
            Position = position;
        }

        public int Id { get; }
        public Vector3 Position { get; }
    }

    /// <summary>A running scan: a flat circle observed until a scaled time.</summary>
    public readonly struct IntelPulse
    {
        public IntelPulse(Vector3 centre, float radius, float until)
        {
            Centre = centre;
            Radius = radius;
            Until = until;
        }

        public Vector3 Centre { get; }
        public float Radius { get; }
        public float Until { get; }
    }

    /// <summary>
    /// The player's knowledge of the running mission. It owns the MissionIntelligence model and feeds it, every 0.2 s of
    /// simulation time, from the sources: friendly sight (range + line of sight, all squad members share it), the hacked
    /// cameras, scan pulses, hostiles that have just fired and (once, at Begin) the briefing. Everything else only asks.
    /// A service without a mission, and a mission whose fog is off, answer "known" to everything. Lives on Systems; the
    /// director calls Begin when a mission is ready and Clear when it is torn down. Passes run on scaled time, so a tactical
    /// pause freezes sight, exposure and scans together; queries keep working.
    /// </summary>
    public sealed class IntelligenceService : MonoBehaviour
    {
        [SerializeField] LayerMask sightBlockers = ~0;
        [SerializeField, Min(0.05f)] float passInterval = 0.2f;
        // Used by presenters for last-known markers.
        [SerializeField] Material markerMaterial;

        readonly RaycastHit[] hits = new RaycastHit[LineOfSight.HitBufferSize];
        readonly List<(UnitAttacker attacker, Action<Health> handler)> watchers = new List<(UnitAttacker, Action<Health>)>();
        readonly Dictionary<Health, float> exposures = new Dictionary<Health, float>();
        readonly List<Health> expired = new List<Health>();
        readonly List<IntelPulse> pulses = new List<IntelPulse>();
        readonly List<IntelDevice> devices = new List<IntelDevice>();
        readonly List<int> regionBuffer = new List<int>();

        IntelligenceSettings settings = new IntelligenceSettings();
        MissionIntelligence model;
        RegionMap map;
        Encounter encounter;
        CameraNetwork network;
        MissionInteractable cameraTerminal;
        float nextPass;
        bool truthView;

        public MissionIntelligence Model => model;
        public IntelligenceSettings Settings => settings;
        public RegionMap Map => map;
        public Material MarkerMaterial => markerMaterial;
        public bool HasMission => model != null;
        /// <summary>A mission is running and its fog is on.</summary>
        public bool IsFogActive => model != null && !model.IsOpen;
        public bool ListsUnknownObjectives => IsFogActive && settings.showUnknownObjectives;

        /// <summary>Developer view: show everything as it is. Display only; targeting is never affected.</summary>
        public bool TruthView
        {
            get => truthView;
            set
            {
                if (truthView == value)
                    return;
                truthView = value;
                Changed?.Invoke();
            }
        }

        /// <summary>Raised when anything the player knows (or the truth view) changes.</summary>
        public event Action Changed;

        /// <summary>Raised when a mission begins, so presenters can build their world objects under its root.</summary>
        public event Action<MissionLayout, RegionMap, Transform> MissionBegun;

        // ---- lifecycle ----

        public void Begin(IntelligenceMission mission, IntelligenceSettings intelligenceSettings, Encounter currentEncounter)
        {
            if (mission == null || mission.Layout == null)
                throw new ArgumentNullException(nameof(mission));
            Clear();
            settings = (intelligenceSettings ?? new IntelligenceSettings()).Validated();
            encounter = currentEncounter;
            network = mission.Network;
            cameraTerminal = mission.CameraTerminal;
            map = new RegionMap(mission.Layout);
            var security = mission.Security ?? SecurityPlan.Empty;
            model = new MissionIntelligence(map, security.DeviceCount, open: !settings.fogEnabled);
            model.Changed += RaiseChanged;
            BuildDevices(mission.Layout, security);
            if (encounter != null)
            {
                foreach (var hostile in encounter.Hostiles)
                {
                    if (hostile == null)
                        continue;
                    model.TrackEnemy(hostile);
                    WatchHostile(hostile);
                }
            }
            ApplyBriefing(mission);
            if (mission.Goals != null)
                model.BindObjectives(mission.Goals, settings.objectives);
            if (cameraTerminal != null)
                cameraTerminal.Completed += OnCameraTerminalCompleted;
            MissionBegun?.Invoke(mission.Layout, map, mission.Root);
            RunPass();
            Changed?.Invoke();
        }

        /// <summary>Forgets the mission: no knowledge, no pulses, no subscriptions survive.</summary>
        public void Clear()
        {
            foreach (var (attacker, handler) in watchers)
            {
                if (attacker == null)
                    continue;
                attacker.Attacked -= handler;
                attacker.Missed -= handler;
            }
            watchers.Clear();
            if (cameraTerminal != null)
                cameraTerminal.Completed -= OnCameraTerminalCompleted;
            cameraTerminal = null;
            network = null;
            exposures.Clear();
            pulses.Clear();
            devices.Clear();
            var had = model != null;
            if (model != null)
                model.Changed -= RaiseChanged;
            model = null;
            map = null;
            encounter = null;
            if (had)
                Changed?.Invoke();
        }

        void OnDisable() => Clear();

        void Update()
        {
            if (model == null || model.IsOpen || !SimulationTime.IsRunning)
                return;
            if (Time.time >= nextPass)
                RunPass();
        }

        void RaiseChanged() => Changed?.Invoke();

        // ---- queries (a missing mission or an open model answers "known") ----

        public bool CanTarget(Health unit) => model == null || model.IsObserved(unit);

        public bool IsUnitShown(Health unit) => model == null || truthView || model.IsObserved(unit);

        public KnowledgeState StateOfEnemy(Health enemy) => model == null ? KnowledgeState.Observed : model.StateOfEnemy(enemy);

        public bool TryLastKnown(Health enemy, out Vector3 position)
        {
            position = default;
            return model != null && model.TryGetLastKnown(enemy, out position);
        }

        public KnowledgeState StateOfRegion(int region) => model == null ? KnowledgeState.Observed : model.StateOfRegion(region);

        /// <summary>The mission's security devices, for the overlay and debug views.</summary>
        public IReadOnlyList<IntelDevice> Devices => devices;

        /// <summary>The scans still running, for the debug views.</summary>
        public IReadOnlyList<IntelPulse> Pulses => pulses;

        public CameraNetwork Network => network;

        public bool CanInteract(MissionInteractable item)
        {
            if (model == null || model.IsOpen || item == null)
                return true;
            if (item == cameraTerminal)
                return model.StateOfDevice(SecurityPlan.TerminalDeviceId) != KnowledgeState.Unknown;
            return model.StateOfPoint(item.Position) != KnowledgeState.Unknown;
        }

        public bool CanSeeCover(CoverLocation cover) =>
            model == null || model.IsOpen || cover == null || model.StateOfPoint(cover.Position) != KnowledgeState.Unknown;

        /// <summary>Whether a cover marker may be drawn: known ground, or the developer truth view.</summary>
        public bool IsCoverShown(CoverLocation cover) => truthView || CanSeeCover(cover);

        public bool IsDeviceShown(int deviceId) =>
            model == null || truthView || model.IsOpen || model.StateOfDevice(deviceId) != KnowledgeState.Unknown;

        // ---- passes ----

        /// <summary>One sampling of every source. Called every passInterval of simulation time, and at once when something changes.</summary>
        public void RunPass()
        {
            if (model == null || model.IsOpen)
                return;
            nextPass = Time.time + passInterval;
            model.BeginPass();
            ObserveFromFriendlies();
            if (network != null && network.Compromised && settings.cameraStaysLive)
            {
                foreach (var camera in network.Cameras)
                    SweepCamera(camera, live: true);
            }
            ObservePulsesAndExposures();
            model.EndPass();
        }

        /// <summary>
        /// A scan pulse (Recon Scan): discovers every region the circle touches for good, discovers the devices inside it,
        /// and observes every hostile inside it for `seconds`, walls notwithstanding (a scan is not sight). Does nothing when
        /// the fog is off.
        /// </summary>
        public void Scan(Vector3 centre, float radius, float seconds)
        {
            if (model == null || model.IsOpen)
                return;
            model.RevealArea(centre, radius);
            foreach (var device in devices)
            {
                if (ObservationRules.InCircle(centre, radius, device.Position))
                    model.RevealDevice(device.Id);
            }
            pulses.Add(new IntelPulse(centre, radius, Time.time + Mathf.Max(0f, seconds)));
            RunPass();
        }

        void ObserveFromFriendlies()
        {
            if (encounter == null)
                return;
            var range = settings.observationRange;
            foreach (var friendly in encounter.Friendlies)
            {
                if (!IsLiving(friendly))
                    continue;
                var pivot = friendly.transform.position;
                var eye = pivot + Vector3.up * LineOfSight.EyeHeight;
                var own = map.RegionAt(pivot);
                if (own >= 0)
                    model.MarkRegionObserved(own);
                map.RegionsInCircle(pivot, range, regionBuffer);
                foreach (var region in regionBuffer)
                {
                    if (region != own && AnySampleVisible(eye, pivot, region, range))
                        model.MarkRegionObserved(region);
                }
                foreach (var hostile in encounter.Hostiles)
                {
                    if (IsLiving(hostile) && ObservationRules.InCircle(pivot, range, hostile.transform.position)
                        && LineOfSight.IsClear(eye, hostile.transform.position, sightBlockers, hits))
                        model.ObserveEnemy(hostile, hostile.transform.position);
                }
                foreach (var device in devices)
                {
                    if (ObservationRules.InCircle(pivot, range, device.Position) && LineOfSight.IsClear(eye, device.Position, sightBlockers, hits))
                        model.RevealDevice(device.Id);
                }
            }
        }

        bool AnySampleVisible(Vector3 eye, Vector3 origin, int region, float range)
        {
            foreach (var sample in map.SamplePoints(region))
            {
                if (ObservationRules.InCircle(origin, range, sample) && LineOfSight.IsClear(eye, sample, sightBlockers, hits))
                    return true;
            }
            return false;
        }

        // A hacked camera: the regions it covers (live: observed this pass; snapshot: discovered for good), the devices it
        // sees, and when live the hostiles it sees.
        void SweepCamera(CameraSpec camera, bool live)
        {
            map.RegionsInCircle(camera.Position, camera.Range, regionBuffer);
            foreach (var region in regionBuffer)
            {
                if (!AnyCameraSampleVisible(camera, region))
                    continue;
                if (live)
                    model.MarkRegionObserved(region);
                else
                    model.RevealRegion(region);
            }
            foreach (var device in devices)
            {
                if (device.Id != camera.DeviceId && CameraSees(camera, device.Position))
                    model.RevealDevice(device.Id);
            }
            if (!live || encounter == null)
                return;
            foreach (var hostile in encounter.Hostiles)
            {
                if (IsLiving(hostile) && CameraSees(camera, hostile.transform.position))
                    model.ObserveEnemy(hostile, hostile.transform.position);
            }
        }

        bool AnyCameraSampleVisible(CameraSpec camera, int region)
        {
            foreach (var sample in map.SamplePoints(region))
            {
                if (CameraSees(camera, sample))
                    return true;
            }
            return false;
        }

        bool CameraSees(CameraSpec camera, Vector3 point) =>
            camera.Covers(point) && LineOfSight.IsClear(camera.Position, point, sightBlockers, hits);

        void ObservePulsesAndExposures()
        {
            for (var i = pulses.Count - 1; i >= 0; i--)
            {
                if (Time.time >= pulses[i].Until)
                    pulses.RemoveAt(i);
            }
            foreach (var pulse in pulses)
            {
                map.RegionsInCircle(pulse.Centre, pulse.Radius, regionBuffer);
                foreach (var region in regionBuffer)
                    model.MarkRegionObserved(region);
            }
            if (encounter != null && pulses.Count > 0)
            {
                foreach (var hostile in encounter.Hostiles)
                {
                    if (!IsLiving(hostile))
                        continue;
                    foreach (var pulse in pulses)
                    {
                        if (!ObservationRules.InCircle(pulse.Centre, pulse.Radius, hostile.transform.position))
                            continue;
                        model.ObserveEnemy(hostile, hostile.transform.position);
                        break;
                    }
                }
            }
            expired.Clear();
            foreach (var pair in exposures)
            {
                if (Time.time >= pair.Value || !IsLiving(pair.Key))
                {
                    expired.Add(pair.Key);
                    continue;
                }
                model.ObserveEnemy(pair.Key, pair.Key.transform.position);
            }
            foreach (var key in expired)
                exposures.Remove(key);
        }

        static bool IsLiving(Health unit) => unit != null && unit.IsAlive && unit.gameObject.activeInHierarchy;

        // ---- sources that arrive as events ----

        // A hostile that fires (hit or miss) is exposed for exposureSeconds wherever it is.
        void WatchHostile(Health hostile)
        {
            if (!hostile.TryGetComponent<UnitAttacker>(out var attacker))
                return;
            Action<Health> handler = _ => Expose(hostile);
            attacker.Attacked += handler;
            attacker.Missed += handler;
            watchers.Add((attacker, handler));
        }

        void Expose(Health hostile)
        {
            if (model == null || model.IsOpen || !IsLiving(hostile))
                return;
            exposures[hostile] = Time.time + settings.exposureSeconds;
            RunPass();
        }

        void OnCameraTerminalCompleted(MissionInteractable item)
        {
            if (network == null || network.Compromised)
                return;
            network.Compromise();
            if (model != null && !model.IsOpen && !settings.cameraStaysLive)
            {
                foreach (var camera in network.Cameras)
                    SweepCamera(camera, live: false);
            }
            RunPass();
            Changed?.Invoke();
        }

        // ---- briefing ----

        void BuildDevices(MissionLayout layout, SecurityPlan security)
        {
            devices.Clear();
            if (security.HasTerminal)
                devices.Add(new IntelDevice(SecurityPlan.TerminalDeviceId, security.TerminalPosition(layout)));
            for (var i = 0; i < security.Cameras.Count; i++)
                devices.Add(new IntelDevice(security.CameraDeviceId(i), security.CameraPosition(layout, i)));
        }

        void ApplyBriefing(IntelligenceMission mission)
        {
            if (model.IsOpen)
                return;
            switch (settings.map)
            {
                case MapKnowledge.Full:
                    for (var region = 0; region < map.Count; region++)
                        model.RevealRegion(region);
                    break;
                case MapKnowledge.Partial:
                    var start = mission.Layout.FriendlyRoom;
                    if (start >= 0 && start < map.RoomCount)
                    {
                        model.RevealRegion(start);
                        foreach (var corridor in map.Neighbours(start))
                            model.RevealRegion(corridor);
                    }
                    if (mission.Objectives != null)
                        model.RevealRegion(mission.Objectives.ExtractionRoom);
                    break;
            }
            if (encounter == null)
                return;
            var markers = Mathf.Min(settings.enemyMarkersAtStart, encounter.Hostiles.Count);
            for (var i = 0; i < markers; i++)
            {
                var hostile = encounter.Hostiles[i];
                if (hostile != null)
                    model.MarkLastKnown(hostile, hostile.transform.position);
            }
        }
    }
}
```

Edit `Assets/_Project/Scripts/Mission/MissionSlots.cs` (`MissionSystems`): add the field and update the summary:

```csharp
        public IntelligenceService intelligence;
```

and in the doc comment `... `camera`, `abilityTargeting`, `interactables`, `roster` and `intelligence` are optional ...`.

Edit `Assets/_Project/Scripts/Mission/MissionDirector.cs`:

1. In `ResetSystems()` add as the first statement:

```csharp
            if (systems.intelligence != null)
                systems.intelligence.Clear();
```

2. In `TryAttempt`, after `Runtime.PhaseChanged += OnRuntimePhaseChanged;` add:

```csharp
            if (systems.intelligence != null)
            {
                systems.intelligence.Begin(new IntelligenceMission
                {
                    Layout = layout,
                    Objectives = plan,
                    Security = security,
                    Network = mission.Network,
                    CameraTerminal = cameraTerminal,
                    Goals = Runtime.Objectives,
                    Root = mission.Root.transform,
                }, request.intelligence, systems.encounter);
            }
```

Note on the hand-built `ObjectivePlan` used by `BriefingMission()` in the tests: its constructor is `internal ObjectivePlan(int terminalRoom, Vector2Int terminalTile, IReadOnlyList<Vector2Int> guardTiles, int extractionRoom, Vector2Int extractionTile)`; the test assembly sees it through `InternalsVisibleTo`.

- [ ] **Step 4: Run to verify pass**

Run, one after another: `Tools/run-tests.sh PlayMode Blackglass.Tests.IntelligenceServiceGeometryTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.IntelligenceServiceSourcesTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.IntelligenceServiceMissionTests`, then `Tools/run-tests.sh PlayMode Blackglass.Tests.MissionDirectorPlayModeTests` and `Tools/run-tests.sh PlayMode Blackglass.Tests.ProceduralMissionSceneTests` (the director now calls the service only when one is wired, so the scene suites are unchanged). Expected: all `EXIT=0`.

If a geometry test fails, check the hand-built numbers first: the corridor walls and the friendly's eye line are computed in the `IntelLayouts` and `IntelRig` comments; print `Physics.Linecast` between the friendly's eye (pivot + 0.5 m up) and the sample or hostile you expect to be visible.
- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Scripts/Intelligence Assets/_Project/Scripts/Mission Assets/_Project/Tests/PlayMode
git commit -m "Add the intelligence service: sight, cameras, scans, exposure and briefing, wired into the pipeline" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```


### Task 7: Objective knowledge in the HUD

**Files:**
- Modify: `Assets/_Project/Scripts/Mission/MissionObjective.cs`, `EliminateHostilesObjective.cs`, `MissionHud.cs` (`MissionHudText` and `MissionHud`), `MissionContent.cs` (`CreateRuntime`)
- Test: `Assets/_Project/Tests/EditMode/ObjectiveKnowledgeTextTests.cs`, `Assets/_Project/Tests/PlayMode/ObjectiveKnowledgeMissionTests.cs`

**Interfaces:**
- Consumes: `EliminateHostilesObjective.ShowCounts` and `MissionIntelligence.BindObjectives` (Task 3), `IntelligenceService.ListsUnknownObjectives` (Task 6).
- Produces: `MissionObjective.VagueTitle` and `SetVagueTitle(string)`; `MissionHudText.Panel(MissionRuntime runtime, bool listUnknown = false)`; `MissionHud.SetIntelligence(IntelligenceService)` (serialized field `intelligence`); `EliminateHostilesObjective.Describe()` honouring `ShowCounts`.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/ObjectiveKnowledgeTextTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ObjectiveKnowledgeTextTests
    {
        readonly List<GameObject> created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                Object.DestroyImmediate(go);
            created.Clear();
        }

        Health Unit()
        {
            var go = new GameObject("Hostile");
            created.Add(go);
            var health = go.AddComponent<Health>();
            health.Initialize(10);
            return health;
        }

        sealed class Stub : MissionObjective
        {
            public Stub(string id, ObjectiveType type, string title) : base(id, type, title, true) { }
            public override string Describe() => Title;
        }

        // MissionRuntime refuses a required extraction and a null squad: this one is optional and the squad is empty.
        sealed class ExtractionStub : MissionObjective
        {
            public ExtractionStub() : base("extract", ObjectiveType.ReachZone, "Extraction", false) { }
            public override string Describe() => Title;
        }

        static MissionRuntime Runtime(params MissionObjective[] goals) =>
            new MissionRuntime(goals, new ExtractionStub(), new List<Health>());

        [Test]
        public void VagueTitle_DefaultsToNull_AndCanBeSet()
        {
            var goal = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            Assert.That(goal.VagueTitle, Is.Null);
            goal.SetVagueTitle("Locate the data terminal");
            Assert.That(goal.VagueTitle, Is.EqualTo("Locate the data terminal"));
        }

        [Test]
        public void Panel_HidesAnUnknownObjective_ByDefault()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetVagueTitle("Locate the data terminal");
            hack.SetKnown(false);
            var text = MissionHudText.Panel(Runtime(hack));
            Assert.That(text, Does.Not.Contain("Access data terminal").And.Not.Contain("Locate the data terminal"));
        }

        [Test]
        public void Panel_WithListUnknown_ShowsTheVagueTitle_AndNeverTheRealOne()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetVagueTitle("Locate the data terminal");
            hack.SetKnown(false);
            var text = MissionHudText.Panel(Runtime(hack), listUnknown: true);
            Assert.That(text, Does.Contain("[?] Locate the data terminal"));
            Assert.That(text, Does.Not.Contain("Access data terminal"));
        }

        [Test]
        public void Panel_WithListUnknown_SkipsAnUnknownObjectiveWithoutAVagueTitle()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetKnown(false);
            Assert.That(MissionHudText.Panel(Runtime(hack), listUnknown: true), Does.Not.Contain("Access data terminal"));
        }

        [Test]
        public void Panel_ShowsAKnownObjectiveNormally_WhateverTheFlag()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetVagueTitle("Locate the data terminal");
            var text = MissionHudText.Panel(Runtime(hack), listUnknown: true);
            Assert.That(text, Does.Contain("Access data terminal"));
            Assert.That(text, Does.Not.Contain("Locate the data terminal"));
        }

        [Test]
        public void MarkerLabel_IsEmptyForAnUnknownObjective()
        {
            var hack = new Stub("hack", ObjectiveType.Interact, "Access data terminal");
            hack.SetKnown(false);
            Assert.That(MissionHudText.MarkerLabel(hack), Is.Empty);
        }

        [Test]
        public void Eliminate_ShowsLivingOverTotal_ByDefault()
        {
            var group = new List<Health> { Unit(), Unit(), Unit() };
            var objective = new EliminateHostilesObjective("e", "Eliminate security team", group);
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team (3/3)"));
        }

        [Test]
        public void Eliminate_WithCountsHidden_ShowsOnlyWhatTheSquadHasDone()
        {
            var group = new List<Health> { Unit(), Unit(), Unit() };
            var objective = new EliminateHostilesObjective("e", "Eliminate security team", group) { ShowCounts = false };
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team"), "no count leaks how many there are");
            group[0].TakeDamage(100);
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team (1 down)"));
            group[1].TakeDamage(100);
            group[2].TakeDamage(100);
            objective.Activate();
            objective.Evaluate();
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team"), "completed: the title alone, as before");
        }
    }
}
```

The extraction stub's own `IsKnown` is true by default, so every `Panel` assertion above also sees the line `[LOCKED] Extraction`; none of them asserts its absence.

Create `Assets/_Project/Tests/PlayMode/ObjectiveKnowledgeMissionTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ObjectiveKnowledgeMissionTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator EveryGeneratedObjective_HasAVagueTitle_ThatNamesNoLocationAndNoCount()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            foreach (var goal in rig.Director.Runtime.Objectives)
            {
                Assert.That(goal.VagueTitle, Is.Not.Null.And.Not.Empty, goal.Id);
                Assert.That(goal.VagueTitle, Does.Not.Match(@"\d"), $"{goal.Id}: no numbers");
            }
        }

        [UnityTest]
        public IEnumerator ABlindMission_ListsTheUnknownObjectivesVaguely_UntilTheyAreLearned()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            var runtime = rig.Director.Runtime;

            var hidden = MissionHudText.Panel(runtime, service.ListsUnknownObjectives);
            Assert.That(service.ListsUnknownObjectives, Is.True);
            Assert.That(hidden, Does.Contain("Locate the data terminal"));
            Assert.That(hidden, Does.Not.Contain("Access data terminal"), "the real objective is not named yet");
            Assert.That(hidden, Does.Contain("Extraction"), "extraction was known from the start");
            Assert.That(hidden, Does.Not.Match(@"\(\d+/\d+\)"), "no hostile count");

            service.Model.RevealArea(rig.Director.Current.Terminal.Position, 1f);   // the terminal's region is discovered
            var learned = MissionHudText.Panel(runtime, service.ListsUnknownObjectives);
            Assert.That(learned, Does.Contain("Access data terminal"));
            Assert.That(learned, Does.Not.Contain("Locate the data terminal"));
        }

        [UnityTest]
        public IEnumerator MissionCompletion_DoesNotDependOnKnowledge()
        {
            rig = new MissionRig();
            rig.AddIntelligence(IntelligenceSettings.Blind());
            yield return rig.Generate(12345);
            var runtime = rig.Director.Runtime;
            Assert.That(runtime.Objectives.Count(g => !g.IsKnown), Is.GreaterThan(0), "Precondition: something is unknown");
            foreach (var hostile in rig.Director.Hostiles)
            {
                hostile.GetComponent<EnemyAI>().enabled = false;
                hostile.GetComponent<Health>().TakeDamage(1000);
            }
            yield return null;
            var eliminate = runtime.Objectives.Single(g => g.Type == ObjectiveType.EliminateHostiles);
            Assert.That(eliminate.State, Is.EqualTo(ObjectiveState.Completed), "an unknown objective still completes");
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.ObjectiveKnowledgeTextTests` -> `EXIT=1` (`VagueTitle`, `SetVagueTitle`, `Panel(..., listUnknown)` missing).

- [ ] **Step 3: Implement**

`MissionObjective.cs`: after `IsKnown` add

```csharp
        /// <summary>
        /// What the HUD says about this objective while the player does not know it exists yet ("Locate the data terminal"):
        /// no place, no count. Null lists nothing. Only shown when the mission's intelligence settings ask for it.
        /// </summary>
        public string VagueTitle { get; private set; }
```

and after `SetKnown`:

```csharp
        public void SetVagueTitle(string title) => VagueTitle = title;
```

`EliminateHostilesObjective.cs`: replace `Describe` with

```csharp
        public override string Describe()
        {
            if (State == ObjectiveState.Completed)
                return Title;
            if (ShowCounts)
                return $"{Title} ({Living}/{group.Count})";
            var down = group.Count - Living;
            return down > 0 ? $"{Title} ({down} down)" : Title;
        }
```

`MissionHud.cs` (`MissionHudText.Panel`): replace the method with

```csharp
        /// <summary>
        /// The MISSION panel: the phase, then one line per objective the player knows about. With `listUnknown`, an unknown
        /// objective that has a vague title is listed by it ("[?] Locate the data terminal"), never by its real title.
        /// </summary>
        public static string Panel(MissionRuntime runtime, bool listUnknown = false)
        {
            var text = new StringBuilder("MISSION - ").Append(PhaseLabel(runtime.Phase));
            foreach (var objective in runtime.Objectives)
            {
                if (objective.IsKnown)
                    text.Append('\n').Append(Line(objective));
                else if (listUnknown && !string.IsNullOrEmpty(objective.VagueTitle))
                    text.Append("\n[?] ").Append(objective.VagueTitle);
            }
            return text.ToString();
        }
```

`MissionHud` (the MonoBehaviour): add the field and setter next to the other serialized fields, and use them:

```csharp
        // Optional: with fog on and showUnknownObjectives, unknown objectives are listed by their vague titles.
        [SerializeField] IntelligenceService intelligence;

        internal void SetIntelligence(IntelligenceService service) => intelligence = service;
```

and in `OnGUI` change `var panel = MissionHudText.Panel(runtime);` to `var panel = MissionHudText.Panel(runtime, intelligence != null && intelligence.ListsUnknownObjectives);`.

`MissionContent.CreateRuntime`: replace the two goal constructions with

```csharp
            if (settings.eliminateHostiles)
            {
                var eliminate = new EliminateHostilesObjective("eliminate", "Eliminate security team", hostileGroup);
                eliminate.SetVagueTitle("Neutralise the hostile presence");
                goals.Add(eliminate);
            }
            if (mission.Terminal != null)
            {
                var hack = new InteractObjective("hack", "Access data terminal", mission.Terminal);
                hack.SetVagueTitle("Locate the data terminal");
                goals.Add(hack);
            }
            var extraction = new ReachZoneObjective("extract", "Extraction", mission.ExtractionZone.position, ZoneRadius,
                settings.extractionUnits, squad, isRequired: false);
            extraction.SetVagueTitle("Find the extraction point");
```

- [ ] **Step 4: Run to verify pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.ObjectiveKnowledgeTextTests`, `Tools/run-tests.sh EditMode Blackglass.Tests.MissionHudTextTests`, `Tools/run-tests.sh EditMode Blackglass.Tests.MissionObjectivesTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.ObjectiveKnowledgeMissionTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.MissionRuntimePlayModeTests`. Expected: all `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Scripts/Mission Assets/_Project/Tests/EditMode/ObjectiveKnowledgeTextTests.cs Assets/_Project/Tests/EditMode/ObjectiveKnowledgeTextTests.cs.meta Assets/_Project/Tests/PlayMode/ObjectiveKnowledgeMissionTests.cs Assets/_Project/Tests/PlayMode/ObjectiveKnowledgeMissionTests.cs.meta
git commit -m "List unknown objectives vaguely, hide the hostile count while fog is on" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Targeting gates (pointer, cursor, abilities, companions, terminals)

**Files:**
- Modify: `Assets/_Project/Scripts/Input/HostileTargets.cs`, `PointerTargetResolver.cs`, `TacticalCursor.cs`; `Assets/_Project/Scripts/Mission/InteractableRegistry.cs`, `MissionSpawner.cs`; `Assets/_Project/Scripts/Abilities/AbilityTargeting.cs`, `UnitAbilities.cs`; `Assets/_Project/Scripts/AI/CompanionAI.cs`; `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`
- Test: `Assets/_Project/Tests/EditMode/HostileTargetsFilterTests.cs`, `Assets/_Project/Tests/PlayMode/IntelTargetingTests.cs`, `Assets/_Project/Tests/PlayMode/IntelTargetingCursorTests.cs`, `Assets/_Project/Tests/PlayMode/IntelAbilityTargetingTests.cs`

**Interfaces:**
- Consumes: `IntelligenceService`, `Knowledge` (Task 6).
- Produces:
  - `HostileTargets.Best(hostiles, origin, facing, Func<Health,bool> accept = null)` and `HostileTargets.Cycle(hostiles, origin, current, direction, Func<Health,bool> accept = null)`.
  - `InteractableRegistry.NearestAvailable(Vector3 point, float radius, Func<MissionInteractable,bool> accept)`.
  - `PointerTargetResolver.Resolve(camera, screenPoint, maxDistance, layers, registry, coverRadius, IntelligenceService intelligence)` (the six-argument overload stays and passes `null`).
  - Optional serialized `intelligence` plus `internal void SetIntelligence(IntelligenceService)` on `TacticalCursor`, `AbilityTargeting`, `UnitAbilities`, `CompanionAI`, `PlayerCommandInput`.
  - `CompanionAI.ChooseAssistTarget()` becomes `internal` (the instance method; the public static overload is unchanged).
  - `AbilityTargeting` filters `areaHits` to targetable hostiles and `TargetHealth` to known targets; `UnitAbilities.Check` treats an unobserved hostile target as "no target".

**Rule recap (spec "Targeting and anti-leak rules"):** an enemy is a target iff Observed; a last-known marker is never a target; ground abilities may still be aimed anywhere in range; the real area effect still hits every hostile it should (the gate is display only).

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/HostileTargetsFilterTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class HostileTargetsFilterTests
    {
        readonly List<GameObject> created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                Object.DestroyImmediate(go);
            created.Clear();
        }

        Health Hostile(float x, float z)
        {
            var go = new GameObject("Hostile");
            go.transform.position = new Vector3(x, 0f, z);
            created.Add(go);
            var health = go.AddComponent<Health>();
            health.Initialize(100);
            return health;
        }

        [Test]
        public void Best_SkipsACandidateTheFilterRejects()
        {
            var hidden = Hostile(0f, 2f);
            var shown = Hostile(0f, 9f);
            Assert.That(HostileTargets.Best(new[] { hidden, shown }, Vector3.zero, Vector3.zero, h => h == shown), Is.SameAs(shown));
            Assert.That(HostileTargets.Best(new[] { hidden, shown }, Vector3.zero, Vector3.zero, h => false), Is.Null);
            Assert.That(HostileTargets.Best(new[] { hidden, shown }, Vector3.zero, Vector3.zero), Is.SameAs(hidden), "no filter: as before");
        }

        [Test]
        public void Cycle_WalksOnlyTheCandidatesTheFilterAdmits()
        {
            var a = Hostile(0f, 2f);
            var b = Hostile(0f, 5f);
            var c = Hostile(0f, 9f);
            System.Func<Health, bool> onlyAC = h => h != b;
            Assert.That(HostileTargets.Cycle(new[] { a, b, c }, Vector3.zero, null, 1, onlyAC), Is.SameAs(a));
            Assert.That(HostileTargets.Cycle(new[] { a, b, c }, Vector3.zero, a, 1, onlyAC), Is.SameAs(c), "b is skipped");
            Assert.That(HostileTargets.Cycle(new[] { a, b, c }, Vector3.zero, c, 1, onlyAC), Is.SameAs(a), "and it wraps");
            Assert.That(HostileTargets.Cycle(new[] { a, b, c }, Vector3.zero, null, 1, h => false), Is.Null);
        }
    }
}
```

Create `Assets/_Project/Tests/PlayMode/IntelTargetingTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The pointer resolver, ability validation, companions and terminals respect what the player knows.</summary>
    public class IntelTargetingTests
    {
        IntelRig rig;
        Camera viewCamera;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        void MakeCamera()
        {
            var cameraObject = rig.World.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();
        }

        PointerTarget Resolve(Vector3 worldPoint, IntelligenceService intelligence) =>
            PointerTargetResolver.Resolve(viewCamera, viewCamera.WorldToScreenPoint(worldPoint), 500f, ~0, null, 0f, intelligence);

        // ---- the pointer ----

        [UnityTest]
        public IEnumerator AClickOnAnUnobservedHostile_ResolvesToGround_NotToTheHostile()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            MakeCamera();
            yield return null;

            var target = Resolve(hostile.transform.position, rig.Service);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Ground), "the collider is still there, the knowledge is not");
            Assert.That(target.Hostile, Is.Null);
        }

        [UnityTest]
        public IEnumerator AClickOnAnObservedHostile_StillResolvesToTheHostile()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            MakeCamera();
            yield return null;
            Assert.That(rig.Service.CanTarget(hostile), Is.True, "Precondition");

            var target = Resolve(hostile.transform.position, rig.Service);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(target.Hostile, Is.SameAs(hostile));
        }

        [UnityTest]
        public IEnumerator WithoutAService_OrWithFogOff_TheResolverIsAsBefore()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());   // fog off
            MakeCamera();
            yield return null;
            Assert.That(Resolve(hostile.transform.position, null).Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(Resolve(hostile.transform.position, rig.Service).Kind, Is.EqualTo(PointerTargetKind.Hostile));
        }

        [UnityTest]
        public IEnumerator AClickOnALastKnownMarkerSpot_IsGround_NotATarget()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.enemyMarkersAtStart = 1));
            MakeCamera();
            yield return null;
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(Resolve(hostile.transform.position, rig.Service).Kind, Is.EqualTo(PointerTargetKind.Ground));
        }

        [UnityTest]
        public IEnumerator AClickOnAnUnknownCameraTerminal_IsNotAnInteractable_UntilItsDeviceIsDiscovered()
        {
            rig = new IntelRig(corridor: false);
            var host = rig.World.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            host.name = "CameraTerminal";
            host.transform.position = new Vector3(-6.5f, 0.6f, -3f);
            host.transform.localScale = new Vector3(0.8f, 1.2f, 0.8f);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, 2f, "Camera control");
            terminal.SetAvailable(true);
            Physics.SyncTransforms();
            var security = new SecurityPlan(true, 0, new Vector2Int(19, 9), new CameraMount[0]);   // device 0 in the far corner: not seen
            rig.Begin(IntelRig.Fog(), new IntelligenceMission { Security = security, CameraTerminal = terminal });
            MakeCamera();
            yield return null;

            Assert.That(Resolve(host.transform.position + Vector3.up * 0.2f, rig.Service).Kind, Is.EqualTo(PointerTargetKind.Ground));
            rig.Service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            Assert.That(Resolve(host.transform.position + Vector3.up * 0.2f, rig.Service).Kind, Is.EqualTo(PointerTargetKind.Interactable));
        }

        [UnityTest]
        public IEnumerator AFriendlyIsAlwaysResolved_FogOrNot()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog());
            MakeCamera();
            yield return null;
            var target = Resolve(rig.Friendly.transform.position, rig.Service);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
        }

        // ---- ability validation ----

        [UnityTest]
        public IEnumerator AUnitAbility_OnAnUnobservedHostile_FailsAsNoTarget_AndWorksOnceItIsObserved()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.OffAxisGround);   // behind the corridor wall: not observed
            rig.Begin(IntelRig.Fog());
            var aimed = rig.World.CreateAimedShot();
            var abilities = rig.World.AddAbilities(rig.Friendly.Unit, rig.Encounter, aimed);
            abilities.SetIntelligence(rig.Service);
            yield return null;

            var hidden = abilities.Check(aimed, hostile, null);
            Assert.That(hidden.Failure, Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max), "nothing was hit");

            rig.Service.Scan(hostile.transform.position, 2f, 5f);
            Assert.That(abilities.Check(aimed, hostile, null).Failure, Is.Not.EqualTo(AbilityFailure.NoTarget));
        }

        [UnityTest]
        public IEnumerator AHealOnAFriendly_IsNeverGated()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog());
            var mend = rig.World.CreateMend();
            var abilities = rig.World.AddAbilities(rig.Friendly.Unit, rig.Encounter, mend);
            abilities.SetIntelligence(rig.Service);
            yield return null;
            Assert.That(abilities.Check(mend, rig.FriendlyHealth, null).Failure, Is.Not.EqualTo(AbilityFailure.NoTarget));
        }

        // ---- companions ----

        [UnityTest]
        public IEnumerator ACompanion_DoesNotAssistAgainstAnUnobservedEngagedHostile()
        {
            rig = new IntelRig(corridor: false);
            var engaged = rig.World.CreateFighter(new Vector3(-1f, 0f, 8f), 60, 10, 1f, CombatRole.Melee, 2f);
            engaged.name = "Engaged";
            var engagedHealth = engaged.GetComponent<Health>();
            rig.Hostiles.Add(engagedHealth);
            rig.Begin(IntelRig.Fog());
            var active = rig.World.CreateActiveCharacter(rig.Friendly.Unit);
            var companion = rig.World.CreateCompanion(new Vector3(-8f, 0f, 8f), active, rig.Encounter);
            companion.Initialize(active, rig.Encounter, assist: 30f);
            companion.SetIntelligence(rig.Service);
            Assert.That(engaged.Issue(new AttackCommand(rig.FriendlyHealth)), Is.True, "Precondition: the hostile is engaged");
            yield return null;
            Assert.That(rig.Service.CanTarget(engagedHealth), Is.False, "Precondition: nobody has seen it");

            Assert.That(companion.ChooseAssistTarget(), Is.Null);
            rig.Service.Scan(engaged.transform.position, 3f, 5f);
            Assert.That(companion.ChooseAssistTarget(), Is.SameAs(engagedHealth), "once it is seen the companion helps");
        }

        // ---- terminals ----

        [UnityTest]
        public IEnumerator NearestAvailable_WithAFilter_SkipsWhatTheFilterRejects()
        {
            rig = new IntelRig(corridor: false);
            var registry = rig.World.Track(new GameObject("Interactables")).AddComponent<InteractableRegistry>();
            MissionInteractable Make(float x)
            {
                var host = rig.World.Track(new GameObject("T" + x));
                host.transform.position = new Vector3(x, 0f, 0f);
                var item = host.AddComponent<MissionInteractable>();
                item.Initialize(1.8f, 2f);
                item.SetAvailable(true);
                return item;
            }
            var near = Make(1f);
            var far = Make(2f);
            registry.Rebuild(new[] { near, far });
            yield return null;
            Assert.That(registry.NearestAvailable(Vector3.zero, 5f), Is.SameAs(near));
            Assert.That(registry.NearestAvailable(Vector3.zero, 5f, item => item != near), Is.SameAs(far));
            Assert.That(registry.NearestAvailable(Vector3.zero, 5f, item => false), Is.Null);
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/IntelTargetingCursorTests.cs` (the controller path: snapping, cycling, soft target, Attack fallback):

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelTargetingCursorTests : InputTestFixture
    {
        InputActionAsset actions;
        IntelRig rig;
        Camera viewCamera;
        TacticalCursor cursor;
        Health shown;
        Health hidden;

        public override void Setup()
        {
            base.Setup();
            InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
        }

        public override void TearDown()
        {
            rig?.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        // The corridor is open: the hostile in line is observed, the one off the corridor's axis is not.
        void Build()
        {
            rig = new IntelRig(corridor: true);
            shown = rig.AddHostile(IntelRig.InLineGround);
            hidden = rig.AddHostile(IntelRig.OffAxisGround);
            rig.Begin(IntelRig.Fog());

            var cameraObject = rig.World.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = rig.World.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(rig.Friendly);
            var active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(rig.Friendly.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, rig.Encounter, null, null,
                TestControls.Ref(actions, "Commands/CursorMove"), TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"), TestControls.Ref(actions, "Commands/PreviousTarget"));
            cursor.SetIntelligence(rig.Service);
            systems.SetActive(true);
            pause.Pause();   // the cursor owns the right stick while paused
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        [UnityTest]
        public IEnumerator Cycling_SkipsTheUnobservedHostile_AndFindsNothingWhenNoneIsObserved()
        {
            Build();
            yield return null;
            cursor.CycleTarget(1);
            Assert.That(cursor.SoftTarget, Is.SameAs(shown));
            cursor.CycleTarget(1);
            Assert.That(cursor.SoftTarget, Is.SameAs(shown), "the only observed hostile again: the hidden one is never visited");
            cursor.CycleTarget(-1);
            Assert.That(cursor.SoftTarget, Is.SameAs(shown));

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);   // the corridor is out of line: nobody is observed
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(cursor.SoftTarget, Is.Null, "a soft target that stops being observed stops being one");
            cursor.CycleTarget(1);
            Assert.That(cursor.SoftTarget, Is.Null);
        }

        [UnityTest]
        public IEnumerator PickAttackTarget_IgnoresUnobservedHostiles()
        {
            Build();
            yield return null;
            var origin = rig.Friendly.transform.position;
            Assert.That(cursor.PickAttackTarget(origin, Vector3.right), Is.SameAs(shown));

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(cursor.PickAttackTarget(rig.Friendly.transform.position, Vector3.right), Is.Null,
                "no observed hostile: Attack has nothing to do, even though two living hostiles exist");
        }

        [UnityTest]
        public IEnumerator TheSnap_NeverLandsOnAnUnobservedHostile()
        {
            Build();
            yield return null;
            cursor.SetScreenPosition(ScreenPointOf(hidden.transform.position));
            Assert.That(cursor.Target.Kind, Is.Not.EqualTo(PointerTargetKind.Hostile));
            cursor.SetScreenPosition(ScreenPointOf(shown.transform.position));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(shown));
        }

        [UnityTest]
        public IEnumerator WhileAimingAtHostiles_TheSnapStillIgnoresTheUnobserved()
        {
            Build();
            yield return null;
            cursor.Aiming = true;
            cursor.SnapTo = PointerTargetKind.Hostile;
            // Ground next to the hidden hostile: within the 1.2 m snap radius, which would snap if it were allowed.
            cursor.SetScreenPosition(ScreenPointOf(hidden.transform.position + new Vector3(0.5f, -1f, 0f)));
            Assert.That(cursor.Target.Kind, Is.Not.EqualTo(PointerTargetKind.Hostile));
        }

        [UnityTest]
        public IEnumerator FogOff_LeavesTheCursorAsBefore()
        {
            rig = new IntelRig(corridor: false);
            var behindWall = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());
            var cameraObject = rig.World.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();
            var systems = rig.World.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(rig.Friendly);
            var active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(rig.Friendly.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, rig.Encounter, null, null,
                TestControls.Ref(actions, "Commands/CursorMove"), TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"), TestControls.Ref(actions, "Commands/PreviousTarget"));
            cursor.SetIntelligence(rig.Service);
            systems.SetActive(true);
            yield return null;
            cursor.CycleTarget(1);
            Assert.That(cursor.SoftTarget, Is.SameAs(behindWall));
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/IntelAbilityTargetingTests.cs` (the `AbilityTargeting` and `PlayerCommandInput` paths on the existing `AbilityRig`):

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelAbilityTargetingTests : InputTestFixture
    {
        InputActionAsset actions;
        AbilityRig rig;
        IntelligenceService service;

        public override void Setup()
        {
            base.Setup();
            InputSystem.AddDevice<Keyboard>();
            InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = AbilityRig.Build(actions, false);
        }

        public override void TearDown()
        {
            service.Clear();
            rig.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        // One big room covering the whole arena (tile (x, y) is world (x - 20, y - 20)); walls and range do the hiding.
        static MissionLayout ArenaLayout()
        {
            const int size = 40;
            var floor = new bool[size * size];
            for (var i = 0; i < floor.Length; i++)
                floor[i] = true;
            return new MissionLayout(1, 1, size, size, floor)
            {
                Rooms = new[] { new MissionRoom(0, Vector2Int.zero, new RectInt(0, 0, size, size)) },
                FriendlyRoom = 0,
            };
        }

        void BeginFog(float observationRange, IntelligenceMission mission = null)
        {
            mission = mission ?? new IntelligenceMission();
            mission.Layout = ArenaLayout();
            service = rig.World.Track(new GameObject("Intelligence")).AddComponent<IntelligenceService>();
            service.Begin(mission,
                new IntelligenceSettings { fogEnabled = true, map = MapKnowledge.None, objectives = ObjectiveKnowledge.None, observationRange = observationRange },
                rig.Encounter);
            rig.Targeting.SetIntelligence(service);
            rig.Abilities.SetIntelligence(service);
            rig.Input.SetIntelligence(service);
        }

        [UnityTest]
        public IEnumerator AnAbilityAimedAtAnUnobservedHostile_PreviewsAsNoAim_AndConfirmingItIsRefused()
        {
            BeginFog(observationRange: 4f);   // the near hostile is 8.5 m from the caster
            yield return null;
            Assert.That(service.CanTarget(rig.Hostile), Is.False, "Precondition");
            Assert.That(rig.Targeting.Arm(0), Is.True);

            var pointed = PointerTarget.OnHostile(rig.Hostile, rig.Hostile.transform.position);
            var preview = rig.Targeting.Evaluate(rig.Aimed, pointed, queued: false);
            Assert.That(preview.HasAim, Is.False, "an unobserved hostile is not a target");
            Assert.That(preview.Failure, Is.EqualTo(AbilityFailure.NoTarget));

            Assert.That(rig.Targeting.Confirm(pointed, queue: false), Is.False);
            Assert.That(rig.Abilities.LastFailure, Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max));
            Assert.That(rig.Targeting.IsArmed, Is.True, "still armed: another target can be tried");
        }

        [UnityTest]
        public IEnumerator AGroundAbility_CanBeAimedAnywhereInRange_ButItsPreviewListsOnlyObservedHostiles()
        {
            BeginFog(observationRange: 4f);
            yield return null;
            Assert.That(rig.Targeting.Arm(1), Is.True);   // Blast: ground, radius 3
            var pointed = PointerTarget.OnGround(AbilityRig.BlastGround);   // 2 m from the unobserved near hostile
            var preview = rig.Targeting.Evaluate(rig.Blast, pointed, queued: false);

            Assert.That(preview.HasAim, Is.True, "undiscovered ground can be aimed at");
            Assert.That(preview.Check.IsValid, Is.True, preview.Check.Failure.ToString());
            Assert.That(rig.Targeting.AreaHits, Is.Empty, "the preview names nobody the player cannot see");
            Assert.That(AbilityDescriptions.Preview(preview, rig.Targeting.AreaHits), Does.Contain("hits 0"));
        }

        [UnityTest]
        public IEnumerator AGroundAbility_StillHitsEveryHostileInTheBlast_EvenOnesTheCasterCannotSee()
        {
            BeginFog(observationRange: 4f);
            yield return null;
            var before = rig.Hostile.Current;
            Assert.That(rig.Abilities.TryUse(AbilityCommand.AtGround(rig.Blast, AbilityRig.BlastGround)), Is.True);
            Assert.That(rig.Hostile.Current, Is.LessThan(before), "blind fire works: the gate is display only");
        }

        [UnityTest]
        public IEnumerator TheGateOpens_WhenTheHostileIsObserved()
        {
            BeginFog(observationRange: 14f);   // 8.5 m: inside the range, in open sight
            yield return null;
            Assert.That(service.CanTarget(rig.Hostile), Is.True, "Precondition");
            Assert.That(rig.Targeting.Arm(1), Is.True);
            rig.Targeting.Evaluate(rig.Blast, PointerTarget.OnGround(AbilityRig.BlastGround), queued: false);
            Assert.That(rig.Targeting.AreaHits, Does.Contain(rig.Hostile));
        }

        [UnityTest]
        public IEnumerator TheTerminalPrompt_FollowsWhetherTheCameraTerminalDeviceIsKnown()
        {
            // The terminal stands beside the caster but its device (the plan's tile, in the far corner of the arena) is not seen.
            var host = rig.World.Track(new GameObject("CameraTerminal"));
            host.transform.position = rig.Caster.transform.position + new Vector3(1f, -1f, 0f);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, 2f, "Camera control");
            terminal.SetAvailable(true);
            var registry = rig.World.Track(new GameObject("Interactables")).AddComponent<InteractableRegistry>();
            registry.Rebuild(new[] { terminal });
            rig.Input.WireInteraction(registry, null);

            BeginFog(observationRange: 4f, new IntelligenceMission
            {
                Security = new SecurityPlan(true, 0, new Vector2Int(39, 39), new CameraMount[0]),
                CameraTerminal = terminal,
            });
            yield return null;

            Assert.That(rig.Input.NearbyInteractable, Is.Null, "the unknown terminal is not offered, though the caster stands beside it");
            service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            Assert.That(rig.Input.NearbyInteractable, Is.SameAs(terminal));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.HostileTargetsFilterTests` -> `EXIT=1` (the `accept` parameters and `SetIntelligence` methods do not exist).

- [ ] **Step 3: Implement**

`HostileTargets.cs`: add `using System;`; change the two signatures and their loops:

```csharp
        public static Health Best(IReadOnlyList<Health> hostiles, Vector3 origin, Vector3 facing, Func<Health, bool> accept = null)
```

with the loop guard `if (!IsValid(candidate) || (accept != null && !accept(candidate))) continue;`, and

```csharp
        public static Health Cycle(IReadOnlyList<Health> hostiles, Vector3 origin, Health current, int direction, Func<Health, bool> accept = null)
```

with `if (IsValid(hostiles[i]) && (accept == null || accept(hostiles[i])))` in place of `if (IsValid(hostiles[i]))`. Update the class summary with one sentence: "An optional filter keeps targets the player cannot see out of every choice (decision 037)."

`InteractableRegistry.cs`: add `using System;` (already there) and replace `NearestAvailable` with an overload pair:

```csharp
        /// <summary>The closest available interactable within `radius` (flat distance) of `point`, or null.</summary>
        public MissionInteractable NearestAvailable(Vector3 point, float radius) => NearestAvailable(point, radius, null);

        /// <summary>As above, skipping any item the filter rejects (the player does not know about it yet).</summary>
        public MissionInteractable NearestAvailable(Vector3 point, float radius, Func<MissionInteractable, bool> accept)
        {
            MissionInteractable best = null;
            var bestDistance = radius;
            foreach (var item in items)
            {
                if (item == null || !item.IsAvailable || (accept != null && !accept(item)))
                    continue;
                var distance = CoverRules.FlatDistance(point, item.Position);
                if (distance > bestDistance)
                    continue;
                best = item;
                bestDistance = distance;
            }
            return best;
        }
```

`PointerTargetResolver.cs`: replace the file with:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Turns a screen point into a PointerTarget: a friendly unit, a living hostile, an available interactable (a
    /// terminal), a cover location within coverRadius of the hit point, or plain ground. The one place a pointer is classified, so the mouse click and the
    /// controller cursor agree. Raycasts the physics scene; while paused no physics step runs, so moved transforms are
    /// pushed to physics first. With an IntelligenceService whose fog is on, the ray passes through what the player cannot
    /// see (an unobserved hostile's collider, an unknown terminal) and cover in unknown regions is not offered, so a click
    /// can never confirm something the player has not found (decision 037).
    /// </summary>
    public static class PointerTargetResolver
    {
        static readonly Func<CoverLocation, bool> acceptAny = _ => true;

        public static PointerTarget Resolve(Camera camera, Vector2 screenPoint, float maxDistance, LayerMask layers,
            CoverRegistry registry, float coverRadius) =>
            Resolve(camera, screenPoint, maxDistance, layers, registry, coverRadius, null);

        public static PointerTarget Resolve(Camera camera, Vector2 screenPoint, float maxDistance, LayerMask layers,
            CoverRegistry registry, float coverRadius, IntelligenceService intelligence)
        {
            if (camera == null)
                return PointerTarget.None;

            Physics.SyncTransforms();
            var ray = camera.ScreenPointToRay(screenPoint);
            var gated = intelligence != null && intelligence.IsFogActive;
            RaycastHit hit;
            if (!gated)
            {
                if (!Physics.Raycast(ray, out hit, maxDistance, layers, QueryTriggerInteraction.Ignore))
                    return PointerTarget.None;
            }
            else if (!TryFirstKnownHit(ray, maxDistance, layers, intelligence, out hit))
            {
                return PointerTarget.None;
            }

            var friendly = hit.collider.GetComponentInParent<SelectableUnit>();
            if (friendly != null)
                return PointerTarget.OnFriendly(friendly, hit.point);

            var health = hit.collider.GetComponentInParent<Health>();
            if (health != null && health.IsAlive)
                return PointerTarget.OnHostile(health, hit.point);

            var interactable = hit.collider.GetComponentInParent<MissionInteractable>();
            if (interactable != null && interactable.IsAvailable)
                return PointerTarget.OnInteractable(interactable, hit.point);

            if (registry != null && coverRadius > 0f
                && CoverRules.TryChooseNearest(registry.Points, hit.point, coverRadius, gated ? intelligence.CanSeeCover : acceptAny, out var cover))
                return PointerTarget.OnCover(cover, hit.point);

            return PointerTarget.OnGround(hit.point);
        }

        // The first collider along the ray that is not hidden from the player. Only used while fog is on, so the per-call
        // allocation of RaycastAll costs nothing in the usual case.
        static bool TryFirstKnownHit(Ray ray, float maxDistance, LayerMask layers, IntelligenceService intelligence, out RaycastHit first)
        {
            var all = Physics.RaycastAll(ray, maxDistance, layers, QueryTriggerInteraction.Ignore);
            Array.Sort(all, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in all)
            {
                if (IsHidden(hit.collider, intelligence))
                    continue;
                first = hit;
                return true;
            }
            first = default;
            return false;
        }

        // A hostile (alive, unit on the other side) the player has not observed, or a terminal the player has not found.
        static bool IsHidden(Collider collider, IntelligenceService intelligence)
        {
            if (collider.GetComponentInParent<SelectableUnit>() != null)
                return false;
            var health = collider.GetComponentInParent<Health>();
            if (health != null && health.IsAlive)
                return !intelligence.CanTarget(health);
            var interactable = collider.GetComponentInParent<MissionInteractable>();
            return interactable != null && interactable.IsAvailable && !intelligence.CanInteract(interactable);
        }
    }
}
```

`TacticalCursor.cs` (apply all of these edits):

1. Replace `static readonly Func<CoverLocation, bool> acceptAny = _ => true;` with nothing (delete the line) and add after the `interactables` field:

```csharp
        // Optional: without it every hostile is a valid target and every terminal and cover location is known (decision 037).
        [SerializeField] IntelligenceService intelligence;
```

2. After `SetInteractables` add:

```csharp
        internal void SetIntelligence(IntelligenceService service) => intelligence = service;

        // Cached so the per-frame snapping and cycling allocate nothing.
        Func<Health, bool> canTarget;
        Func<MissionInteractable, bool> canInteract;
        Func<CoverLocation, bool> canSeeCover;
        Func<Health, bool> CanTargetFilter => canTarget ??= unit => Knowledge.CanTarget(intelligence, unit);
        Func<MissionInteractable, bool> CanInteractFilter => canInteract ??= item => Knowledge.CanInteract(intelligence, item);
        Func<CoverLocation, bool> CanSeeCoverFilter => canSeeCover ??= point => Knowledge.CanSeeCover(intelligence, point);
```

3. `SoftTarget`: `public Health SoftTarget => HostileTargets.IsValid(softTarget) && Knowledge.CanTarget(intelligence, softTarget) ? softTarget : null;`

4. `PickAttackTarget`: change the first condition to `if (IsActive && target.Kind == PointerTargetKind.Hostile && HostileTargets.IsValid(target.Hostile) && Knowledge.CanTarget(intelligence, target.Hostile))` and the last line to `return HostileTargets.Best(encounter.Hostiles, origin, facing, CanTargetFilter);`.

5. `CycleTarget`: `var next = HostileTargets.Cycle(encounter.Hostiles, CycleOrigin(), SoftTarget, direction, CanTargetFilter);`

6. `Resolve(Vector2 point)`: the raw call becomes `PointerTargetResolver.Resolve(viewCamera, point, maxDistance, clickableLayers, null, 0f, intelligence);`, the interactable line `interactables.NearestAvailable(ground, interactableSnapRadius, CanInteractFilter)`, and the cover line `CoverRules.TryChooseNearest(coverRegistry.Points, ground, coverSnapRadius, CanSeeCoverFilter, out var cover)`.

7. `NearestHostile`: `if (!HostileTargets.IsValid(hostile) || !Knowledge.CanTarget(intelligence, hostile)) continue;`

`AbilityTargeting.cs`:

1. Fields/setter (after `cursor`): `[SerializeField] IntelligenceService intelligence;` and `internal void SetIntelligence(IntelligenceService service) => intelligence = service;`
2. `ResolvePointer`: pass `intelligence` as the last argument of `PointerTargetResolver.Resolve(...)`.
3. `TargetHealth` becomes an instance method (remove `static`) and gates hostiles:

```csharp
        Health TargetHealth(PointerTarget pointed)
        {
            switch (pointed.Kind)
            {
                case PointerTargetKind.Hostile:
                    return Knowledge.CanTarget(intelligence, pointed.Hostile) ? pointed.Hostile : null;
                case PointerTargetKind.Friendly:
                    return pointed.Friendly != null ? pointed.Friendly.GetComponent<Health>() : null;
                default:
                    return null;
            }
        }
```

4. In `Evaluate`, replace `casterAbilities.CollectArea(ability, pointed.Point, areaHits);` with

```csharp
                // The preview names only what the player can see; the effect itself still reaches everyone it should.
                // A reveal has no victims at all.
                if (ability.Effect == AbilityEffect.Damage)
                {
                    casterAbilities.CollectArea(ability, pointed.Point, areaHits);
                    for (var i = areaHits.Count - 1; i >= 0; i--)
                    {
                        if (!Knowledge.CanTarget(intelligence, areaHits[i]))
                            areaHits.RemoveAt(i);
                    }
                }
```

(`AbilityEffect.Reveal` is added in Task 9; until then this compiles because `Damage` exists. The `Reveal` enum member arrives with the next task, which is why the condition is written as `== Damage`.)

`UnitAbilities.cs`: add after `encounter`: `[SerializeField] IntelligenceService intelligence;`, add `internal void SetIntelligence(IntelligenceService service) => intelligence = service;` near `SetModifiers`, and in `Check` change

```csharp
            var hasTarget = unitMode && target != null;
```

to

```csharp
            // A hostile the player has not observed is not a target: the same answer as no target at all.
            var hasTarget = unitMode && target != null && Knowledge.CanTarget(intelligence, target);
```

`CompanionAI.cs`: add `using System;` (present) and the field `[SerializeField] IntelligenceService intelligence;` with `internal void SetIntelligence(IntelligenceService service) => intelligence = service;`; add the cached delegate field `Func<Health, bool> engagedAndKnown;`; change the private instance `Health ChooseAssistTarget()` to `internal Health ChooseAssistTarget()` and its call to

```csharp
            canReach ??= Mover.CanReach;   // plain delegates (this one and the engagement test), cached so ticks allocate nothing
            engagedAndKnown ??= hostile => Knowledge.CanTarget(intelligence, hostile) && IsEngaged(hostile);
            return ChooseAssistTarget(transform.position, assistRange, leaderTarget, encounter.Hostiles, engagedAndKnown, canReach);
```

(the static `isEngaged` field is then unused: delete it).

`PlayerCommandInput.cs`: add `[SerializeField] IntelligenceService intelligence;` (optional; comment: "unknown terminals and hidden hostiles are not offered") and `internal void SetIntelligence(IntelligenceService service) => intelligence = service;`; in `HandleClick` pass `intelligence` as the last argument of `PointerTargetResolver.Resolve`; in `TryFindReachableTerminal` change the skip condition to `if (item == null || !item.IsAvailable || item.IsInUseByOther(unit) || !Knowledge.CanInteract(intelligence, item))`.

`MissionSpawner.cs`: wire the spawned units (only when the systems have a service; the setters accept `null`):

- friendlies, replace the abilities line with

```csharp
                if (slot.abilities != null && slot.abilities.Length > 0)
                {
                    var abilities = unit.gameObject.AddComponent<UnitAbilities>();
                    abilities.Initialize(systems.encounter, slot.abilities);
                    abilities.SetIntelligence(systems.intelligence);
                }
```

- after `unit.GetComponent<CompanionAI>().HoldUntilLeaderMoves();` add `unit.GetComponent<CompanionAI>().SetIntelligence(systems.intelligence);`

- [ ] **Step 4: Run to verify pass**

Run, one after another: `Tools/run-tests.sh EditMode Blackglass.Tests.HostileTargetsFilterTests`, `Tools/run-tests.sh EditMode Blackglass.Tests.HostileTargetsTests`, then `Tools/run-tests.sh PlayMode` with filters `Blackglass.Tests.IntelTargetingTests`, `Blackglass.Tests.IntelTargetingCursorTests`, `Blackglass.Tests.IntelAbilityTargetingTests`, and the regression classes `Blackglass.Tests.PointerTargetResolverTests`, `Blackglass.Tests.TacticalCursorTests`, `Blackglass.Tests.AbilityTargetingTests`, `Blackglass.Tests.CompanionAIPlayModeTests`, `Blackglass.Tests.PlayerCommandInputTests`, `Blackglass.Tests.InteractInputPlayModeTests`. Expected: all `EXIT=0`.

If `ACompanion_DoesNotAssistAgainstAnUnobservedEngagedHostile` fails at its precondition (`Issue` returns false), place the engaged hostile within its melee reach of the friendly's NavMesh position instead of far away: the precondition only needs the hostile to have an `AttackCommand` as current command (`AttackTarget != null`). In `IntelTargetingCursorTests.FogOff_LeavesTheCursorAsBefore` the duplicated cursor setup is deliberate (a rig without fog); keep it or extract a helper, either is fine.

- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Scripts Assets/_Project/Tests
git commit -m "Gate pointer, cursor, ability, companion and terminal targeting on what the player knows" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Recon Scan (the Reveal ability effect)

**Files:**
- Modify: `Assets/_Project/Scripts/Abilities/AbilityDefinition.cs`, `AbilityDescriptions.cs`, `UnitAbilities.cs`; `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (`CreateReconScan`)
- Test: `Assets/_Project/Tests/EditMode/AbilityDefinitionRevealTests.cs`, `Assets/_Project/Tests/PlayMode/ReconScanPlayModeTests.cs`

**Interfaces:**
- Consumes: `IntelligenceService.Scan` (Task 6), `UnitAbilities.SetIntelligence` (Task 8).
- Produces: `AbilityEffect.Reveal`; `AbilityDefinition.RevealSeconds`; `AbilityDefinition.Create(..., float radius = 0f, float revealSeconds = 0f)`; `TestWorld.CreateReconScan()` (range 18 / no line of sight / cooldown 25 / radius 12 / 6 s). A Reveal ability is Ground-targeted, always reports `Effect == Reveal`, and its `UnitAbilities.Apply` calls `IntelligenceService.Scan(aim, Radius, RevealSeconds)`.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/AbilityDefinitionRevealTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class AbilityDefinitionRevealTests
    {
        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created)
                Object.DestroyImmediate(o);
            created.Clear();
        }

        AbilityDefinition Scan(float radius = 12f, float seconds = 6f)
        {
            var definition = AbilityDefinition.Create("Recon Scan", AbilityTargetMode.Ground, 18f, false, AbilityCoverRule.Ignored, 25f,
                AbilityEffect.Reveal, 0, radius, seconds);
            created.Add(definition);
            return definition;
        }

        [Test]
        public void AGroundReveal_KeepsItsEffect_AndExposesItsNumbers()
        {
            var scan = Scan();
            Assert.That(scan.Effect, Is.EqualTo(AbilityEffect.Reveal));
            Assert.That(scan.TargetMode, Is.EqualTo(AbilityTargetMode.Ground));
            Assert.That(scan.Radius, Is.EqualTo(12f));
            Assert.That(scan.RevealSeconds, Is.EqualTo(6f));
            Assert.That(scan.Range, Is.EqualTo(18f));
            Assert.That(scan.RequiresLineOfSight, Is.False);
            Assert.That(scan.Cooldown, Is.EqualTo(25f));
            Assert.That(scan.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile), "a reveal is not a heal: it never targets friendlies");
        }

        [Test]
        public void AUnitReveal_IsRefused()
        {
            Assert.That(() => AbilityDefinition.Create("Bad", AbilityTargetMode.Unit, 10f, true, AbilityCoverRule.Applies, 5f,
                AbilityEffect.Reveal, 0, 3f, 4f), Throws.ArgumentException);
        }

        [Test]
        public void AGroundRevealNeedsARadius()
        {
            Assert.That(() => Scan(radius: 0f), Throws.ArgumentException);
        }

        [Test]
        public void AGroundHeal_IsStillRefused()
        {
            Assert.That(() => AbilityDefinition.Create("Bad", AbilityTargetMode.Ground, 10f, true, AbilityCoverRule.Applies, 5f,
                AbilityEffect.Heal, 10, 3f), Throws.ArgumentException);
        }

        [Test]
        public void ADamageAbility_HasNoRevealSeconds()
        {
            var blast = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 12f, true, AbilityCoverRule.Ignored, 10f,
                AbilityEffect.Damage, 35, 3f);
            created.Add(blast);
            Assert.That(blast.Effect, Is.EqualTo(AbilityEffect.Damage));
            Assert.That(blast.RevealSeconds, Is.EqualTo(0f));
        }

        [Test]
        public void ARevealStoredOnAUnitModeAsset_ReadsAsDamage()
        {
            var asset = ScriptableObject.CreateInstance<AbilityDefinition>();
            created.Add(asset);
            var serialized = new UnityEditor.SerializedObject(asset);
            serialized.FindProperty("effect").enumValueIndex = (int)AbilityEffect.Reveal;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(asset.TargetMode, Is.EqualTo(AbilityTargetMode.Unit));
            Assert.That(asset.Effect, Is.Not.EqualTo(AbilityEffect.Reveal), "a hand-edited asset cannot make a unit reveal");
        }

        [Test]
        public void ThePreview_DescribesAReveal_WithNoVictimList()
        {
            var scan = Scan();
            var preview = new AbilityPreview(scan, PointerTargetKind.Ground, null, new Vector3(2f, 0f, 3f), true,
                new AbilityCheck(AbilityFailure.None, 5f, true, false, 1f), false);
            var text = AbilityDescriptions.Preview(preview, new List<Health>());
            Assert.That(text, Does.Contain("Recon Scan @ (2.0, 3.0)"));
            Assert.That(text, Does.Contain("reveals 12.0 m for 6.0 s"));
            Assert.That(text, Does.Contain("no LOS needed"));
            Assert.That(text, Does.Not.Contain("hits"));
            Assert.That(text, Does.Not.Contain("cover"));
            Assert.That(text, Does.EndWith("OK"));
        }
    }
}
```

(`using UnityEditor;` inside an EditMode test assembly is fine; the other EditMode tests use `AssetDatabase`. Adjust the `SerializedObject` reference to `UnityEditor.SerializedObject` as written.)

Create `Assets/_Project/Tests/PlayMode/ReconScanPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ReconScanPlayModeTests
    {
        IntelRig rig;
        AbilityDefinition scan;
        UnitAbilities abilities;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            Time.timeScale = 1f;
        }

        void Build(bool corridor, IntelligenceSettings settings)
        {
            rig = new IntelRig(corridor);
            rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(settings);
            scan = rig.World.CreateReconScan();
            abilities = rig.World.AddAbilities(rig.Friendly.Unit, rig.Encounter, scan);
            abilities.SetIntelligence(rig.Service);
        }

        [UnityTest]
        public IEnumerator UsingTheScan_RevealsTheRoomBehindAWall_StartsTheCooldown_AndMakesTheEnemyATargetForAWhile()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var hostile = rig.Hostiles[0];
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "Precondition");

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, IntelRig.InLineGround)), Is.True, abilities.LastFailure.ToString());
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(rig.Service.CanTarget(hostile), Is.True);
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(25f).Within(0.5f));
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, IntelRig.InLineGround)), Is.False, "on cooldown");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OnCooldown));
        }

        [UnityTest]
        public IEnumerator TheScan_RespectsItsRange_ButNeverNeedsLineOfSight()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var tooFar = abilities.Check(scan, null, new Vector3(30f, 0f, -1.5f));   // 36.5 m from the friendly
            Assert.That(tooFar.Failure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, new Vector3(6.5f, 0f, -1.5f))), Is.True,
                "13 m, through a closed wall: no line of sight is needed");
        }

        [UnityTest]
        public IEnumerator TheScan_AimedIntoUndiscoveredGround_IsAllowed()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
            var check = abilities.Check(scan, null, IntelRig.InLineGround);
            Assert.That(check.IsValid, Is.True, "undiscovered terrain may be targeted by a ground ability");
        }

        [UnityTest]
        public IEnumerator TheScan_WithFogOff_IsHarmless()
        {
            Build(false, new IntelligenceSettings());
            yield return null;
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, IntelRig.InLineGround)), Is.True);
            Assert.That(rig.Service.IsFogActive, Is.False);
        }

        [UnityTest]
        public IEnumerator TheScan_WithoutAService_DoesNothingAndDoesNotThrow()
        {
            rig = new IntelRig(false);
            rig.Begin(IntelRig.Fog());
            scan = rig.World.CreateReconScan();
            abilities = rig.World.AddAbilities(rig.Friendly.Unit, rig.Encounter, scan);   // SetIntelligence never called
            yield return null;
            Assert.DoesNotThrow(() => abilities.TryUse(AbilityCommand.AtGround(scan, IntelRig.InLineGround)));
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
        }

        [UnityTest]
        public IEnumerator AnOrderGivenWhilePaused_RunsOnlyWhenTheGameResumes_ThroughTheNormalOrderPath()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Time.timeScale = 0f;   // tactical pause: the unit's order path does not run
            Assert.That(rig.Friendly.Unit.Issue(AbilityCommand.AtGround(scan, IntelRig.InLineGround)), Is.True);
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown), "nothing happens while paused");

            Time.timeScale = 1f;
            yield return TestWorld.WaitUntil(() => rig.Service.StateOfRegion(1) != KnowledgeState.Unknown, 3f);
            Assert.That(rig.Service.StateOfRegion(1), Is.Not.EqualTo(KnowledgeState.Unknown));
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AQueuedScan_RunsAfterTheOrdersBeforeIt()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var move = new MoveCommand(rig.Friendly.transform.position + new Vector3(0f, 0f, 1.2f));
            Assert.That(rig.Friendly.Unit.Issue(move), Is.True);
            Assert.That(rig.Friendly.Unit.Issue(AbilityCommand.AtGround(scan, IntelRig.InLineGround), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => abilities.UsedCount == 1, 6f);
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
            Assert.That(rig.Service.StateOfRegion(1), Is.Not.EqualTo(KnowledgeState.Unknown));
        }
    }
}
#endif
```

Append this test to the `IntelAbilityTargetingTests` class created in Task 8 (the scan's preview on the ability-targeting path):

```csharp
        [UnityTest]
        public IEnumerator TheScanPreview_NamesNoHostiles_EvenWhenOneIsInTheCircle()
        {
            BeginFog(observationRange: 14f);
            var scan = rig.World.CreateReconScan();
            rig.Abilities.Initialize(rig.Encounter, rig.Aimed, rig.Blast, scan);   // slot 2 is the scan
            yield return null;
            Assert.That(rig.Targeting.Arm(2), Is.True);
            var preview = rig.Targeting.Evaluate(scan, PointerTarget.OnGround(AbilityRig.BlastGround), queued: false);
            Assert.That(preview.IsValid, Is.True, preview.Check.Failure.ToString());
            Assert.That(rig.Targeting.AreaHits, Is.Empty, "a scan has no victims");
            Assert.That(AbilityDescriptions.Preview(preview, rig.Targeting.AreaHits), Does.Contain("reveals 12.0 m for 6.0 s"));
        }
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.AbilityDefinitionRevealTests` -> `EXIT=1`.

- [ ] **Step 3: Implement**

`AbilityDefinition.cs`:

1. `public enum AbilityEffect { Damage, Heal, Reveal }` (append `Reveal` last, so stored values keep their numbers).
2. Add the serialized field after `radius`: `[SerializeField, Min(0f)] float revealSeconds = 6f;` (comment: "Reveal only: how long the enemies inside the circle stay observed") and the property `public float RevealSeconds => Effect == AbilityEffect.Reveal ? revealSeconds : 0f;`.
3. Replace the `Effect` getter:

```csharp
        /// <summary>
        /// A ground ability is area damage or a reveal (a stored Heal reads as Damage); a unit ability is damage or healing
        /// (a stored Reveal reads as Damage).
        /// </summary>
        public AbilityEffect Effect =>
            targetMode == AbilityTargetMode.Ground
                ? (effect == AbilityEffect.Reveal ? AbilityEffect.Reveal : AbilityEffect.Damage)
                : (effect == AbilityEffect.Reveal ? AbilityEffect.Damage : effect);
```

4. `Create(...)`: add the trailing parameter `float revealSeconds = 0f`; replace the mode checks with

```csharp
            if (mode == AbilityTargetMode.Ground)
            {
                if (effect == AbilityEffect.Heal)
                    throw new ArgumentException("A ground ability is area damage or a reveal; it cannot heal.", nameof(effect));
                if (radius <= 0f)
                    throw new ArgumentException("A ground ability needs a positive radius.", nameof(radius));
            }
            else if (effect == AbilityEffect.Reveal)
            {
                throw new ArgumentException("A reveal is aimed at the ground; a unit ability cannot reveal.", nameof(effect));
            }
```

and `definition.revealSeconds = revealSeconds;` with the other assignments.
5. `SideFor`: `effect == AbilityEffect.Heal ? AbilityTargetSide.Friendly : AbilityTargetSide.Hostile`.
6. `OnValidate`: replace the ground block with

```csharp
            if (targetMode == AbilityTargetMode.Ground)
            {
                if (effect != AbilityEffect.Reveal)
                    effect = AbilityEffect.Damage;
                if (radius <= 0f)
                    radius = 1f;
            }
            else if (effect == AbilityEffect.Reveal)
            {
                effect = AbilityEffect.Damage;
            }
            targetSide = SideFor(effect);
```

7. Update the class summary: add "A ground ability is area damage or a reveal (Recon Scan, decision 037)." in place of "A ground ability is always area damage."

`AbilityDescriptions.cs` (`Preview`): replace the ground victims line with

```csharp
            if (ability.TargetMode == AbilityTargetMode.Ground)
            {
                if (ability.Effect == AbilityEffect.Reveal)
                    text.Append($" | reveals {Number(ability.Radius)} m for {Number(ability.RevealSeconds)} s");
                else
                    text.Append($" | hits {areaHits.Count}{VictimNames(areaHits)}");
            }
```

`UnitAbilities.cs` (`Apply`): add a case before the damage cases:

```csharp
                case AbilityEffect.Reveal:
                    // A scan is information, not damage: it goes to the intelligence service (nothing happens without one).
                    if (intelligence != null)
                        intelligence.Scan(aim, ability.Radius, ability.RevealSeconds);
                    break;
```

`Tests/PlayMode/TestSupport/TestWorld.cs`: after `CreateMend` add

```csharp
        public AbilityDefinition CreateReconScan() => Track(AbilityDefinition.Create("Recon Scan", AbilityTargetMode.Ground,
            18f, false, AbilityCoverRule.Ignored, 25f, AbilityEffect.Reveal, 0, 12f, 6f));
```

- [ ] **Step 4: Run to verify pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.AbilityDefinitionRevealTests`, `Tools/run-tests.sh EditMode Blackglass.Tests.AbilityDefinitionTests`, `Tools/run-tests.sh EditMode Blackglass.Tests.AbilityDescriptionsTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.ReconScanPlayModeTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.IntelAbilityTargetingTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.UnitAbilitiesPlayModeTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.CommandableUnitAbilityPlayModeTests`. Expected: all `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Scripts/Abilities Assets/_Project/Tests
git commit -m "Add the Reveal ability effect (Recon Scan) on the existing ground-ability path" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: World presentation (fog, hostile presenters, last-known markers, devices)

**Files:**
- Create: `Assets/_Project/Scripts/Intelligence/FogPresenter.cs`, `HostilePresenter.cs`, `DevicePresenter.cs`
- Modify: `Assets/_Project/Scripts/Mission/MissionSpawner.cs`, `MissionDirector.cs`; `Assets/_Project/Tests/PlayMode/TestSupport/IntelLayouts.cs`
- Test: `Assets/_Project/Tests/PlayMode/FogPresenterTests.cs`, `HostilePresenterTests.cs`, `DevicePresenterTests.cs`, `PresentationMissionTests.cs`

**Interfaces:**
- Consumes: `IntelligenceService.MissionBegun`, `.Changed`, `.StateOfRegion`, `.IsUnitShown`, `.StateOfEnemy`, `.TryLastKnown`, `.IsDeviceShown`, `.TruthView`, `.IsFogActive`, `.MarkerMaterial`; `RegionMap.RegionsAdjacentTo`; `MissionLayout.Boxes`; `GeneratedMission.CameraTerminal/Network`.
- Produces:
  - `FogPresenter` (scene MonoBehaviour; `internal void Initialize(IntelligenceService, Material fog, Material veil)`; builds one collider-free volume per region and per wall box under the mission root, named `Fog`; test accessors `internal int VolumeCount`, `internal bool IsVolumeActive(int index)`, `internal bool IsWallVolume(int index)`, `internal Material VolumeMaterial(int index)`). Region volumes: Unknown opaque (`fog`), Discovered veil (`veil`, hidden if no veil material), Observed hidden. A wall volume is shown only while every adjacent region is Unknown. Truth view or fog off hides every volume.
  - `HostilePresenter` (`Bind(IntelligenceService, Transform markerParent)`, `bool IsHidden`, `internal GameObject Marker`): turns `Renderer.forceRenderingOff` on every renderer of the hostile while it is not shown; creates a `LastKnownMarker` disc at the stored position while it is last known.
  - `DevicePresenter` (`Bind(IntelligenceService, int deviceId)`; `static void AttachTo(GeneratedMission, IntelligenceService)`): hides a camera or the camera terminal until its device is discovered.
  - The spawner attaches `HostilePresenter` to every hostile when `systems.intelligence` is set; the director attaches `DevicePresenter`s after `Begin`.
  - `IntelLayouts.TwoRooms` also fills `Boxes` (the same walls the rig builds) so wall volumes can be tested.

- [ ] **Step 1: Write the failing tests**

Edit `Assets/_Project/Tests/PlayMode/TestSupport/IntelLayouts.cs`: in `TwoRooms`, give the layout its wall boxes (footprints in tiles, as the colliders in `IntelRig` stand in world metres) and one wall that touches only room B; replace the `return new MissionLayout(...)` initializer's `HostileSpawns = ...,` line with

```csharp
                HostileSpawns = new[] { new Vector2Int(16, 3) },
                Boxes = corridor
                    ? new[]
                    {
                        new MissionBox("Wall_Below", MissionBoxKind.Wall, new RectInt(7, 0, 6, 2), 3f),
                        new MissionBox("Wall_Above", MissionBoxKind.Wall, new RectInt(7, 5, 6, 5), 3f),
                        new MissionBox("Wall_EastOfB", MissionBoxKind.Wall, new RectInt(19, 1, 1, 6), 3f),
                    }
                    : new[]
                    {
                        new MissionBox("Wall_Gap", MissionBoxKind.Wall, new RectInt(7, 0, 6, 10), 3f),
                        new MissionBox("Wall_EastOfB", MissionBoxKind.Wall, new RectInt(19, 1, 1, 6), 3f),
                    },
```

Create `Assets/_Project/Tests/PlayMode/FogPresenterTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class FogPresenterTests
    {
        IntelRig rig;
        FogPresenter fog;
        Material fogMaterial;
        Material veilMaterial;
        Transform root;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            if (fogMaterial != null) Object.DestroyImmediate(fogMaterial);
            if (veilMaterial != null) Object.DestroyImmediate(veilMaterial);
        }

        void Build(bool corridor, IntelligenceSettings settings, bool withVeil = true)
        {
            rig = new IntelRig(corridor);
            rig.AddHostile(IntelRig.InLineGround);
            fogMaterial = new Material(Shader.Find("Sprites/Default")) { name = "TestFog" };
            veilMaterial = new Material(Shader.Find("Sprites/Default")) { name = "TestVeil" };
            var host = rig.World.Track(new GameObject("FogHost"));
            host.SetActive(false);
            fog = host.AddComponent<FogPresenter>();
            fog.Initialize(rig.Service, fogMaterial, withVeil ? veilMaterial : null);
            host.SetActive(true);
            root = rig.World.Track(new GameObject("MissionRoot")).transform;
            rig.Begin(settings, new IntelligenceMission { Root = root });
        }

        // Region volumes come first (ids 0..2 for a corridor layout, 0..1 without), wall volumes after.
        int RegionVolumes => rig.Service.Map.Count;

        [UnityTest]
        public IEnumerator EveryRegionAndEveryWallBoxGetsAColliderFreeVolume_UnderTheMissionRoot()
        {
            Build(true, IntelRig.Fog());
            yield return null;
            Assert.That(fog.VolumeCount, Is.EqualTo(RegionVolumes + 3), "three regions and three wall boxes");
            Assert.That(root.Find("Fog"), Is.Not.Null, "built under the mission root");
            Assert.That(root.GetComponentsInChildren<Collider>(), Is.Empty, "fog never blocks a ray, a click or the NavMesh");
        }

        [UnityTest]
        public IEnumerator UnknownRegionsAreFogged_ObservedOnesAreClear_DiscoveredOnesAreVeiled()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.IsVolumeActive(0), Is.False, "room A: the squad is in it, observed");
            Assert.That(fog.IsVolumeActive(1), Is.True, "room B: unknown behind a closed wall");
            Assert.That(fog.VolumeMaterial(1), Is.SameAs(fogMaterial));

            rig.Service.Model.RevealRegion(1);
            Assert.That(fog.IsVolumeActive(1), Is.True, "discovered, not in sight: a veil");
            Assert.That(fog.VolumeMaterial(1), Is.SameAs(veilMaterial));
        }

        [UnityTest]
        public IEnumerator WithoutAVeilMaterial_ADiscoveredRegionIsSimplyClear()
        {
            Build(false, IntelRig.Fog(), withVeil: false);
            yield return null;
            rig.Service.Model.RevealRegion(1);
            Assert.That(fog.IsVolumeActive(1), Is.False);
        }

        [UnityTest]
        public IEnumerator AWallVolume_IsShownOnlyWhileEveryRegionBesideItIsUnknown()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var walls = Enumerable.Range(0, fog.VolumeCount).Where(fog.IsWallVolume).ToArray();
            Assert.That(walls.Length, Is.EqualTo(2), "the gap wall and the wall east of room B");
            var gap = walls[0];       // beside rooms A and B: A is observed
            var east = walls[1];      // beside room B only
            Assert.That(fog.IsVolumeActive(gap), Is.False, "a wall of a known room is a wall the player can see");
            Assert.That(fog.IsVolumeActive(east), Is.True, "a wall only beside an unknown room is hidden");

            rig.Service.Model.RevealRegion(1);
            Assert.That(fog.IsVolumeActive(east), Is.False);
        }

        [UnityTest]
        public IEnumerator TruthView_ShowsEverything_AndFogOffNeverFogs()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.IsVolumeActive(1), Is.True);
            rig.Service.TruthView = true;
            for (var i = 0; i < fog.VolumeCount; i++)
                Assert.That(fog.IsVolumeActive(i), Is.False, $"volume {i} in truth view");
            rig.Service.TruthView = false;
            Assert.That(fog.IsVolumeActive(1), Is.True);

            rig.Service.Begin(new IntelligenceMission { Layout = rig.Layout, Root = root }, new IntelligenceSettings(), rig.Encounter);
            yield return null;
            for (var i = 0; i < fog.VolumeCount; i++)
                Assert.That(fog.IsVolumeActive(i), Is.False, "fog off");
        }

        [UnityTest]
        public IEnumerator ABeginningMission_ReplacesTheOldVolumes()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var old = root.Find("Fog").gameObject;
            rig.Service.Begin(new IntelligenceMission { Layout = rig.Layout, Root = root }, IntelRig.Fog(), rig.Encounter);
            yield return null;
            Assert.That(old == null, Is.True, "the previous mission's volumes are destroyed");
            Assert.That(root.Cast<Transform>().Count(t => t.name == "Fog"), Is.EqualTo(1));
        }

        // The volumes are destroyed with the mission root by the director; the presenter's contract on Clear is only that no
        // volume stays on (the service then reports "no fog").
        [UnityTest]
        public IEnumerator WhenTheMissionIsCleared_NoVolumeStaysOn()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.IsVolumeActive(1), Is.True);
            rig.Service.Clear();
            for (var i = 0; i < fog.VolumeCount; i++)
                Assert.That(fog.IsVolumeActive(i), Is.False);
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/HostilePresenterTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class HostilePresenterTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        HostilePresenter Present(Health hostile, Transform markerParent = null)
        {
            var presenter = hostile.gameObject.AddComponent<HostilePresenter>();
            presenter.Bind(rig.Service, markerParent);
            return presenter;
        }

        static bool AllRenderingOff(Health hostile) =>
            hostile.GetComponentsInChildren<Renderer>().All(r => r.forceRenderingOff);

        static bool AllRenderingOn(Health hostile) =>
            hostile.GetComponentsInChildren<Renderer>().All(r => !r.forceRenderingOff);

        [UnityTest]
        public IEnumerator AnUnobservedHostile_IsNotDrawn_AndColliderAndAIAreUntouched()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.IsHidden, Is.True);
            Assert.That(AllRenderingOff(hostile), Is.True);
            Assert.That(hostile.gameObject.activeSelf, Is.True, "hidden is not disabled");
            Assert.That(hostile.GetComponent<Collider>().enabled, Is.True, "the collider stays: only knowledge hides it");
        }

        [UnityTest]
        public IEnumerator AnObservedHostile_IsDrawn()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.IsHidden, Is.False);
            Assert.That(AllRenderingOn(hostile), Is.True);
        }

        [UnityTest]
        public IEnumerator ItIsHiddenAgain_WhenItLeavesObservation_AndAMarkerStaysWhereItWasLastSeen()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var parent = rig.World.Track(new GameObject("Actors")).transform;
            var presenter = Present(hostile, parent);
            yield return null;
            Assert.That(presenter.Marker == null, Is.True, "no marker while it is in sight");

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            yield return null;
            Assert.That(presenter.IsHidden, Is.True);
            Assert.That(presenter.Marker, Is.Not.Null);
            Assert.That(presenter.Marker.activeSelf, Is.True);
            Assert.That(presenter.Marker.transform.IsChildOf(parent), Is.True, "destroyed with the mission");
            Assert.That(presenter.Marker.GetComponentsInChildren<Collider>(), Is.Empty, "a marker is not a target and blocks nothing");
            Assert.That(Vector3.Distance(Flat(presenter.Marker.transform.position), Flat(IntelRig.InLineGround)), Is.LessThan(0.01f));

            hostile.transform.position = new Vector3(8f, 1f, 1.5f);   // walks off unseen: the marker does not follow
            rig.Service.RunPass();
            yield return null;
            Assert.That(Vector3.Distance(Flat(presenter.Marker.transform.position), Flat(IntelRig.InLineGround)), Is.LessThan(0.01f));
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        [UnityTest]
        public IEnumerator ADeadHostile_LeavesNoMarkerAndNoException()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.enemyMarkersAtStart = 1));
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.Marker, Is.Not.Null, "Precondition: a briefing marker");
            hostile.TakeDamage(1000);
            yield return null;
            Assert.That(presenter.Marker == null || !presenter.Marker.activeSelf, Is.True);
        }

        [UnityTest]
        public IEnumerator TruthView_ShowsTheHostileAndNoMarker_ButDoesNotMakeItATarget()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.IsHidden, Is.True);
            rig.Service.TruthView = true;
            yield return null;
            Assert.That(presenter.IsHidden, Is.False);
            Assert.That(AllRenderingOn(hostile), Is.True);
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "truth view is display only");
        }

        [UnityTest]
        public IEnumerator FogOff_DrawsEveryHostile()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.IsHidden, Is.False);
        }

        [UnityTest]
        public IEnumerator AnAttackLineFromAHiddenHostile_IsNotDrawn()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            var line = new GameObject("Line").AddComponent<LineRenderer>();
            line.transform.SetParent(hostile.transform, false);
            line.positionCount = 2;
            line.enabled = true;
            rig.Begin(IntelRig.Fog());
            Present(hostile);
            yield return null;
            Assert.That(line.forceRenderingOff, Is.True, "line renderers under the hostile are hidden with it");
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/DevicePresenterTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class DevicePresenterTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator ADevice_IsHiddenUntilItsDeviceIsDiscovered_AndShownInTruthView()
        {
            rig = new IntelRig(corridor: false);
            var visual = rig.World.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            var security = new SecurityPlan(true, 0, new Vector2Int(19, 9), new CameraMount[0]);
            rig.Begin(IntelRig.Fog(), new IntelligenceMission { Security = security });
            visual.AddComponent<DevicePresenter>().Bind(rig.Service, SecurityPlan.TerminalDeviceId);
            var renderer = visual.GetComponent<Renderer>();
            yield return null;
            Assert.That(renderer.forceRenderingOff, Is.True);

            rig.Service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            yield return null;
            Assert.That(renderer.forceRenderingOff, Is.False);

            rig.Service.Clear();
            yield return null;
            Assert.That(renderer.forceRenderingOff, Is.False, "no mission: everything shown");
        }

        [UnityTest]
        public IEnumerator TruthView_ShowsAnUndiscoveredDevice()
        {
            rig = new IntelRig(corridor: false);
            var visual = rig.World.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            var security = new SecurityPlan(true, 0, new Vector2Int(19, 9), new CameraMount[0]);
            rig.Begin(IntelRig.Fog(), new IntelligenceMission { Security = security });
            visual.AddComponent<DevicePresenter>().Bind(rig.Service, SecurityPlan.TerminalDeviceId);
            yield return null;
            rig.Service.TruthView = true;
            yield return null;
            Assert.That(visual.GetComponent<Renderer>().forceRenderingOff, Is.False);
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/PresentationMissionTests.cs` (the real pipeline):

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PresentationMissionTests
    {
        MissionRig rig;
        Material fogMaterial;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            if (fogMaterial != null) Object.DestroyImmediate(fogMaterial);
        }

        FogPresenter AddFog(IntelligenceService service)
        {
            fogMaterial = new Material(Shader.Find("Sprites/Default"));
            var host = rig.World.Track(new GameObject("FogHost"));
            host.SetActive(false);
            var fog = host.AddComponent<FogPresenter>();
            fog.Initialize(service, fogMaterial, null);
            host.SetActive(true);
            return fog;
        }

        [UnityTest]
        public IEnumerator ABlindMission_HidesItsHostilesAndItsCameras_AndFogsMostOfTheMap()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            var fog = AddFog(service);
            yield return rig.Generate(12345);
            yield return null;

            foreach (var hostile in rig.Director.Hostiles)
            {
                Assert.That(hostile.GetComponent<HostilePresenter>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponentsInChildren<Renderer>().All(r => r.forceRenderingOff), Is.True, hostile.name);
            }
            foreach (var friendly in rig.Director.Friendlies)
                Assert.That(friendly.GetComponentsInChildren<Renderer>().Any(r => r.forceRenderingOff), Is.False, "the squad is always drawn");
            foreach (var camera in rig.Director.Current.Network.Cameras)
                Assert.That(camera.Visual.GetComponentsInChildren<Renderer>().All(r => r.forceRenderingOff), Is.True, "undiscovered cameras are not drawn");

            var active = Enumerable.Range(0, fog.VolumeCount).Count(fog.IsVolumeActive);
            Assert.That(active, Is.GreaterThan(fog.VolumeCount / 2));
            Assert.That(rig.Director.Current.Root.transform.Find("Fog"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator AFullKnowledgeMission_DrawsEverything_AndBuildsNoActiveFog()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Full());
            var fog = AddFog(service);
            yield return rig.Generate(12345);
            yield return null;
            foreach (var hostile in rig.Director.Hostiles)
                Assert.That(hostile.GetComponentsInChildren<Renderer>().Any(r => r.forceRenderingOff), Is.False);
            Assert.That(Enumerable.Range(0, fog.VolumeCount).Any(fog.IsVolumeActive), Is.False);
        }

        [UnityTest]
        public IEnumerator Regenerating_LeavesNoFogAndNoMarkersFromTheLastMission()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            AddFog(service);
            yield return rig.Generate(12345);
            yield return rig.Generate(31);
            yield return null;
            Assert.That(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name == "Fog"), Is.EqualTo(1));
            Assert.That(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name == "LastKnownMarker"), Is.EqualTo(0));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.FogPresenterTests` -> `EXIT=1` (`FogPresenter` etc. missing).

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/Intelligence/FogPresenter.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Hides what the player has not found. When a mission begins it builds, under the mission root, one opaque dark volume
    /// per region and one per wall box (cubes with no collider, so rays, clicks and the NavMesh never see them), and keeps
    /// them in step with the knowledge model: an Unknown region is fogged, a Discovered one (known but not in sight) gets a
    /// translucent veil (or is simply clear when no veil material is set), an Observed one is clear. A wall is fogged only
    /// while every region beside it is Unknown, so the walls of a known room stay visible from outside. Presentation only
    /// (decision 037): knowledge lives in MissionIntelligence, never in these renderers, and the volumes work with any
    /// environment theme or none. The developer truth view and fog-off missions show everything.
    /// </summary>
    public sealed class FogPresenter : MonoBehaviour
    {
        // A little taller than the walls (3 m), so nothing inside shows above the top.
        const float VolumeHeight = 3.2f;
        // Wall volumes are a hair larger than the wall, so the two surfaces never fight.
        const float WallGrow = 0.04f;

        sealed class Volume
        {
            public GameObject Object;
            public Renderer Renderer;
            public int[] Regions;
            public bool IsWall;
        }

        [SerializeField] IntelligenceService intelligence;
        [SerializeField] Material fogMaterial;
        [SerializeField] Material veilMaterial;

        readonly List<Volume> volumes = new List<Volume>();
        readonly List<int> buffer = new List<int>();
        Transform container;

        internal int VolumeCount => volumes.Count;
        internal bool IsVolumeActive(int index) => volumes[index].Object != null && volumes[index].Object.activeSelf;
        internal bool IsWallVolume(int index) => volumes[index].IsWall;
        internal Material VolumeMaterial(int index) => volumes[index].Renderer.sharedMaterial;

        internal void Initialize(IntelligenceService service, Material fog, Material veil)
        {
            intelligence = service;
            fogMaterial = fog;
            veilMaterial = veil;
        }

        void OnEnable()
        {
            if (intelligence == null)
                return;
            intelligence.MissionBegun += Build;
            intelligence.Changed += Refresh;
        }

        void OnDisable()
        {
            if (intelligence != null)
            {
                intelligence.MissionBegun -= Build;
                intelligence.Changed -= Refresh;
            }
            Clear();
        }

        void Build(MissionLayout layout, RegionMap map, Transform root)
        {
            Clear();
            container = new GameObject("Fog").transform;
            if (root != null)
                container.SetParent(root, false);
            for (var region = 0; region < map.Count; region++)
                volumes.Add(Make($"FogRegion_{region}", layout, map[region].Rect, new[] { region }, false));
            foreach (var box in layout.Boxes)
            {
                if (box.Kind != MissionBoxKind.Wall)
                    continue;
                map.RegionsAdjacentTo(box.Footprint, buffer);
                if (buffer.Count > 0)
                    volumes.Add(Make($"{box.Name}_Fog", layout, box.Footprint, buffer.ToArray(), true));
            }
            Refresh();
        }

        Volume Make(string objectName, MissionLayout layout, RectInt rect, int[] regions, bool isWall)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = objectName;
            DestroyImmediate(cube.GetComponent<Collider>());
            cube.transform.SetParent(container, false);
            var grow = isWall ? WallGrow : 0f;
            cube.transform.position = layout.RectCenter(rect) + Vector3.up * (VolumeHeight * 0.5f);
            cube.transform.localScale = new Vector3(rect.width + grow, VolumeHeight, rect.height + grow);
            var renderer = cube.GetComponent<Renderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return new Volume { Object = cube, Renderer = renderer, Regions = regions, IsWall = isWall };
        }

        void Refresh()
        {
            var show = intelligence != null && intelligence.IsFogActive && !intelligence.TruthView;
            foreach (var volume in volumes)
            {
                if (volume.Object == null)
                    continue;
                var active = false;
                var material = fogMaterial;
                if (show && volume.IsWall)
                {
                    active = true;
                    foreach (var region in volume.Regions)
                    {
                        if (intelligence.StateOfRegion(region) != KnowledgeState.Unknown)
                        {
                            active = false;
                            break;
                        }
                    }
                }
                else if (show)
                {
                    switch (intelligence.StateOfRegion(volume.Regions[0]))
                    {
                        case KnowledgeState.Unknown:
                            active = true;
                            break;
                        case KnowledgeState.Discovered:
                            active = veilMaterial != null;
                            material = veilMaterial;
                            break;
                    }
                }
                if (active && material != null && volume.Renderer.sharedMaterial != material)
                    volume.Renderer.sharedMaterial = material;
                if (volume.Object.activeSelf != active)
                    volume.Object.SetActive(active);
            }
        }

        void Clear()
        {
            volumes.Clear();
            if (container != null)
                Destroy(container.gameObject);
            container = null;
        }
    }
}
```

Create `Assets/_Project/Scripts/Intelligence/HostilePresenter.cs`:

```csharp
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Draws a hostile only while the player may see it (decision 037). Sets Renderer.forceRenderingOff on every renderer under
    /// the hostile (not Renderer.enabled, which other code toggles), so the unit, its attack line and any other drawn child
    /// vanish together while its colliders, AI and Health go on exactly as before. While the hostile is last known it keeps a
    /// small disc-and-post marker at the position it was last seen; the marker is not a target and has no collider. Added to
    /// every hostile by the spawner. The developer truth view draws everything and no markers.
    /// </summary>
    public sealed class HostilePresenter : MonoBehaviour
    {
        public const string MarkerName = "LastKnownMarker";
        const float MarkerHeight = 0.06f;
        const float MarkerDiameter = 0.9f;

        IntelligenceService intelligence;
        Health health;
        Renderer[] renderers = new Renderer[0];
        Transform markerParent;
        GameObject marker;
        bool drawn = true;

        public bool IsHidden => !drawn;
        internal GameObject Marker => marker;

        /// <summary>Wires the presenter; `parentForMarker` (the mission's Actors object) owns the marker, so regeneration destroys it.</summary>
        public void Bind(IntelligenceService service, Transform parentForMarker)
        {
            intelligence = service;
            markerParent = parentForMarker;
            health = GetComponent<Health>();
            renderers = GetComponentsInChildren<Renderer>(true);
            drawn = true;
            Apply(IsShown());
        }

        void LateUpdate()
        {
            Apply(IsShown());
            UpdateMarker();
        }

        // Health deactivates a dead unit: it leaves no marker.
        void OnDisable() => HideMarker();

        void OnDestroy()
        {
            if (marker != null)
                Destroy(marker);
        }

        bool IsShown() => intelligence == null || health == null || intelligence.IsUnitShown(health);

        void Apply(bool shown)
        {
            if (shown == drawn)
                return;
            drawn = shown;
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.forceRenderingOff = !shown;
            }
        }

        void UpdateMarker()
        {
            var at = default(Vector3);
            var wanted = intelligence != null && health != null && health.IsAlive && !intelligence.TruthView
                && intelligence.StateOfEnemy(health) == KnowledgeState.Discovered && intelligence.TryLastKnown(health, out at);
            if (!wanted)
            {
                HideMarker();
                return;
            }
            if (marker == null)
                marker = CreateMarker();
            marker.transform.position = new Vector3(at.x, MarkerHeight, at.z);
            if (!marker.activeSelf)
                marker.SetActive(true);
        }

        void HideMarker()
        {
            if (marker != null && marker.activeSelf)
                marker.SetActive(false);
        }

        GameObject CreateMarker()
        {
            var root = new GameObject(MarkerName);
            if (markerParent != null)
                root.transform.SetParent(markerParent, false);
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Disc";
            DestroyImmediate(disc.GetComponent<Collider>());
            disc.transform.SetParent(root.transform, false);
            disc.transform.localScale = new Vector3(MarkerDiameter, 0.01f, MarkerDiameter);
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "Post";
            DestroyImmediate(post.GetComponent<Collider>());
            post.transform.SetParent(root.transform, false);
            post.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            post.transform.localScale = new Vector3(0.1f, 1.2f, 0.1f);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (intelligence != null && intelligence.MarkerMaterial != null)
                    renderer.sharedMaterial = intelligence.MarkerMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            return root;
        }
    }
}
```

Create `Assets/_Project/Scripts/Intelligence/DevicePresenter.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Draws a security device (a camera, the camera-control terminal) only once the player has found it (decision 037).
    /// Renderers only: the terminal's collider and the interactable stay (the pointer and the prompt gate it separately).
    /// </summary>
    public sealed class DevicePresenter : MonoBehaviour
    {
        IntelligenceService intelligence;
        int deviceId;
        Renderer[] renderers = new Renderer[0];
        bool drawn = true;

        public void Bind(IntelligenceService service, int id)
        {
            intelligence = service;
            deviceId = id;
            renderers = GetComponentsInChildren<Renderer>(true);
            drawn = true;
            Apply();
        }

        void LateUpdate() => Apply();

        void Apply()
        {
            var shown = intelligence == null || intelligence.IsDeviceShown(deviceId);
            if (shown == drawn)
                return;
            drawn = shown;
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.forceRenderingOff = !shown;
            }
        }

        /// <summary>Attaches a presenter to the mission's camera-control terminal and to every camera.</summary>
        public static void AttachTo(GeneratedMission mission, IntelligenceService service)
        {
            if (mission == null || service == null)
                return;
            if (mission.CameraTerminal != null)
                mission.CameraTerminal.gameObject.AddComponent<DevicePresenter>().Bind(service, SecurityPlan.TerminalDeviceId);
            if (mission.Network == null)
                return;
            foreach (var camera in mission.Network.Cameras)
            {
                if (camera.Visual != null)
                    camera.Visual.AddComponent<DevicePresenter>().Bind(service, camera.DeviceId);
            }
        }
    }
}
```

`MissionSpawner.cs` (hostile loop): after `unit.GetComponent<UnitCover>().Wire(systems.coverRegistry);` in the hostile loop add

```csharp
                if (systems.intelligence != null)
                    unit.gameObject.AddComponent<HostilePresenter>().Bind(systems.intelligence, mission.Actors);
```

`MissionDirector.cs`: directly after the `systems.intelligence.Begin(...)` call added in Task 6 add

```csharp
                DevicePresenter.AttachTo(mission, systems.intelligence);
```

(inside the same `if (systems.intelligence != null)` block).

- [ ] **Step 4: Run to verify pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.FogPresenterTests`, `...HostilePresenterTests`, `...DevicePresenterTests`, `...PresentationMissionTests`, then regressions `Blackglass.Tests.MissionDirectorPlayModeTests`, `Blackglass.Tests.ProceduralMissionSceneTests`, `Blackglass.Tests.EnvironmentVisualsPlayModeTests`. Expected: all `EXIT=0`.

If the real-mission test `ABlindMission_HidesItsHostilesAndItsCameras...` shows a hostile's renderers still on after one frame, the presenter's `LateUpdate` ran before `Begin` (the spawner attaches it earlier in the same attempt): wait two frames in the test (`yield return null; yield return null;`) before asserting; do not change the behaviour.

- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Scripts Assets/_Project/Tests
git commit -m "Present the knowledge layer: fog volumes, hidden hostiles with last-known markers, hidden devices" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```


### Task 11: Close the remaining leak channels (labels, counts, attack lines, queue lines, cover markers, debug text)

**Files:**
- Modify: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`, `AttackLineView.cs`, `CommandQueueView.cs`, `CoverView.cs`; `Assets/_Project/Scripts/Mission/MissionDebugView.cs`, `MissionGizmoView.cs`, `MissionSpawner.cs`
- Test: `Assets/_Project/Tests/EditMode/LeakTextTests.cs`, `Assets/_Project/Tests/PlayMode/LeakGatingTests.cs`

**Interfaces:**
- Consumes: `Knowledge.IsShown`, `Knowledge.IsCoverShown`, `IntelligenceService.IsFogActive/TruthView/StateOfEnemy` (Task 6).
- Produces: `PrototypeHud.DescribeSidesHidden(int livingFriendlies, int friendlies)`; `PrototypeHud.ShowsUnitLabel(IntelligenceService, Health unit, bool hostile)`; `MissionDebugText.DescribeHidden(MissionState, MissionReport)`; optional serialized `intelligence` plus `internal SetIntelligence(IntelligenceService)` on `PrototypeHud`, `AttackLineView`, `CommandQueueView`, `CoverView`, `MissionDebugView`, `MissionGizmoView`.

**What is closed here (every item is a display of hidden information):** unit labels (health, AI state, line of sight, cover) of hostiles that are not shown; the "Hostiles alive n/m" count; the line of a shot at an unshown target; a friendly's order line to an unshown target; cover markers in unknown regions and cover held by an unshown hostile; the debug text and gizmos that print room, spawn and guard data. Truth view shows all of them, tagged.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/LeakTextTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class LeakTextTests
    {
        [Test]
        public void TheSidesLine_WithHostilesHidden_NamesNoHostileCount()
        {
            var text = PrototypeHud.DescribeSidesHidden(3, 3);
            Assert.That(text, Does.Contain("Friendlies alive 3/3"));
            Assert.That(text, Does.Not.Match(@"Hostiles alive \d"));
            Assert.That(text, Does.Contain("Hostiles: unknown"));
        }

        [Test]
        public void TheDebugText_WhenHidden_KeepsOnlyTheStateAndTheSeed()
        {
            var report = new MissionReport
            {
                Seed = 77, AttemptsMade = 2, MaxAttempts = 20, Rooms = 6, Connections = 7, FloorTiles = 400, NavArea = 380f,
                PathsChecked = 12, CoverTotal = 50, FriendlySpawns = 3, HostileSpawns = 3, TeamSeparation = 20f, Bounds = new Bounds(Vector3.zero, new Vector3(50f, 3f, 40f)),
            };
            var hidden = MissionDebugText.DescribeHidden(MissionState.Ready, report);
            Assert.That(hidden, Does.Contain("Mission: Ready | Seed 77"));
            Assert.That(hidden, Does.Contain("attempt 2/20"));
            Assert.That(hidden, Does.Not.Contain("rooms").And.Not.Contain("Spawns").And.Not.Contain("hostile").And.Not.Contain("Cover"));
            Assert.That(MissionDebugText.Describe(MissionState.Ready, report), Does.Contain("Spawns 3 friendly / 3 hostile"), "the full text is unchanged");
        }

        [Test]
        public void ShowsUnitLabel_IsAlwaysTrueForFriendliesAndForAMissingService()
        {
            Assert.That(PrototypeHud.ShowsUnitLabel(null, null, hostile: true), Is.True, "no service: everything known");
            Assert.That(PrototypeHud.ShowsUnitLabel(null, null, hostile: false), Is.True);
        }
    }
}
```

Create `Assets/_Project/Tests/PlayMode/LeakGatingTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The display channels: every one of them must stay silent about what the player has not observed.</summary>
    public class LeakGatingTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator UnitLabels_AreDrawnForFriendliesAndObservedHostiles_NeverForUnobservedOnes()
        {
            rig = new IntelRig(corridor: true);
            var shown = rig.AddHostile(IntelRig.InLineGround);
            var hidden = rig.AddHostile(IntelRig.OffAxisGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(PrototypeHud.ShowsUnitLabel(rig.Service, shown, hostile: true), Is.True);
            Assert.That(PrototypeHud.ShowsUnitLabel(rig.Service, hidden, hostile: true), Is.False);
            Assert.That(PrototypeHud.ShowsUnitLabel(rig.Service, rig.FriendlyHealth, hostile: false), Is.True);

            rig.Service.TruthView = true;
            Assert.That(PrototypeHud.ShowsUnitLabel(rig.Service, hidden, hostile: true), Is.True, "truth view labels everything");
        }

        [UnityTest]
        public IEnumerator AShotAtAnUnshownTarget_DrawsNoAttackLine_AShotAtAShownOneDoes()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.observationRange = 8f));   // 13 m: out of sight, but in the friendly's weapon range
            var attacker = rig.Friendly.GetComponent<UnitAttacker>();
            attacker.Initialize(30f, 10, 0.1f, CombatRole.Ranged);
            var lineObject = new GameObject("Line");
            lineObject.transform.SetParent(rig.Friendly.transform, false);
            var line = lineObject.AddComponent<LineRenderer>();
            var view = rig.Friendly.gameObject.AddComponent<AttackLineView>();
            view.Initialize(line);
            view.SetIntelligence(rig.Service);
            yield return null;
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "Precondition");

            Assert.That(attacker.TryAttack(hostile), Is.True, "Precondition: the friendly can shoot what it cannot see");
            Assert.That(view.IsShowing, Is.False, "the line would show where the hidden hostile stands");

            rig.Service.Scan(hostile.transform.position, 3f, 5f);
            yield return new WaitForSeconds(0.2f);
            Assert.That(attacker.TryAttack(hostile), Is.True);
            Assert.That(view.IsShowing, Is.True, "now it is observed: the line shows as before");
        }

        [UnityTest]
        public IEnumerator AnOrderLineToAnUnshownTarget_IsNotDrawn()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.observationRange = 8f));
            rig.Friendly.gameObject.AddComponent<LineRenderer>();
            var view = rig.Friendly.gameObject.AddComponent<CommandQueueView>();
            view.SetIntelligence(rig.Service);
            Assert.That(rig.Friendly.Unit.Issue(new AttackCommand(hostile)), Is.True);
            yield return null;
            yield return null;
            Assert.That(view.LinePointCount, Is.EqualTo(0), "no line to an unobserved hostile's position");

            rig.Service.Scan(hostile.transform.position, 3f, 5f);
            yield return null;
            Assert.That(view.LinePointCount, Is.EqualTo(2), "a line once it is observed");
        }

        // Two cover points in room A (world x -6 and -4, z 1) and one in room B; the rig's wall closes the gap.
        CoverRegistry MakeCover(out CoverLocation a1, out CoverLocation a2, out CoverLocation b)
        {
            CoverLocation At(string name, Vector3 ground)
            {
                var wall = rig.World.CreateObstacle(ground + new Vector3(0f, 0.45f, 1f), new Vector3(1f, 0.9f, 0.4f));
                return rig.World.CreateCoverPoint(ground, Vector3.forward, wall.GetComponent<Collider>());
            }
            a1 = At("A1", new Vector3(-6f, 0f, -3f));
            a2 = At("A2", new Vector3(-4f, 0f, -3f));
            b = At("B1", new Vector3(6f, 0f, -3f));
            return rig.World.CreateRegistry(a1, a2, b);
        }

        CoverView MakeCoverView(CoverRegistry registry, TacticalPause pause)
        {
            var host = rig.World.Track(new GameObject("CoverView"));
            host.SetActive(false);
            var view = host.AddComponent<CoverView>();
            view.Initialize(registry, pause, null);
            view.SetIntelligence(rig.Service);
            host.SetActive(true);
            return view;
        }

        [UnityTest]
        public IEnumerator WhilePaused_CoverMarkersAppearOnlyInKnownRegions()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog());
            var registry = MakeCover(out _, out _, out _);
            var pause = rig.World.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var view = MakeCoverView(registry, pause);
            pause.Pause();
            yield return null;
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(2), "the two points in the known room; the one in the unknown room is not drawn");

            rig.Service.Model.RevealRegion(1);
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(3));

            rig.Service.Clear();
            rig.Begin(IntelRig.Fog());
            rig.Service.TruthView = true;
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(3), "truth view draws all");
        }

        [UnityTest]
        public IEnumerator ACoverPointHeldByAnUnshownHostile_IsNotDrawn_OneHeldByAFriendlyIs()
        {
            rig = new IntelRig(corridor: false);
            var (shooter, _) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var registry = MakeCover(out var a1, out var a2, out _);
            var hostileCover = shooter.GetComponent<UnitCover>();
            hostileCover.Initialize(registry);
            Assert.That(hostileCover.TryReserve(a1), Is.True);
            var friendlyCover = rig.Friendly.GetComponent<UnitCover>();
            friendlyCover.Initialize(registry);
            Assert.That(friendlyCover.TryReserve(a2), Is.True);
            var pause = rig.World.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var view = MakeCoverView(registry, pause);   // not paused: only claimed points show
            yield return null;
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(1), "the friendly's point only; the hostile's claim would show where it hides");
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.LeakTextTests` -> `EXIT=1` (`DescribeSidesHidden`, `DescribeHidden`, `ShowsUnitLabel` missing).

- [ ] **Step 3: Implement**

`PrototypeHud.cs`:

1. Field and setter next to the other serialized references:

```csharp
        // Optional: with fog on, hostiles that are not observed get no label and no count (decision 037).
        [SerializeField] IntelligenceService intelligence;

        internal void SetIntelligence(IntelligenceService service) => intelligence = service;
```

2. After `DescribeSides`:

```csharp
        /// <summary>The sides line while the hostiles are not known: the squad's own count and nothing about them.</summary>
        internal static string DescribeSidesHidden(int livingFriendlies, int friendlies) =>
            $"Friendlies alive {livingFriendlies}/{friendlies} | Hostiles: unknown";

        /// <summary>Whether a unit gets an on-screen label: every friendly, and hostiles only while the player may see them.</summary>
        internal static bool ShowsUnitLabel(IntelligenceService intelligence, Health unit, bool hostile) =>
            !hostile || Knowledge.IsShown(intelligence, unit);
```

3. `OnGUI`: replace the `if (encounter != null)` sides block with

```csharp
            if (encounter != null)
            {
                var hideHostiles = intelligence != null && intelligence.IsFogActive && !intelligence.TruthView;
                GUI.Label(new Rect(10f, 95f, 420f, 22f), hideHostiles
                    ? DescribeSidesHidden(encounter.LivingFriendlies, encounter.Friendlies.Count)
                    : DescribeSides(encounter.LivingFriendlies, encounter.Friendlies.Count, encounter.LivingHostiles, encounter.Hostiles.Count));
            }
```

4. `DrawUnitLabel`: first guard becomes

```csharp
            if (health == null || !health.IsAlive || !health.gameObject.activeInHierarchy || !ShowsUnitLabel(intelligence, health, hostile))
                return;
```

then change the companion assist name and the friendly's target sight/cover lines so they never describe an unshown target:

```csharp
                activity = AppendCooldown(DescribeCompanion(DescribeOrders(unit.CurrentCommand, unit.PendingCommands.Count),
                    companion.State, companion.AssistTarget != null && Knowledge.IsShown(intelligence, companion.AssistTarget) ? companion.AssistTarget.name : null,
                    companion.IsParked, companion.IsHeld), cooldown);
```

and `if (hasAttacker && attacker.NeedsLineOfSight && unit != null && unit.CurrentCommand is AttackCommand attack && Knowledge.IsShown(intelligence, attack.Target))`. In truth view, after the activity lines for a hostile, add the state tag:

```csharp
            if (hostile && intelligence != null && intelligence.TruthView)
                text += $"\n[truth: {intelligence.StateOfEnemy(health)}]";
```

`AttackLineView.cs`: add the field/setter (`[SerializeField] IntelligenceService intelligence;`, `internal void SetIntelligence(IntelligenceService service) => intelligence = service;`) and change the first line of `OnAttacked` to `if (line == null || target == null || !Knowledge.IsShown(intelligence, target)) return;` (a hostile shooting at a friendly is covered by its presenter hiding the line with the hostile; this gate covers a friendly shooting at a target it cannot see).

`CommandQueueView.cs`: add the field/setter, and gate the two target cases in `AddOrder`:

```csharp
                case AttackCommand attack when attack.Target != null && attack.Target.gameObject.activeInHierarchy
                    && Knowledge.IsShown(intelligence, attack.Target):
                    points.Add(OnGround(attack.Target.transform.position));
                    break;
```

and in the ability case replace `(ability.Target != null && ability.Target.gameObject.activeInHierarchy)` with `(ability.Target != null && ability.Target.gameObject.activeInHierarchy && Knowledge.IsShown(intelligence, ability.Target))`.

`CoverView.cs`: add the field/setter (`internal void SetIntelligence(IntelligenceService service) => intelligence = service;`) and in `LateUpdate` replace the `show` line with

```csharp
                var show = point != null && point.IsValid && (paused || point.IsClaimed)
                    && Knowledge.IsCoverShown(intelligence, point) && ClaimantIsShown(point);
```

with the helper

```csharp
        // A location held by a hostile the player cannot see would show where it hides.
        bool ClaimantIsShown(CoverLocation point) =>
            !point.IsClaimed || !point.Claimant.TryGetComponent<Health>(out var claimant) || Knowledge.IsShown(intelligence, claimant);
```

`MissionDebugView.cs`: add `using` nothing new; add to `MissionDebugText`:

```csharp
        /// <summary>The state line only: no room, spawn, cover or bounds data while the fog hides the mission.</summary>
        public static string DescribeHidden(MissionState state, MissionReport report)
        {
            var text = FormattableString.Invariant($"Mission: {state} | Seed {report.Seed}");
            if (report.AttemptsMade > 0)
                text += FormattableString.Invariant($" | attempt {report.AttemptsMade}/{report.MaxAttempts}");
            if (state == MissionState.Failed && !string.IsNullOrEmpty(report.Failure))
                text += "\n" + report.Failure;
            return text;
        }
```

and in the `MissionDebugView` MonoBehaviour: `[SerializeField] IntelligenceService intelligence;` + `internal void SetIntelligence(...)`; in `OnGUI` build the text with

```csharp
            var hidden = intelligence != null && intelligence.IsFogActive && !intelligence.TruthView;
            var text = (hidden ? MissionDebugText.DescribeHidden(director.State, director.Report) : MissionDebugText.Describe(director.State, director.Report)) + "\n" + MissionDebugText.Hints;
```

`MissionGizmoView.cs`: field/setter, and `if (!draw || director == null || director.Current == null || (intelligence != null && intelligence.IsFogActive && !intelligence.TruthView)) return;`.

`MissionSpawner.cs` (friendlies loop, after `unit.gameObject.AddComponent<UnitInteractor>();`):

```csharp
                if (unit.TryGetComponent<AttackLineView>(out var attackLine))
                    attackLine.SetIntelligence(systems.intelligence);
                if (unit.TryGetComponent<CommandQueueView>(out var queueView))
                    queueView.SetIntelligence(systems.intelligence);
```

- [ ] **Step 4: Run to verify pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.LeakTextTests`, `Tools/run-tests.sh EditMode Blackglass.Tests.PrototypeHudTests`, `Tools/run-tests.sh EditMode Blackglass.Tests.MissionDebugTextTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.LeakGatingTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.AttackLineViewTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.CoverViewTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.CommandQueueViewTests`. Expected: all `EXIT=0`.

If `ACoverPointHeldByAnUnshownHostile_IsNotDrawn_OneHeldByAFriendlyIs` finds a reservation gone (`UnitCover` may release a reservation that no order is walking to), hold each point the way the game does instead: give each unit a `MoveToCoverCommand(point)` with the unit already standing on the point (see `CoverOrderPlayModeTests` for the arrangement) and wait until `Status == CoverStatus.Occupied`; keep the assertions.

- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Scripts Assets/_Project/Tests
git commit -m "Close the display channels: labels, counts, attack and order lines, cover markers, debug text" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Intel map overlay, truth view, debug views and developer keys

**Files:**
- Create: `Assets/_Project/Scripts/Intelligence/IntelMapView.cs`, `IntelligenceDebugView.cs`, `IntelligenceDeveloperInput.cs`
- Create: `Assets/_Project/Scripts/Intelligence/IntelMapView.cs`, `IntelligenceDebugView.cs`, `IntelligenceGizmoView.cs`, `IntelligenceDeveloperInput.cs`
- Test: `Assets/_Project/Tests/EditMode/IntelMapProjectionTests.cs`, `Assets/_Project/Tests/EditMode/IntelligenceInputAssetTests.cs`, `Assets/_Project/Tests/PlayMode/IntelMapViewTests.cs`, `Assets/_Project/Tests/PlayMode/IntelligenceDebugTextTests.cs`, `Assets/_Project/Tests/PlayMode/IntelligenceDeveloperInputTests.cs`

**Interfaces:**
- Consumes: `IntelligenceService` (`Map`, `StateOfRegion`, `StateOfEnemy`, `TryLastKnown`, `Devices`, `IsDeviceShown`, `Pulses`, `Network`, `TruthView`, `IsFogActive`, `Settings`), `Encounter`, `MissionDirector.Runtime`, `RegionMap.WorldRect`.
- Produces:
  - `static class IntelMapProjection { Rect WorldBounds(RegionMap); Vector2 ToPanel(Rect panel, Rect bounds, Vector3 world); Rect ToPanel(Rect panel, Rect bounds, Rect worldRect); }` (IMGUI coordinates, y down; world z up).
  - `enum MapMarkKind { Region, Friendly, Enemy, LastKnown, Device, Objective }`, `readonly struct MapMark { MapMarkKind Kind; Rect Rect; Color Color; string Label; }`.
  - `IntelMapView : MonoBehaviour` with `IsVisible`, `Toggle()`, `internal IReadOnlyList<MapMark> BuildMarks(Rect panel)`, `internal void Initialize(IntelligenceService, Encounter, MissionDirector)`; IMGUI drawing in `OnGUI` (hidden when fog is off).
  - `static class IntelligenceDebugText { string Describe(IntelligenceService, IReadOnlyList<Health> hostiles, IReadOnlyList<MissionObjective> goals) }` and `IntelligenceDebugView` (draws it only while `TruthView` is on) and `IntelligenceGizmoView` (camera cones, scan circles and region outlines, truth view only).
  - `IntelligenceDeveloperInput` with three actions and three new `Developer` input actions: `ToggleTruthView` (F12), `CycleIntelligencePreset` (F5), `ToggleIntelMap` (M), all keyboard only.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/IntelMapProjectionTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class IntelMapProjectionTests
    {
        // World bounds x 0..10, z 0..5 into a 106x56 panel with 6 px padding: scale 8.8, content 88x44, origin (9, 6).
        static readonly Rect Panel = new Rect(0f, 0f, 106f, 56f);
        static readonly Rect Bounds = Rect.MinMaxRect(0f, 0f, 10f, 5f);

        [Test]
        public void ToPanel_PutsTheWorldCornersOnTheContentCorners_WithZPointingUp()
        {
            var bottomLeft = IntelMapProjection.ToPanel(Panel, Bounds, new Vector3(0f, 0f, 0f));
            var topRight = IntelMapProjection.ToPanel(Panel, Bounds, new Vector3(10f, 0f, 5f));
            Assert.That(bottomLeft.x, Is.EqualTo(9f).Within(1e-3f));
            Assert.That(bottomLeft.y, Is.EqualTo(50f).Within(1e-3f), "low z is low on screen (large y)");
            Assert.That(topRight.x, Is.EqualTo(97f).Within(1e-3f));
            Assert.That(topRight.y, Is.EqualTo(6f).Within(1e-3f));
        }

        [Test]
        public void ToPanel_ForARect_KeepsItsSizeInScale_AndItsTopLeftCorner()
        {
            var rect = IntelMapProjection.ToPanel(Panel, Bounds, Rect.MinMaxRect(2f, 1f, 4f, 3f));
            Assert.That(rect.width, Is.EqualTo(17.6f).Within(1e-3f));
            Assert.That(rect.height, Is.EqualTo(17.6f).Within(1e-3f));
            Assert.That(rect.x, Is.EqualTo(9f + 2f * 8.8f).Within(1e-3f));
            Assert.That(rect.y, Is.EqualTo(6f + (5f - 3f) * 8.8f).Within(1e-3f));
        }

        [Test]
        public void ToPanel_TheCentreMapsToThePanelCentre()
        {
            var centre = IntelMapProjection.ToPanel(Panel, Bounds, new Vector3(5f, 0f, 2.5f));
            Assert.That(centre.x, Is.EqualTo(53f).Within(1e-3f));
            Assert.That(centre.y, Is.EqualTo(28f).Within(1e-3f));
        }

        [Test]
        public void ADegenerateBounds_DoesNotDivideByZero()
        {
            var point = IntelMapProjection.ToPanel(Panel, Rect.MinMaxRect(1f, 1f, 1f, 1f), new Vector3(1f, 0f, 1f));
            Assert.That(float.IsNaN(point.x) || float.IsInfinity(point.x), Is.False);
            Assert.That(float.IsNaN(point.y) || float.IsInfinity(point.y), Is.False);
        }
    }
}
```

Create `Assets/_Project/Tests/EditMode/IntelligenceInputAssetTests.cs`:

```csharp
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class IntelligenceInputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static InputActionAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null, $"Input actions not found at {AssetPath}");
            return asset;
        }

        [TestCase("ToggleTruthView", "<Keyboard>/f12")]
        [TestCase("CycleIntelligencePreset", "<Keyboard>/f5")]
        [TestCase("ToggleIntelMap", "<Keyboard>/m")]
        public void TheDeveloperMap_HasTheIntelligenceActions_OnKeyboardKeys(string action, string path)
        {
            var found = Load().FindAction("Developer/" + action);
            Assert.That(found, Is.Not.Null, action);
            Assert.That(found.type, Is.EqualTo(InputActionType.Button));
            Assert.That(found.bindings.Select(b => b.path), Is.EqualTo(new[] { path }));
            Assert.That(found.bindings.Single().groups, Is.EqualTo("KeyboardMouse"));
        }

        [Test]
        public void TheIntelligenceKeys_DoNotClashWithAnyOtherBinding()
        {
            foreach (var key in new[] { "<Keyboard>/f12", "<Keyboard>/f5", "<Keyboard>/m" })
            {
                var users = Load().actionMaps.SelectMany(m => m.actions).Where(a => a.bindings.Any(b => b.path == key)).Select(a => a.name).ToArray();
                Assert.That(users, Has.Length.EqualTo(1), key + " is bound by: " + string.Join(", ", users));
            }
        }

        [Test]
        public void TheActionsHaveInputActionReferences_ForSceneWiring()
        {
            var references = AssetDatabase.LoadAllAssetsAtPath(AssetPath).OfType<InputActionReference>().Select(r => r.action?.name).ToArray();
            Assert.That(references, Has.Member("ToggleTruthView"));
            Assert.That(references, Has.Member("CycleIntelligencePreset"));
            Assert.That(references, Has.Member("ToggleIntelMap"));
        }
    }
}
```

Create `Assets/_Project/Tests/PlayMode/IntelMapViewTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelMapViewTests
    {
        static readonly Rect Panel = new Rect(0f, 0f, 260f, 190f);
        IntelRig rig;
        IntelMapView view;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        void Build(bool corridor, IntelligenceSettings settings, IntelligenceMission mission = null)
        {
            rig = new IntelRig(corridor);
            rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(settings, mission);
            var host = rig.World.Track(new GameObject("Map"));
            view = host.AddComponent<IntelMapView>();
            view.Initialize(rig.Service, rig.Encounter, null);
        }

        static MapMark[] Of(System.Collections.Generic.IReadOnlyList<MapMark> marks, MapMarkKind kind) => marks.Where(m => m.Kind == kind).ToArray();

        [UnityTest]
        public IEnumerator Regions_AreColouredByWhatThePlayerKnows_UnknownDarkDiscoveredGreyObservedLight()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            rig.Service.Model.RevealRegion(1);
            var regions = Of(view.BuildMarks(Panel), MapMarkKind.Region);
            Assert.That(regions.Length, Is.EqualTo(rig.Service.Map.Count));
            Assert.That(regions[0].Color, Is.EqualTo(IntelMapView.ObservedColor), "room A: in sight");
            Assert.That(regions[1].Color, Is.EqualTo(IntelMapView.DiscoveredColor), "room B: found but not in sight");

            rig.Service.Clear();
            rig.Begin(IntelRig.Fog());
            regions = Of(view.BuildMarks(Panel), MapMarkKind.Region);
            Assert.That(regions[1].Color, Is.EqualTo(IntelMapView.UnknownColor));
        }

        [UnityTest]
        public IEnumerator Enemies_AreDrawnOnlyWhileObserved_AMarkerWhenLastKnown_AndNothingOtherwise()
        {
            Build(true, IntelRig.Fog());
            yield return null;
            var observed = view.BuildMarks(Panel);
            Assert.That(Of(observed, MapMarkKind.Enemy).Length, Is.EqualTo(1), "in line down the corridor");
            Assert.That(Of(observed, MapMarkKind.LastKnown), Is.Empty);

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            var lost = view.BuildMarks(Panel);
            Assert.That(Of(lost, MapMarkKind.Enemy), Is.Empty);
            Assert.That(Of(lost, MapMarkKind.LastKnown).Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AnUnknownEnemy_IsNotOnTheMap_ExceptInTruthView()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Enemy), Is.Empty);
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.LastKnown), Is.Empty);
            rig.Service.TruthView = true;
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Enemy).Length, Is.EqualTo(1));
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Region)[1].Color, Is.EqualTo(IntelMapView.TruthUnknownColor));
        }

        [UnityTest]
        public IEnumerator Friendlies_AreAlwaysOnTheMap()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Friendly).Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Devices_AreDrawnOnlyOnceDiscovered()
        {
            var security = new SecurityPlan(true, 0, new Vector2Int(19, 9), new CameraMount[0]);
            Build(false, IntelRig.Fog(), new IntelligenceMission { Security = security });
            yield return null;
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Device), Is.Empty);
            rig.Service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Device).Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator WithFogOff_TheMapDrawsNothing()
        {
            Build(false, new IntelligenceSettings());
            yield return null;
            Assert.That(view.BuildMarks(Panel), Is.Empty);
        }

        [UnityTest]
        public IEnumerator ToggleHidesAndShowsTheOverlay()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(view.IsVisible, Is.True);
            view.Toggle();
            Assert.That(view.IsVisible, Is.False);
            view.Toggle();
            Assert.That(view.IsVisible, Is.True);
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/IntelligenceDebugTextTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelligenceDebugTextTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [Test]
        public void WithoutAMission_ItSaysSo()
        {
            rig = new IntelRig(corridor: false);
            Assert.That(IntelligenceDebugText.Describe(rig.Service, new Health[0], new MissionObjective[0]), Does.Contain("no mission"));
        }

        [UnityTest]
        public IEnumerator ItShowsActualVersusKnown_ForRegionsEnemiesAndObjectives()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            hostile.name = "Hostile_1";
            rig.Begin(IntelRig.Fog());
            yield return null;
            var goal = new ObjectiveStub("hack");
            goal.SetKnown(false);

            var text = IntelligenceDebugText.Describe(rig.Service, rig.Hostiles, new MissionObjective[] { goal });
            Assert.That(text, Does.Contain("INTEL TRUTH VIEW"));
            Assert.That(text, Does.Contain("fog=True"));
            Assert.That(text, Does.Contain("R0 Room known=Observed hostiles(actual)=0"));
            Assert.That(text, Does.Contain("R1 Room known=Unknown hostiles(actual)=1"));
            Assert.That(text, Does.Contain("Hostile_1: Unknown actual=(6.5, -1.5)"));
            Assert.That(text, Does.Contain("hack: UNKNOWN"));
        }

        [UnityTest]
        public IEnumerator ItShowsTheCameraNetwork_AndRunningScans()
        {
            rig = new IntelRig(corridor: false);
            var mount = new CameraMount(new Vector2Int(18, 3), new Vector2Int(1, 0), 1);
            var security = new SecurityPlan(true, 0, new Vector2Int(2, 2), new[] { mount });
            var spec = new CameraSpec(1, security.CameraPosition(rig.Layout, 0), security.CameraForward(0), 12f, 45f);
            var network = new CameraNetwork(new[] { spec });
            rig.Begin(IntelRig.Fog(), new IntelligenceMission { Security = security, Network = network });
            yield return null;
            var before = IntelligenceDebugText.Describe(rig.Service, new Health[0], new MissionObjective[0]);
            Assert.That(before, Does.Contain("Cameras: intact x1"));
            Assert.That(before, Does.Contain("Scans: none"));

            network.Compromise();
            rig.Service.Scan(IntelRig.InLineGround, 3f, 4f);
            var after = IntelligenceDebugText.Describe(rig.Service, new Health[0], new MissionObjective[0]);
            Assert.That(after, Does.Contain("Cameras: hacked x1"));
            Assert.That(after, Does.Contain("Scans: 1"));
        }

        sealed class ObjectiveStub : MissionObjective
        {
            public ObjectiveStub(string id) : base(id, ObjectiveType.Interact, id, true) { }
            public override string Describe() => Title;
        }
    }
}
#endif
```

Create `Assets/_Project/Tests/PlayMode/IntelligenceDeveloperInputTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelligenceDeveloperInputTests : InputTestFixture
    {
        Keyboard keyboard;
        InputActionAsset actions;
        MissionRig rig;
        IntelligenceService service;
        IntelMapView map;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            rig = new MissionRig();
            service = rig.AddIntelligence(IntelligenceSettings.Full());
            var mapHost = rig.World.Track(new GameObject("Map"));
            map = mapHost.AddComponent<IntelMapView>();
            map.Initialize(service, rig.Encounter, rig.Director);
            // Inactive while wiring, so OnEnable subscribes to the real actions, not to the empty references.
            var host = rig.World.Track(new GameObject("IntelDeveloperInput"));
            host.SetActive(false);
            var input = host.AddComponent<IntelligenceDeveloperInput>();
            input.Initialize(service, rig.Director, map, TestControls.Ref(actions, "Developer/ToggleTruthView"),
                TestControls.Ref(actions, "Developer/CycleIntelligencePreset"), TestControls.Ref(actions, "Developer/ToggleIntelMap"));
            host.SetActive(true);
        }

        public override void TearDown()
        {
            rig.Dispose();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(UnityEngine.InputSystem.Controls.KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator F12_TogglesTruthView_AndOnlyTheDisplay()
        {
            yield return rig.Generate(12345);
            Assert.That(service.TruthView, Is.False);
            yield return Tap(keyboard.f12Key);
            Assert.That(service.TruthView, Is.True);
            yield return Tap(keyboard.f12Key);
            Assert.That(service.TruthView, Is.False);
        }

        [UnityTest]
        public IEnumerator M_TogglesTheMapOverlay()
        {
            yield return rig.Generate(12345);
            Assert.That(map.IsVisible, Is.True);
            yield return Tap(keyboard.mKey);
            Assert.That(map.IsVisible, Is.False);
        }

        [UnityTest]
        public IEnumerator F5_CyclesThePreset_AndRegeneratesTheSameSeed()
        {
            yield return rig.Generate(12345);
            Assert.That(service.IsFogActive, Is.False, "the Full preset");
            var layoutHash = rig.Director.Report.LayoutHash;

            Press(keyboard.f5Key);
            yield return null;
            Release(keyboard.f5Key);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(rig.Director.Settings.seed, Is.EqualTo(12345));
            Assert.That(rig.Director.Settings.intelligence.fogEnabled, Is.True, "the next preset is Blind");
            Assert.That(service.IsFogActive, Is.True);
            Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(layoutHash), "same seed, same level: fog never changes the mission");
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.IntelMapProjectionTests` -> `EXIT=1`.

- [ ] **Step 3: Implement**

Edit `Assets/_Project/Input/BlackglassControls.inputactions` (JSON; keep the file's one-object-per-line style). In the `Developer` map's `actions` array replace the last action line

```
                { "name": "ResetProgression", "type": "Button", "id": "d29a6e4c-b2f5-4c81-a037-5e4b9c1f2d63", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
```

with that same line followed by a comma and the three new actions:

```
                { "name": "ResetProgression", "type": "Button", "id": "d29a6e4c-b2f5-4c81-a037-5e4b9c1f2d63", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "ToggleTruthView", "type": "Button", "id": "c3a1f0d2-4b6e-4f87-9a05-6e2d8b1c7f31", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "CycleIntelligencePreset", "type": "Button", "id": "d4b2a1e3-5c7f-4098-8b16-7f3e9c2d8a42", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "ToggleIntelMap", "type": "Button", "id": "e5c3b2f4-6d80-41a9-9c27-804fad3e9b53", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
```

and in the same map's `bindings` array replace the last binding line (the `f11` / `ResetProgression` one) with itself plus a comma and:

```
                { "name": "", "id": "f6d4c3a5-7e91-42ba-8d38-915abe4fac64", "path": "<Keyboard>/f12", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "ToggleTruthView", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "07e5d4b6-8fa2-43cb-9e49-a26bcf5ab075", "path": "<Keyboard>/f5", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "CycleIntelligencePreset", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "18f6e5c7-90b3-44dc-8f5a-b37cd06bc186", "path": "<Keyboard>/m", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "ToggleIntelMap", "isComposite": false, "isPartOfComposite": false }
```

Create `Assets/_Project/Scripts/Intelligence/IntelMapView.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Maps the mission's world rectangle into a screen panel (IMGUI coordinates: y down, so world z points up the panel).</summary>
    public static class IntelMapProjection
    {
        const float Padding = 6f;

        /// <summary>The union of every region's world rectangle (x and y of the Rect are world x and z).</summary>
        public static Rect WorldBounds(RegionMap map)
        {
            var bounds = map.WorldRect(0);
            for (var i = 1; i < map.Count; i++)
            {
                var r = map.WorldRect(i);
                bounds = Rect.MinMaxRect(Mathf.Min(bounds.xMin, r.xMin), Mathf.Min(bounds.yMin, r.yMin),
                    Mathf.Max(bounds.xMax, r.xMax), Mathf.Max(bounds.yMax, r.yMax));
            }
            return bounds;
        }

        public static Vector2 ToPanel(Rect panel, Rect bounds, Vector3 world)
        {
            var scale = Scale(panel, bounds);
            var origin = Origin(panel, bounds, scale);
            return new Vector2(origin.x + (world.x - bounds.xMin) * scale, origin.y + (bounds.yMax - world.z) * scale);
        }

        public static Rect ToPanel(Rect panel, Rect bounds, Rect worldRect)
        {
            var scale = Scale(panel, bounds);
            var origin = Origin(panel, bounds, scale);
            return new Rect(origin.x + (worldRect.xMin - bounds.xMin) * scale, origin.y + (bounds.yMax - worldRect.yMax) * scale,
                worldRect.width * scale, worldRect.height * scale);
        }

        static float Scale(Rect panel, Rect bounds)
        {
            var width = Mathf.Max(bounds.width, 0.01f);
            var height = Mathf.Max(bounds.height, 0.01f);
            return Mathf.Max(0.01f, Mathf.Min((panel.width - 2f * Padding) / width, (panel.height - 2f * Padding) / height));
        }

        static Vector2 Origin(Rect panel, Rect bounds, float scale) =>
            new Vector2(panel.x + (panel.width - bounds.width * scale) * 0.5f, panel.y + (panel.height - bounds.height * scale) * 0.5f);
    }

    public enum MapMarkKind
    {
        Region,
        Friendly,
        Enemy,
        LastKnown,
        Device,
        Objective,
    }

    /// <summary>One thing the overlay draws.</summary>
    public readonly struct MapMark
    {
        public MapMark(MapMarkKind kind, Rect rect, Color color, string label = "")
        {
            Kind = kind;
            Rect = rect;
            Color = color;
            Label = label;
        }

        public MapMarkKind Kind { get; }
        public Rect Rect { get; }
        public Color Color { get; }
        public string Label { get; }
    }

    /// <summary>
    /// The prototype tactical map (IMGUI, bottom right, works while paused): the regions coloured by what the player knows
    /// (unknown dark, discovered grey, observed light), the squad, hostiles that are in sight, a marker where a hostile was
    /// last seen, discovered security devices and known objectives. Shown only while fog is on; the developer truth view
    /// also draws the unknown regions and every hostile. Not a production minimap. The marks are built by BuildMarks so tests
    /// can read them.
    /// </summary>
    public sealed class IntelMapView : MonoBehaviour
    {
        internal static readonly Color UnknownColor = new Color(0.07f, 0.07f, 0.09f);
        internal static readonly Color DiscoveredColor = new Color(0.36f, 0.38f, 0.42f);
        internal static readonly Color ObservedColor = new Color(0.78f, 0.82f, 0.88f);
        internal static readonly Color TruthUnknownColor = new Color(0.30f, 0.12f, 0.12f);
        static readonly Color FriendlyColor = new Color(0.25f, 0.9f, 0.35f);
        static readonly Color EnemyColor = new Color(0.95f, 0.2f, 0.2f);
        static readonly Color TruthEnemyColor = new Color(0.5f, 0.1f, 0.1f);
        static readonly Color LastKnownColor = new Color(1f, 0.6f, 0.1f);
        static readonly Color DeviceColor = new Color(0.2f, 0.85f, 0.95f);
        static readonly Color ObjectiveColor = new Color(0.98f, 0.9f, 0.2f);

        [SerializeField] IntelligenceService intelligence;
        [SerializeField] Encounter encounter;
        // Optional: gives the known objectives.
        [SerializeField] MissionDirector director;
        [SerializeField] Vector2 size = new Vector2(260f, 190f);

        readonly List<MapMark> marks = new List<MapMark>();
        bool visible = true;
        GUIStyle labelStyle;

        public bool IsVisible => visible;

        public void Toggle() => visible = !visible;

        internal void Initialize(IntelligenceService service, Encounter currentEncounter, MissionDirector missionDirector)
        {
            intelligence = service;
            encounter = currentEncounter;
            director = missionDirector;
        }

        internal IReadOnlyList<MapMark> BuildMarks(Rect panel)
        {
            marks.Clear();
            if (intelligence == null || !intelligence.IsFogActive || intelligence.Map == null)
                return marks;
            var map = intelligence.Map;
            var truth = intelligence.TruthView;
            var bounds = IntelMapProjection.WorldBounds(map);

            for (var region = 0; region < map.Count; region++)
            {
                var state = intelligence.StateOfRegion(region);
                var color = state == KnowledgeState.Observed ? ObservedColor
                    : state == KnowledgeState.Discovered ? DiscoveredColor
                    : truth ? TruthUnknownColor : UnknownColor;
                marks.Add(new MapMark(MapMarkKind.Region, IntelMapProjection.ToPanel(panel, bounds, map.WorldRect(region)), color));
            }
            foreach (var device in intelligence.Devices)
            {
                if (intelligence.IsDeviceShown(device.Id))
                    marks.Add(Dot(MapMarkKind.Device, panel, bounds, device.Position, DeviceColor, 5f, device.Id == SecurityPlan.TerminalDeviceId ? "T" : "C"));
            }
            if (director != null && director.Runtime != null)
            {
                foreach (var goal in director.Runtime.Objectives)
                {
                    if (goal.IsKnown && goal.HasTarget && goal.State != ObjectiveState.Completed)
                        marks.Add(Dot(MapMarkKind.Objective, panel, bounds, goal.TargetPosition, ObjectiveColor, 7f, "!"));
                }
            }
            if (encounter != null)
            {
                foreach (var hostile in encounter.Hostiles)
                {
                    if (hostile == null || !hostile.IsAlive)
                        continue;
                    var state = intelligence.StateOfEnemy(hostile);
                    if (state == KnowledgeState.Observed)
                        marks.Add(Dot(MapMarkKind.Enemy, panel, bounds, hostile.transform.position, EnemyColor, 6f));
                    else if (state == KnowledgeState.Discovered && intelligence.TryLastKnown(hostile, out var seen))
                        marks.Add(Dot(MapMarkKind.LastKnown, panel, bounds, seen, LastKnownColor, 6f, "?"));
                    else if (truth)
                        marks.Add(Dot(MapMarkKind.Enemy, panel, bounds, hostile.transform.position, TruthEnemyColor, 6f, "x"));
                }
                foreach (var friendly in encounter.Friendlies)
                {
                    if (friendly != null && friendly.IsAlive)
                        marks.Add(Dot(MapMarkKind.Friendly, panel, bounds, friendly.transform.position, FriendlyColor, 6f));
                }
            }
            return marks;
        }

        static MapMark Dot(MapMarkKind kind, Rect panel, Rect bounds, Vector3 world, Color color, float size, string label = "")
        {
            var at = IntelMapProjection.ToPanel(panel, bounds, world);
            return new MapMark(kind, new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), color, label);
        }

        void OnGUI()
        {
            if (!visible || intelligence == null || !intelligence.IsFogActive)
                return;
            var panel = new Rect(Screen.width - size.x - 10f, Screen.height - size.y - 10f, size.x, size.y);
            labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            foreach (var mark in BuildMarks(panel))
            {
                GUI.color = mark.Color;
                GUI.DrawTexture(mark.Rect, Texture2D.whiteTexture);
                if (mark.Label.Length > 0)
                {
                    GUI.color = Color.white;
                    GUI.Label(new Rect(mark.Rect.x - 6f, mark.Rect.y - 8f, mark.Rect.width + 12f, 16f), mark.Label, labelStyle);
                }
            }
            GUI.color = Color.white;
            GUI.Label(new Rect(panel.x + 4f, panel.y + 2f, 200f, 18f), intelligence.TruthView ? "INTEL (truth view)" : "INTEL");
        }
    }
}
```

Create `Assets/_Project/Scripts/Intelligence/IntelligenceDebugView.cs` (the pure text and the IMGUI component that draws it):

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>The truth-view text: what exists next to what the player knows. Pure strings, so tests can read it.</summary>
    public static class IntelligenceDebugText
    {
        public static string Describe(IntelligenceService service, IReadOnlyList<Health> hostiles, IReadOnlyList<MissionObjective> goals)
        {
            if (service == null || !service.HasMission)
                return "INTEL TRUTH VIEW | no mission";
            var text = new StringBuilder("INTEL TRUTH VIEW | ").Append(service.Settings.Describe());
            var map = service.Map;
            for (var region = 0; region < map.Count; region++)
            {
                var actual = 0;
                foreach (var hostile in hostiles)
                {
                    if (hostile != null && hostile.IsAlive && map.RegionAt(hostile.transform.position) == region)
                        actual++;
                }
                text.Append(FormattableString.Invariant($"\nR{region} {map[region].Kind} known={service.StateOfRegion(region)} hostiles(actual)={actual}"));
            }
            foreach (var hostile in hostiles)
            {
                if (hostile == null || !hostile.IsAlive)
                    continue;
                var p = hostile.transform.position;
                text.Append(FormattableString.Invariant($"\n{hostile.name}: {service.StateOfEnemy(hostile)} actual=({p.x:0.#}, {p.z:0.#})"));
                if (service.TryLastKnown(hostile, out var seen))
                    text.Append(FormattableString.Invariant($" lastKnown=({seen.x:0.#}, {seen.z:0.#})"));
            }
            foreach (var goal in goals)
                text.Append(FormattableString.Invariant($"\n{goal.Id}: {(goal.IsKnown ? "known" : "UNKNOWN")} {goal.State}"));
            var network = service.Network;
            if (network != null)
                text.Append(FormattableString.Invariant($"\nCameras: {(network.Compromised ? "hacked" : "intact")} x{network.Cameras.Count}"));
            text.Append(service.Pulses.Count == 0 ? "\nScans: none" : FormattableString.Invariant($"\nScans: {service.Pulses.Count}"));
            return text.ToString();
        }
    }

    /// <summary>Debug-only IMGUI: the truth-view text, drawn only while the developer truth view is on.</summary>
    public sealed class IntelligenceDebugView : MonoBehaviour
    {
        [SerializeField] IntelligenceService intelligence;
        [SerializeField] Encounter encounter;
        [SerializeField] MissionDirector director;

        GUIStyle style;

        void OnGUI()
        {
            if (intelligence == null || !intelligence.TruthView || encounter == null)
                return;
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            var goals = director != null && director.Runtime != null ? (IReadOnlyList<MissionObjective>)director.Runtime.Objectives : Array.Empty<MissionObjective>();
            var text = IntelligenceDebugText.Describe(intelligence, encounter.Hostiles, goals);
            var area = new Rect(10f, 330f, 560f, Screen.height - 340f);
            GUI.color = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), text, style);
            GUI.color = Color.white;
            GUI.Label(area, text, style);
        }
    }

}
```

Create `Assets/_Project/Scripts/Intelligence/IntelligenceGizmoView.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug-only gizmos for the truth view: region outlines coloured by state, camera cones and running scans.</summary>
    public sealed class IntelligenceGizmoView : MonoBehaviour
    {
        [SerializeField] IntelligenceService intelligence;

        void OnDrawGizmos()
        {
            if (intelligence == null || !intelligence.TruthView || intelligence.Map == null)
                return;
            var map = intelligence.Map;
            for (var region = 0; region < map.Count; region++)
            {
                var state = intelligence.StateOfRegion(region);
                Gizmos.color = state == KnowledgeState.Observed ? Color.white : state == KnowledgeState.Discovered ? Color.gray : new Color(0.6f, 0.2f, 0.2f);
                var rect = map.WorldRect(region);
                Gizmos.DrawWireCube(new Vector3(rect.center.x, 0.1f, rect.center.y), new Vector3(rect.width, 0.1f, rect.height));
            }
            var network = intelligence.Network;
            if (network != null)
            {
                foreach (var camera in network.Cameras)
                {
                    Gizmos.color = network.Compromised ? Color.green : Color.yellow;
                    var left = Quaternion.Euler(0f, -camera.HalfAngle, 0f) * camera.Forward * camera.Range;
                    var right = Quaternion.Euler(0f, camera.HalfAngle, 0f) * camera.Forward * camera.Range;
                    Gizmos.DrawLine(camera.Position, camera.Position + left);
                    Gizmos.DrawLine(camera.Position, camera.Position + right);
                    Gizmos.DrawLine(camera.Position + left, camera.Position + right);
                }
            }
            Gizmos.color = Color.cyan;
            foreach (var pulse in intelligence.Pulses)
                Gizmos.DrawWireSphere(pulse.Centre, pulse.Radius);
        }
    }
}
```

(Unity needs each `MonoBehaviour` in a file named after it to serialize it in a scene, so the gizmo component has its own file above. `IntelMapView.cs` holds the component plus its non-component helpers `IntelMapProjection`, `MapMarkKind` and `MapMark`, which is fine.)

Create `Assets/_Project/Scripts/Intelligence/IntelligenceDeveloperInput.cs`:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Developer keys for the intelligence layer as input actions (F12 truth view, F5 next preset + regenerate the same
    /// seed, M map overlay). They ask the service, the director and the overlay; the rules live there. Keyboard only, like
    /// the other developer keys, and never part of normal play.
    /// </summary>
    public sealed class IntelligenceDeveloperInput : MonoBehaviour
    {
        [SerializeField] IntelligenceService intelligence;
        [SerializeField] MissionDirector director;
        [SerializeField] IntelMapView map;
        [SerializeField] InputActionReference truthViewAction;
        [SerializeField] InputActionReference cyclePresetAction;
        [SerializeField] InputActionReference toggleMapAction;

        int presetIndex = -1;

        internal void Initialize(IntelligenceService service, MissionDirector missionDirector, IntelMapView mapView,
            InputActionReference truthView, InputActionReference cyclePreset, InputActionReference toggleMap)
        {
            intelligence = service;
            director = missionDirector;
            map = mapView;
            truthViewAction = truthView;
            cyclePresetAction = cyclePreset;
            toggleMapAction = toggleMap;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, truthViewAction, cyclePresetAction, toggleMapAction);
            if (truthViewAction != null)
                truthViewAction.action.performed += OnTruthView;
            if (cyclePresetAction != null)
                cyclePresetAction.action.performed += OnCyclePreset;
            if (toggleMapAction != null)
                toggleMapAction.action.performed += OnToggleMap;
        }

        void OnDisable()
        {
            if (truthViewAction != null)
                truthViewAction.action.performed -= OnTruthView;
            if (cyclePresetAction != null)
                cyclePresetAction.action.performed -= OnCyclePreset;
            if (toggleMapAction != null)
                toggleMapAction.action.performed -= OnToggleMap;
            InputActionUtility.SetEnabled(false, truthViewAction, cyclePresetAction, toggleMapAction);
        }

        void OnTruthView(InputAction.CallbackContext context)
        {
            if (intelligence != null)
                intelligence.TruthView = !intelligence.TruthView;
        }

        void OnToggleMap(InputAction.CallbackContext context)
        {
            if (map != null)
                map.Toggle();
        }

        // The next preset replaces the director's intelligence settings and the same seed is generated again.
        void OnCyclePreset(InputAction.CallbackContext context)
        {
            if (director == null)
                return;
            if (presetIndex < 0)
                presetIndex = CurrentPresetIndex();
            presetIndex = (presetIndex + 1) % IntelligenceSettings.PresetCount;
            director.Settings.intelligence = IntelligenceSettings.Preset(presetIndex);
            director.RegenerateSame();
        }

        // The preset the director is on now (0, Full, when it matches none).
        int CurrentPresetIndex()
        {
            var current = director.Settings.intelligence != null ? director.Settings.intelligence.Describe() : string.Empty;
            for (var i = 0; i < IntelligenceSettings.PresetCount; i++)
            {
                if (IntelligenceSettings.Preset(i).Describe() == current)
                    return i;
            }
            return 0;
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.IntelMapProjectionTests`, `...IntelligenceInputAssetTests`, `...InputAssetTests`, `...OperativeInputAssetTests`, `...AbilityInputAssetTests`, `...NoDeviceTypesInGameplayTests`, then PlayMode `Blackglass.Tests.IntelMapViewTests`, `...IntelligenceDebugTextTests`, `...IntelligenceDeveloperInputTests`, `...MissionDeveloperInputTests`, `...OperativeDeveloperInputTests`. Expected: all `EXIT=0`. (If the reimported input asset loses an `InputActionReference` sub-asset for an existing action, an older test fails: re-run once so Unity finishes the import, and if it persists open the JSON and check the commas.)

- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Scripts Assets/_Project/Input Assets/_Project/Tests
git commit -m "Add the intel map overlay, truth-view debug views and developer keys" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Scene wiring, Recon Scan asset on Kestrel, and the existing scene suites' opt-out

**Files:**
- Create: `Assets/_Project/Editor/IntelligenceSceneBuilder.cs`; generated by running it: `Assets/_Project/Data/Abilities/ReconScan.asset`, `Assets/_Project/Materials/IntelFog.mat`, `IntelVeil.mat`, `IntelMarker.mat`
- Modify: `Assets/_Project/Scenes/ProceduralMission.unity` (by the builder), `Assets/_Project/Data/Operatives/Definitions/Kestrel.asset` (by the builder), `Assets/_Project/Tests/PlayMode/ProceduralMissionSceneTests.cs`, `Assets/_Project/Tests/PlayMode/MissionObjectivesSceneTests.cs`
- Test: `Assets/_Project/Tests/EditMode/IntelligenceAssetTests.cs`, `Assets/_Project/Tests/PlayMode/IntelligenceSceneTests.cs`

**Interfaces:**
- Consumes: everything above; `OperativeDataBuilder`'s conventions (open the scene first, `SerializedObject`, idempotent, `-executeMethod` entry).
- Produces: menu `Blackglass/Intelligence/Wire ProceduralMission Scene` and `IntelligenceSceneBuilder.BuildAndWireFromCommandLine()`. The scene ships with the **Blind** preset (decision 037); the scene suites that click enemies or count interactables opt into full knowledge by setting `director.Settings.intelligence = IntelligenceSettings.Full()` before their deterministic regeneration.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/IntelligenceAssetTests.cs`:

```csharp
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class IntelligenceAssetTests
    {
        const string Root = "Assets/_Project/";

        [Test]
        public void ReconScan_HasTheSpecifiedNumbers()
        {
            var scan = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(Root + "Data/Abilities/ReconScan.asset");
            Assert.That(scan, Is.Not.Null, "run Blackglass/Intelligence/Wire ProceduralMission Scene");
            Assert.That(scan.DisplayName, Is.EqualTo("Recon Scan"));
            Assert.That(scan.TargetMode, Is.EqualTo(AbilityTargetMode.Ground));
            Assert.That(scan.Effect, Is.EqualTo(AbilityEffect.Reveal));
            Assert.That(scan.Range, Is.EqualTo(18f));
            Assert.That(scan.Radius, Is.EqualTo(12f));
            Assert.That(scan.Cooldown, Is.EqualTo(25f));
            Assert.That(scan.RevealSeconds, Is.EqualTo(6f));
            Assert.That(scan.RequiresLineOfSight, Is.False);
        }

        [Test]
        public void Kestrel_CarriesTheScanAsHerSecondAbility()
        {
            var kestrel = AssetDatabase.LoadAssetAtPath<OperativeDefinition>(Root + "Data/Operatives/Definitions/Kestrel.asset");
            var scan = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(Root + "Data/Abilities/ReconScan.asset");
            Assert.That(kestrel.Abilities.Length, Is.EqualTo(2));
            Assert.That(kestrel.Abilities[0].DisplayName, Is.EqualTo("Aimed Shot"));
            Assert.That(kestrel.Abilities[1], Is.SameAs(scan));
        }

        [Test]
        public void TheOtherOperatives_AreUnchanged()
        {
            var darius = AssetDatabase.LoadAssetAtPath<OperativeDefinition>(Root + "Data/Operatives/Definitions/Darius.asset");
            var sable = AssetDatabase.LoadAssetAtPath<OperativeDefinition>(Root + "Data/Operatives/Definitions/Sable.asset");
            Assert.That(darius.Abilities.Select(a => a.DisplayName), Is.EqualTo(new[] { "Aimed Shot", "Blast" }));
            Assert.That(sable.Abilities.Select(a => a.DisplayName), Is.EqualTo(new[] { "Mend", "Aimed Shot" }));
        }

        [TestCase("IntelFog")]
        [TestCase("IntelVeil")]
        [TestCase("IntelMarker")]
        public void TheIntelMaterials_Exist(string name)
        {
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(Root + "Materials/" + name + ".mat"), Is.Not.Null, name);
        }

        [Test]
        public void TheVeilMaterial_IsTransparent_AndTheFogIsNot()
        {
            var veil = AssetDatabase.LoadAssetAtPath<Material>(Root + "Materials/IntelVeil.mat");
            var fog = AssetDatabase.LoadAssetAtPath<Material>(Root + "Materials/IntelFog.mat");
            Assert.That(veil.renderQueue, Is.GreaterThanOrEqualTo(3000));
            Assert.That(fog.renderQueue, Is.LessThan(3000));
        }
    }
}
```

Create `Assets/_Project/Tests/PlayMode/IntelligenceSceneTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The ProceduralMission scene as shipped: Blind preset, everything wired, the first mission fogged.</summary>
    public class IntelligenceSceneTests : InputTestFixture
    {
        Keyboard keyboard;
        InputActionAsset actions;
        MissionDirector director;
        IntelligenceService service;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            Time.timeScale = 1f;
            if (SceneManager.GetActiveScene().name == "ProceduralMission")
                PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Load()
        {
            yield return SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
            director = Object.FindFirstObjectByType<MissionDirector>();
            service = Object.FindFirstObjectByType<IntelligenceService>();
            Assert.That(director, Is.Not.Null);
            Assert.That(service, Is.Not.Null, "run Blackglass/Intelligence/Wire ProceduralMission Scene");
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), director.Report.Failure);
            yield return null;
        }

        static bool Wired(Object component, string property)
        {
            var found = new SerializedObject(component).FindProperty(property);
            return found != null && found.objectReferenceValue != null;
        }

        [UnityTest]
        public IEnumerator TheScene_ShipsTheBlindPreset_AndTheFirstMissionIsFogged()
        {
            yield return Load();
            Assert.That(director.Settings.intelligence.Describe(), Is.EqualTo(IntelligenceSettings.Blind().Describe()));
            Assert.That(service.IsFogActive, Is.True);
            Assert.That(service.StateOfRegion(director.Current.Layout.FriendlyRoom), Is.EqualTo(KnowledgeState.Observed));
            foreach (var hostile in director.Hostiles)
                Assert.That(service.CanTarget(hostile.GetComponent<Health>()), Is.False, hostile.name);
            Assert.That(director.Current.CameraTerminal, Is.Not.Null);
            Assert.That(director.Runtime.Objectives.Single(g => g.Type == ObjectiveType.ReachZone).IsKnown, Is.True);
            Assert.That(director.Runtime.Objectives.Single(g => g.Type == ObjectiveType.Interact).IsKnown, Is.False);
        }

        [UnityTest]
        public IEnumerator EveryConsumer_IsWiredToTheService()
        {
            yield return Load();
            Assert.That(Wired(director, "systems.intelligence"), Is.True);
            foreach (var (component, name) in new (Object, string)[]
            {
                (Object.FindFirstObjectByType<TacticalCursor>(), "TacticalCursor"),
                (Object.FindFirstObjectByType<AbilityTargeting>(), "AbilityTargeting"),
                (Object.FindFirstObjectByType<PlayerCommandInput>(), "PlayerCommandInput"),
                (Object.FindFirstObjectByType<PrototypeHud>(), "PrototypeHud"),
                (Object.FindFirstObjectByType<MissionHud>(), "MissionHud"),
                (Object.FindFirstObjectByType<MissionDebugView>(), "MissionDebugView"),
                (Object.FindFirstObjectByType<CoverView>(), "CoverView"),
                (Object.FindFirstObjectByType<FogPresenter>(), "FogPresenter"),
                (Object.FindFirstObjectByType<IntelMapView>(), "IntelMapView"),
                (Object.FindFirstObjectByType<IntelligenceDebugView>(), "IntelligenceDebugView"),
                (Object.FindFirstObjectByType<IntelligenceGizmoView>(), "IntelligenceGizmoView"),
                (Object.FindFirstObjectByType<IntelligenceDeveloperInput>(), "IntelligenceDeveloperInput"),
            })
            {
                Assert.That(component, Is.Not.Null, name + " is missing from the scene");
                Assert.That(Wired(component, "intelligence"), Is.True, name + " has no intelligence service");
            }
            Assert.That(Wired(Object.FindFirstObjectByType<FogPresenter>(), "fogMaterial"), Is.True);
            Assert.That(Wired(Object.FindFirstObjectByType<FogPresenter>(), "veilMaterial"), Is.True);
            Assert.That(Wired(service, "markerMaterial"), Is.True);
        }

        [UnityTest]
        public IEnumerator TheSpawnedUnits_AreWiredToo()
        {
            yield return Load();
            foreach (var hostile in director.Hostiles)
                Assert.That(hostile.GetComponent<HostilePresenter>(), Is.Not.Null, hostile.name);
            foreach (var friendly in director.Friendlies)
                Assert.That(friendly.GetComponent<CompanionAI>(), Is.Not.Null);
            var kestrel = director.Friendlies.Select(f => f.GetComponent<UnitAbilities>())
                .FirstOrDefault(a => a != null && Enumerable.Range(0, a.Count).Any(i => a.Definition(i).Effect == AbilityEffect.Reveal));
            Assert.That(kestrel, Is.Not.Null, "one squad member carries the Recon Scan");
        }

        [UnityTest]
        public IEnumerator Kestrel_CanUseTheScanOnTheScene_AndRevealsARoom()
        {
            yield return Load();
            var casterAbilities = director.Friendlies.Select(f => f.GetComponent<UnitAbilities>())
                .First(a => a != null && Enumerable.Range(0, a.Count).Any(i => a.Definition(i).Effect == AbilityEffect.Reveal));
            var scan = Enumerable.Range(0, casterAbilities.Count).Select(casterAbilities.Definition).First(d => d.Effect == AbilityEffect.Reveal);
            var layout = director.Current.Layout;
            var farRoom = Enumerable.Range(0, layout.Rooms.Count)
                .Where(r => service.StateOfRegion(r) == KnowledgeState.Unknown)
                .OrderBy(r => Vector3.Distance(layout.RectCenter(layout.Rooms[r].Rect), casterAbilities.transform.position))
                .First();
            var centre = layout.RectCenter(layout.Rooms[farRoom].Rect);
            var inRange = casterAbilities.transform.position + (centre - casterAbilities.transform.position).normalized * Mathf.Min(scan.Range - 1f,
                Vector3.Distance(centre, casterAbilities.transform.position));
            Assert.That(casterAbilities.TryUse(AbilityCommand.AtGround(scan, inRange)), Is.True, casterAbilities.LastFailure.ToString());
            Assert.That(Enumerable.Range(0, service.Map.Count).Count(r => service.StateOfRegion(r) != KnowledgeState.Unknown),
                Is.GreaterThan(1), "the scan found more than the start room");
        }

        [UnityTest]
        public IEnumerator F12_ShowsTheTruth_ButTheHostilesStayUntargetable()
        {
            yield return Load();
            var hostile = director.Hostiles[0].GetComponent<Health>();
            Press(keyboard.f12Key);
            yield return null;
            Release(keyboard.f12Key);
            yield return null;
            Assert.That(service.TruthView, Is.True);
            Assert.That(service.IsUnitShown(hostile), Is.True);
            Assert.That(service.CanTarget(hostile), Is.False, "truth view is display only");
        }

        [UnityTest]
        public IEnumerator Regenerating_KeepsTheFogOn_AndStartsFromTheBriefing()
        {
            yield return Load();
            Assert.That(director.RegenerateSame(), Is.True);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready, 20f);
            Assert.That(service.IsFogActive, Is.True);
            foreach (var hostile in director.Hostiles)
                Assert.That(service.CanTarget(hostile.GetComponent<Health>()), Is.False);
        }
    }
}
#endif
```

Edit the two existing scene suites so they keep testing the systems they test, not the fog (the scene now ships Blind): in `ProceduralMissionSceneTests.LoadMission`, immediately before `Assert.That(director.Generate(DeterministicSeed), Is.True, ...)` add

```csharp
            // The scene ships with fog (decision 037); these tests click enemies and count terminals, so they play the same mission with full knowledge.
            director.Settings.intelligence = IntelligenceSettings.Full();
```

and the same two lines in `MissionObjectivesSceneTests.LoadMission`, right after `director.Settings.interactionSeconds = interactionSeconds;`. Leave `Scene_OpensOnADrawnSeed_NotOnTheInspectorSeed_EveryTime` alone (it asserts nothing about knowledge).

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.IntelligenceAssetTests` -> failures: the assets do not exist yet (this is the expected red state; the code compiles).

- [ ] **Step 3: Implement the builder, run it**

Create `Assets/_Project/Editor/IntelligenceSceneBuilder.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Creates the Recon Scan ability and the three intelligence materials, gives Kestrel the scan, and wires the
    /// ProceduralMission scene for battlefield uncertainty (the Blind preset, the service, the presenters, the overlay and
    /// the developer keys, and every consumer's reference). Idempotent; existing assets are kept so hand tuning survives.
    /// </summary>
    public static class IntelligenceSceneBuilder
    {
        const string DataRoot = "Assets/_Project/Data";
        const string ScanPath = DataRoot + "/Abilities/ReconScan.asset";
        const string KestrelPath = DataRoot + "/Operatives/Definitions/Kestrel.asset";
        const string AimedShotPath = DataRoot + "/Abilities/AimedShot.asset";
        const string MaterialRoot = "Assets/_Project/Materials";
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        [MenuItem("Blackglass/Intelligence/Wire ProceduralMission Scene")]
        public static void WireSceneMenu() => BuildAndWire();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildAndWireFromCommandLine() => BuildAndWire();

        public static void BuildAndWire()
        {
            var scan = CreateScan();
            GiveKestrelTheScan(scan);
            var fog = MakeMaterial(MaterialRoot + "/IntelFog.mat", new Color(0.02f, 0.02f, 0.03f, 1f), transparent: false);
            var veil = MakeMaterial(MaterialRoot + "/IntelVeil.mat", new Color(0f, 0f, 0.02f, 0.55f), transparent: true);
            var marker = MakeMaterial(MaterialRoot + "/IntelMarker.mat", new Color(1f, 0.6f, 0.1f, 1f), transparent: false);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            WireScene(fog, veil, marker);
        }

        static AbilityDefinition CreateScan()
        {
            var existing = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(ScanPath);
            if (existing != null)
                return existing;
            var asset = ScriptableObject.CreateInstance<AbilityDefinition>();
            AssetDatabase.CreateAsset(asset, ScanPath);
            var serialized = new SerializedObject(asset);
            serialized.FindProperty("displayName").stringValue = "Recon Scan";
            serialized.FindProperty("targetMode").enumValueIndex = (int)AbilityTargetMode.Ground;
            serialized.FindProperty("range").floatValue = 18f;
            serialized.FindProperty("requiresLineOfSight").boolValue = false;
            serialized.FindProperty("coverRule").enumValueIndex = (int)AbilityCoverRule.Ignored;
            serialized.FindProperty("cooldown").floatValue = 25f;
            serialized.FindProperty("effect").enumValueIndex = (int)AbilityEffect.Reveal;
            serialized.FindProperty("amount").intValue = 0;
            serialized.FindProperty("radius").floatValue = 12f;
            serialized.FindProperty("revealSeconds").floatValue = 6f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // Kestrel (Recon) already has the Aimed Shot; the scan becomes her second ability.
        static void GiveKestrelTheScan(AbilityDefinition scan)
        {
            var kestrel = AssetDatabase.LoadAssetAtPath<OperativeDefinition>(KestrelPath);
            if (kestrel == null)
                throw new InvalidOperationException("Kestrel's definition is missing: run Blackglass/Operatives first.");
            var serialized = new SerializedObject(kestrel);
            var abilities = serialized.FindProperty("abilities");
            for (var i = 0; i < abilities.arraySize; i++)
            {
                if (abilities.GetArrayElementAtIndex(i).objectReferenceValue == scan)
                    return;
            }
            abilities.arraySize++;
            abilities.GetArrayElementAtIndex(abilities.arraySize - 1).objectReferenceValue = scan;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(kestrel);
        }

        static Material MakeMaterial(string path, Color color, bool transparent)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                throw new InvalidOperationException("The URP Unlit shader is missing.");
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            material.SetColor("_BaseColor", color);
            if (transparent)
            {
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetFloat("_ZWrite", 0f);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetOverrideTag("RenderType", "Transparent");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Transparent;
                material.SetShaderPassEnabled("ShadowCaster", false);
            }
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static void WireScene(Material fog, Material veil, Material marker)
        {
            // Open the scene first: opening it in Single mode unloads unreferenced assets (see OperativeDataBuilder).
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = UnityEngine.Object.FindFirstObjectByType<MissionDirector>();
            var encounter = UnityEngine.Object.FindFirstObjectByType<Encounter>();
            if (director == null || encounter == null)
                throw new InvalidOperationException("ProceduralMission needs a MissionDirector and an Encounter.");

            var host = FindOrAdd<IntelligenceService>("Intelligence");
            Set(host, "markerMaterial", marker);

            var fogPresenter = GetOrAdd<FogPresenter>(host.gameObject);
            Set(fogPresenter, "intelligence", host);
            Set(fogPresenter, "fogMaterial", fog);
            Set(fogPresenter, "veilMaterial", veil);

            var map = GetOrAdd<IntelMapView>(host.gameObject);
            Set(map, "intelligence", host);
            Set(map, "encounter", encounter);
            Set(map, "director", director);

            var debug = GetOrAdd<IntelligenceDebugView>(host.gameObject);
            Set(debug, "intelligence", host);
            Set(debug, "encounter", encounter);
            Set(debug, "director", director);

            var gizmos = GetOrAdd<IntelligenceGizmoView>(host.gameObject);
            Set(gizmos, "intelligence", host);

            var input = GetOrAdd<IntelligenceDeveloperInput>(host.gameObject);
            Set(input, "intelligence", host);
            Set(input, "director", director);
            Set(input, "map", map);
            Set(input, "truthViewAction", ActionReference("ToggleTruthView"));
            Set(input, "cyclePresetAction", ActionReference("CycleIntelligencePreset"));
            Set(input, "toggleMapAction", ActionReference("ToggleIntelMap"));

            // The consumers: each takes the service through a serialized `intelligence` reference.
            Set(director, "systems.intelligence", host);
            Consumer<TacticalCursor>(host);
            Consumer<AbilityTargeting>(host);
            Consumer<PlayerCommandInput>(host);
            Consumer<PrototypeHud>(host);
            Consumer<MissionHud>(host);
            Consumer<MissionDebugView>(host);
            Consumer<MissionGizmoView>(host);
            Consumer<CoverView>(host);

            // The scene ships with fog: nothing known but the extraction and what the squad can see.
            director.Settings.intelligence = IntelligenceSettings.Blind();
            EditorUtility.SetDirty(director);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static void Consumer<T>(IntelligenceService service) where T : Component
        {
            var component = UnityEngine.Object.FindFirstObjectByType<T>();
            if (component == null)
            {
                Debug.LogWarning($"IntelligenceSceneBuilder: no {typeof(T).Name} in ProceduralMission; skipped.");
                return;
            }
            Set(component, "intelligence", service);
        }

        static T FindOrAdd<T>(string objectName) where T : Component
        {
            var existing = UnityEngine.Object.FindFirstObjectByType<T>();
            return existing != null ? existing : new GameObject(objectName).AddComponent<T>();
        }

        static T GetOrAdd<T>(GameObject host) where T : Component
        {
            var existing = host.GetComponent<T>();
            return existing != null ? existing : host.AddComponent<T>();
        }

        static void Set(UnityEngine.Object target, string path, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(path);
            if (property == null)
                throw new InvalidOperationException($"{target.GetType().Name} has no serialized field '{path}'.");
            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static InputActionReference ActionReference(string actionName)
        {
            var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.name == actionName);
            if (reference == null)
                throw new InvalidOperationException($"No InputActionReference for '{actionName}' in {ControlsPath}.");
            return reference;
        }
    }
}
#endif
```


Run the builder from the command line (Unity closed):

```bash
UNITY="/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe"
"$UNITY" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod Blackglass.EditorTools.IntelligenceSceneBuilder.BuildAndWireFromCommandLine -logFile Logs/intel-build.log
echo EXIT=$?
```

Expected: `EXIT=0`, no `error` lines in `Logs/intel-build.log` (`grep -n "error\|Exception" Logs/intel-build.log`). Then inspect the change set: `git status --short` must show exactly the new `ReconScan.asset(.meta)`, three new `.mat(.meta)` files, a changed `Kestrel.asset`, a changed `ProceduralMission.unity` and the script `.meta` files; **not** the pre-existing `Assets/Settings/Mobile_RPAsset.asset` and `ProjectSettings/URPProjectSettings.asset` (leave those two alone, they were dirty before this phase). Review `git diff --stat Assets/_Project/Scenes/ProceduralMission.unity` and skim the diff: only added objects/components, the new `intelligence` references and the director's `intelligence` settings block may differ.

- [ ] **Step 4: Run to verify pass**

Run, one after another: `Tools/run-tests.sh EditMode Blackglass.Tests.IntelligenceAssetTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.IntelligenceSceneTests`, then the two edited suites `Tools/run-tests.sh PlayMode Blackglass.Tests.ProceduralMissionSceneTests` and `Tools/run-tests.sh PlayMode Blackglass.Tests.MissionObjectivesSceneTests`, and `Tools/run-tests.sh EditMode Blackglass.Tests.OperativeAssetTests`, `Tools/run-tests.sh PlayMode Blackglass.Tests.OperativeSpawnPlayModeTests`. Expected: all `EXIT=0`.

If `OperativeSpawnPlayModeTests` or another operative test asserts Kestrel's exact ability list (one ability), update that assertion to the two-ability list: the data change is intended (spec: Kestrel carries the scan); say so in the commit message. If `ProceduralMissionSceneTests` fails on a transparent `Material` or shader warning, the console is not clean: fix the material (the veil's shader keywords), do not suppress the log.

- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Editor Assets/_Project/Data Assets/_Project/Materials Assets/_Project/Scenes/ProceduralMission.unity Assets/_Project/Tests
git commit -m "Wire the ProceduralMission scene for fog, give Kestrel the Recon Scan, opt the scene suites out" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Decision record, manual test plan, full verification

**Files:**
- Modify: `Docs/Decisions.md`
- Create: `Docs/Phase11-ManualTests.md`

- [ ] **Step 1: Decision record**

Append to `Docs/Decisions.md` (match the style of 035 and 036: a numbered `##` heading, then bullets) the following entry, replacing nothing:

```markdown
## 037 — Mission intelligence: what exists versus what the player knows

- **Decided (2026-10-08):**
  - **Layers.** The mission (layout, NavMesh, colliders, objectives, AI) is never changed by knowledge. A knowledge layer sits between it and everything the player sees or can target: `MissionIntelligence` (pure model) is fed through a reveal API (`RevealRegion`, `RevealArea`, `ObserveEnemy`, `RevealDevice`, `RevealObjective`, `MarkLastKnown`) by `IntelligenceService`, and every consumer asks the service through an optional serialized reference and the `Knowledge` helpers. A null service or a fog-off mission means "everything known", so the default behaviour is Phase 10's.
  - **Knowledge-state model.** One enum, `KnowledgeState { Unknown, Discovered, Observed }`, for regions, devices and enemies. Regions and devices: Discovered is permanent, Observed is in sight this pass. Enemies: Discovered means last known (a marker), Observed means live. Only Observed enemies are targets.
  - **Region discovery.** Regions are the layout's rooms plus its corridor strips (`RegionMap`, derived from `MissionLayout`, not a second level representation). A friendly observes its own region and any region where a coarse sample point is within range (14 m) and in line of sight (the combat sight test): a closed wall reveals nothing behind it, an opening reveals what the aperture allows. Discovered layout persists; live sight does not. Passes run every 0.2 s of scaled time.
  - **Last-known behaviour.** An enemy that leaves observation becomes a marker at the last seen position and is no longer tracked; it is not a target. The marker never clears by looking at its spot (see limitations).
  - **Exposure by firing.** A hostile that fires (hit or miss, `UnitAttacker.Attacked`/`Missed`) is Observed for `exposureSeconds` (3 s) wherever it is, then becomes last known. This keeps a long-range shooter from being both lethal and unanswerable.
  - **Objective knowledge.** Existence is separate from knowledge: `MissionObjective.IsKnown` (the decision 033 seam) is written only by the model. Starting knowledge is configured (`All`, `ExtractionAndTerminal`, `ExtractionOnly`, `None`); an objective with a place is learned when its region is discovered, the elimination objective when the first hostile is observed. Unknown objectives are listed by a vague title ("Locate the data terminal") when the settings ask for it; the hostile count is replaced by kills. Completion logic never reads knowledge.
  - **Hacked cameras.** A camera-control terminal (a second `MissionInteractable`, available from the start, found like any device) compromises the camera network; hacked cameras observe by position, facing, range, a 90 degree field and line of sight, and stay live by default (`cameraStaysLive`; false gives a one-time reveal of what they cover). Devices (cameras, the terminal) are discovered by being seen. `SecurityPlacer` draws terminal and cameras from its own salted stream after the objectives.
  - **Recon Scan.** A ground-targeted ability with the new `Reveal` effect (range 18 m, radius 12 m, 25 s cooldown, 6 s, no line of sight needed), on Kestrel. It discovers every region the circle touches, discovers devices inside it and observes hostiles inside it for 6 s, through walls (a scan is not sight), then they become markers. It uses the existing ability, queue, pause and controller path unchanged.
  - **Anti-leak rules.** Targets: the pointer ray passes through unobserved hostile colliders and unknown terminals; cursor snap, cycling, soft target, Attack fallback, `AbilityTargeting.TargetHealth`, `UnitAbilities.Check` and companion assist all use the same test. Displays: unit labels, hostile counts, attack lines, order lines, cover markers (unknown regions, cover held by an unshown hostile), debug text and gizmos, objective counts. Ground abilities may be aimed anywhere in range, including undiscovered ground; their preview lists only observed hostiles while the effect still hits every hostile it should.
  - **Presentation.** `FogPresenter` builds collider-free volumes under the mission root (unknown regions opaque, discovered ones a veil, walls only while every region beside them is unknown); `HostilePresenter` sets `Renderer.forceRenderingOff` on hidden hostiles and draws the last-known marker. Knowledge never lives in renderer flags.
  - **Debug.** F12 truth view (display only: targeting stays restricted), F5 next fog preset + regenerate the same seed, M map overlay; truth view shows region/enemy/objective/camera/scan data. The scene ships with the Blind preset; the code default is fog off.
- **Why:** the player's information must be a design variable (briefings, recon, cameras), not an accident of what is rendered. Keeping the model pure and the consumers behind one tiny query surface lets every system be gated without any of them learning how knowledge is gathered, and keeps generation, AI and combat untouched (same seed, same mission).
- **Rejected:** a tile-grid fog of war (costlier, not the unit the brief asks for, a second level representation); renderer-visibility flags as the data model (breaks with themes and cannot be queried); disabling or destroying unknown content (breaks NavMesh, AI and determinism); fogging enemy AI (the brief keeps its perception); each system keeping its own notion of "visible" (guarantees a leak); changing combat code for exposure (the existing attack events are enough).
- **Implications:** a new information source is one more caller of the reveal API; a new display of enemy or hidden-room data must go through `Knowledge`; Phase 12 may add clearing markers by sight, alerts, or fogged AI without touching the model's shape.
- **Known limitations:**
  - A unit already chasing a target keeps chasing after it leaves observation; its movement can hint at where the target is.
  - Last-known markers only update when the enemy is observed again; looking at an empty spot does not clear them.
  - A newly seen enemy appears up to 0.2 s late (pass interval).
  - Death marker discs are not hidden: a hostile that dies unseen leaves a disc.
  - The "discovered" veil depends on a runtime URP transparent material; without one a discovered region is simply clear.
  - A scan ignores walls, so it can mark an enemy behind several of them.
  - Companion assist acts only on observed hostiles; `AutoRetaliate` reacts to damage and is not gated.
  - A wall box beside several regions stays visible once any one of them is discovered.
```

- [ ] **Step 2: Manual test plan**

Create `Docs/Phase11-ManualTests.md`:

```markdown
# Phase 11 manual test plan: battlefield uncertainty

Play `ProceduralMission`. It ships with the **Blind** preset. Keys: F5 next preset (regenerates the same seed), F6 same seed, F7 new seed, F12 truth view, M map overlay, Space pause, Tab switch character, 1-4 abilities. Presets cycle Full knowledge, Blind, Layout known, Objectives known, Briefed.

1. **Full knowledge.** Press F5 until the preset is "Full knowledge" (the map overlay disappears). Everything is visible, every hostile is targetable, the objective panel shows all objectives with counts. It plays exactly like Phase 10.
2. **No knowledge (Blind).** F5 to Blind. Only the room the squad stands in is drawn; the rest is dark. The mission panel lists extraction, "[?] Locate the data terminal" and "[?] Neutralise the hostile presence". The top-left line reads "Hostiles: unknown". Click on dark areas: the squad moves (ground), nothing else happens.
3. **Exploring rooms.** Walk through a door. The next room lights as you see into it; leave and it stays on the map and in the world under a dark veil (discovered), brightening again while in sight.
4. **Walls do not reveal.** Stand beside a wall of a dark room: it stays dark; the room is revealed only through a door or opening. Check on the overlay (M) that an unknown room beside you stays dark.
5. **Line of sight.** Look down a corridor into a lit room: part of it appears. An enemy in line shows (red dot, label, health); an enemy in the same room but behind a wall piece does not.
6. **Last-known behaviour.** Let an enemy see you, step out of its sight: it vanishes, an orange post marks where it was; it does not follow it. Walk back into sight: it is live again. Kill an enemy: no marker stays.
7. **Exposure.** Stay out of sight and let an enemy shoot: it shows for about 3 s while it fires, then becomes a marker. You can shoot back while it shows.
8. **Target-cycling leak test.** With an unseen enemy nearby: Next/Previous target (controller) and Attack (controller, keyboard click on it) must never pick it or aim at it; the cursor must not snap to it; ability aiming (Aimed Shot) on its spot says there is no target; a Blast preview over it says "hits 0" but, if fired, still hurts it. Pause (Space) and repeat: nothing changes.
9. **Camera hack.** Find the "Camera control" terminal in the start area (or a neighbouring room). Press R (or Confirm) to hack it. Cameras (grey housing, lens turns green) now show their rooms live: enemies in their cone are targetable. Set `cameraStaysLive` false in the Inspector (MissionSettings, Intelligence) and repeat: the rooms are revealed once, enemies are not watched.
10. **Recon Scan.** Tab to Kestrel, press 2, aim anywhere within the white range ring (also on dark ground), confirm: the 12 m circle's rooms are discovered, enemies inside show for 6 s through walls, then become markers; the ability goes on a 25 s cooldown. Do it with the mouse, with a controller, queued behind a move (Shift / LT), and while paused (the order runs when you resume).
11. **Unknown and revealed objectives.** The data terminal is a vague line until you discover its room; then it shows "Access data terminal" with a marker. The elimination line shows "(n down)" and never a total. Complete the mission with objectives you never "found": it still completes.
12. **Tactical pause with intel.** Pause during a fight: the overlay, cursor, target selection and queueing work; nothing hidden advances (an exposed enemy stays exposed, scans do not tick down).
13. **Keyboard and controller targeting.** Repeat 8 and 10 with a controller (right stick cursor): snap, cycling, Confirm and Attack behave the same.
14. **Regeneration.** F6, F7, F5 mid-mission (also while paused): knowledge resets to the briefing, no stale markers, fog or labels, the console stays clean.
15. **Truth view vs normal view.** F12: all rooms outlined and tinted by what you know, every enemy shown with its state, objectives marked known/UNKNOWN, camera cones and scan circles drawn (Scene view gizmos), the truth text bottom-left. Targeting is still restricted. F12 again: back to normal. None of this appears without F12.
16. **Briefed preset.** F5 to "Briefed": the start room, its corridors and the extraction room are known, the terminal objective is known, one enemy starts as an orange marker.
```

- [ ] **Step 3: Full verification**

With the Unity Editor closed run the whole suites, one at a time:

```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```

Expected: both `EXIT=0`. Record the printed totals (the script prints `total=... passed=...`). The baselines measured before this phase were EditMode 788/788 and PlayMode 729/729, so both totals must be larger by the number of tests this phase added, with `failed="0"`. Write the two totals into the commit message and into the completion report. Also run `grep -c "error\|Exception" Logs/TestRun-PlayMode.log` and open any hit that is not an expected test log; the console must be clean.

If a previously passing test fails, do not edit it to pass: find which task's change caused it (the likely candidates are the scene suites' loaders, the `Kestrel` ability count and `InputAssetTests`), fix the cause, and re-run.

- [ ] **Step 4: Commit**

```bash
git add Docs/Decisions.md Docs/Phase11-ManualTests.md
git commit -m "Record decision 037 and the Phase 11 manual test plan" -m "Verified: EditMode <n>/<n>, PlayMode <m>/<m> (baselines 788 and 729)." -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 5: Hand back**

Do not merge or push. Report to the owner: the branch `phase-11-intelligence`, the two totals, the manual test plan path, the list of files created and changed, and the limitations in decision 037. The session's controller then records the Phase 11 state in memory.

---

## Spec coverage map

| Spec requirement | Task |
|---|---|
| Knowledge-state model, per-mission configuration, presets | 1, 3 |
| Region map consuming the generated layout; discovery through openings, walls reveal nothing | 2, 6 (geometry tests) |
| Enemy observation, last known, no wallhack, exposure by firing | 3, 6, 10 |
| Objective knowledge separate from existence; vague listing; counts hidden | 3, 7 |
| Security camera + control terminal; live cameras; configurable | 4, 5, 6 |
| Recon Scan ability through the ability path (keyboard, controller, queue, pause) | 9 |
| Tactical map overlay (unknown / discovered / observed) | 12 |
| Targeting restrictions, controller snapping, ground-ability decision | 8, 9 |
| Leak audit (labels, counts, lines, cover, debug text, objective counts) | 8, 10, 11 |
| Companions share intel; enemy AI not fogged | 6, 8 |
| Pause; regeneration reset; determinism | 6, 10, 13 |
| Debug: truth view, region/enemy/objective/camera/scan data | 12 |
| Decision record, manual test plan, completion report | 14 |
