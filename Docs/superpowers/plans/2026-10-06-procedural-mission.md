# Phase 8 Procedural Mission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generate a small, seeded, deterministic combat mission at runtime (layout, geometry, NavMesh, cover, spawns, encounter) in a new `ProceduralMission` scene, regenerable in Play Mode, reusing every existing combat system unchanged.

**Architecture:** A pure `MissionGenerator` (seeded SplitMix64, integer tile grid) produces a `MissionLayout`. `MissionBuilder` turns it into cubes under `GeneratedMissionRoot/Geometry` and builds a runtime `NavMeshSurface`; `MissionNavigation` validates it; the existing `CoverDiscovery` (scoped to the mission root, with a reachability filter) derives all cover from the geometry; `MissionSpawner` instantiates the existing unit prefabs and wires them to the persistent systems; `MissionDirector` runs the bounded sequence as a coroutine and owns teardown.

**Tech Stack:** Unity 6.3 LTS (6000.3.25f1), C#, NUnit via Unity Test Framework, `com.unity.ai.navigation` 2.0.14 (already installed), Input System 1.20.

**Spec:** `Docs/superpowers/specs/2026-10-06-procedural-mission-design.md` (read it first; the "Plan notes" below record where the plan deviates from the spec's first draft, and the spec has been updated to match).

## Global Constraints

- Unity 6.3 LTS `6000.3.25f1`; no new packages; no third-party procedural packages.
- Input System only; gameplay code names no device types (`NoDeviceTypesInGameplayTests`); developer keys go through input actions.
- Layout generation uses `SeededRandom` only: never `UnityEngine.Random`, never `System.Random`, never `DateTime` inside `MissionGenerator`.
- Layout decisions are integer tile decisions; every footprint is a whole number of metres; unordered collections are never iterated for a decision.
- Mission geometry: walls and tall obstacles 3 m high, 1 m thick; low cover 1 m high; floor top at y = 0, 0.2 m thick.
- Defaults: 4x3 grid, 14 m cells, 6 rooms, corridor width 3, 1 extra loop, 1 baffle per room, low-cover density 3 per 100 m2, 3 friendlies, 3 hostiles, team separation 16 m, 20 attempts.
- Every generated object lives under `GeneratedMissionRoot`; persistent systems are never generated.
- Do not modify `Prototype.unity` or the tests that load it. Existing behaviour of `CoverDiscovery.Discover()` (no arguments) is unchanged.
- No static mutable state, no singletons. Console must stay free of errors and warnings during generation (units are spawned inside an inactive `Actors` object and wired before activation so `OnEnable` warnings never fire).
- Work stays on branch `procedural-mission`. Commits end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Unity batch runs need exclusive access to the project (one Editor instance at a time): run `Tools/run-tests.sh` only with the Editor closed, never two runs at once. `EXIT=1` means a compile error, `EXIT=2` test failures, `EXIT=0` all passed.
- EditMode tests get no `Awake`/`OnEnable`/`Update`; never use `??`/`?.` on `UnityEngine.Object`; colliders created at runtime need `Physics.SyncTransforms()` before raycasts.

## Plan notes (deviations from the spec, with reasons)

1. `Encounter.Initialize(friendlies, hostiles)` (already `internal`) is reused instead of a new `SetSides`.
2. `CoverDiscovery.Discover(root, isReachable)` does **not** report a dropped-unreachable count (the generator also calls the predicate for peek points, so the number would be misleading).
3. Obstacle clearance applies to walls, door gaps and other obstacles, **not** to spawn regions (a spawn region is the room interior, so that rule could never hold). Spawn tiles instead stay at least 1 tile from every obstacle and 1 tile inside the room walls.
4. Minimum NavMesh area ratio is 0.3, not 0.5: the eroded NavMesh of a cluttered room is legitimately well below its floor area; the check only guards against an empty or broken build.
5. The `Developer` action map is keyboard-only and `InputAssetTests.PadActions` is an explicit list, so no asset test needs changing.
6. Spawned units are wired with three new one-line internal methods (`CompanionAI.Wire`, `EnemyAI.Wire`, `UnitCover.Wire`) so prefab-tuned values are not overwritten.
7. `friendlyCount` is a `MissionSettings` field (the director sets it from the number of friendly slots).

## Review Focus

Failure modes the spec implies but no obvious task test covers; each line has a test in the named task:

- A tiny/degenerate configuration (2 rooms, minimum cell size, `roomCount` above the grid size, `maxAttempts` 1) must fail or clamp cleanly, never hang or throw. Task 3 (`Generate_ClampsDegenerateSettings`, `Generate_FailsCleanlyWhenTheTeamsCannotBeSeparated`).
- A seed whose first attempt fails must still give the same final layout every time. Task 3 (`Attempt_IsAPureFunctionOfSeedAndAttemptNumber`).
- Regenerating twice quickly (F6 pressed again while generating) must not start two overlapping builds. Task 6 (`Generate_WhileGenerating_IsIgnored`).
- Regenerating while the game is paused, or with a unit dead, a unit selected, an ability armed and queued orders pending, must leave no stale state. Task 6 (`Regenerate_LeavesNoStaleState`).
- A death marker, a move marker or any other root object created during play must not survive regeneration. Task 6 (same test compares scene roots).
- A unit spawned must not be inside geometry or off the NavMesh, and the director must not start the encounter when a point cannot be placed. Task 6.
- The camera must be re-bounded and re-focused after every generation, so the squad is on screen. Task 6.
- Controller cursor must snap to generated cover and enemies (no mouse-only interaction). Task 8.

## File Structure

New, in `Assets/_Project/Scripts/Mission/` (namespace `Blackglass`):

| File | Responsibility |
|---|---|
| `SeededRandom.cs` | SplitMix64 PRNG class, `ForAttempt(seed, attempt)` |
| `MissionSettings.cs` | serializable config, `Validated()`, `Describe()`, `MissionConstants` |
| `MissionLayout.cs` | pure data: rooms, connections, floor, boxes, spawns, bounds, hash |
| `RectCover.cs` | deterministic greedy rectangle decomposition of a bool mask |
| `MissionGenerator.cs` | rooms, connections, floor, walls, obstacles, spawns, bounded retry |
| `MissionBuilder.cs` | layout -> `GeneratedMissionRoot` cubes + `NavMeshSurface` |
| `MissionNavigation.cs` | post-build NavMesh validation |
| `MissionSlots.cs` | `FriendlySlot`, `HostileSlot`, `MissionSystems` serializable data |
| `MissionSpawner.cs` | placement checks, instantiate, wire, persistent-system update |
| `MissionDirector.cs` | state, coroutine, teardown, report, camera, developer API |
| `MissionDeveloperInput.cs` | F6/F7 -> director |
| `MissionDebugView.cs` | IMGUI text; `MissionDebugText` pure formatter |
| `MissionGizmoView.cs` | `OnDrawGizmos` rooms, graph, regions, bounds |

Modified: `Blackglass.asmdef` (+`Unity.AI.Navigation`), `Cover/CoverDiscovery.cs`, `CameraControl/TacticalCameraController.cs`, `AI/CompanionAI.cs`, `AI/EnemyAI.cs`, `Units/UnitCover.cs`, `Input/BlackglassControls.inputactions` (+`Developer` map), `ProjectSettings/EditorBuildSettings.asset` (+ the new scene), `Docs/Decisions.md` (record 026).

New scene: `Assets/_Project/Scenes/ProceduralMission.unity` (a copy of `Prototype` with the arena and units removed and the mission objects added, authored by a temporary editor script that is deleted afterwards, as in Phase 7).

New tests: `Tests/EditMode/{SeededRandomTests,MissionSettingsTests,MissionGeneratorTests,MissionLayoutInvariantTests,MissionDebugTextTests}.cs`; `Tests/PlayMode/{MissionBuilderPlayModeTests,MissionDirectorPlayModeTests,MissionDeveloperInputTests,ProceduralMissionSceneTests,CoverDiscoveryScopedPlayModeTests,CameraBoundsPlayModeTests}.cs`; `Tests/PlayMode/TestSupport/MissionRig.cs`.

## Test totals (measured baseline: EditMode 517 passed; PlayMode 513 passed)

| After task | EditMode | PlayMode |
|---|---|---|
| baseline | 517 | 513 |
| 1 | 527 | 513 |
| 2 | 533 | 513 |
| 3 | 548 | 513 |
| 4 | 548 | 519 |
| 5 | 548 | 526 |
| 6 | 548 | 534 |
| 7 | 552 | 536 |
| 8 | 552 | 546 |
| 9 | 552 | 546 |

(Task 3's golden-hash test uses 3 `TestCase`s, each counted. Recount with a real run after each task and correct this table in the same commit if a count differs.)

---

### Task 1: SeededRandom and MissionSettings

**Files:**
- Create: `Assets/_Project/Scripts/Mission/SeededRandom.cs`, `Assets/_Project/Scripts/Mission/MissionSettings.cs`
- Test: `Assets/_Project/Tests/EditMode/SeededRandomTests.cs`, `Assets/_Project/Tests/EditMode/MissionSettingsTests.cs`

**Interfaces:**
- Produces: `SeededRandom(ulong seed)`, `static SeededRandom ForAttempt(int seed, int attempt)`, `ulong NextULong()`, `int NextInt(int maxExclusive)`, `int NextInt(int min, int maxExclusive)`, `float NextFloat()` (0..1), `bool Chance(float p)`, `void Shuffle<T>(IList<T>)`.
- Produces: `MissionSettings` (public fields below), `MissionSettings Validated()`, `string Describe()`, static `MissionConstants`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/SeededRandomTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class SeededRandomTests
    {
        [Test]
        public void SeedZero_StartsWithTheReferenceSplitMix64Value() =>
            Assert.That(new SeededRandom(0UL).NextULong(), Is.EqualTo(0xE220A8397B1DCDAFUL));

        [Test]
        public void SameSeed_GivesTheSameSequence()
        {
            var a = new SeededRandom(42UL);
            var b = new SeededRandom(42UL);
            for (var i = 0; i < 100; i++)
                Assert.That(a.NextULong(), Is.EqualTo(b.NextULong()));
        }

        [Test]
        public void ForAttempt_DiffersByAttemptAndBySeed_AndIsRepeatable()
        {
            Assert.That(SeededRandom.ForAttempt(12345, 1).NextULong(), Is.EqualTo(SeededRandom.ForAttempt(12345, 1).NextULong()));
            Assert.That(SeededRandom.ForAttempt(12345, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForAttempt(12345, 2).NextULong()));
            Assert.That(SeededRandom.ForAttempt(12345, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForAttempt(12346, 1).NextULong()));
        }

        [Test]
        public void NextInt_StaysInRange_AndCoversIt()
        {
            var rng = new SeededRandom(7UL);
            var seen = new HashSet<int>();
            for (var i = 0; i < 500; i++)
            {
                var value = rng.NextInt(3, 8);
                Assert.That(value, Is.InRange(3, 7));
                seen.Add(value);
            }
            Assert.That(seen, Has.Count.EqualTo(5));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rng.NextInt(0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
        }

        [Test]
        public void NextFloat_IsInZeroToOne_AndChanceRespectsTheExtremes()
        {
            var rng = new SeededRandom(9UL);
            for (var i = 0; i < 500; i++)
                Assert.That(rng.NextFloat(), Is.GreaterThanOrEqualTo(0f).And.LessThan(1f));
            Assert.That(Enumerable.Range(0, 100).All(_ => !rng.Chance(0f)), Is.True);
            Assert.That(Enumerable.Range(0, 100).All(_ => rng.Chance(1f)), Is.True);
        }

        [Test]
        public void Shuffle_KeepsTheSameItems_AndIsRepeatable()
        {
            var a = Enumerable.Range(0, 20).ToList();
            var b = Enumerable.Range(0, 20).ToList();
            new SeededRandom(5UL).Shuffle(a);
            new SeededRandom(5UL).Shuffle(b);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.OrderBy(x => x), Is.EqualTo(Enumerable.Range(0, 20)));
            Assert.That(a, Is.Not.EqualTo(Enumerable.Range(0, 20).ToList()));
        }
    }
}
```

`Assets/_Project/Tests/EditMode/MissionSettingsTests.cs`:

```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class MissionSettingsTests
    {
        [Test]
        public void Defaults_AreTheValuesTheSpecNames()
        {
            var s = new MissionSettings();
            Assert.That((s.gridColumns, s.gridRows, s.cellSize, s.roomCount), Is.EqualTo((4, 3, 14, 6)));
            Assert.That((s.corridorWidth, s.extraLoops, s.bafflesPerRoom), Is.EqualTo((3, 1, 1)));
            Assert.That((s.friendlyCount, s.hostileCount, s.maxAttempts), Is.EqualTo((3, 3, 20)));
            Assert.That((s.lowCoverDensity, s.minTeamSeparation), Is.EqualTo((3f, 16f)));
        }

        [Test]
        public void Validated_ClampsEveryFieldIntoItsRange_AndLeavesTheOriginalAlone()
        {
            var raw = new MissionSettings
            {
                gridColumns = 99, gridRows = 0, cellSize = 3, roomCount = 500, corridorWidth = 1, extraLoops = -4,
                bafflesPerRoom = 40, lowCoverDensity = 900f, friendlyCount = 0, hostileCount = 99, minTeamSeparation = -5f,
                maxAttempts = 0,
            };
            var v = raw.Validated();
            Assert.That(v.gridColumns, Is.EqualTo(6));
            Assert.That(v.gridRows, Is.EqualTo(2));
            Assert.That(v.cellSize, Is.EqualTo(12));
            Assert.That(v.roomCount, Is.EqualTo(v.gridColumns * v.gridRows));
            Assert.That(v.corridorWidth, Is.EqualTo(3));
            Assert.That(v.extraLoops, Is.EqualTo(0));
            Assert.That(v.bafflesPerRoom, Is.EqualTo(3));
            Assert.That(v.lowCoverDensity, Is.EqualTo(10f));
            Assert.That(v.friendlyCount, Is.EqualTo(1));
            Assert.That(v.hostileCount, Is.EqualTo(8));
            Assert.That(v.minTeamSeparation, Is.EqualTo(0f));
            Assert.That(v.maxAttempts, Is.EqualTo(1));
            Assert.That(raw.gridColumns, Is.EqualTo(99), "the original is not modified");
        }

        [Test]
        public void Validated_KeepsAtLeastTwoRooms_EvenOnTheSmallestGrid()
        {
            var v = new MissionSettings { gridColumns = 2, gridRows = 2, roomCount = 1 }.Validated();
            Assert.That(v.roomCount, Is.EqualTo(2));
        }

        [Test]
        public void Describe_NamesTheSeedAndEverySetting()
        {
            var text = new MissionSettings { seed = 777 }.Describe();
            foreach (var part in new[] { "seed=777", "grid=4x3", "cell=14", "rooms=6", "corridor=3", "loops=1", "baffles=1",
                         "lowCover=3", "friendlies=3", "hostiles=3", "separation=16", "attempts=20" })
                Assert.That(text, Does.Contain(part));
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.SeededRandomTests"` (Editor closed).
Expected: `EXIT=1` with `error CS0246` for `SeededRandom` (compile failure is the expected "fail").

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Mission/SeededRandom.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// SplitMix64. Used for every mission layout decision instead of System.Random or UnityEngine.Random, so a seed
    /// means the same mission after any runtime or Unity upgrade and generation never disturbs combat randomness.
    /// A class, so a copy can never silently fork the sequence.
    /// </summary>
    public sealed class SeededRandom
    {
        const ulong Gamma = 0x9E3779B97F4A7C15UL;

        ulong state;

        public SeededRandom(ulong seed) => state = seed;

        /// <summary>The generator for one attempt of one seed: attempt n always gets the same stream.</summary>
        public static SeededRandom ForAttempt(int seed, int attempt) =>
            new SeededRandom(Finish(unchecked((ulong)(uint)seed) * Gamma + Finish(unchecked((ulong)(uint)attempt) + 0x632BE59BD9B4E019UL)));

        static ulong Finish(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public ulong NextULong()
        {
            unchecked
            {
                state += Gamma;
                return Finish(state);
            }
        }

        /// <summary>0 (inclusive) to maxExclusive (exclusive).</summary>
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "The upper bound must be positive.");
            return (int)(NextULong() % (ulong)maxExclusive);
        }

        /// <summary>min (inclusive) to maxExclusive (exclusive).</summary>
        public int NextInt(int min, int maxExclusive)
        {
            if (maxExclusive <= min)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "The range is empty.");
            return min + NextInt(maxExclusive - min);
        }

        /// <summary>0 (inclusive) to 1 (exclusive), 24 bits of precision.</summary>
        public float NextFloat() => (NextULong() >> 40) / 16777216f;

        public bool Chance(float probability) => NextFloat() < probability;

        /// <summary>Fisher-Yates.</summary>
        public void Shuffle<T>(IList<T> list)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = NextInt(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
```

`Assets/_Project/Scripts/Mission/MissionSettings.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Fixed numbers of the mission generator that are deliberately not exposed as settings.</summary>
    public static class MissionConstants
    {
        public const float WallHeight = 3f;
        public const float LowHeight = 1f;
        public const float FloorThickness = 0.2f;
        public const int RoomMin = 7;
        public const int RoomMax = 10;
        /// <summary>Tiles between a cell edge and its room, so the gutter between rooms is at least twice this.</summary>
        public const int RoomInset = 2;
        /// <summary>Free tiles kept between an obstacle and any wall or other obstacle.</summary>
        public const int Clearance = 2;
        public const float SpawnSpacing = 2.5f;
        /// <summary>The eroded NavMesh must cover at least this share of the floor: a sanity check, not a quality bar.</summary>
        public const float MinNavAreaRatio = 0.3f;
    }

    /// <summary>Plain tuning data for the mission generator. Same seed + same settings = same layout.</summary>
    [Serializable]
    public sealed class MissionSettings
    {
        public int seed = 12345;
        public int gridColumns = 4;
        public int gridRows = 3;
        /// <summary>Metres (= tiles) per grid cell.</summary>
        public int cellSize = 14;
        public int roomCount = 6;
        public int corridorWidth = 3;
        /// <summary>Extra connections beyond the spanning tree (loops).</summary>
        public int extraLoops = 1;
        /// <summary>Most free-standing tall wall segments per room.</summary>
        public int bafflesPerRoom = 1;
        /// <summary>Low-cover objects per 100 square metres of floor.</summary>
        public float lowCoverDensity = 3f;
        public int friendlyCount = 3;
        public int hostileCount = 3;
        /// <summary>Straight-line metres between any friendly and any hostile spawn.</summary>
        public float minTeamSeparation = 16f;
        public int maxAttempts = 20;

        /// <summary>A clamped copy; the original is untouched. Everything downstream works on the copy.</summary>
        public MissionSettings Validated()
        {
            var copy = (MissionSettings)MemberwiseClone();
            copy.gridColumns = Mathf.Clamp(gridColumns, 2, 6);
            copy.gridRows = Mathf.Clamp(gridRows, 2, 6);
            copy.cellSize = Mathf.Clamp(cellSize, 12, 20);
            copy.roomCount = Mathf.Clamp(roomCount, 2, copy.gridColumns * copy.gridRows);
            copy.corridorWidth = Mathf.Clamp(corridorWidth, 3, 5);
            copy.extraLoops = Mathf.Clamp(extraLoops, 0, 4);
            copy.bafflesPerRoom = Mathf.Clamp(bafflesPerRoom, 0, 3);
            copy.lowCoverDensity = Mathf.Clamp(lowCoverDensity, 0f, 10f);
            copy.friendlyCount = Mathf.Clamp(friendlyCount, 1, 6);
            copy.hostileCount = Mathf.Clamp(hostileCount, 1, 8);
            copy.minTeamSeparation = Mathf.Clamp(minTeamSeparation, 0f, 60f);
            copy.maxAttempts = Mathf.Clamp(maxAttempts, 1, 100);
            return copy;
        }

        /// <summary>One line with every setting, so a failure message plus its seed reproduces the layout.</summary>
        public string Describe() =>
            $"seed={seed} grid={gridColumns}x{gridRows} cell={cellSize} rooms={roomCount} corridor={corridorWidth} " +
            $"loops={extraLoops} baffles={bafflesPerRoom} lowCover={lowCoverDensity:0.##} friendlies={friendlyCount} " +
            $"hostiles={hostileCount} separation={minTeamSeparation:0.##} attempts={maxAttempts}";
    }
}
```

Note: `Describe` formats floats with the current culture; make it culture-safe by adding `using System.Globalization;` and using `string.Create(CultureInfo.InvariantCulture, $"...")` or `FormattableString.Invariant($"...")` so the test passes on any locale.

- [ ] **Step 4: Run to verify they pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.SeededRandomTests"` then `... "Blackglass.Tests.MissionSettingsTests"`.
Expected: both `EXIT=0`, 6 and 4 passed. If `SeedZero_...` fails with a different value, the finaliser constants are wrong: they must be the SplitMix64 ones above.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Mission Assets/_Project/Tests/EditMode/SeededRandomTests.cs Assets/_Project/Tests/EditMode/MissionSettingsTests.cs
git commit -m "Add the seeded random source and mission settings

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

(Include the `.meta` files Unity creates next to each new file; `git status` must show nothing untracked under `Assets/` afterwards.)

---

### Task 2: Layout data, rectangle cover, rooms, corridors, floor and walls

**Files:**
- Create: `Assets/_Project/Scripts/Mission/MissionLayout.cs`, `RectCover.cs`, `MissionGenerator.cs`
- Test: `Assets/_Project/Tests/EditMode/MissionGeneratorTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SeededRandom`, `MissionSettings`, `MissionConstants`).
- Produces: `MissionBoxKind {Wall, Baffle, Pillar, LowWall, Crate}`; `MissionBox(string name, MissionBoxKind kind, RectInt footprint, float height)` with `Name, Kind, Footprint, Height, IsTall`; `MissionRoom(int index, Vector2Int cell, RectInt rect)` with `Index, Cell, Rect`; `MissionConnection(int roomA, int roomB, RectInt strip)`; `MissionLayout` (below); `MissionGenerator.TryAttempt(MissionSettings validated, int attempt, out MissionLayout layout, out string reason)` and `MissionGenerator.Generate(MissionSettings)` returning `MissionGenerationResult { bool Succeeded; MissionLayout Layout; MissionSettings Settings; IReadOnlyList<string> Failures; string Describe() }`. In this task the layout's obstacle and spawn parts are empty; Task 3 fills them.

`MissionLayout` public surface (all read-only for callers):
`int Seed, Attempt, Width, Height`; `bool IsFloor(int x, int y)`; `int FloorTileCount`; `IReadOnlyList<MissionRoom> Rooms`; `IReadOnlyList<MissionConnection> Connections`; `IReadOnlyList<RectInt> FloorRects`; `IReadOnlyList<MissionBox> Boxes` (walls first, then obstacles); `int FriendlyRoom`; `IReadOnlyList<int> HostileRooms`; `RectInt FriendlyRegion`; `IReadOnlyList<RectInt> HostileRegions`; `IReadOnlyList<Vector2Int> FriendlySpawns, HostileSpawns`; `RectInt UsedTiles`; `ulong Hash`; `Vector3 ToWorld(float tileX, float tileY)`; `Vector3 TileCenter(Vector2Int tile)`; `Vector3 RectCenter(RectInt rect)`; `Bounds WorldBounds`.

- [ ] **Step 1: Write the failing tests** (`MissionGeneratorTests.cs`, EditMode)

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionGeneratorTests
    {
        static MissionLayout Make(int seed, Action<MissionSettings> tweak = null)
        {
            var settings = new MissionSettings { seed = seed };
            tweak?.Invoke(settings);
            var result = MissionGenerator.Generate(settings);
            Assert.That(result.Succeeded, Is.True, result.Describe());
            return result.Layout;
        }

        [Test]
        public void SameSeedTwice_GivesTheSameLayout() =>
            Assert.That(Make(12345).Hash, Is.EqualTo(Make(12345).Hash));

        [Test]
        public void DifferentSeeds_GiveDifferentLayouts()
        {
            var hashes = Enumerable.Range(1, 30).Select(s => Make(s).Hash).Distinct().Count();
            Assert.That(hashes, Is.GreaterThanOrEqualTo(28));
        }

        [Test]
        public void Rooms_AreTheRequestedCount_InsideTheirCells_AndNeverOverlap()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = Make(seed);
                Assert.That(layout.Rooms, Has.Count.EqualTo(6));
                foreach (var room in layout.Rooms)
                {
                    Assert.That(room.Rect.width, Is.InRange(MissionConstants.RoomMin, MissionConstants.RoomMax));
                    Assert.That(room.Rect.height, Is.InRange(MissionConstants.RoomMin, MissionConstants.RoomMax));
                    var cell = new RectInt(room.Cell.x * 14, room.Cell.y * 14, 14, 14);
                    Assert.That(room.Rect.xMin, Is.GreaterThanOrEqualTo(cell.xMin + MissionConstants.RoomInset));
                    Assert.That(room.Rect.xMax, Is.LessThanOrEqualTo(cell.xMax - MissionConstants.RoomInset));
                    Assert.That(room.Rect.yMin, Is.GreaterThanOrEqualTo(cell.yMin + MissionConstants.RoomInset));
                    Assert.That(room.Rect.yMax, Is.LessThanOrEqualTo(cell.yMax - MissionConstants.RoomInset));
                }
                for (var i = 0; i < layout.Rooms.Count; i++)
                    for (var j = i + 1; j < layout.Rooms.Count; j++)
                        Assert.That(layout.Rooms[i].Rect.Overlaps(layout.Rooms[j].Rect), Is.False);
            }
        }

        [Test]
        public void Connections_FormASpanningTreePlusLoops_WithCorridorsAsWideAsConfigured()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = Make(seed);
                Assert.That(layout.Connections.Count, Is.InRange(5, 6), "5 tree edges + at most 1 loop");
                foreach (var c in layout.Connections)
                {
                    var narrow = Mathf.Min(c.Strip.width, c.Strip.height);
                    Assert.That(narrow, Is.EqualTo(3));
                    Assert.That(Mathf.Max(c.Strip.width, c.Strip.height), Is.GreaterThanOrEqualTo(4), "crosses the gutter");
                }
            }
        }

        [Test]
        public void Floor_IsOneConnectedRegion_TheUnionOfRoomsAndCorridors()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = Make(seed);
                var floor = new HashSet<Vector2Int>();
                for (var x = 0; x < layout.Width; x++)
                    for (var y = 0; y < layout.Height; y++)
                        if (layout.IsFloor(x, y))
                            floor.Add(new Vector2Int(x, y));
                Assert.That(floor, Has.Count.EqualTo(layout.FloorTileCount));
                Assert.That(Flood(floor, floor.First()), Is.EqualTo(floor.Count), $"seed {seed}");
                var rects = layout.FloorRects.SelectMany(Tiles).ToList();
                Assert.That(rects, Has.Count.EqualTo(floor.Count), "floor rectangles cover the floor exactly once");
                Assert.That(rects.ToHashSet(), Is.EquivalentTo(floor));
            }
        }

        [Test]
        public void Walls_RingTheFloor_NeverOverlapIt_AndLeaveDoorGaps()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = Make(seed);
                var wallTiles = layout.Boxes.Where(b => b.Kind == MissionBoxKind.Wall).SelectMany(b => Tiles(b.Footprint)).ToList();
                Assert.That(wallTiles.Distinct().Count(), Is.EqualTo(wallTiles.Count), "wall boxes do not overlap");
                var walls = wallTiles.ToHashSet();
                foreach (var tile in walls)
                    Assert.That(layout.IsFloor(tile.x, tile.y), Is.False, $"wall tile {tile} is on the floor");
                for (var x = 1; x < layout.Width - 1; x++)
                    for (var y = 1; y < layout.Height - 1; y++)
                        if (layout.IsFloor(x, y))
                            foreach (var n in Neighbours8(x, y))
                                Assert.That(layout.IsFloor(n.x, n.y) || walls.Contains(n), Is.True, $"seed {seed}: gap at {n}");
                foreach (var c in layout.Connections)
                    foreach (var t in Tiles(c.Strip))
                        Assert.That(walls.Contains(t), Is.False, "a corridor tile is never a wall");
                Assert.That(layout.Boxes.Where(b => b.Kind == MissionBoxKind.Wall).All(b => b.Height == MissionConstants.WallHeight), Is.True);
            }
        }

        static IEnumerable<Vector2Int> Tiles(RectInt r)
        {
            for (var x = r.xMin; x < r.xMax; x++)
                for (var y = r.yMin; y < r.yMax; y++)
                    yield return new Vector2Int(x, y);
        }

        static IEnumerable<Vector2Int> Neighbours8(int x, int y)
        {
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        yield return new Vector2Int(x + dx, y + dy);
        }

        static int Flood(HashSet<Vector2Int> open, Vector2Int start)
        {
            var seen = new HashSet<Vector2Int> { start };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                foreach (var d in new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right })
                    if (open.Contains(t + d) && seen.Add(t + d))
                        queue.Enqueue(t + d);
            }
            return seen.Count;
        }
    }
}
```


- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionGeneratorTests"`. Expected: `EXIT=1`, `CS0246` for `MissionGenerator`/`MissionLayout`.

- [ ] **Step 3: Implement `MissionLayout.cs`**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public enum MissionBoxKind
    {
        Wall,
        Baffle,
        Pillar,
        LowWall,
        Crate,
    }

    /// <summary>An upright axis-aligned box, in tile units, standing on y = 0.</summary>
    public readonly struct MissionBox
    {
        public MissionBox(string name, MissionBoxKind kind, RectInt footprint, float height)
        {
            Name = name;
            Kind = kind;
            Footprint = footprint;
            Height = height;
        }

        public string Name { get; }
        public MissionBoxKind Kind { get; }
        public RectInt Footprint { get; }
        public float Height { get; }
        public bool IsTall => Kind == MissionBoxKind.Wall || Kind == MissionBoxKind.Baffle || Kind == MissionBoxKind.Pillar;
    }

    public readonly struct MissionRoom
    {
        public MissionRoom(int index, Vector2Int cell, RectInt rect)
        {
            Index = index;
            Cell = cell;
            Rect = rect;
        }

        public int Index { get; }
        public Vector2Int Cell { get; }
        public RectInt Rect { get; }
    }

    public readonly struct MissionConnection
    {
        public MissionConnection(int roomA, int roomB, RectInt strip)
        {
            RoomA = roomA;
            RoomB = roomB;
            Strip = strip;
        }

        public int RoomA { get; }
        public int RoomB { get; }
        /// <summary>The corridor floor between the two rooms (the gutter), as tiles.</summary>
        public RectInt Strip { get; }
    }

    /// <summary>
    /// The pure result of mission generation: integer tile data only. The grid is Width x Height tiles of 1 m; tile
    /// (x, y) is world (x, z) after ToWorld centres the grid on the origin.
    /// </summary>
    public sealed class MissionLayout
    {
        readonly bool[] floor;

        internal MissionLayout(int seed, int attempt, int width, int height, bool[] floorMask)
        {
            Seed = seed;
            Attempt = attempt;
            Width = width;
            Height = height;
            floor = floorMask;
            for (var i = 0; i < floor.Length; i++)
                if (floor[i])
                    FloorTileCount++;
        }

        public int Seed { get; }
        public int Attempt { get; }
        public int Width { get; }
        public int Height { get; }
        public int FloorTileCount { get; }

        public IReadOnlyList<MissionRoom> Rooms { get; internal set; } = Array.Empty<MissionRoom>();
        public IReadOnlyList<MissionConnection> Connections { get; internal set; } = Array.Empty<MissionConnection>();
        public IReadOnlyList<RectInt> FloorRects { get; internal set; } = Array.Empty<RectInt>();
        /// <summary>Walls first, then obstacles.</summary>
        public IReadOnlyList<MissionBox> Boxes { get; internal set; } = Array.Empty<MissionBox>();
        public int FriendlyRoom { get; internal set; } = -1;
        public IReadOnlyList<int> HostileRooms { get; internal set; } = Array.Empty<int>();
        public RectInt FriendlyRegion { get; internal set; }
        public IReadOnlyList<RectInt> HostileRegions { get; internal set; } = Array.Empty<RectInt>();
        public IReadOnlyList<Vector2Int> FriendlySpawns { get; internal set; } = Array.Empty<Vector2Int>();
        public IReadOnlyList<Vector2Int> HostileSpawns { get; internal set; } = Array.Empty<Vector2Int>();
        /// <summary>Tight tile rectangle around the floor and its wall ring.</summary>
        public RectInt UsedTiles { get; internal set; }
        public ulong Hash { get; internal set; }

        public bool IsFloor(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && floor[y * Width + x];

        public Vector3 ToWorld(float tileX, float tileY) => new Vector3(tileX - Width * 0.5f, 0f, tileY - Height * 0.5f);

        public Vector3 TileCenter(Vector2Int tile) => ToWorld(tile.x + 0.5f, tile.y + 0.5f);

        public Vector3 RectCenter(RectInt rect) => ToWorld(rect.xMin + rect.width * 0.5f, rect.yMin + rect.height * 0.5f);

        public Bounds WorldBounds
        {
            get
            {
                var min = ToWorld(UsedTiles.xMin, UsedTiles.yMin);
                var max = ToWorld(UsedTiles.xMax, UsedTiles.yMax);
                var bounds = new Bounds();
                bounds.SetMinMax(min, new Vector3(max.x, MissionConstants.WallHeight, max.z));
                return bounds;
            }
        }
    }
}
```

- [ ] **Step 4: Implement `RectCover.cs`**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Covers the true tiles of a mask with non-overlapping rectangles, deterministically: scan rows from y = 0 and
    /// columns from x = 0; each uncovered tile starts a rectangle that grows right as far as it can, then up while
    /// the whole row segment is still free.
    /// </summary>
    internal static class RectCover
    {
        public static List<RectInt> Decompose(bool[] mask, int width, int height)
        {
            var used = new bool[mask.Length];
            var result = new List<RectInt>();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    if (!mask[index] || used[index])
                        continue;
                    var run = 1;
                    while (x + run < width && mask[index + run] && !used[index + run])
                        run++;
                    var rows = 1;
                    while (y + rows < height && RowFree(mask, used, width, x, y + rows, run))
                        rows++;
                    for (var dy = 0; dy < rows; dy++)
                        for (var dx = 0; dx < run; dx++)
                            used[(y + dy) * width + x + dx] = true;
                    result.Add(new RectInt(x, y, run, rows));
                }
            }
            return result;
        }

        static bool RowFree(bool[] mask, bool[] used, int width, int x, int y, int run)
        {
            for (var dx = 0; dx < run; dx++)
            {
                var index = y * width + x + dx;
                if (!mask[index] || used[index])
                    return false;
            }
            return true;
        }
    }
}
```

- [ ] **Step 5: Implement `MissionGenerator.cs` (this task's part)**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public sealed class MissionGenerationResult
    {
        internal MissionGenerationResult(MissionSettings settings, MissionLayout layout, IReadOnlyList<string> failures)
        {
            Settings = settings;
            Layout = layout;
            Failures = failures;
        }

        public bool Succeeded => Layout != null;
        public MissionLayout Layout { get; }
        /// <summary>The validated settings the attempts ran with.</summary>
        public MissionSettings Settings { get; }
        /// <summary>One line per failed attempt.</summary>
        public IReadOnlyList<string> Failures { get; }

        public string Describe() => Succeeded
            ? $"Mission seed {Settings.seed} ok on attempt {Layout.Attempt}"
            : $"Mission generation failed after {Failures.Count} attempts. {Settings.Describe()}\n  " + string.Join("\n  ", Failures);
    }

    /// <summary>
    /// Pure mission layout generation: a connected set of rooms on a coarse grid, corridors across the gutters between
    /// neighbouring rooms, a wall ring derived from the floor, then obstacles and spawns. Every random choice comes
    /// from SeededRandom.ForAttempt(seed, attempt), so attempt n of seed s is always the same layout.
    /// </summary>
    public static class MissionGenerator
    {
        /// <summary>Tries attempts 1..maxAttempts and returns the first valid layout, or the reason of every failure.</summary>
        public static MissionGenerationResult Generate(MissionSettings requested)
        {
            if (requested == null)
                throw new ArgumentNullException(nameof(requested));
            var settings = requested.Validated();
            var failures = new List<string>();
            for (var attempt = 1; attempt <= settings.maxAttempts; attempt++)
            {
                if (TryAttempt(settings, attempt, out var layout, out var reason))
                    return new MissionGenerationResult(settings, layout, failures);
                failures.Add($"attempt {attempt}: {reason}");
            }
            return new MissionGenerationResult(settings, null, failures);
        }

        /// <summary>One attempt. `settings` must already be validated. On failure `reason` says why and `layout` is null.</summary>
        public static bool TryAttempt(MissionSettings settings, int attempt, out MissionLayout layout, out string reason)
        {
            layout = null;
            reason = null;
            var rng = SeededRandom.ForAttempt(settings.seed, attempt);
            var width = settings.gridColumns * settings.cellSize;
            var height = settings.gridRows * settings.cellSize;

            var rooms = PlaceRooms(settings, rng);
            if (!TryConnect(settings, rng, rooms, out var connections, out reason))
                return false;

            var floor = new bool[width * height];
            foreach (var room in rooms)
                Fill(floor, width, room.Rect);
            foreach (var connection in connections)
                Fill(floor, width, connection.Strip);

            var boxes = new List<MissionBox>();
            AddWalls(floor, width, height, boxes);

            layout = new MissionLayout(settings.seed, attempt, width, height, floor)
            {
                Rooms = rooms,
                Connections = connections,
                FloorRects = RectCover.Decompose(floor, width, height),
                Boxes = boxes,
                UsedTiles = UsedRect(floor, width, height),
            };
            return true;
        }

        static List<MissionRoom> PlaceRooms(MissionSettings s, SeededRandom rng)
        {
            var cols = s.gridColumns;
            var cellCount = cols * s.gridRows;
            var chosen = new List<int> { rng.NextInt(cellCount) };
            var isChosen = new bool[cellCount];
            isChosen[chosen[0]] = true;
            var frontier = new List<int>();
            while (chosen.Count < s.roomCount)
            {
                frontier.Clear();
                for (var cell = 0; cell < cellCount; cell++)
                    if (!isChosen[cell] && TouchesChosen(cell, cols, s.gridRows, isChosen))
                        frontier.Add(cell);
                var next = frontier[rng.NextInt(frontier.Count)];
                isChosen[next] = true;
                chosen.Add(next);
            }

            var rooms = new List<MissionRoom>();
            var interior = s.cellSize - 2 * MissionConstants.RoomInset;
            var largest = Math.Min(MissionConstants.RoomMax, interior);
            for (var i = 0; i < chosen.Count; i++)
            {
                var cx = chosen[i] % cols;
                var cy = chosen[i] / cols;
                var w = rng.NextInt(MissionConstants.RoomMin, largest + 1);
                var h = rng.NextInt(MissionConstants.RoomMin, largest + 1);
                var ox = rng.NextInt(0, interior - w + 1);
                var oy = rng.NextInt(0, interior - h + 1);
                var rect = new RectInt(cx * s.cellSize + MissionConstants.RoomInset + ox,
                    cy * s.cellSize + MissionConstants.RoomInset + oy, w, h);
                rooms.Add(new MissionRoom(i, new Vector2Int(cx, cy), rect));
            }
            return rooms;
        }

        static bool TouchesChosen(int cell, int cols, int rows, bool[] isChosen)
        {
            var x = cell % cols;
            var y = cell / cols;
            return (x > 0 && isChosen[cell - 1]) || (x < cols - 1 && isChosen[cell + 1])
                || (y > 0 && isChosen[cell - cols]) || (y < rows - 1 && isChosen[cell + cols]);
        }

        static bool TryConnect(MissionSettings s, SeededRandom rng, List<MissionRoom> rooms,
            out List<MissionConnection> connections, out string reason)
        {
            reason = null;
            connections = new List<MissionConnection>();
            var legal = new List<MissionConnection>();
            for (var i = 0; i < rooms.Count; i++)
            {
                for (var j = i + 1; j < rooms.Count; j++)
                {
                    if (TryStrip(s, rng, rooms[i], rooms[j], out var strip))
                        legal.Add(new MissionConnection(i, j, strip));
                }
            }
            rng.Shuffle(legal);

            var parent = new int[rooms.Count];
            for (var i = 0; i < parent.Length; i++)
                parent[i] = i;
            var extra = new List<MissionConnection>();
            foreach (var candidate in legal)
            {
                var a = Find(parent, candidate.RoomA);
                var b = Find(parent, candidate.RoomB);
                if (a != b)
                {
                    parent[a] = b;
                    connections.Add(candidate);
                }
                else
                {
                    extra.Add(candidate);
                }
            }
            if (connections.Count != rooms.Count - 1)
            {
                reason = "the rooms cannot all be connected (corridors need an overlap of at least the corridor width)";
                return false;
            }
            for (var i = 0; i < extra.Count && i < s.extraLoops; i++)
                connections.Add(extra[i]);
            return true;
        }

        // A corridor between two rooms in neighbouring cells, across the gutter, inside the overlap of their extents.
        static bool TryStrip(MissionSettings s, SeededRandom rng, MissionRoom a, MissionRoom b, out RectInt strip)
        {
            strip = default;
            var dx = b.Cell.x - a.Cell.x;
            var dy = b.Cell.y - a.Cell.y;
            if (Math.Abs(dx) + Math.Abs(dy) != 1)
                return false;
            var width = s.corridorWidth;
            // Order so that `first` is the room with the smaller cell coordinate along the shared axis.
            var first = dx + dy > 0 ? a.Rect : b.Rect;
            var second = dx + dy > 0 ? b.Rect : a.Rect;
            if (dx != 0)
            {
                var lo = Math.Max(first.yMin, second.yMin);
                var hi = Math.Min(first.yMax, second.yMax);
                if (hi - lo < width)
                    return false;
                var y0 = rng.NextInt(lo, hi - width + 1);
                strip = new RectInt(first.xMax, y0, second.xMin - first.xMax, width);
            }
            else
            {
                var lo = Math.Max(first.xMin, second.xMin);
                var hi = Math.Min(first.xMax, second.xMax);
                if (hi - lo < width)
                    return false;
                var x0 = rng.NextInt(lo, hi - width + 1);
                strip = new RectInt(x0, first.yMax, width, second.yMin - first.yMax);
            }
            return true;
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        static void Fill(bool[] mask, int width, RectInt rect)
        {
            for (var y = rect.yMin; y < rect.yMax; y++)
                for (var x = rect.xMin; x < rect.xMax; x++)
                    mask[y * width + x] = true;
        }

        // Wall tiles are the void tiles next to a floor tile, diagonals included so corners close.
        static void AddWalls(bool[] floor, int width, int height, List<MissionBox> boxes)
        {
            var wall = new bool[floor.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (floor[y * width + x])
                        continue;
                    for (var dy = -1; dy <= 1 && !wall[y * width + x]; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = x + dx;
                            var ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < width && ny < height && floor[ny * width + nx])
                            {
                                wall[y * width + x] = true;
                                break;
                            }
                        }
                    }
                }
            }
            var n = 0;
            foreach (var rect in RectCover.Decompose(wall, width, height))
                boxes.Add(new MissionBox($"Wall_{++n}", MissionBoxKind.Wall, rect, MissionConstants.WallHeight));
        }

        static RectInt UsedRect(bool[] floor, int width, int height)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!floor[y * width + x])
                        continue;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
            return new RectInt(minX - 1, minY - 1, maxX - minX + 3, maxY - minY + 3);
        }
    }
}
```

The layout `Hash` is still 0 here; Task 3 computes it. Until then `SameSeedTwice_GivesTheSameLayout` and `DifferentSeeds_GiveDifferentLayouts` cannot pass: **temporarily** compute a hash in this task so the two tests are meaningful: add the following to `MissionGenerator` and set `Hash = ComputeHash(layout)` right after the object initializer in `TryAttempt` (Task 3 extends it with spawns and keeps the same method):

```csharp
        internal static ulong ComputeHash(MissionLayout layout)
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
            Add(layout.Width);
            Add(layout.Height);
            for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                    Add(layout.IsFloor(x, y) ? 1 : 0);
            foreach (var room in layout.Rooms)
            {
                Add(room.Rect.x); Add(room.Rect.y); Add(room.Rect.width); Add(room.Rect.height);
            }
            foreach (var c in layout.Connections)
            {
                Add(c.RoomA); Add(c.RoomB); Add(c.Strip.x); Add(c.Strip.y); Add(c.Strip.width); Add(c.Strip.height);
            }
            foreach (var box in layout.Boxes)
            {
                Add((int)box.Kind); Add(box.Footprint.x); Add(box.Footprint.y); Add(box.Footprint.width); Add(box.Footprint.height);
                Add(Mathf.RoundToInt(box.Height * 10f));
            }
            foreach (var t in layout.FriendlySpawns) { Add(t.x); Add(t.y); }
            Add(-1);
            foreach (var t in layout.HostileSpawns) { Add(t.x); Add(t.y); }
            return h;
        }
```

- [ ] **Step 6: Run to verify they pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionGeneratorTests"`. Expected: `EXIT=0`, 6 passed. If `Floor_IsOneConnectedRegion...` fails on the exact-cover assertion, check `RectCover` marks `used` for the whole rectangle. If `Connections_...` fails on `5..6`, a seed produced a tree needing fewer/more legal pairs: investigate (do not loosen) — a failed attempt falls through to the next attempt, so the final layout always has `roomCount - 1 + extraLoops` capped by available pairs.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Mission Assets/_Project/Tests/EditMode/MissionGeneratorTests.cs
git commit -m "Generate mission rooms, corridors, floor and walls from a seed

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Obstacles, spawns, connectivity, bounded retry, golden hashes

**Files:**
- Modify: `Assets/_Project/Scripts/Mission/MissionGenerator.cs`
- Test: `Assets/_Project/Tests/EditMode/MissionLayoutInvariantTests.cs`; extend `MissionGeneratorTests.cs`

**Interfaces:**
- Consumes: Task 2 types.
- Produces: finished `MissionGenerator.TryAttempt` (obstacles, spawns, hash); `internal static bool MissionGenerator.IsConnected(bool[] floor, bool[] blocked, int width, int height, out string reason)`.

- [ ] **Step 1: Write the failing tests**

`MissionLayoutInvariantTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionLayoutInvariantTests
    {
        const int Seeds = 60;

        static IEnumerable<MissionLayout> Layouts()
        {
            for (var seed = 1; seed <= Seeds; seed++)
            {
                var result = MissionGenerator.Generate(new MissionSettings { seed = seed });
                Assert.That(result.Succeeded, Is.True, result.Describe());
                yield return result.Layout;
            }
        }

        static IEnumerable<Vector2Int> Tiles(RectInt r)
        {
            for (var x = r.xMin; x < r.xMax; x++)
                for (var y = r.yMin; y < r.yMax; y++)
                    yield return new Vector2Int(x, y);
        }

        static IEnumerable<MissionBox> Obstacles(MissionLayout layout) => layout.Boxes.Where(b => b.Kind != MissionBoxKind.Wall);

        [Test]
        public void EveryDefaultSeed_Succeeds_WithinTheAttemptBudget() =>
            Assert.That(Layouts().All(l => l.Attempt >= 1 && l.Attempt <= 20), Is.True);

        [Test]
        public void Obstacles_StandOnFloorInsideRooms_NeverOnCorridors_AndKeepTheirClearance()
        {
            foreach (var layout in Layouts())
            {
                var obstacles = Obstacles(layout).ToList();
                foreach (var box in obstacles)
                {
                    Assert.That(Tiles(box.Footprint).All(t => layout.IsFloor(t.x, t.y)), Is.True, $"{box.Name} floats");
                    Assert.That(layout.Rooms.Any(r => Contains(r.Rect, box.Footprint, MissionConstants.Clearance)), Is.True,
                        $"{box.Name} is closer than {MissionConstants.Clearance} tiles to a wall or door");
                    foreach (var c in layout.Connections)
                        Assert.That(box.Footprint.Overlaps(c.Strip), Is.False);
                }
                for (var i = 0; i < obstacles.Count; i++)
                    for (var j = i + 1; j < obstacles.Count; j++)
                        Assert.That(Inflate(obstacles[i].Footprint, MissionConstants.Clearance).Overlaps(obstacles[j].Footprint), Is.False,
                            $"{obstacles[i].Name} and {obstacles[j].Name} are too close");
            }
        }

        [Test]
        public void ObstacleHeightsAndSizes_MatchTheirKind()
        {
            foreach (var layout in Layouts())
                foreach (var b in Obstacles(layout))
                {
                    var f = b.Footprint;
                    var tall = b.Kind == MissionBoxKind.Baffle || b.Kind == MissionBoxKind.Pillar;
                    Assert.That(b.Height, Is.EqualTo(tall ? MissionConstants.WallHeight : MissionConstants.LowHeight), b.Name);
                    if (b.Kind == MissionBoxKind.Baffle)
                    {
                        Assert.That(Mathf.Min(f.width, f.height), Is.EqualTo(1), b.Name);
                        Assert.That(Mathf.Max(f.width, f.height), Is.InRange(3, 5), b.Name);
                    }
                    if (b.Kind == MissionBoxKind.Pillar || b.Kind == MissionBoxKind.Crate)
                        Assert.That((f.width, f.height), Is.EqualTo((1, 1)), b.Name);
                    if (b.Kind == MissionBoxKind.LowWall)
                        Assert.That((Mathf.Min(f.width, f.height), Mathf.Max(f.width, f.height)), Is.EqualTo((1, 3)), b.Name);
                }
        }

        [Test]
        public void TheWalkableFloor_StaysConnected_AroundEveryObstacle()
        {
            foreach (var layout in Layouts())
            {
                var blocked = Obstacles(layout).SelectMany(b => Tiles(b.Footprint)).ToHashSet();
                var open = new HashSet<Vector2Int>();
                for (var x = 0; x < layout.Width; x++)
                    for (var y = 0; y < layout.Height; y++)
                        if (layout.IsFloor(x, y) && !blocked.Contains(new Vector2Int(x, y)))
                            open.Add(new Vector2Int(x, y));
                var seen = new HashSet<Vector2Int> { layout.FriendlySpawns[0] };
                var queue = new Queue<Vector2Int>(seen);
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    foreach (var d in new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right })
                        if (open.Contains(t + d) && seen.Add(t + d))
                            queue.Enqueue(t + d);
                }
                Assert.That(seen, Has.Count.EqualTo(open.Count), $"seed {layout.Seed}: isolated floor");
                Assert.That(layout.HostileSpawns.All(seen.Contains), Is.True, "every hostile spawn is reachable");
            }
        }

        [Test]
        public void Spawns_AreOnFreeFloor_Apart_AndTheTeamsStartFarApart()
        {
            foreach (var layout in Layouts())
            {
                Assert.That(layout.FriendlySpawns, Has.Count.EqualTo(3));
                Assert.That(layout.HostileSpawns, Has.Count.EqualTo(3));
                var all = layout.FriendlySpawns.Concat(layout.HostileSpawns).ToList();
                var obstacleTiles = Obstacles(layout).SelectMany(b => Tiles(Inflate(b.Footprint, 1))).ToHashSet();
                foreach (var t in all)
                {
                    Assert.That(layout.IsFloor(t.x, t.y), Is.True);
                    Assert.That(obstacleTiles.Contains(t), Is.False, $"spawn {t} is next to an obstacle");
                }
                for (var i = 0; i < all.Count; i++)
                    for (var j = i + 1; j < all.Count; j++)
                        Assert.That(Vector2.Distance(all[i], all[j]), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing));
                foreach (var f in layout.FriendlySpawns)
                    foreach (var h in layout.HostileSpawns)
                        Assert.That(Vector2.Distance(f, h), Is.GreaterThanOrEqualTo(16f), $"seed {layout.Seed}");
                Assert.That(layout.FriendlySpawns.All(t => layout.FriendlyRegion.Contains(t)), Is.True);
                Assert.That(layout.HostileSpawns.All(t => layout.HostileRegions.Any(r => r.Contains(t))), Is.True);
            }
        }

        [Test]
        public void FriendlyAndHostileRooms_AreDifferentRooms_AtTheGreatestGraphDistance()
        {
            foreach (var layout in Layouts())
            {
                Assert.That(layout.HostileRooms, Does.Not.Contain(layout.FriendlyRoom));
                Assert.That(layout.HostileRooms.Count, Is.InRange(1, 2));
            }
        }

        [Test]
        public void Bounds_ContainEveryBoxAndSpawn()
        {
            foreach (var layout in Layouts())
            {
                var bounds = layout.WorldBounds;
                foreach (var b in layout.Boxes)
                {
                    var min = layout.ToWorld(b.Footprint.xMin, b.Footprint.yMin);
                    var max = layout.ToWorld(b.Footprint.xMax, b.Footprint.yMax);
                    Assert.That(bounds.Contains(new Vector3(min.x + 0.01f, 1f, min.z + 0.01f)), Is.True, b.Name);
                    Assert.That(bounds.Contains(new Vector3(max.x - 0.01f, 1f, max.z - 0.01f)), Is.True, b.Name);
                }
                foreach (var t in layout.FriendlySpawns.Concat(layout.HostileSpawns))
                    Assert.That(bounds.Contains(layout.TileCenter(t) + Vector3.up), Is.True);
            }
        }

        [Test]
        public void MostMissionsHaveTallObstaclesAndLowCover()
        {
            var withBaffle = Layouts().Count(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.Baffle));
            var withLow = Layouts().Count(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.LowWall || b.Kind == MissionBoxKind.Crate));
            Assert.That(withBaffle, Is.GreaterThanOrEqualTo(Seeds * 9 / 10));
            Assert.That(withLow, Is.GreaterThanOrEqualTo(Seeds * 9 / 10));
        }

        static RectInt Inflate(RectInt r, int by) => new RectInt(r.x - by, r.y - by, r.width + 2 * by, r.height + 2 * by);

        static bool Contains(RectInt outer, RectInt footprint, int margin) =>
            footprint.xMin - margin >= outer.xMin && footprint.xMax + margin <= outer.xMax
            && footprint.yMin - margin >= outer.yMin && footprint.yMax + margin <= outer.yMax;
    }
}
```

Append to `MissionGeneratorTests.cs`:

```csharp
        [Test]
        public void Attempt_IsAPureFunctionOfSeedAndAttemptNumber()
        {
            var settings = new MissionSettings { seed = 99 }.Validated();
            Assert.That(MissionGenerator.TryAttempt(settings, 3, out var a, out var ra), Is.EqualTo(MissionGenerator.TryAttempt(settings, 3, out var b, out var rb)));
            Assert.That(ra, Is.EqualTo(rb));
            if (a != null)
                Assert.That(a.Hash, Is.EqualTo(b.Hash));
            // The final layout does not depend on how often the generator was called before.
            MissionGenerator.Generate(new MissionSettings { seed = 5 });
            Assert.That(Make(99).Hash, Is.EqualTo(Make(99).Hash));
        }

        [Test]
        public void Generate_DoesNotTouchUnityEngineRandom()
        {
            UnityEngine.Random.InitState(2024);
            var expected = new[] { UnityEngine.Random.value, UnityEngine.Random.value };
            UnityEngine.Random.InitState(2024);
            MissionGenerator.Generate(new MissionSettings { seed = 321 });
            var actual = new[] { UnityEngine.Random.value, UnityEngine.Random.value };
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void Generate_FailsCleanlyWhenTheTeamsCannotBeSeparated()
        {
            var result = MissionGenerator.Generate(new MissionSettings { seed = 1, minTeamSeparation = 60f, maxAttempts = 5 });
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failures, Has.Count.EqualTo(5), "exactly maxAttempts attempts, then it stops");
            Assert.That(result.Describe(), Does.Contain("seed=1").And.Contain("attempts=5").And.Contain("attempt 5:"));
        }

        [Test]
        public void Generate_ClampsDegenerateSettings_InsteadOfThrowingOrHanging()
        {
            var result = MissionGenerator.Generate(new MissionSettings
            {
                gridColumns = 1, gridRows = 1, cellSize = 1, roomCount = 99, corridorWidth = 0, maxAttempts = 1000,
                hostileCount = 0, friendlyCount = 0,
            });
            Assert.That(result.Settings.maxAttempts, Is.EqualTo(100));
            Assert.That(result.Settings.roomCount, Is.EqualTo(4));
            Assert.That(result.Succeeded || result.Failures.Count == 100, Is.True);
        }

        // Golden layouts: the hashes pin determinism across code changes. Change the generator on purpose and these
        // move: update them in the same commit and say so in the message.
        static readonly Dictionary<int, ulong> Golden = new Dictionary<int, ulong>
        {
            { 12345, 0UL },
            { 1, 0UL },
            { 2, 0UL },
        };

        [TestCase(12345)]
        [TestCase(1)]
        [TestCase(2)]
        public void GoldenSeeds_KeepTheirLayoutHash(int seed) =>
            Assert.That(Make(seed).Hash, Is.EqualTo(Golden[seed]), $"seed {seed}");
```

- [ ] **Step 2: Run to verify they fail**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionLayoutInvariantTests"` and `... "Blackglass.Tests.MissionGeneratorTests"`. Expected: failures (`FriendlySpawns[0]` out of range, no obstacles, failure-case tests failing), `EXIT=2`.

- [ ] **Step 3: Implement the rest of `TryAttempt`**

Replace the body of `TryAttempt` after the `Fill` loops with the version below, and add the new members (keep `PlaceRooms`, `TryConnect`, `TryStrip`, `Find`, `Fill`, `AddWalls`, `UsedRect`, `ComputeHash` as they are).

```csharp
            var obstacles = new ObstaclePlacer(floor, width, height);
            PlaceTallObstacles(settings, rng, rooms, obstacles);
            PlaceLowCover(settings, rng, rooms, floor, obstacles);
            if (!IsConnected(floor, obstacles.Blocked, width, height, out reason))
                return false;

            if (!TryPlaceSpawns(settings, rng, rooms, connections, obstacles.Boxes, width, height, out var spawns, out reason))
                return false;

            var boxes = new List<MissionBox>();
            AddWalls(floor, width, height, boxes);
            boxes.AddRange(obstacles.Boxes);

            layout = new MissionLayout(settings.seed, attempt, width, height, floor)
            {
                Rooms = rooms,
                Connections = connections,
                FloorRects = RectCover.Decompose(floor, width, height),
                Boxes = boxes,
                FriendlyRoom = spawns.FriendlyRoom,
                HostileRooms = spawns.HostileRooms,
                FriendlyRegion = spawns.FriendlyRegion,
                HostileRegions = spawns.HostileRegions,
                FriendlySpawns = spawns.FriendlySpawns,
                HostileSpawns = spawns.HostileSpawns,
                UsedTiles = UsedRect(floor, width, height),
            };
            layout.Hash = ComputeHash(layout);
            return true;
        }

        const int PlacementTries = 8;

        static void PlaceTallObstacles(MissionSettings s, SeededRandom rng, List<MissionRoom> rooms, ObstaclePlacer placer)
        {
            foreach (var room in rooms)
            {
                for (var b = 0; b < s.bafflesPerRoom; b++)
                {
                    for (var attempt = 0; attempt < PlacementTries; attempt++)
                    {
                        var horizontal = rng.Chance(0.5f);
                        var length = rng.NextInt(3, 6);
                        if (placer.TryPlaceRandom(rng, room, horizontal ? length : 1, horizontal ? 1 : length,
                                MissionBoxKind.Baffle, MissionConstants.WallHeight))
                            break;
                    }
                }
                if (rng.Chance(0.5f))
                {
                    for (var attempt = 0; attempt < PlacementTries; attempt++)
                    {
                        if (placer.TryPlaceRandom(rng, room, 1, 1, MissionBoxKind.Pillar, MissionConstants.WallHeight))
                            break;
                    }
                }
            }
        }

        static void PlaceLowCover(MissionSettings s, SeededRandom rng, List<MissionRoom> rooms, bool[] floor, ObstaclePlacer placer)
        {
            var floorTiles = 0;
            foreach (var tile in floor)
                if (tile)
                    floorTiles++;
            var target = (int)(s.lowCoverDensity * floorTiles / 100f + 0.5f);
            for (var i = 0; i < target; i++)
            {
                var room = rooms[rng.NextInt(rooms.Count)];
                for (var attempt = 0; attempt < PlacementTries; attempt++)
                {
                    var wall = rng.Chance(0.6f);
                    var horizontal = rng.Chance(0.5f);
                    var w = wall ? (horizontal ? 3 : 1) : 1;
                    var h = wall ? (horizontal ? 1 : 3) : 1;
                    if (placer.TryPlaceRandom(rng, room, w, h, wall ? MissionBoxKind.LowWall : MissionBoxKind.Crate, MissionConstants.LowHeight))
                        break;
                }
            }
        }

        /// <summary>
        /// True when every floor tile that no obstacle covers is reachable (4-connected) from the first such tile.
        /// </summary>
        internal static bool IsConnected(bool[] floor, bool[] blocked, int width, int height, out string reason)
        {
            reason = null;
            var start = -1;
            var open = 0;
            for (var i = 0; i < floor.Length; i++)
            {
                if (!floor[i] || blocked[i])
                    continue;
                open++;
                if (start < 0)
                    start = i;
            }
            if (start < 0)
            {
                reason = "no walkable floor";
                return false;
            }
            var seen = new bool[floor.Length];
            var queue = new Queue<int>();
            queue.Enqueue(start);
            seen[start] = true;
            var reached = 1;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                for (var d = 0; d < 4; d++)
                {
                    var nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    var ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        continue;
                    var next = ny * width + nx;
                    if (!floor[next] || blocked[next] || seen[next])
                        continue;
                    seen[next] = true;
                    reached++;
                    queue.Enqueue(next);
                }
            }
            if (reached != open)
            {
                reason = $"the walkable floor is split: {reached} of {open} tiles reachable";
                return false;
            }
            return true;
        }

        sealed class ObstaclePlacer
        {
            readonly bool[] floor;
            readonly int width;
            readonly int height;
            readonly Dictionary<MissionBoxKind, int> counts = new Dictionary<MissionBoxKind, int>();

            public ObstaclePlacer(bool[] floorMask, int gridWidth, int gridHeight)
            {
                floor = floorMask;
                width = gridWidth;
                height = gridHeight;
                Blocked = new bool[floorMask.Length];
            }

            public bool[] Blocked { get; }
            public List<MissionBox> Boxes { get; } = new List<MissionBox>();

            public bool TryPlaceRandom(SeededRandom rng, MissionRoom room, int w, int h, MissionBoxKind kind, float boxHeight)
            {
                var c = MissionConstants.Clearance;
                var minX = room.Rect.xMin + c;
                var maxX = room.Rect.xMax - c - w;
                var minY = room.Rect.yMin + c;
                var maxY = room.Rect.yMax - c - h;
                if (maxX < minX || maxY < minY)
                    return false;
                var x = rng.NextInt(minX, maxX + 1);
                var y = rng.NextInt(minY, maxY + 1);
                return TryPlace(new RectInt(x, y, w, h), kind, boxHeight);
            }

            // Callers guarantee the footprint sits at least Clearance tiles inside a room (TryPlaceRandom), which also
            // keeps it off every door; the rest is the gap to earlier obstacles and the connectivity check.
            bool TryPlace(RectInt rect, MissionBoxKind kind, float boxHeight)
            {
                var c = MissionConstants.Clearance;
                var inflated = new RectInt(rect.x - c, rect.y - c, rect.width + 2 * c, rect.height + 2 * c);
                foreach (var existing in Boxes)
                    if (inflated.Overlaps(existing.Footprint))
                        return false;
                Mark(rect, true);
                if (!IsConnected(floor, Blocked, width, height, out _))
                {
                    Mark(rect, false);
                    return false;
                }
                counts.TryGetValue(kind, out var n);
                counts[kind] = ++n;
                Boxes.Add(new MissionBox($"{kind}_{n}", kind, rect, boxHeight));
                return true;
            }

            void Mark(RectInt rect, bool value)
            {
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        Blocked[y * width + x] = value;
            }
        }

        sealed class SpawnPlan
        {
            public int FriendlyRoom;
            public List<int> HostileRooms = new List<int>();
            public RectInt FriendlyRegion;
            public List<RectInt> HostileRegions = new List<RectInt>();
            public List<Vector2Int> FriendlySpawns = new List<Vector2Int>();
            public List<Vector2Int> HostileSpawns = new List<Vector2Int>();
        }

        static bool TryPlaceSpawns(MissionSettings s, SeededRandom rng, List<MissionRoom> rooms, List<MissionConnection> connections,
            List<MissionBox> obstacles, int width, int height, out SpawnPlan plan, out string reason)
        {
            reason = null;
            plan = new SpawnPlan();
            var n = rooms.Count;

            // Room graph distances (breadth-first from every room).
            var adjacency = new List<int>[n];
            for (var i = 0; i < n; i++)
                adjacency[i] = new List<int>();
            foreach (var c in connections)
            {
                adjacency[c.RoomA].Add(c.RoomB);
                adjacency[c.RoomB].Add(c.RoomA);
            }
            var distance = new int[n][];
            for (var from = 0; from < n; from++)
            {
                var d = new int[n];
                for (var i = 0; i < n; i++)
                    d[i] = -1;
                d[from] = 0;
                var queue = new Queue<int>();
                queue.Enqueue(from);
                while (queue.Count > 0)
                {
                    var room = queue.Dequeue();
                    foreach (var next in adjacency[room])
                    {
                        if (d[next] >= 0)
                            continue;
                        d[next] = d[room] + 1;
                        queue.Enqueue(next);
                    }
                }
                distance[from] = d;
            }

            var best = -1;
            var pairs = new List<(int a, int b)>();
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    if (distance[i][j] > best)
                    {
                        best = distance[i][j];
                        pairs.Clear();
                    }
                    if (distance[i][j] == best)
                        pairs.Add((i, j));
                }
            }
            var pair = pairs[rng.NextInt(pairs.Count)];
            var friendlyRoom = pair.a;
            var mainHostile = pair.b;
            if (rng.Chance(0.5f))
                (friendlyRoom, mainHostile) = (mainHostile, friendlyRoom);

            var secondHostile = -1;
            var secondDistance = -1;
            for (var r = 0; r < n; r++)
            {
                if (r == friendlyRoom || r == mainHostile || distance[friendlyRoom][r] <= secondDistance)
                    continue;
                secondHostile = r;
                secondDistance = distance[friendlyRoom][r];
            }

            plan.FriendlyRoom = friendlyRoom;
            plan.FriendlyRegion = Shrink(rooms[friendlyRoom].Rect, 1);
            plan.HostileRooms.Add(mainHostile);
            plan.HostileRegions.Add(Shrink(rooms[mainHostile].Rect, 1));
            if (secondHostile >= 0)
            {
                plan.HostileRooms.Add(secondHostile);
                plan.HostileRegions.Add(Shrink(rooms[secondHostile].Rect, 1));
            }

            // Tiles within one tile of an obstacle are never spawn points.
            var avoid = new bool[width * height];
            foreach (var box in obstacles)
            {
                var f = box.Footprint;
                for (var y = f.yMin - 1; y < f.yMax + 1; y++)
                    for (var x = f.xMin - 1; x < f.xMax + 1; x++)
                        avoid[y * width + x] = true;
            }

            var placed = new List<Vector2Int>();
            Pick(Candidates(plan.FriendlyRegion, avoid, width), rng, s.friendlyCount, plan.FriendlySpawns, placed, null, 0f);
            if (plan.FriendlySpawns.Count < s.friendlyCount)
            {
                reason = $"the friendly room fits only {plan.FriendlySpawns.Count} of {s.friendlyCount} spawns";
                return false;
            }
            foreach (var region in plan.HostileRegions)
            {
                if (plan.HostileSpawns.Count >= s.hostileCount)
                    break;
                Pick(Candidates(region, avoid, width), rng, s.hostileCount, plan.HostileSpawns, placed, plan.FriendlySpawns, s.minTeamSeparation);
            }
            if (plan.HostileSpawns.Count < s.hostileCount)
            {
                reason = $"only {plan.HostileSpawns.Count} of {s.hostileCount} hostile spawns fit at {s.minTeamSeparation:0.#} m from the friendlies";
                return false;
            }
            return true;
        }

        static RectInt Shrink(RectInt rect, int by) => new RectInt(rect.x + by, rect.y + by, rect.width - 2 * by, rect.height - 2 * by);

        static List<Vector2Int> Candidates(RectInt region, bool[] avoid, int width)
        {
            var list = new List<Vector2Int>();
            for (var y = region.yMin; y < region.yMax; y++)
                for (var x = region.xMin; x < region.xMax; x++)
                    if (!avoid[y * width + x])
                        list.Add(new Vector2Int(x, y));
            return list;
        }

        static void Pick(List<Vector2Int> candidates, SeededRandom rng, int needed, List<Vector2Int> into, List<Vector2Int> placed,
            List<Vector2Int> otherTeam, float teamSeparation)
        {
            rng.Shuffle(candidates);
            foreach (var tile in candidates)
            {
                if (into.Count >= needed)
                    return;
                if (TooClose(tile, placed, MissionConstants.SpawnSpacing) || (otherTeam != null && TooClose(tile, otherTeam, teamSeparation)))
                    continue;
                into.Add(tile);
                placed.Add(tile);
            }
        }

        static bool TooClose(Vector2Int tile, List<Vector2Int> others, float distance)
        {
            foreach (var other in others)
                if (Vector2.Distance(tile, other) < distance)
                    return true;
            return false;
        }
```

(The old end of `TryAttempt` that built the layout and returned `true` is replaced by the code at the top of this step; keep one closing brace for the method as shown.)

- [ ] **Step 4: Run, then pin the golden hashes**

Run `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionLayoutInvariantTests"`, then `... "Blackglass.Tests.MissionGeneratorTests"`.
Expected: invariants pass; `GoldenSeeds_KeepTheirLayoutHash` fails with `Expected: 0 But was: <value>` for each of the three cases. Copy the three actual `But was` values into the `Golden` table (as `...UL` literals), rerun: `EXIT=0`.

If an invariant fails, fix the generator, not the test; the likely culprits and fixes:
- `EveryDefaultSeed_Succeeds` or `MostMissionsHave...` fails: print `result.Failures` for the failing seed; a recurring reason is a room too small for its spawn region (raise nothing: it falls to the next attempt, so only a >50% attempt failure rate is a bug worth fixing, by choosing rooms to prefer larger spawn rooms).
- `Spawns_...` separation fails: `TooClose` must be called against `otherTeam` for hostile picks (it is).

- [ ] **Step 5: Full EditMode run and commit**

Run: `Tools/run-tests.sh EditMode`. Expected: `EXIT=0`, total 548 passed (the table above; correct it if a count differs).

```bash
git add Assets/_Project/Scripts/Mission Assets/_Project/Tests/EditMode
git commit -m "Place obstacles and spawns, validate connectivity and bound the retries

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Small additions to existing classes (cover scope, camera bounds, wiring, asmdef)

**Files:**
- Modify: `Assets/_Project/Scripts/Blackglass.asmdef`, `Cover/CoverDiscovery.cs`, `CameraControl/TacticalCameraController.cs`, `AI/CompanionAI.cs`, `AI/EnemyAI.cs`, `Units/UnitCover.cs`
- Test: create `Assets/_Project/Tests/PlayMode/CoverDiscoveryScopedPlayModeTests.cs`; add three tests to `Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs`

**Interfaces:**
- Produces: `int CoverDiscovery.Discover(Transform root, Func<Vector3, bool> isReachable = null)`; `void TacticalCameraController.SetBounds(Rect flat)` (`Rect.x/y` are world x/z minimum, `width/height` the extent); `void TacticalCameraController.FocusOn(Vector3 point)` (snap, no glide); `void CompanionAI.Wire(ActiveCharacter active, Encounter encounter)`, `void EnemyAI.Wire(Encounter encounter, CoverRegistry registry)`, `void UnitCover.Wire(CoverRegistry registry)` (all `internal`, set references only).

- [ ] **Step 1: Write the failing tests**

`CoverDiscoveryScopedPlayModeTests.cs` (read `CoverDiscoveryPlayModeTests.cs` first; same setup style):

```csharp
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverDiscoveryScopedPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        BoxCollider outside;
        CoverRegistry registry;
        CoverDiscovery discovery;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // Obstacle 0 (under the environment root): a 4 m low wall. `outside` is another tagged box NOT under the root.
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            TestWorld.ObstacleCollider(environment, 0).gameObject.AddComponent<CoverSurface>();
            var loose = world.CreateObstacle(new Vector3(-12f, 0.45f, -8f), new Vector3(4f, 0.9f, 0.5f));
            loose.AddComponent<CoverSurface>();
            outside = loose.GetComponent<BoxCollider>();
            var systems = world.Track(new GameObject("Systems"));
            registry = systems.AddComponent<CoverRegistry>();
            discovery = systems.AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        [UnityTest]
        public IEnumerator Discover_WithARoot_UsesOnlyTheSurfacesUnderIt()
        {
            yield return null;
            var count = discovery.Discover(environment.transform);
            Assert.That(count, Is.GreaterThan(0));
            Assert.That(registry.Points.All(p => p.Obstacle != outside), Is.True);
        }

        [UnityTest]
        public IEnumerator Discover_WithoutArguments_StillFindsEverySurfaceInTheScene()
        {
            yield return null;
            discovery.Discover();
            Assert.That(registry.Points.Any(p => p.Obstacle == outside), Is.True);
        }

        [UnityTest]
        public IEnumerator Discover_WithAReachabilityFilter_DropsWhatItRejects()
        {
            yield return null;
            var all = discovery.Discover(environment.transform);
            var filtered = discovery.Discover(environment.transform, point => point.x > 0f);
            Assert.That(filtered, Is.LessThan(all));
            Assert.That(filtered, Is.GreaterThan(0));
            Assert.That(registry.Points.All(p => p.Position.x > 0f), Is.True);
        }
    }
}
```

Add inside the class in `TacticalCameraControllerTests.cs` (before the closing braces of the class; the file is inside `#if UNITY_EDITOR`):

```csharp
        [UnityTest]
        public IEnumerator SetBounds_ClampsPanningToTheGivenRectangle()
        {
            controller.SetBounds(new Rect(-2f, -2f, 4f, 4f));
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.6f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.EqualTo(2f).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator FocusOn_SnapsAtOnce_ClampedToTheBounds_AndKeepsTheHeight()
        {
            controller.SetBounds(new Rect(10f, 10f, 20f, 20f));
            var height = controller.transform.position.y;
            controller.FocusOn(new Vector3(100f, 0f, 15f));
            var snapped = controller.transform.position;
            yield return null;

            Assert.That(snapped.x, Is.EqualTo(30f).Within(0.001f));
            Assert.That(snapped.z, Is.EqualTo(15f).Within(0.001f));
            Assert.That(snapped.y, Is.EqualTo(height).Within(0.001f));
            Assert.That(controller.transform.position, Is.EqualTo(snapped), "no glide after a snap");
        }

        [UnityTest]
        public IEnumerator WithoutSetBounds_TheOriginalSquareStillApplies()
        {
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(2.8f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.EqualTo(25f).Within(0.01f));
        }
```

- [ ] **Step 2: Run to verify they fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverDiscoveryScopedPlayModeTests"`. Expected: `EXIT=1`, `CS1501` (no `Discover` overload taking arguments).

- [ ] **Step 3: Implement**

`Blackglass.asmdef`: add `"Unity.AI.Navigation"` to `references` (next to `"Unity.InputSystem"`).

`CoverDiscovery.cs`: replace `Discover()` and add the scoped overload and a shared private method (`using System;` at the top for `Func`):

```csharp
        /// <summary>Scans, generates and rebuilds the registry. Returns the number of locations now listed.</summary>
        public int Discover() => DiscoverFrom(FindObjectsByType<CoverSurface>(FindObjectsSortMode.None), null);

        /// <summary>
        /// Like Discover(), but only the CoverSurfaces under `root` count, and when `isReachable` is given a location
        /// is kept only if it also passes it (a mission uses "reachable from the friendly spawn").
        /// </summary>
        public int Discover(Transform root, Func<Vector3, bool> isReachable = null)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            return DiscoverFrom(root.GetComponentsInChildren<CoverSurface>(), isReachable);
        }

        int DiscoverFrom(IEnumerable<CoverSurface> found, Func<Vector3, bool> isReachable)
        {
            if (registry == null)
            {
                Debug.LogWarning($"{name}: CoverDiscovery has no registry wired, so nothing was discovered.", this);
                return 0;
            }
            var surfaces = new List<CoverSurface>(found);
            // A stable order makes names and tests repeatable whatever order Unity returns the objects in.
            surfaces.Sort(CompareSurfaces);
            var boxes = new List<CoverBox>(surfaces.Count);
            foreach (var surface in surfaces)
                boxes.Add(surface.ToBox());

            var tolerance = settings.walkableTolerance;
            var locations = CoverGenerator.Generate(boxes, settings,
                point => NavMesh.SamplePosition(point, out _, tolerance, NavMesh.AllAreas) && (isReachable == null || isReachable(point)));
            registry.Rebuild(locations);
            return locations.Count;
        }
```

`TacticalCameraController.cs`: add a private field `Rect? customBounds;`, the two public methods and the new clamp:

```csharp
        /// <summary>Limits panning and focusing to this rectangle (x/y = world x/z minimum) instead of the square of `boundsHalfSize`.</summary>
        public void SetBounds(Rect flat) => customBounds = flat;

        /// <summary>Puts the camera over a ground point at once (clamped to the bounds), with no glide.</summary>
        public void FocusOn(Vector3 point)
        {
            point.y = transform.position.y;
            transform.position = ClampToBounds(point);
            lastSeenUnit = CurrentUnit;
            isFocusing = false;
            ApplyCameraPose();
        }

        Vector3 ClampToBounds(Vector3 position)
        {
            if (customBounds.HasValue)
            {
                var bounds = customBounds.Value;
                position.x = Mathf.Clamp(position.x, bounds.xMin, bounds.xMax);
                position.z = Mathf.Clamp(position.z, bounds.yMin, bounds.yMax);
                return position;
            }
            position.x = Mathf.Clamp(position.x, -boundsHalfSize, boundsHalfSize);
            position.z = Mathf.Clamp(position.z, -boundsHalfSize, boundsHalfSize);
            return position;
        }
```
(Replace the existing `ClampToBounds`; keep everything else.)

Wiring methods, each next to its class's `Initialize`:

```csharp
// CompanionAI
        internal void Wire(ActiveCharacter active, Encounter encounterToAssist)
        {
            activeCharacter = active;
            encounter = encounterToAssist;
        }

// EnemyAI
        internal void Wire(Encounter encounterToFight, CoverRegistry registry)
        {
            encounter = encounterToFight;
            coverRegistry = registry;
        }

// UnitCover
        internal void Wire(CoverRegistry coverRegistry) => registry = coverRegistry;
```

- [ ] **Step 4: Run to verify they pass, plus both full suites**

Run `Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverDiscoveryScopedPlayModeTests"` (3 pass), `... "Blackglass.Tests.TacticalCameraControllerTests"` (all pass), then `Tools/run-tests.sh EditMode` and `Tools/run-tests.sh PlayMode` (the table's task-4 row; every existing test, `Prototype` scene tests included, still passes).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests/PlayMode
git commit -m "Scope cover discovery to a root, add camera bounds and focus, add unit wiring methods

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: MissionBuilder and MissionNavigation

**Files:**
- Create: `Scripts/Mission/MissionBuilder.cs`, `Scripts/Mission/MissionNavigation.cs`
- Test: `Assets/_Project/Tests/PlayMode/MissionBuilderPlayModeTests.cs`

**Interfaces:**
- Consumes: Tasks 2-4 (`MissionLayout`, `CoverDiscovery.Discover(root, reachable)`).
- Produces:
  - `GeneratedMission { GameObject Root; Transform Geometry; Transform Actors; NavMeshSurface Surface; MissionLayout Layout; const string RootName = "GeneratedMissionRoot"; }`
  - `static GeneratedMission MissionBuilder.Build(MissionLayout layout, Material groundMaterial, Material obstacleMaterial)` (materials may be null).
  - `readonly struct MissionNavigationReport { float NavArea; float FloorArea; int PathsChecked; }`
  - `static bool MissionNavigation.Validate(MissionLayout layout, out string reason, out MissionNavigationReport report)`
  - `static Func<Vector3, bool> MissionNavigation.ReachableFrom(Vector3 origin)` (a complete NavMesh path from `origin` to a walkable point within 0.5 m of the argument).
  - `static float MissionNavigation.NavMeshArea()`.

- [ ] **Step 1: Write the failing tests** (`MissionBuilderPlayModeTests.cs`, PlayMode, no `InputTestFixture` needed)

```csharp
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Unity.AI.Navigation;

namespace Blackglass.Tests
{
    public class MissionBuilderPlayModeTests
    {
        TestWorld world;
        GeneratedMission mission;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            DestroyMission();
            world.Dispose();
        }

        void DestroyMission()
        {
            if (mission != null && mission.Root != null)
                Object.DestroyImmediate(mission.Root);   // immediate: the surface removes its NavMesh data on disable
            mission = null;
        }

        MissionLayout Layout(int seed)
        {
            var result = MissionGenerator.Generate(new MissionSettings { seed = seed });
            Assert.That(result.Succeeded, Is.True, result.Describe());
            return result.Layout;
        }

        MissionLayout LayoutWith(System.Func<MissionLayout, bool> predicate)
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var layout = Layout(seed);
                if (predicate(layout))
                    return layout;
            }
            Assert.Fail("no seed in 1..60 gives such a layout");
            return null;
        }

        CoverRegistry Discover(GeneratedMission built)
        {
            var registry = world.CreateRegistry();
            var discovery = world.Track(new GameObject("Discovery")).AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
            var origin = built.Layout.TileCenter(built.Layout.FriendlySpawns[0]);
            discovery.Discover(built.Geometry, MissionNavigation.ReachableFrom(origin));
            return registry;
        }

        [UnityTest]
        public IEnumerator Build_CreatesTheOwnedRoot_WithOneCubePerFloorRectangleAndBox()
        {
            var layout = Layout(12345);
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;

            Assert.That(mission.Root.name, Is.EqualTo(GeneratedMission.RootName));
            Assert.That(mission.Geometry.parent, Is.EqualTo(mission.Root.transform));
            Assert.That(mission.Actors.parent, Is.EqualTo(mission.Root.transform));
            Assert.That(mission.Geometry.childCount, Is.EqualTo(layout.FloorRects.Count + layout.Boxes.Count));
            foreach (var box in layout.Boxes)
            {
                var cube = mission.Geometry.Find(box.Name);
                Assert.That(cube, Is.Not.Null, box.Name);
                Assert.That(cube.GetComponent<CoverSurface>(), Is.Not.Null, box.Name);
                var modifier = cube.GetComponent<NavMeshModifier>();
                Assert.That(modifier != null && modifier.overrideArea && modifier.area == 1, Is.True, box.Name + " is not carved");
                Assert.That(cube.GetComponent<BoxCollider>().bounds.size.y, Is.EqualTo(box.Height).Within(0.01f));
            }
            Assert.That(mission.Geometry.GetComponentsInChildren<CoverSurface>(), Has.Length.EqualTo(layout.Boxes.Count));
        }

        [UnityTest]
        public IEnumerator Build_GivesANavMeshThatValidates_ForSeveralSeeds()
        {
            for (var seed = 1; seed <= 8; seed++)
            {
                mission = MissionBuilder.Build(Layout(seed), null, null);
                yield return null;
                Assert.That(MissionNavigation.Validate(mission.Layout, out var reason, out var report), Is.True, $"seed {seed}: {reason}");
                Assert.That(report.NavArea, Is.GreaterThan(report.FloorArea * MissionConstants.MinNavAreaRatio));
                Assert.That(report.PathsChecked, Is.GreaterThanOrEqualTo(mission.Layout.Rooms.Count));
                DestroyMission();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Validate_FailsWhenThereIsNoNavMesh()
        {
            yield return null;
            Assert.That(MissionNavigation.Validate(Layout(12345), out var reason, out _), Is.False);
            Assert.That(reason, Does.Contain("NavMesh"));
        }

        [UnityTest]
        public IEnumerator TallWallEnds_ProduceCornerLocations_AndEveryBaffleGetsFour()
        {
            var layout = LayoutWith(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.Baffle));
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;
            var registry = Discover(mission);

            var corners = registry.Points.Where(p => p.Placement == CoverPlacement.Corner && p.Height == CoverHeight.Tall).ToList();
            Assert.That(corners.Count, Is.GreaterThanOrEqualTo(4));
            foreach (var baffle in layout.Boxes.Where(b => b.Kind == MissionBoxKind.Baffle))
                Assert.That(corners.Count(p => p.Obstacle.gameObject.name == baffle.Name), Is.EqualTo(4), baffle.Name);
            Assert.That(corners.Any(p => p.Obstacle.gameObject.name.StartsWith("Wall_")), Is.True, "room walls end at door gaps too");
        }

        [UnityTest]
        public IEnumerator LowObstacles_ProduceLowFaceLocationsOnly()
        {
            var layout = LayoutWith(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.LowWall));
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;
            var registry = Discover(mission);

            var low = layout.Boxes.Where(b => b.Kind == MissionBoxKind.LowWall || b.Kind == MissionBoxKind.Crate).Select(b => b.Name).ToHashSet();
            var own = registry.Points.Where(p => low.Contains(p.Obstacle.gameObject.name)).ToList();
            Assert.That(own, Is.Not.Empty);
            Assert.That(own.All(p => p.Height == CoverHeight.Low && p.Placement == CoverPlacement.Face), Is.True);
        }

        [UnityTest]
        public IEnumerator EveryLocation_IsReachableFromTheFriendlySpawn()
        {
            mission = MissionBuilder.Build(Layout(12345), null, null);
            yield return null;
            var registry = Discover(mission);
            Assert.That(registry.Points, Is.Not.Empty);

            NavMesh.SamplePosition(mission.Layout.TileCenter(mission.Layout.FriendlySpawns[0]), out var start, 1f, NavMesh.AllAreas);
            var path = new NavMeshPath();
            foreach (var point in registry.Points)
            {
                Assert.That(NavMesh.SamplePosition(point.Position, out var hit, 0.5f, NavMesh.AllAreas), Is.True, point.Name);
                Assert.That(NavMesh.CalculatePath(start.position, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete,
                    Is.True, point.Name);
            }
        }

        [UnityTest]
        public IEnumerator SameSeedTwice_GivesTheSameCoverCount()
        {
            mission = MissionBuilder.Build(Layout(777), null, null);
            yield return null;
            var first = Discover(mission).Points.Count;
            DestroyMission();
            yield return null;
            mission = MissionBuilder.Build(Layout(777), null, null);
            yield return null;
            var second = Discover(mission).Points.Count;

            Assert.That(first, Is.GreaterThan(0));
            Assert.That(second, Is.EqualTo(first));
        }
    }
}
```

`CoverLocation.Name`, `.Obstacle` (a `Collider`), `.Height`, `.Placement`, `.Position` exist; `CoverHeight.Low/Tall` and `CoverPlacement.Face/Corner` are the enum members.

- [ ] **Step 2: Run to verify they fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionBuilderPlayModeTests"`. Expected: `EXIT=1`, `CS0246 MissionBuilder`.

- [ ] **Step 3: Implement `MissionBuilder.cs`**

```csharp
using Unity.AI.Navigation;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What a built mission leaves in the scene: one root to destroy, the geometry parent and the actors parent.</summary>
    public sealed class GeneratedMission
    {
        public const string RootName = "GeneratedMissionRoot";

        public GameObject Root { get; internal set; }
        /// <summary>Floor and obstacle cubes. The NavMeshSurface sits here, so only this node is baked.</summary>
        public Transform Geometry { get; internal set; }
        /// <summary>Parent of every spawned unit.</summary>
        public Transform Actors { get; internal set; }
        public NavMeshSurface Surface { get; internal set; }
        public MissionLayout Layout { get; internal set; }
    }

    /// <summary>
    /// Turns a layout into primitive geometry and a runtime NavMesh. Floors are plain cubes; every obstacle cube is a
    /// CoverSurface (so cover discovery needs no special knowledge of missions) and a Not Walkable NavMeshModifier (so
    /// no walkable island forms on a wall or crate top). Materials are optional placeholders.
    /// </summary>
    public static class MissionBuilder
    {
        const int NotWalkableArea = 1;

        public static GeneratedMission Build(MissionLayout layout, Material groundMaterial, Material obstacleMaterial)
        {
            var root = new GameObject(GeneratedMission.RootName);
            var geometry = new GameObject("Geometry");
            geometry.transform.SetParent(root.transform, false);
            var actors = new GameObject("Actors");
            actors.transform.SetParent(root.transform, false);

            var floorIndex = 0;
            foreach (var rect in layout.FloorRects)
            {
                var size = new Vector3(rect.width, MissionConstants.FloorThickness, rect.height);
                var center = layout.RectCenter(rect) + Vector3.down * (MissionConstants.FloorThickness * 0.5f);
                Cube($"Floor_{++floorIndex}", center, size, groundMaterial, geometry.transform, false);
            }
            foreach (var box in layout.Boxes)
            {
                var size = new Vector3(box.Footprint.width, box.Height, box.Footprint.height);
                var center = layout.RectCenter(box.Footprint) + Vector3.up * (box.Height * 0.5f);
                Cube(box.Name, center, size, obstacleMaterial, geometry.transform, true);
            }

            // Auto sync is off: without this the colliders sit where CreatePrimitive made them until the next physics step.
            Physics.SyncTransforms();

            var surface = geometry.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();

            return new GeneratedMission
            {
                Root = root,
                Geometry = geometry.transform,
                Actors = actors.transform,
                Surface = surface,
                Layout = layout,
            };
        }

        static void Cube(string name, Vector3 center, Vector3 size, Material material, Transform parent, bool obstacle)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.position = center;
            cube.transform.localScale = size;
            if (material != null)
                cube.GetComponent<Renderer>().sharedMaterial = material;
            if (!obstacle)
                return;
            cube.AddComponent<CoverSurface>();
            var modifier = cube.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = NotWalkableArea;
        }
    }
}
```

- [ ] **Step 4: Implement `MissionNavigation.cs`**

```csharp
using System;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    public readonly struct MissionNavigationReport
    {
        public MissionNavigationReport(float navArea, float floorArea, int pathsChecked)
        {
            NavArea = navArea;
            FloorArea = floorArea;
            PathsChecked = pathsChecked;
        }

        public float NavArea { get; }
        public float FloorArea { get; }
        public int PathsChecked { get; }
    }

    /// <summary>Checks a freshly built NavMesh against the layout it was built from.</summary>
    public static class MissionNavigation
    {
        const float SnapRadius = 2f;

        /// <summary>
        /// Valid when the NavMesh is not empty and covers a sane share of the floor, the first friendly spawn is on it,
        /// and a complete path leads from there to every room centre and every spawn point.
        /// </summary>
        public static bool Validate(MissionLayout layout, out string reason, out MissionNavigationReport report)
        {
            var navArea = NavMeshArea();
            report = new MissionNavigationReport(navArea, layout.FloorTileCount, 0);
            if (navArea <= 0f)
            {
                reason = "the NavMesh is empty";
                return false;
            }
            if (navArea < layout.FloorTileCount * MissionConstants.MinNavAreaRatio)
            {
                reason = $"the NavMesh covers only {navArea:0} of {layout.FloorTileCount} m2 of floor";
                return false;
            }
            if (!NavMesh.SamplePosition(layout.TileCenter(layout.FriendlySpawns[0]), out var start, 1f, NavMesh.AllAreas))
            {
                reason = "the first friendly spawn is not on the NavMesh";
                return false;
            }

            var path = new NavMeshPath();
            var checkedPaths = 0;
            foreach (var room in layout.Rooms)
            {
                if (!PathExists(start.position, layout.RectCenter(room.Rect), path, out reason, $"room {room.Index}"))
                    return false;
                checkedPaths++;
            }
            foreach (var tile in layout.FriendlySpawns)
            {
                if (!PathExists(start.position, layout.TileCenter(tile), path, out reason, "a friendly spawn"))
                    return false;
                checkedPaths++;
            }
            foreach (var tile in layout.HostileSpawns)
            {
                if (!PathExists(start.position, layout.TileCenter(tile), path, out reason, "a hostile spawn"))
                    return false;
                checkedPaths++;
            }
            report = new MissionNavigationReport(navArea, layout.FloorTileCount, checkedPaths);
            reason = null;
            return true;
        }

        static bool PathExists(Vector3 from, Vector3 to, NavMeshPath path, out string reason, string label)
        {
            reason = null;
            if (!NavMesh.SamplePosition(to, out var target, SnapRadius, NavMesh.AllAreas))
            {
                reason = $"{label} is not on the NavMesh";
                return false;
            }
            if (!NavMesh.CalculatePath(from, target.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                reason = $"no complete path from the friendly spawn to {label}";
                return false;
            }
            return true;
        }

        /// <summary>A predicate for cover discovery: true when a complete path leads from `origin` to a walkable point within 0.5 m.</summary>
        public static Func<Vector3, bool> ReachableFrom(Vector3 origin)
        {
            var path = new NavMeshPath();
            return point =>
            {
                if (!NavMesh.SamplePosition(point, out var hit, 0.5f, NavMesh.AllAreas))
                    return false;
                return NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            };
        }

        /// <summary>Total area, in square metres, of the triangles of every active NavMesh.</summary>
        public static float NavMeshArea()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            var vertices = triangulation.vertices;
            var indices = triangulation.indices;
            var area = 0f;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                area += 0.5f * Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z));
            }
            return area;
        }
    }
}
```

`ReachableFrom(origin)`: pass a point on the NavMesh (the tests pass `TileCenter` of a friendly spawn, which lies on the NavMesh by the spawn rules; `CalculatePath` snaps the source itself).

- [ ] **Step 5: Run to verify they pass**

Run `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionBuilderPlayModeTests"`. Expected: 7 passed. Likely problems and the fix to try, in order:
- `Build_GivesANavMeshThatValidates...` fails on area: print `report`; if the NavMesh is empty, confirm the geometry children are collected (`collectObjects = Children`, colliders enabled) and that `Physics.SyncTransforms()` runs before the bake.
- No corners from baffles: a baffle's stand points are 0.75 m off the long face; the generator guarantees 2 tiles of clearance on all sides. If a corner is missing, confirm `area == 1` is "Not Walkable" in `ProjectSettings/NavMeshAreas.asset` (it is, index 1) and that the modifier is on the obstacle itself.
- A NavMesh left over between tests: the tests use `DestroyImmediate`; do not switch to `Destroy`.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Mission Assets/_Project/Tests/PlayMode
git commit -m "Build mission geometry and a runtime NavMesh, and validate navigation

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: MissionSpawner, MissionDirector and the regeneration lifecycle

**Files:**
- Create: `Scripts/Mission/MissionSlots.cs`, `MissionSpawner.cs`, `MissionDirector.cs`; test support `Tests/PlayMode/TestSupport/MissionRig.cs`
- Test: `Tests/PlayMode/MissionDirectorPlayModeTests.cs`

**Interfaces:**
- Consumes: Tasks 2-5.
- Produces:
  - `FriendlySlot { GameObject prefab; CombatArchetype archetype; AbilityDefinition[] abilities; }`, `HostileSlot { GameObject prefab; CombatArchetype archetype; }`, `MissionSystems { Encounter encounter; UnitSelection selection; ActiveCharacter activeCharacter; CoverRegistry coverRegistry; CoverDiscovery coverDiscovery; TacticalPause pause; TacticalCameraController camera; AbilityTargeting abilityTargeting; }` (all public fields, serializable; `camera` and `abilityTargeting` optional).
  - `enum MissionState { Idle, Generating, Ready, Failed }`.
  - `MissionReport` (public fields): `int Seed, Attempt, AttemptsMade, MaxAttempts; bool Succeeded; string Failure; List<string> Failures; int Rooms, Connections, FloorTiles; float NavArea; int PathsChecked; int CoverTotal, CoverLow, CoverTall, CoverCorner; int FriendlySpawns, HostileSpawns; float TeamSeparation; Bounds Bounds; ulong LayoutHash;`
  - `MissionDirector`: `MissionState State`, `MissionReport Report`, `GeneratedMission Current`, `MissionSettings Settings`, `IReadOnlyList<CommandableUnit> Friendlies, Hostiles`, `event Action<MissionState> StateChanged`, `bool Generate(int seed)`, `bool RegenerateSame()`, `bool GenerateNew()`, `void Clear()`, `internal void Initialize(MissionSettings, FriendlySlot[], HostileSlot[], MissionSystems, Material ground, Material obstacle, bool generateOnStart)`.
  - `MissionSpawner.TrySpawn(GeneratedMission, IReadOnlyList<FriendlySlot>, IReadOnlyList<HostileSlot>, MissionSystems, out MissionSpawnResult, out string failure)`; `MissionSpawnResult { List<CommandableUnit> Friendlies; List<CommandableUnit> Hostiles; }`.

- [ ] **Step 1: Write the test rig and the failing tests**

`Tests/PlayMode/TestSupport/MissionRig.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The persistent systems a MissionDirector needs, plus a director, built from the project's real prefabs and data.</summary>
    internal sealed class MissionRig : IDisposable
    {
        public readonly TestWorld World = new TestWorld();
        public readonly TacticalPause Pause;
        public readonly Encounter Encounter;
        public readonly UnitSelection Selection;
        public readonly ActiveCharacter Active;
        public readonly CoverRegistry Registry;
        public readonly CoverDiscovery Discovery;
        public readonly TacticalCameraController Camera;
        public readonly MissionDirector Director;

        public MissionRig(MissionSettings settings = null, bool withCamera = false)
        {
            Pause = World.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            Encounter = World.CreateEncounter();
            Selection = World.Track(new GameObject("Selection")).AddComponent<UnitSelection>();
            Active = World.Track(new GameObject("ActiveCharacter")).AddComponent<ActiveCharacter>();
            Active.Initialize(null, Pause, Selection);
            Registry = World.CreateRegistry();
            Discovery = World.Track(new GameObject("Discovery")).AddComponent<CoverDiscovery>();
            Discovery.Initialize(Registry, discoverAtStart: false);
            if (withCamera)
            {
                var rig = World.Track(new GameObject("CameraRig"));
                rig.SetActive(false);
                var cameraObject = new GameObject("Camera");
                cameraObject.transform.SetParent(rig.transform, false);
                var viewCamera = cameraObject.AddComponent<UnityEngine.Camera>();
                Camera = rig.AddComponent<TacticalCameraController>();
                Camera.Initialize(viewCamera, null, null, null, null, null, Active);
                rig.SetActive(true);
            }

            var systems = new MissionSystems
            {
                encounter = Encounter, selection = Selection, activeCharacter = Active, coverRegistry = Registry,
                coverDiscovery = Discovery, pause = Pause, camera = Camera,
            };
            Director = World.Track(new GameObject("Director")).AddComponent<MissionDirector>();
            Director.Initialize(settings ?? new MissionSettings(), FriendlySlots(), HostileSlots(), systems,
                Load<Material>("Materials/Ground.mat"), Load<Material>("Materials/Obstacle.mat"), generateAtStart: false);
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>("Assets/_Project/" + path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path);
            return asset;
        }

        public static FriendlySlot[] FriendlySlots()
        {
            var prefab = Load<GameObject>("Prefabs/FriendlyUnit.prefab");
            var abilities = new[] { Load<AbilityDefinition>("Data/Abilities/AimedShot.asset"),
                Load<AbilityDefinition>("Data/Abilities/Blast.asset"), Load<AbilityDefinition>("Data/Abilities/Mend.asset") };
            return new[]
            {
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Melee.asset"), abilities = abilities },
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Marksman.asset"), abilities = abilities },
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Ranged.asset"), abilities = abilities },
            };
        }

        public static HostileSlot[] HostileSlots()
        {
            var prefab = Load<GameObject>("Prefabs/HostileUnit.prefab");
            return new[]
            {
                new HostileSlot { prefab = prefab },
                new HostileSlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Ranged.asset") },
                new HostileSlot { prefab = prefab },
            };
        }

        /// <summary>Runs one generation to the end (Ready or Failed).</summary>
        public System.Collections.IEnumerator Generate(int seed)
        {
            Director.Generate(seed);
            yield return TestWorld.WaitUntil(() => Director.State == MissionState.Ready || Director.State == MissionState.Failed, 20f);
        }

        public void Dispose()
        {
            Director.Clear();
            World.Dispose();
            Time.timeScale = 1f;
        }
    }
}
#endif
```

`Tests/PlayMode/MissionDirectorPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class MissionDirectorPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        static string[] RootNames() => SceneManager.GetActiveScene().GetRootGameObjects().Select(g => g.name).OrderBy(n => n).ToArray();

        [UnityTest]
        public IEnumerator Generate_ReachesReady_WithTheSquadAndHostilesOnTheNavMesh_AndWiredToTheSystems()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var d = rig.Director;

            Assert.That(d.State, Is.EqualTo(MissionState.Ready), string.Join("\n", d.Report.Failures));
            Assert.That(d.Friendlies, Has.Count.EqualTo(3));
            Assert.That(d.Hostiles, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Friendlies, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Hostiles, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(rig.Selection.Roster, Has.Count.EqualTo(3));
            Assert.That(rig.Active.Unit, Is.EqualTo(d.Friendlies[0]));
            Assert.That(rig.Active.IsTakeoverOn, Is.False);
            Assert.That(rig.Registry.Points, Is.Not.Empty);
            Assert.That(d.Report.CoverCorner, Is.GreaterThan(0));
            foreach (var unit in d.Friendlies.Concat(d.Hostiles))
            {
                Assert.That(unit.GetComponent<NavMeshAgent>().isOnNavMesh, Is.True, unit.name);
                Assert.That(unit.transform.IsChildOf(d.Current.Actors), Is.True, unit.name);
                Assert.That(unit.Cover.IsWired, Is.True, unit.name);
            }
            foreach (var friendly in d.Friendlies)
            {
                Assert.That(friendly.GetComponent<CompanionAI>().IsWired, Is.True);
                Assert.That(friendly.GetComponent<UnitAbilities>().Count, Is.EqualTo(3));
            }
            foreach (var hostile in d.Hostiles)
            {
                Assert.That(hostile.GetComponent<EnemyAI>().IsCoverWired, Is.True);
                Assert.That(hostile.GetComponent<UnitAbilities>(), Is.Null);
            }
            Assert.That(d.Friendlies[1].GetComponent<UnitAttacker>().Archetype.DisplayName, Is.EqualTo("Marksman"));
        }

        [UnityTest]
        public IEnumerator Spawns_AreOutsideGeometry_Apart_AndTheTeamsStartSeparated()
        {
            rig = new MissionRig();
            foreach (var seed in new[] { 12345, 1, 2, 3 })
            {
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}");
                var all = rig.Director.Friendlies.Concat(rig.Director.Hostiles).ToList();
                foreach (var unit in all)
                {
                    var ground = unit.transform.position - Vector3.up;
                    Assert.That(Physics.CheckCapsule(ground + Vector3.up * 0.55f, ground + Vector3.up * 1.45f, 0.45f, ~0, QueryTriggerInteraction.Ignore),
                        Is.False, $"seed {seed}: {unit.name} stands inside geometry");
                }
                for (var i = 0; i < all.Count; i++)
                    for (var j = i + 1; j < all.Count; j++)
                        Assert.That(TestWorld.HorizontalDistance(all[i].transform.position, all[j].transform.position),
                            Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing - 0.2f), $"seed {seed}");
                Assert.That(rig.Director.Report.TeamSeparation, Is.GreaterThanOrEqualTo(15.8f), $"seed {seed}");
            }
        }

        [UnityTest]
        public IEnumerator SameSeedTwice_GivesTheSameLayoutAndTheSameCover()
        {
            rig = new MissionRig();
            yield return rig.Generate(4242);
            var first = rig.Director.Report;
            var firstPositions = rig.Director.Friendlies.Concat(rig.Director.Hostiles).Select(u => u.transform.position).ToList();
            yield return rig.Generate(4242);
            var second = rig.Director.Report;

            Assert.That(second.LayoutHash, Is.EqualTo(first.LayoutHash));
            Assert.That(second.CoverTotal, Is.EqualTo(first.CoverTotal));
            Assert.That(second.CoverCorner, Is.EqualTo(first.CoverCorner));
            var secondPositions = rig.Director.Friendlies.Concat(rig.Director.Hostiles).Select(u => u.transform.position).ToList();
            for (var i = 0; i < firstPositions.Count; i++)
                Assert.That(Vector3.Distance(firstPositions[i], secondPositions[i]), Is.LessThan(0.05f));
        }

        [UnityTest]
        public IEnumerator Regenerate_LeavesNoStaleState()
        {
            rig = new MissionRig();
            var persistentRoots = RootNames();
            yield return rig.Generate(11);
            var d = rig.Director;
            var oldRoot = d.Current.Root;
            var oldLocations = rig.Registry.Points.ToList();
            var oldFriendly = d.Friendlies[0];
            var oldHostile = d.Hostiles[0];

            // Dirty every kind of state: a selection, a reserved cover point, queued orders, a dead hostile (death marker).
            rig.Selection.Select(oldFriendly.GetComponent<SelectableUnit>());
            var reserved = oldLocations.First(p => p.Height == CoverHeight.Low);
            Assert.That(oldFriendly.Issue(new MoveToCoverCommand(reserved)), Is.True);
            oldFriendly.Issue(new MoveCommand(oldFriendly.transform.position + Vector3.right), IssueMode.Append);
            oldHostile.GetComponent<Health>().TakeDamage(1000);
            yield return null;
            Assert.That(oldHostile.GetComponent<DeathMarker>().LastMarker, Is.Not.Null);
            Assert.That(reserved.IsClaimed, Is.True);

            yield return rig.Generate(12);
            yield return null;

            Assert.That(d.State, Is.EqualTo(MissionState.Ready));
            Assert.That(oldRoot == null, Is.True, "the old mission root is gone");
            Assert.That(oldFriendly == null && oldHostile == null, Is.True, "old units are gone");
            Assert.That(oldLocations.All(l => !l.IsValid), Is.True, "old cover locations are retired");
            Assert.That(oldLocations.All(l => !l.IsClaimed), Is.True, "no old claim survives");
            Assert.That(rig.Registry.Points.All(p => !oldLocations.Contains(p)), Is.True);
            Assert.That(rig.Selection.Selected, Is.Empty);
            Assert.That(rig.Selection.Roster, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Friendlies.All(h => h != null && h.IsAlive), Is.True);
            Assert.That(rig.Encounter.Hostiles, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.LivingHostiles, Is.EqualTo(3));
            Assert.That(rig.Encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(rig.Registry.Points.All(p => !p.IsClaimed), Is.True, "no reservation on the new cover");
            var expectedRoots = persistentRoots.Concat(new[] { GeneratedMission.RootName }).OrderBy(n => n).ToArray();
            Assert.That(RootNames(), Is.EqualTo(expectedRoots), "no marker or other object survived at the scene root");
        }

        [UnityTest]
        public IEnumerator Regenerate_WhilePaused_ResumesAndWorks()
        {
            rig = new MissionRig();
            yield return rig.Generate(5);
            rig.Pause.Pause();
            Assert.That(Time.timeScale, Is.EqualTo(0f));

            yield return rig.Generate(6);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(rig.Pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator Generate_WhileGenerating_IsIgnored()
        {
            rig = new MissionRig();
            Assert.That(rig.Director.Generate(21), Is.True);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Generating));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("already being generated"));
            Assert.That(rig.Director.Generate(22), Is.False, "a second request while generating is refused");
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(rig.Director.Report.Seed, Is.EqualTo(21));
            Assert.That(SceneManager.GetActiveScene().GetRootGameObjects().Count(g => g.name == GeneratedMission.RootName), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ImpossibleSettings_FailCleanly_NoUnitsNoEncounter_AndTheErrorNamesTheSeed()
        {
            rig = new MissionRig(new MissionSettings { minTeamSeparation = 60f, maxAttempts = 3 });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"Mission generation failed.*seed=77.*attempts=3", System.Text.RegularExpressions.RegexOptions.Singleline));
            yield return rig.Generate(77);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Failed));
            Assert.That(rig.Director.Report.Succeeded, Is.False);
            Assert.That(rig.Director.Report.Failures, Has.Count.EqualTo(3));
            Assert.That(rig.Director.Friendlies, Is.Empty);
            Assert.That(rig.Encounter.Friendlies, Is.Empty);
            Assert.That(rig.Encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(rig.Registry.Points, Is.Empty);
            Assert.That(GameObject.Find(GeneratedMission.RootName), Is.Null);
        }

        [UnityTest]
        public IEnumerator Generate_RebindsAndFocusesTheCamera()
        {
            rig = new MissionRig(withCamera: true);
            yield return rig.Generate(12345);
            var bounds = rig.Director.Report.Bounds;
            var squadCentre = rig.Director.Friendlies.Aggregate(Vector3.zero, (sum, u) => sum + u.transform.position) / 3f;
            yield return null;

            var position = rig.Camera.transform.position;
            Assert.That(position.x, Is.InRange(bounds.min.x - 2.1f, bounds.max.x + 2.1f));
            Assert.That(position.z, Is.InRange(bounds.min.z - 2.1f, bounds.max.z + 2.1f));
            Assert.That(TestWorld.HorizontalDistance(position, squadCentre), Is.LessThan(8f), "the squad is where the camera looks");
        }
    }
}
#endif
```

`CoverLocation` members used above (all verified to exist): `IsValid`, `IsClaimed`, `Position`, `Obstacle`, `Name`, `Height`, `Placement`.

- [ ] **Step 2: Run to verify they fail**

Run `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDirectorPlayModeTests"`. Expected: `EXIT=1` (`MissionDirector` undefined).

- [ ] **Step 3: Implement `MissionSlots.cs`**

```csharp
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

    /// <summary>The persistent systems a mission is generated into. `camera` and `abilityTargeting` are optional.</summary>
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
    }
}
```

- [ ] **Step 4: Implement `MissionSpawner.cs`**

```csharp
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
```

- [ ] **Step 5: Implement `MissionDirector.cs`**

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public enum MissionState
    {
        Idle,
        Generating,
        Ready,
        Failed,
    }

    /// <summary>Everything the debug view shows about the last generation. Filled by the director; nothing reads it back.</summary>
    public sealed class MissionReport
    {
        public int Seed;
        public int Attempt;
        public int AttemptsMade;
        public int MaxAttempts;
        public bool Succeeded;
        public string Failure;
        public List<string> Failures = new List<string>();
        public int Rooms;
        public int Connections;
        public int FloorTiles;
        public float NavArea;
        public int PathsChecked;
        public int CoverTotal;
        public int CoverLow;
        public int CoverTall;
        public int CoverCorner;
        public int FriendlySpawns;
        public int HostileSpawns;
        public float TeamSeparation;
        public Bounds Bounds;
        public ulong LayoutHash;
    }

    /// <summary>
    /// Runs the mission pipeline: teardown, layout, geometry, NavMesh, validation, cover, spawn, camera. Persistent
    /// systems are only reset and refilled, never created or destroyed. One generation at a time; every generated object
    /// lives under GeneratedMissionRoot, which is all teardown has to destroy (plus the death markers units leave at
    /// the scene root).
    /// </summary>
    public sealed class MissionDirector : MonoBehaviour
    {
        [SerializeField] MissionSettings settings = new MissionSettings();
        [SerializeField] FriendlySlot[] friendlySlots = new FriendlySlot[0];
        [SerializeField] HostileSlot[] hostileSlots = new HostileSlot[0];
        [SerializeField] MissionSystems systems = new MissionSystems();
        [SerializeField] Material groundMaterial;
        [SerializeField] Material obstacleMaterial;
        [SerializeField] bool generateOnStart = true;
        [SerializeField, Min(0f)] float cameraMargin = 2f;

        readonly List<CommandableUnit> friendlies = new List<CommandableUnit>();
        readonly List<CommandableUnit> hostiles = new List<CommandableUnit>();

        public MissionState State { get; private set; }
        public MissionReport Report { get; private set; } = new MissionReport();
        public GeneratedMission Current { get; private set; }
        public MissionSettings Settings => settings;
        public IReadOnlyList<CommandableUnit> Friendlies => friendlies;
        public IReadOnlyList<CommandableUnit> Hostiles => hostiles;

        public event Action<MissionState> StateChanged;

        internal void Initialize(MissionSettings missionSettings, FriendlySlot[] friendly, HostileSlot[] hostile, MissionSystems persistent,
            Material ground, Material obstacle, bool generateAtStart)
        {
            settings = missionSettings;
            friendlySlots = friendly;
            hostileSlots = hostile;
            systems = persistent;
            groundMaterial = ground;
            obstacleMaterial = obstacle;
            generateOnStart = generateAtStart;
        }

        void Start()
        {
            if (generateOnStart)
                Generate(settings.seed);
        }

        /// <summary>Starts generating this seed. Refused (false, with a warning) while a generation is running.</summary>
        public bool Generate(int seed)
        {
            if (State == MissionState.Generating)
            {
                Debug.LogWarning($"{name}: a mission is already being generated; the request for seed {seed} was ignored.", this);
                return false;
            }
            settings.seed = seed;
            StartCoroutine(Run());
            return true;
        }

        public bool RegenerateSame() => Generate(settings.seed);

        /// <summary>A fresh seed from the clock, never from UnityEngine.Random (generation must not disturb combat randomness).</summary>
        public bool GenerateNew() => Generate(unchecked(Environment.TickCount * 397) & 0x7FFFFFFF);

        /// <summary>Destroys the current mission and resets the persistent systems. Takes effect at the end of the frame.</summary>
        public void Clear()
        {
            Teardown();
            SetState(MissionState.Idle);
        }

        IEnumerator Run()
        {
            SetState(MissionState.Generating);
            var request = settings.Validated();
            if (friendlySlots.Length > 0)
                request.friendlyCount = Mathf.Clamp(friendlySlots.Length, 1, 6);
            Report = new MissionReport { Seed = request.seed, MaxAttempts = request.maxAttempts };
            Teardown();
            yield return null;   // let Destroy finish so the old NavMesh and colliders are really gone

            for (var attempt = 1; attempt <= request.maxAttempts; attempt++)
            {
                Report.AttemptsMade = attempt;
                if (!MissionGenerator.TryAttempt(request, attempt, out var layout, out var reason))
                {
                    Report.Failures.Add($"attempt {attempt}: {reason}");
                    continue;
                }
                var mission = MissionBuilder.Build(layout, groundMaterial, obstacleMaterial);
                if (!MissionNavigation.Validate(layout, out reason, out var navigation))
                {
                    Report.Failures.Add($"attempt {attempt}: navigation: {reason}");
                    DestroyImmediate(mission.Root);   // immediate: the next attempt must not see this NavMesh
                    continue;
                }

                var origin = layout.TileCenter(layout.FriendlySpawns[0]);
                systems.coverDiscovery.Discover(mission.Geometry, MissionNavigation.ReachableFrom(origin));

                if (!MissionSpawner.TrySpawn(mission, friendlySlots, hostileSlots, systems, out var spawned, out reason))
                {
                    Report.Failures.Add($"attempt {attempt}: spawn: {reason}");
                    systems.coverRegistry.Rebuild(Array.Empty<CoverLocation>());
                    DestroyImmediate(mission.Root);
                    continue;
                }

                Current = mission;
                friendlies.AddRange(spawned.Friendlies);
                hostiles.AddRange(spawned.Hostiles);
                Fill(Report, layout, navigation);
                FrameCamera(layout);
                SetState(MissionState.Ready);
                yield break;
            }

            var failureText = $"Mission generation failed. {request.Describe()}\n  " + string.Join("\n  ", Report.Failures);
            Report.Failure = failureText;
            Debug.LogError(failureText, this);
            SetState(MissionState.Failed);
        }

        void Teardown()
        {
            if (systems.coverRegistry != null)
                systems.coverRegistry.Rebuild(Array.Empty<CoverLocation>());
            if (systems.encounter != null)
                systems.encounter.Initialize(Array.Empty<Health>(), Array.Empty<Health>());
            if (systems.selection != null)
            {
                systems.selection.Clear();
                systems.selection.Initialize();
            }
            if (systems.activeCharacter != null)
                systems.activeCharacter.SetUnit(null);
            if (systems.abilityTargeting != null)
                systems.abilityTargeting.Disarm();
            foreach (var unit in friendlies)
                DestroyMarker(unit);
            foreach (var unit in hostiles)
                DestroyMarker(unit);
            friendlies.Clear();
            hostiles.Clear();
            if (Current != null && Current.Root != null)
                Destroy(Current.Root);
            Current = null;
            if (systems.pause != null)
                systems.pause.Resume();
        }

        // A death marker is a root object that outlives its unit, so it is not under the mission root.
        static void DestroyMarker(CommandableUnit unit)
        {
            if (unit == null || !unit.TryGetComponent<DeathMarker>(out var marker) || marker.LastMarker == null)
                return;
            Destroy(marker.LastMarker);
        }

        void FrameCamera(MissionLayout layout)
        {
            if (systems.camera == null)
                return;
            var bounds = layout.WorldBounds;
            systems.camera.SetBounds(new Rect(bounds.min.x - cameraMargin, bounds.min.z - cameraMargin,
                bounds.size.x + 2f * cameraMargin, bounds.size.z + 2f * cameraMargin));
            var sum = Vector3.zero;
            foreach (var unit in friendlies)
                sum += unit.transform.position;
            systems.camera.FocusOn(sum / Mathf.Max(1, friendlies.Count));
        }

        void Fill(MissionReport report, MissionLayout layout, MissionNavigationReport navigation)
        {
            report.Succeeded = true;
            report.Attempt = layout.Attempt;
            report.Rooms = layout.Rooms.Count;
            report.Connections = layout.Connections.Count;
            report.FloorTiles = layout.FloorTileCount;
            report.NavArea = navigation.NavArea;
            report.PathsChecked = navigation.PathsChecked;
            report.FriendlySpawns = layout.FriendlySpawns.Count;
            report.HostileSpawns = layout.HostileSpawns.Count;
            report.Bounds = layout.WorldBounds;
            report.LayoutHash = layout.Hash;
            var separation = float.MaxValue;
            foreach (var f in layout.FriendlySpawns)
                foreach (var h in layout.HostileSpawns)
                    separation = Mathf.Min(separation, Vector2.Distance(f, h));
            report.TeamSeparation = separation;
            foreach (var point in systems.coverRegistry.Points)
            {
                report.CoverTotal++;
                if (point.Height == CoverHeight.Low)
                    report.CoverLow++;
                else
                    report.CoverTall++;
                if (point.Placement == CoverPlacement.Corner)
                    report.CoverCorner++;
            }
        }

        void SetState(MissionState state)
        {
            if (State == state)
                return;
            State = state;
            StateChanged?.Invoke(state);
        }
    }
}
```

Notes for the implementer: `Teardown()` runs while `Current.Root`'s units are still in `friendlies/hostiles`, which is why markers are destroyed first. `SetState(Generating)` must happen synchronously inside `Generate` (it does: the coroutine body runs up to its first `yield` immediately), which is what `Generate_WhileGenerating_IsIgnored` pins. `friendlyCount` is taken from the slot count so the layout always has one spawn per slot. `MissionDirector.Clear()` is also used by the test rig's `Dispose`; it only needs the systems that exist.

- [ ] **Step 6: Run to verify they pass**

Run `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDirectorPlayModeTests"`. Expected: 8 passed and no unexpected Console error or warning (the test framework fails a test on an unexpected error log; the failure test and the double-request test each expect exactly one log). If an `OnEnable` warning appears ("has no ActiveCharacter or Encounter wired"), a unit was activated before wiring: confirm `Actors` is inactive while instantiating. If `Regenerate_LeavesNoStaleState` reports extra scene roots, name them from the assertion and trace their creator (`CommandQueueView` markers, `DeathMarker`): make that creator parent its object under the unit or add it to the teardown, whichever is smaller.

- [ ] **Step 7: Run both full suites and commit**

Run `Tools/run-tests.sh EditMode` and `Tools/run-tests.sh PlayMode`; correct the totals table if a count differs.

```bash
git add Assets/_Project/Scripts/Mission Assets/_Project/Tests/PlayMode
git commit -m "Spawn the squad and hostiles into a built mission and run the regeneration lifecycle

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Developer input and debug views

**Files:**
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions` (new `Developer` map)
- Create: `Scripts/Mission/MissionDeveloperInput.cs`, `MissionDebugView.cs`, `MissionGizmoView.cs`
- Test: `Tests/EditMode/MissionDebugTextTests.cs`, `Tests/PlayMode/MissionDeveloperInputTests.cs`

**Interfaces:**
- Consumes: Task 6 (`MissionDirector`, `MissionReport`, `MissionState`).
- Produces: actions `Developer/RegenerateSame` (`<Keyboard>/f6`) and `Developer/RegenerateNew` (`<Keyboard>/f7`), both Buttons in group `KeyboardMouse`; `MissionDeveloperInput` (serialized `director`, `regenerateSameAction`, `regenerateNewAction`; `internal void Initialize(MissionDirector, InputActionReference same, InputActionReference fresh)`); `static class MissionDebugText { static string Describe(MissionState state, MissionReport report); const string Hints }` (pure); `MissionDebugView` (serialized `director`); `MissionGizmoView` (serialized `director`, `bool draw = true`).

- [ ] **Step 1: Write the failing tests**

`MissionDebugTextTests.cs` (EditMode):

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionDebugTextTests
    {
        static MissionReport Ready() => new MissionReport
        {
            Seed = 12345, Attempt = 2, AttemptsMade = 2, MaxAttempts = 20, Succeeded = true, Rooms = 6, Connections = 6,
            FloorTiles = 520, NavArea = 311.4f, PathsChecked = 15, CoverTotal = 180, CoverLow = 70, CoverTall = 110, CoverCorner = 24,
            FriendlySpawns = 3, HostileSpawns = 3, TeamSeparation = 21.3f, Bounds = new Bounds(Vector3.zero, new Vector3(50f, 3f, 38f)),
        };

        [Test]
        public void Ready_ShowsSeedAttemptStructureNavigationCoverSpawnsAndBounds()
        {
            var text = MissionDebugText.Describe(MissionState.Ready, Ready());
            foreach (var part in new[] { "Seed 12345", "attempt 2/20", "Ready", "6 rooms", "6 connections", "Nav 311", "15 paths ok",
                         "Cover 180", "low 70", "tall 110", "corner 24", "Spawns 3 friendly / 3 hostile", "21.3 m apart", "50 x 38" })
                Assert.That(text, Does.Contain(part));
        }

        [Test]
        public void Failed_ShowsTheFailureAndTheSeed()
        {
            var report = new MissionReport { Seed = 9, AttemptsMade = 3, MaxAttempts = 3, Failure = "Mission generation failed. seed=9 attempts=3" };
            var text = MissionDebugText.Describe(MissionState.Failed, report);
            Assert.That(text, Does.Contain("Failed").And.Contain("Seed 9").And.Contain("seed=9 attempts=3"));
        }

        [Test]
        public void Generating_SaysSo_WithoutNumbersThatDoNotExistYet()
        {
            var text = MissionDebugText.Describe(MissionState.Generating, new MissionReport { Seed = 5, MaxAttempts = 20 });
            Assert.That(text, Does.Contain("Generating").And.Contain("Seed 5"));
            Assert.That(text, Does.Not.Contain("rooms"));
        }

        [Test]
        public void Hints_NameBothDeveloperKeys() =>
            Assert.That(MissionDebugText.Hints, Does.Contain("F6").And.Contain("F7"));
    }
}
```

`MissionDeveloperInputTests.cs` (PlayMode):

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class MissionDeveloperInputTests : InputTestFixture
    {
        Keyboard keyboard;
        InputActionAsset actions;
        MissionRig rig;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            rig = new MissionRig();
            var input = rig.World.Track(new GameObject("DeveloperInput")).AddComponent<MissionDeveloperInput>();
            input.Initialize(rig.Director, TestControls.Ref(actions, "Developer/RegenerateSame"), TestControls.Ref(actions, "Developer/RegenerateNew"));
        }

        public override void TearDown()
        {
            rig.Dispose();
            TestControls.Reset(actions);
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator F6_RegeneratesTheSameSeed()
        {
            yield return rig.Generate(31);
            var hash = rig.Director.Report.LayoutHash;
            var oldRoot = rig.Director.Current.Root;

            Press(keyboard.f6Key);
            yield return null;
            Release(keyboard.f6Key);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Generating));
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(rig.Director.Settings.seed, Is.EqualTo(31));
            Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(hash));
            Assert.That(oldRoot == null, Is.True);
        }

        [UnityTest]
        public IEnumerator F7_GeneratesANewSeed_AndShowsItInTheSettings()
        {
            yield return rig.Generate(31);

            Press(keyboard.f7Key);
            yield return null;
            Release(keyboard.f7Key);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(rig.Director.Settings.seed, Is.Not.EqualTo(31));
            Assert.That(rig.Director.Report.Seed, Is.EqualTo(rig.Director.Settings.seed));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify they fail**

Run `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionDebugTextTests"`. Expected `EXIT=1` (`MissionDebugText` undefined).

- [ ] **Step 3: Add the `Developer` input map**

Open `Assets/_Project/Input/BlackglassControls.inputactions` and add a fourth map to `"maps"` after `Character`, in the file's existing JSON style (new GUIDs, both bindings in group `KeyboardMouse`):

```json
        {
            "name": "Developer",
            "id": "0c8e5d7a-3b1f-4e62-9a04-7d2b6f1c8e30",
            "actions": [
                { "name": "RegenerateSame", "type": "Button", "id": "5a1d9c42-7e0b-4f83-a6d1-2c4e8b0f9a11", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "RegenerateNew", "type": "Button", "id": "9f3b7e15-2d6a-4c08-b5e9-1a7c3d5f0e22", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
            ],
            "bindings": [
                { "name": "", "id": "d4e6a8c0-1b3f-4527-8960-a1b2c3d4e5f1", "path": "<Keyboard>/f6", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "RegenerateSame", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "e5f7b9d1-2c4a-4638-9a71-b2c3d4e5f6a2", "path": "<Keyboard>/f7", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "RegenerateNew", "isComposite": false, "isPartOfComposite": false }
            ]
        }
```

Let Unity import it (it regenerates the `InputActionReference` sub-assets). `InputAssetTests` (unique IDs, one known group per binding, keyboard group holds only keyboard paths) must stay green; the explicit `PadActions` list is unaffected.

- [ ] **Step 4: Implement the three components**

`MissionDeveloperInput.cs`:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>Developer keys (F6 same seed, F7 new seed) as input actions; calls the director and nothing else.</summary>
    public sealed class MissionDeveloperInput : MonoBehaviour
    {
        [SerializeField] MissionDirector director;
        [SerializeField] InputActionReference regenerateSameAction;
        [SerializeField] InputActionReference regenerateNewAction;

        internal void Initialize(MissionDirector missionDirector, InputActionReference same, InputActionReference fresh)
        {
            director = missionDirector;
            regenerateSameAction = same;
            regenerateNewAction = fresh;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, regenerateSameAction, regenerateNewAction);
            if (regenerateSameAction != null)
                regenerateSameAction.action.performed += OnSame;
            if (regenerateNewAction != null)
                regenerateNewAction.action.performed += OnNew;
        }

        void OnDisable()
        {
            if (regenerateSameAction != null)
                regenerateSameAction.action.performed -= OnSame;
            if (regenerateNewAction != null)
                regenerateNewAction.action.performed -= OnNew;
            InputActionUtility.SetEnabled(false, regenerateSameAction, regenerateNewAction);
        }

        void OnSame(InputAction.CallbackContext context) => director.RegenerateSame();

        void OnNew(InputAction.CallbackContext context) => director.GenerateNew();
    }
}
```

`MissionDebugView.cs` (IMGUI, debug only, same style as `PrototypeHud`; draws at the top right so it does not cover the HUD's top-left text):

```csharp
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>The text the mission debug view shows. Pure, so it is tested without a scene.</summary>
    public static class MissionDebugText
    {
        public const string Hints = "F6: regenerate this seed   F7: new random seed   (set a seed in the MissionDirector Inspector, then F6)";

        public static string Describe(MissionState state, MissionReport report)
        {
            var c = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            text.Append(c, $"Mission: {state} | Seed {report.Seed}");
            if (report.AttemptsMade > 0)
                text.Append(c, $" | attempt {report.AttemptsMade}/{report.MaxAttempts}");
            if (state == MissionState.Ready)
            {
                text.Append(c, $"\n{report.Rooms} rooms, {report.Connections} connections, floor {report.FloorTiles} m2");
                text.Append(c, $"\nNav {report.NavArea:0} m2, {report.PathsChecked} paths ok");
                text.Append(c, $"\nCover {report.CoverTotal} (low {report.CoverLow}, tall {report.CoverTall}, corner {report.CoverCorner})");
                text.Append(c, $"\nSpawns {report.FriendlySpawns} friendly / {report.HostileSpawns} hostile, {report.TeamSeparation:0.0} m apart");
                text.Append(c, $"\nBounds {report.Bounds.size.x:0} x {report.Bounds.size.z:0} m");
            }
            else if (state == MissionState.Failed && !string.IsNullOrEmpty(report.Failure))
            {
                text.Append('\n').Append(report.Failure);
            }
            return text.ToString();
        }
    }

    /// <summary>Debug-only IMGUI text about the generated mission. Not production UI. Works while paused.</summary>
    public sealed class MissionDebugView : MonoBehaviour
    {
        [SerializeField] MissionDirector director;

        GUIStyle style;

        void OnGUI()
        {
            if (director == null)
                return;
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.UpperRight, wordWrap = true };
            var text = MissionDebugText.Describe(director.State, director.Report) + "\n" + MissionDebugText.Hints;
            var width = Mathf.Min(560f, Screen.width * 0.45f);
            var area = new Rect(Screen.width - width - 10f, 10f, width, Screen.height * 0.5f);
            GUI.color = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), text, style);
            GUI.color = Color.white;
            GUI.Label(area, text, style);
        }
    }
}
```

`style ??= ...` is fine: `GUIStyle` is a plain C# class, not a `UnityEngine.Object`. If `StringBuilder.Append(IFormatProvider, ref AppendInterpolatedStringHandler)` is not available in this C# version, build each line with `string.Format(CultureInfo.InvariantCulture, ...)` instead.

`MissionGizmoView.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug drawing of the mission structure: room rectangles, the room graph, spawn regions and bounds. Gizmos only.</summary>
    public sealed class MissionGizmoView : MonoBehaviour
    {
        [SerializeField] MissionDirector director;
        [SerializeField] bool draw = true;

        void OnDrawGizmos()
        {
            if (!draw || director == null || director.Current == null)
                return;
            var layout = director.Current.Layout;
            Gizmos.color = new Color(0.6f, 0.6f, 0.6f);
            foreach (var room in layout.Rooms)
                Rect(layout, room.Rect, 0.05f);
            Gizmos.color = Color.yellow;
            foreach (var connection in layout.Connections)
                Gizmos.DrawLine(layout.RectCenter(layout.Rooms[connection.RoomA].Rect) + Vector3.up * 0.3f,
                    layout.RectCenter(layout.Rooms[connection.RoomB].Rect) + Vector3.up * 0.3f);
            Gizmos.color = Color.green;
            Rect(layout, layout.FriendlyRegion, 0.1f);
            Gizmos.color = Color.red;
            foreach (var region in layout.HostileRegions)
                Rect(layout, region, 0.1f);
            Gizmos.color = Color.cyan;
            var bounds = layout.WorldBounds;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
        }

        static void Rect(MissionLayout layout, RectInt rect, float height)
        {
            var a = layout.ToWorld(rect.xMin, rect.yMin) + Vector3.up * height;
            var b = layout.ToWorld(rect.xMax, rect.yMin) + Vector3.up * height;
            var c = layout.ToWorld(rect.xMax, rect.yMax) + Vector3.up * height;
            var d = layout.ToWorld(rect.xMin, rect.yMax) + Vector3.up * height;
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, a);
        }
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionDebugTextTests"` (4 pass), `... "Blackglass.Tests.InputAssetTests"` (still pass), then `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDeveloperInputTests"` (2 pass).

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Mission Assets/_Project/Input Assets/_Project/Tests
git commit -m "Add developer regeneration keys and the mission debug views

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: The ProceduralMission scene and gameplay-compatibility tests

**Files:**
- Create (temporary, deleted in this task): `Assets/_Project/Editor/MissionSceneBuilder.cs`
- Create: `Assets/_Project/Scenes/ProceduralMission.unity` (by the builder), `Tests/PlayMode/ProceduralMissionSceneTests.cs`
- Modify: `ProjectSettings/EditorBuildSettings.asset` (the builder appends the scene)

**Interfaces:**
- Consumes: Tasks 1-7 and the Phase 6/7 assets (`Prefabs/FriendlyUnit.prefab`, `Prefabs/HostileUnit.prefab`, `Data/Archetypes/*`, `Data/Abilities/*`, `Materials/Ground.mat`, `Materials/Obstacle.mat`).
- Produces: a scene named `ProceduralMission` in the build settings, with persistent systems (everything `Prototype` has except the arena and the units), a `Mission` object holding `MissionDirector`, `MissionDeveloperInput`, `MissionDebugView`, `MissionGizmoView`, and `CoverDiscovery.discoverOnStart = false`.

- [ ] **Step 1: Write the failing scene tests** (`ProceduralMissionSceneTests.cs`). Read `PrototypeSceneAbilityTests.cs` and `PrototypeSceneControllerTests.cs` first: the helpers below are copied from them and the controller test is a port of `Scene_PadCover_CursorSnapsToGeneratedCover_AndMoveToCoverQueues`.

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ProceduralMissionSceneTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        MissionDirector director;
        CommandableUnit[] squad;
        CommandableUnit[] hostiles;
        TacticalPause pause;
        UnitSelection selection;
        Encounter encounter;
        CoverRegistry registry;
        ActiveCharacter active;
        AbilityTargeting targeting;
        TacticalCursor cursor;
        Camera viewCamera;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            Time.timeScale = 1f;
            PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        // The scene's director generates its own seed at start; wait for it, then bind the scene's systems.
        IEnumerator LoadMission()
        {
            yield return SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
            director = Object.FindFirstObjectByType<MissionDirector>();
            Assert.That(director, Is.Not.Null, "The scene has no MissionDirector: run the scene builder");
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), director.Report.Failure);
            Bind();
            yield return null;
        }

        void Bind()
        {
            squad = director.Friendlies.ToArray();
            hostiles = director.Hostiles.ToArray();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            encounter = Object.FindFirstObjectByType<Encounter>();
            registry = Object.FindFirstObjectByType<CoverRegistry>();
            active = Object.FindFirstObjectByType<ActiveCharacter>();
            targeting = Object.FindFirstObjectByType<AbilityTargeting>();
            cursor = Object.FindFirstObjectByType<TacticalCursor>();
            viewCamera = Camera.main;
        }

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        // Stands a hostile `distance` metres from the caster on open ground in sight of it, with its AI off.
        Health BringHostileNear(CommandableUnit hostile, CommandableUnit caster, float distance)
        {
            hostile.GetComponent<EnemyAI>().enabled = false;
            hostile.Issue(new StopCommand());
            var attacker = caster.GetComponent<UnitAttacker>();
            var ground = caster.transform.position - Vector3.up;
            for (var step = 0; step < 16; step++)
            {
                var angle = step * Mathf.PI * 2f / 16f;
                var candidate = ground + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
                if (!NavMesh.SamplePosition(candidate, out var hit, 0.5f, NavMesh.AllAreas))
                    continue;
                if (!attacker.HasLineOfSightToPoint(hit.position + Vector3.up))
                    continue;
                hostile.GetComponent<NavMeshAgent>().Warp(hit.position);
                return HealthOf(hostile);
            }
            Assert.Fail($"No open, visible spot {distance} m from {caster.name}");
            return null;
        }

        [UnityTest]
        public IEnumerator Scene_GeneratesAMissionOnLoad_WithTheWholeSquadAndHostiles()
        {
            yield return LoadMission();

            Assert.That(squad, Has.Length.EqualTo(3));
            Assert.That(hostiles, Has.Length.EqualTo(3));
            Assert.That(registry.Points.Any(p => p.Placement == CoverPlacement.Corner), Is.True);
            Assert.That(registry.Points.Any(p => p.Height == CoverHeight.Low), Is.True);
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(active.Unit, Is.EqualTo(squad[0]));
        }

        [UnityTest]
        public IEnumerator Scene_Move_ThenAttack_ThenTheHostileDies()
        {
            yield return LoadMission();
            var hostile = BringHostileNear(hostiles[0], squad[0], 6f);
            var destination = squad[0].transform.position - Vector3.up;
            Assert.That(squad[1].Issue(new MoveCommand(destination + new Vector3(1.5f, 0f, 0f))), Is.True);
            yield return TestWorld.WaitUntil(() => squad[1].CurrentCommand == null, 10f);
            Assert.That(squad[1].CurrentCommand, Is.Null, "the move order ended");

            Assert.That(squad[0].Issue(new AttackCommand(hostile)), Is.True);
            yield return TestWorld.WaitUntil(() => !hostile.IsAlive, 20f);

            Assert.That(hostile.IsAlive, Is.False);
            Assert.That(encounter.LivingHostiles, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Scene_MoveToCover_ReachesAGeneratedLocation_AndOccupiesIt()
        {
            yield return LoadMission();
            var unit = squad[0];
            var location = registry.Points.Where(p => p.Height == CoverHeight.Low)
                .OrderBy(p => TestWorld.HorizontalDistance(p.Position, unit.transform.position)).First();

            Assert.That(unit.Issue(new MoveToCoverCommand(location)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.Cover.Status == CoverStatus.Occupied, 15f);

            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(unit.Cover.Point, Is.EqualTo(location));
        }

        [UnityTest]
        public IEnumerator Scene_Abilities_AimedShotHitsInRange_BlastHitsTheGround_MendHeals_AndRangeAndCooldownApply()
        {
            yield return LoadMission();
            var caster = squad[1];   // the Marksman
            var abilities = caster.GetComponent<UnitAbilities>();
            var hostile = BringHostileNear(hostiles[0], caster, 9f);
            var before = hostile.Current;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(abilities.Definition(0), hostile)), Is.True);
            yield return TestWorld.WaitUntil(() => hostile.Current < before, 3f);
            Assert.That(hostile.Current, Is.LessThan(before), "single-target ability");
            Assert.That(abilities.IsReady(0), Is.False, "cooldown starts");

            var second = BringHostileNear(hostiles[1], caster, 8f);
            var secondBefore = second.Current;
            var friendlyBefore = squad.Select(u => HealthOf(u).Current).ToArray();
            Assert.That(caster.Issue(AbilityCommand.AtGround(abilities.Definition(1), second.transform.position)), Is.True);
            yield return TestWorld.WaitUntil(() => second.Current < secondBefore, 3f);
            Assert.That(second.Current, Is.LessThan(secondBefore), "ground-targeted ability");
            Assert.That(squad.Select(u => HealthOf(u).Current).ToArray(), Is.EqualTo(friendlyBefore), "no friendly fire");

            HealthOf(squad[2]).TakeDamage(50);
            var hurt = HealthOf(squad[2]).Current;
            Assert.That(caster.Issue(AbilityCommand.OnUnit(abilities.Definition(2), HealthOf(squad[2]))), Is.True,
                "the Marksman stands within Mend range of the squad");
            yield return TestWorld.WaitUntil(() => HealthOf(squad[2]).Current > hurt, 3f);

            var far = BringHostileNear(hostiles[2], caster, 17f);
            Assert.That(abilities.Check(abilities.Definition(0), far, null).IsValid, Is.False, "out of range or on cooldown is refused");
        }

        [UnityTest]
        public IEnumerator Scene_TacticalPause_FreezesSimulation_PlansOrders_AndResumesThem()
        {
            yield return LoadMission();
            var unit = squad[0];
            var start = unit.transform.position;
            pause.Pause();
            Assert.That(unit.Issue(new MoveCommand(start - Vector3.up + new Vector3(2f, 0f, 0f))), Is.True);
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(Vector3.Distance(unit.transform.position, start), Is.LessThan(0.05f), "frozen while paused");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => Vector3.Distance(unit.transform.position, start) > 1f, 5f);
            Assert.That(Vector3.Distance(unit.transform.position, start), Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator Scene_EnemyAI_AcquiresAFriendlyInSight_AndTheCompanionsFollow()
        {
            yield return LoadMission();
            var hostile = hostiles[0];
            var warp = squad[0].transform.position - Vector3.up;
            NavMesh.SamplePosition(warp + new Vector3(6f, 0f, 0f), out var near, 4f, NavMesh.AllAreas);
            hostile.GetComponent<NavMeshAgent>().Warp(near.position);
            yield return TestWorld.WaitUntil(() => hostile.CurrentCommand is AttackCommand || hostile.CurrentCommand is MoveToCoverCommand, 5f);
            Assert.That(hostile.CurrentCommand, Is.Not.Null, "a hostile within detection range and sight acts");

            active.SetUnit(squad[0]);
            var far = squad[2].transform.position;
            squad[0].Issue(new MoveCommand(squad[0].transform.position - Vector3.up + new Vector3(0f, 0f, -8f)));
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(squad[2].transform.position, far) > 1f, 8f);
            Assert.That(TestWorld.HorizontalDistance(squad[2].transform.position, far), Is.GreaterThan(1f), "a companion moves with the leader");
        }

        [UnityTest]
        public IEnumerator Scene_VictoryAndDefeat_Resolve()
        {
            yield return LoadMission();
            foreach (var hostile in hostiles)
                HealthOf(hostile).TakeDamage(1000);
            yield return null;
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory));

            yield return LoadMission();
            foreach (var friendly in squad)
                HealthOf(friendly).TakeDamage(1000);
            yield return null;
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Defeat));
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardF6_Regenerates_AndLeavesNoStaleUnitsOrCover()
        {
            yield return LoadMission();
            var oldUnits = squad.Concat(hostiles).ToArray();
            var oldLocations = registry.Points.ToList();
            var hash = director.Report.LayoutHash;

            Press(keyboard.f6Key);
            yield return null;
            Release(keyboard.f6Key);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready, 20f);
            yield return null;

            Assert.That(director.Report.LayoutHash, Is.EqualTo(hash), "same seed, same layout");
            Assert.That(oldUnits.All(u => u == null), Is.True);
            Assert.That(oldLocations.All(l => !l.IsValid), Is.True);
            Assert.That(Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None), Has.Length.EqualTo(3));
            Assert.That(Object.FindObjectsByType<SelectableUnit>(FindObjectsSortMode.None), Has.Length.EqualTo(3));
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardAndMouse_StillOrderTheSelectedUnit()
        {
            yield return LoadMission();
            selection.Select(squad[0].GetComponent<SelectableUnit>());
            var ground = squad[0].transform.position - Vector3.up;
            NavMesh.SamplePosition(ground + new Vector3(3f, 0f, 0f), out var target, 2f, NavMesh.AllAreas);
            var screen = viewCamera.WorldToScreenPoint(target.position);
            Set(mouse.position, screen);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;

            Assert.That(squad[0].CurrentCommand is MoveCommand || squad[0].CurrentCommand is MoveToCoverCommand, Is.True);
        }
    }
}
#endif
```

The controller test goes in the same file; add it after reading the `Scene_PadCover_...` test in `PrototypeSceneControllerTests.cs` (lines 290-335) and port it: same pad device, same stick/cursor calls, but pick the target location from the generated registry instead of the arena's hard-coded point, and bring the cursor to it by placing the active character next to a Low location with `NavMeshAgent.Warp` (the cursor snaps to cover within range of the character). It asserts: the cursor snaps to a `CoverLocation`; Confirm while paused queues a `MoveToCoverCommand`; an armed ability (`Ability` trigger+D-pad chord, as in `Scene_ThePad_TriggerPlusDpad_ArmsMend_...`) snaps to a friendly; cycling targets (`NextTarget`) snaps to a hostile; the right stick still moves the camera while running. Name it `Scene_Pad_CursorSnapsToGeneratedCoverEnemiesAndFriendlies_AndCameraStaysUsable`. This is the tenth test of the task (the table counts it).

`UnitAttacker.HasLineOfSightToPoint(Vector3)` and `UnitAbilities.Check(definition, target, point).IsValid` exist as used above. Ability orders are built with `AbilityCommand.OnUnit(definition, health)` and `AbilityCommand.AtGround(definition, point)`.

- [ ] **Step 2: Run to verify they fail**

Run `Tools/run-tests.sh PlayMode "Blackglass.Tests.ProceduralMissionSceneTests"`. Expected: tests fail (scene `ProceduralMission` not in the build settings: `Scene 'ProceduralMission' couldn't be loaded`).

- [ ] **Step 3: Write the temporary scene builder**

`Assets/_Project/Editor/MissionSceneBuilder.cs` (delete in Step 6; copy the `Reference`, `Find`, `GetOrAdd`, `Set`, `SetList` helpers from the Phase 7 plan's `AbilitySceneBuilder` (`Docs/superpowers/plans/2026-10-06-abilities.md`, Task 10), and add the two below):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Blackglass;

public static class MissionSceneBuilder
{
    const string Source = "Assets/_Project/Scenes/Prototype.unity";
    const string Target = "Assets/_Project/Scenes/ProceduralMission.unity";
    const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

    static readonly HashSet<string> Arena = new HashSet<string>
    {
        "Environment", "Ground", "Obstacle_A", "Obstacle_B", "Obstacle_C", "Obstacle_D", "Obstacle_E", "Obstacle_F",
        "Obstacle_CentralWall", "Pillar_G", "Pillar_H", "Barrier_I", "Crate_J", "Crate_K", "LowWall_L", "LowWall_M", "LowWall_N",
        "FriendlyUnit_1", "FriendlyUnit_2", "FriendlyUnit_3", "HostileUnit_1", "HostileUnit_2", "HostileUnit_3",
    };

    // ... Reference / Find / GetOrAdd / Set / SetList copied from AbilitySceneBuilder ...

    static T Load<T>(string path) where T : UnityEngine.Object
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset == null)
            throw new Exception($"Missing asset {path}");
        return asset;
    }

    static void SetPath(Component component, string path, UnityEngine.Object value)
    {
        var so = new SerializedObject(component);
        var property = so.FindProperty(path);
        if (property == null)
            throw new Exception($"{component.GetType().Name} has no serialized path '{path}'");
        property.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    public static void Build()
    {
        AssetDatabase.DeleteAsset(Target);
        if (!AssetDatabase.CopyAsset(Source, Target))
            throw new Exception("Copying the Prototype scene failed");
        var scene = EditorSceneManager.OpenScene(Target, OpenSceneMode.Single);

        // Remove the hand-built arena and units wherever they sit in the hierarchy.
        var doomed = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .Where(t => Arena.Contains(t.name)).Select(t => t.gameObject).Distinct().ToList();
        foreach (var go in doomed)
            if (go != null)
                UnityEngine.Object.DestroyImmediate(go);
        Debug.Log("[MissionSceneBuilder] Remaining roots: " + string.Join(", ", scene.GetRootGameObjects().Select(g => g.name)));

        // Empty the lists that pointed at the removed units.
        var encounter = Find<Encounter>();
        SetList(encounter, "friendlies");
        SetList(encounter, "hostiles");
        SetList(Find<UnitSelection>(), "roster");
        Set(Find<ActiveCharacter>(), "unit", null);
        var discovery = Find<CoverDiscovery>();
        var discoverySo = new SerializedObject(discovery);
        discoverySo.FindProperty("discoverOnStart").boolValue = false;
        discoverySo.ApplyModifiedPropertiesWithoutUndo();

        var mission = new GameObject("Mission");
        var director = mission.AddComponent<MissionDirector>();
        var developer = mission.AddComponent<MissionDeveloperInput>();
        var debugView = mission.AddComponent<MissionDebugView>();
        var gizmos = mission.AddComponent<MissionGizmoView>();

        // Systems the director fills.
        SetPath(director, "systems.encounter", encounter);
        SetPath(director, "systems.selection", Find<UnitSelection>());
        SetPath(director, "systems.activeCharacter", Find<ActiveCharacter>());
        SetPath(director, "systems.coverRegistry", Find<CoverRegistry>());
        SetPath(director, "systems.coverDiscovery", discovery);
        SetPath(director, "systems.pause", Find<TacticalPause>());
        SetPath(director, "systems.camera", Find<TacticalCameraController>());
        SetPath(director, "systems.abilityTargeting", Find<AbilityTargeting>());
        SetPath(director, "groundMaterial", Load<Material>("Assets/_Project/Materials/Ground.mat"));
        SetPath(director, "obstacleMaterial", Load<Material>("Assets/_Project/Materials/Obstacle.mat"));

        var friendlyPrefab = Load<GameObject>("Assets/_Project/Prefabs/FriendlyUnit.prefab");
        var hostilePrefab = Load<GameObject>("Assets/_Project/Prefabs/HostileUnit.prefab");
        var abilities = new UnityEngine.Object[]
        {
            Load<AbilityDefinition>("Assets/_Project/Data/Abilities/AimedShot.asset"),
            Load<AbilityDefinition>("Assets/_Project/Data/Abilities/Blast.asset"),
            Load<AbilityDefinition>("Assets/_Project/Data/Abilities/Mend.asset"),
        };
        var friendlyArchetypes = new[] { "Melee", "Marksman", "Ranged" };
        var so = new SerializedObject(director);
        var friendly = so.FindProperty("friendlySlots");
        friendly.arraySize = 3;
        for (var i = 0; i < 3; i++)
        {
            var slot = friendly.GetArrayElementAtIndex(i);
            slot.FindPropertyRelative("prefab").objectReferenceValue = friendlyPrefab;
            slot.FindPropertyRelative("archetype").objectReferenceValue =
                Load<CombatArchetype>($"Assets/_Project/Data/Archetypes/{friendlyArchetypes[i]}.asset");
            var list = slot.FindPropertyRelative("abilities");
            list.arraySize = abilities.Length;
            for (var a = 0; a < abilities.Length; a++)
                list.GetArrayElementAtIndex(a).objectReferenceValue = abilities[a];
        }
        var hostile = so.FindProperty("hostileSlots");
        hostile.arraySize = 3;
        for (var i = 0; i < 3; i++)
        {
            var slot = hostile.GetArrayElementAtIndex(i);
            slot.FindPropertyRelative("prefab").objectReferenceValue = hostilePrefab;
            // Slot 1 is a ranged hostile (cover use); slots 0 and 2 keep the prefab's own melee stats.
            slot.FindPropertyRelative("archetype").objectReferenceValue =
                i == 1 ? Load<CombatArchetype>("Assets/_Project/Data/Archetypes/Ranged.asset") : null;
        }
        so.ApplyModifiedPropertiesWithoutUndo();

        Set(developer, "director", director);
        Set(developer, "regenerateSameAction", Reference("Developer/RegenerateSame"));
        Set(developer, "regenerateNewAction", Reference("Developer/RegenerateNew"));
        Set(debugView, "director", director);
        Set(gizmos, "director", director);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Saving the scene failed");

        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.All(s => s.path != Target))
            scenes.Add(new EditorBuildSettingsScene(Target, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log($"[MissionSceneBuilder] Built {Target}");
    }
}
```

`Set(component, field, null)` must accept a null value (the copied helper assigns `objectReferenceValue = value`, which is fine with null).

- [ ] **Step 4: Run the builder (Editor closed) and inspect the result**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod MissionSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildMissionScene.log"; echo "EXIT=$?"
grep -E '\[MissionSceneBuilder\]|error CS|Exception' Logs/BuildMissionScene.log | head -20
git status --short
```
Expected: `EXIT=0`, the `Remaining roots:` line lists only persistent roots (the light, `Systems`, `CameraRig`, the marker object; no obstacle or unit), the `Built ...` line, no exception. `git status` shows the new scene (+ `.meta`), the builder (to be deleted), `ProjectSettings/EditorBuildSettings.asset` modified, and **`Prototype.unity` unchanged**. If any arena object name remains, add it to `Arena` and rerun.

- [ ] **Step 5: Run the scene tests, fix what they find**

Run `Tools/run-tests.sh PlayMode "Blackglass.Tests.ProceduralMissionSceneTests"`. Expected: all pass. A failing gameplay assertion is a finding, not a test to loosen: reproduce it with the failing seed, and say in the commit message what was fixed. Known risks to check first: `BringHostileNear` finds no spot (try more angles), the controller test needs the cursor `Aiming`/`SnapTo` calls exactly as in the Prototype port, and `Scene_VictoryAndDefeat_Resolve` loads the scene twice (a second `LoadMission` replaces the first; the first teardown must not leak).

- [ ] **Step 6: Delete the builder, run both full suites, commit**

```bash
rm Assets/_Project/Editor/MissionSceneBuilder.cs Assets/_Project/Editor/MissionSceneBuilder.cs.meta
rmdir Assets/_Project/Editor 2>/dev/null; rm -f Assets/_Project/Editor.meta
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
git add Assets/_Project/Scenes Assets/_Project/Tests ProjectSettings/EditorBuildSettings.asset
git commit -m "Add the ProceduralMission scene and test generated missions with every existing system

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```
Expected: both suites `EXIT=0` at the table's task-8 row; `git status` clean except `.claude/`.

---

### Task 9: Decision record, spec corrections and the completion report

**Files:**
- Modify: `Docs/Decisions.md` (append record 026), `Docs/superpowers/specs/2026-10-06-procedural-mission-design.md` (apply the "Plan notes" corrections and mark it implemented)
- No code.

- [ ] **Step 1: Mark the spec implemented.** The plan's deviations were already folded into the spec when the plan was written; change only its status line to `Status: implemented on branch procedural-mission (see Docs/Decisions.md record 026)`, and add to it anything the implementation changed since (check the git log of the branch).

- [ ] **Step 2: Append decision record 026** (same format as 023/025: Decided, Why, Rejected, Implications, Known limitations). Content: the pipeline and ownership (`MissionDirector`, root/Geometry/Actors), SplitMix64 + per-attempt sub-seeds + integer tile layout + the golden hashes, grid-rooms-and-corridors model and the wall-ring rule, the two validation levels and the bounded retry, `CoverDiscovery.Discover(root, reachable)` and no second cover system, spawn rules, the regeneration lifecycle (including the death-marker finding), the new-scene decision, developer controls (F6/F7, seed in the Inspector, why no clickable buttons), the camera additions, the rejected alternatives from spec §12, and known limitations from spec §13. Record the measured test totals and the retained seeds.

- [ ] **Step 3: Collect the retained seeds.** Run a throwaway EditMode loop (a temporary test, not committed) over seeds 1..200 with the default settings and record, for the completion report: seeds that needed more than 1 attempt (the retry path), the seeds with the most `Baffle` boxes, the most `LowWall`/`Crate` boxes and the most wall-end corners, and the five seeds with the smallest team separation. Note them in record 026 as regression seeds (12345, 1 and 2 are already pinned by golden hashes).

- [ ] **Step 4: Final verification** (superpowers:verification-before-completion). Run `Tools/run-tests.sh EditMode` and `Tools/run-tests.sh PlayMode`; both `EXIT=0` at the table's final row. Open the Editor once and play `ProceduralMission` for a minute: the Console shows no error or warning beyond the known OS-version line; press F6 and F7 a few times.

- [ ] **Step 5: Commit**

```bash
git add Docs
git commit -m "Record decision 026: procedural missions

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 6: Write the completion report** in the format the owner asked for (files created and changed, algorithm, seed handling, deterministic random strategy, layout representation, connectivity validation, geometry strategy, runtime navigation, cover integration, spawn placement, regeneration lifecycle, bounds, configuration, the manual test sequence below, known limitations, retained seeds, concerns before Phase 9). **Do not begin Phase 9.**

Manual test sequence to include in the report:
1. Play `ProceduralMission`; read the seed in the top-right panel; press F6: same layout (same room shapes, same cover count in the panel).
2. Press F7 five times: five different layouts; each time check the Console is clean, the squad is on screen, hostiles stand in far rooms.
3. Set `Seed` in the `Mission` object's `MissionDirector` Inspector to a seed from the retained list (many baffles) and press F6: look for tall-wall ends with corner markers in the cover view while paused (Space).
4. Same with a low-cover-heavy seed.
5. Mouse and keyboard: select (click, box select), move, attack, queue with Shift, MoveToCover by clicking a marker, abilities 1-4, Space pause and plan, Tab switch, F follow, V takeover.
6. Controller (DualSense or Xbox): cursor snapping to cover, enemies and friendlies; Confirm to order; R2 + D-pad abilities with cursor aiming; right stick camera; pause and plan.
7. Regenerate during combat, while paused, with an ability armed and orders queued: nothing stale (no old enemies, markers or cover claims).
8. Play to Victory and to Defeat on two different seeds, then F6 again.
9. Break it on purpose: set `Min Team Separation` to 60 and `Max Attempts` to 3 in the Inspector, press F6: one clear error with the seed and settings, no units, no frozen game; restore the values.

## Plan self-review (done at writing time)

- **Spec coverage:** §2 architecture and ownership: Tasks 5, 6; §3 seed/determinism: Tasks 1, 3 (RNG, hash, isolation test, attempt purity); §4 layout, connectivity, bounded retry: Tasks 2, 3; §5 geometry/navigation/validation: Task 5; §6 cover: Tasks 4, 5; §7 spawn/wiring: Tasks 4, 6; §8 lifecycle, failure, camera, developer input: Tasks 4, 6, 7; §9 debug: Task 7; §10 configuration: Task 1; §11 tests: every task. Owner validation items: 1-5 (Tasks 2, 3, 6); 6-10 (Tasks 3, 5, 6); 11-16 (Task 5); 17-18 and 33 (Task 6); 19-28 and 31-32 (Task 8); 29-30 (Task 8 controller test plus manual); 34 (Tasks 4, 6 and manual); 35 (Task 6 failure test); 36 (every PlayMode test fails on an unexpected Console error).
- **Type consistency:** `MissionGenerator.TryAttempt(MissionSettings, int, out MissionLayout, out string)` (Tasks 2, 3, 6); `CoverDiscovery.Discover(Transform, Func<Vector3,bool>)` (Tasks 4, 5, 6); `MissionNavigation.ReachableFrom(Vector3)` and `Validate(layout, out reason, out report)` (Tasks 5, 6); `MissionDirector.Generate/RegenerateSame/GenerateNew/Clear/Initialize` (Tasks 6, 7, 8); `GeneratedMission.RootName/Geometry/Actors/Layout/Root` (Tasks 5, 6, 8); `MissionReport` fields (Tasks 6, 7).
- **Placeholders:** the golden hashes (Task 3) and the retained seeds (Task 9) are measured values with an explicit procedure; the controller scene test is specified as a port of a named existing test with its assertions listed. Test counts in the totals table are expectations to be corrected from real runs.
