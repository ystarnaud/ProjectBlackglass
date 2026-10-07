# Phase 9.5B Environment Visual Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put a swappable visual-module layer between the procedural mission layout and the scene, with one prototype theme (about 14 primitive prefabs), without changing navigation, cover, LOS, objective or AI behaviour.

**Architecture:** A pure `EnvironmentVisualPlanner` turns a `MissionLayout` into semantic `VisualPlacement`s (element, position, yaw, tile). An `EnvironmentTheme` ScriptableObject maps each `EnvironmentElement` to prefab variants; a stateless hash picks the variant. `EnvironmentVisualBuilder` instantiates them under `GeneratedMissionRoot/Visuals`, stripping any collider. The existing cube objects under `Geometry` stay the only colliders / `CoverSurface` / `NavMeshModifier` carriers; with a theme their renderers are disabled.

**Tech Stack:** Unity 6000.3.25f1, URP, C#, NUnit via `Tools/run-tests.sh` (Unity Editor must be closed).

**Spec:** `docs/superpowers/specs/2026-10-07-environment-visuals-design.md`

## Global Constraints

- Scale stays: 1 unit = 1 m, tile = 1 m, wall height 3 m (`MissionConstants.WallHeight`), low cover 1 m (`LowHeight`), floor 0.2 m thick (`FloorThickness`). Nothing is rescaled.
- Do not change the render pipeline (URP). Do not install packages. No new gameplay input (the only new action is the developer-only F8 visuals toggle in the existing `Developer` map).
- Layout/objective randomness is untouched: no new draws from `SeededRandom` layout or objective streams; `MissionLayout.Hash` and `ObjectivePlan` for a seed must not change.
- Visual prefabs carry **no colliders** (the builder strips any). Gameplay colliders live on the generated gameplay objects.
- Never use `??` / `?.` on `UnityEngine.Object` references (fake-null); use `TryGetComponent` / `== null`.
- Commit messages end with: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Do not commit the pre-existing dirty files `Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset`, or `.claude/`. Stage only the files each task names.
- Orientation convention (used by the planner and every prefab): world +Z is "north" = tile y+1, +X = east = tile x+1. A module's yaw rotates it clockwise seen from above: yaw 0/90/180/270 maps authored +Z to N/E/S/W.

## Review Focus

- A theme with a missing element (partial/third-party theme) must still build: magenta placeholder plus one warning per element (Task 3).
- An imported prefab that ships its own colliders must not change NavMesh area or cover locations (Task 3 strips them; Task 4 parity test uses a collider-bearing theme).
- A theme entry with an empty or null `variants` array counts as missing, never an exception (Task 2).
- `theme == null` (old tests, old scenes) must behave exactly as before: visible cubes, no `Visuals` root (Task 4).
- Debug "hide visuals" state must survive regeneration and leave no stale objects after repeated regenerations (Task 6).
- A terminal visual with no child named `Display` is tinted on all its renderers instead of failing (Task 5).

## File Structure

New, under `Assets/_Project/Scripts/Environment/` (same `Blackglass` assembly):
- `EnvironmentElement.cs`: the semantic enum.
- `VisualPlacement.cs`: plain data struct.
- `EnvironmentVisualPlanner.cs`: pure layout -> placements.
- `VisualVariants.cs`: stateless deterministic hash/pick.
- `EnvironmentTheme.cs`: ScriptableObject element -> variants.
- `EnvironmentVisualBuilder.cs`: instantiates placements, strips colliders, placeholder fallback.

Changed: `MissionBuilder.cs`, `MissionContent.cs`, `ObjectiveMarker.cs`, `MissionDirector.cs`, `MissionDeveloperInput.cs`, `Input/BlackglassControls.inputactions`, `Tests/PlayMode/TestSupport/MissionRig.cs`, `Tests/PlayMode/ObjectiveMarkerPlayModeTests.cs` (+ any test the full run shows broken by the terminal restructure).

New editor tool: `Assets/_Project/Editor/EnvironmentKitBuilder.cs` (menu `Blackglass/Environment/...`). New assets: `Assets/_Project/Environment/{Materials,Prefabs,Themes/CorporatePrototype}`. New docs: `Docs/EnvironmentAssetGuide.md`, decision 035 in `docs/Decisions.md`.

Tests new: `Tests/EditMode/EnvironmentVisualPlannerTests.cs`, `VisualVariantsTests.cs`, `EnvironmentThemeTests.cs`; `Tests/PlayMode/EnvironmentVisualsPlayModeTests.cs`, `TestSupport/TestTheme.cs`.

**Test running:** the Unity Editor must be closed. `Tools/run-tests.sh EditMode [filter]` / `PlayMode [filter]`; exit 0 = pass, 2 = failures, 1 = compile error. New `.cs` files need their `.meta` files committed: Unity creates them on the next run, so `git add` the `.meta` alongside each file after a run.

---

### Task 0: Branch and baseline

**Files:** none.

- [ ] **Step 1: Create the branch**

```bash
cd /f/Programs/ProjectBlackglass
git checkout -b phase-9.5b-environment-visuals
```

- [ ] **Step 2: Measure the baseline (do not trust remembered totals)**

Run: `Tools/run-tests.sh EditMode` then `Tools/run-tests.sh PlayMode`
Expected: EXIT=0 for both. Write down `total=` / `passed=` for each in your report as BASE_EDIT and BASE_PLAY. If anything fails at baseline, stop and report.

---

### Task 1: Semantic types and the pure planner

**Files:**
- Create: `Assets/_Project/Scripts/Environment/EnvironmentElement.cs`
- Create: `Assets/_Project/Scripts/Environment/VisualPlacement.cs`
- Create: `Assets/_Project/Scripts/Environment/VisualVariants.cs` (hash only; `Pick` added in Task 2)
- Create: `Assets/_Project/Scripts/Environment/EnvironmentVisualPlanner.cs`
- Test: `Assets/_Project/Tests/EditMode/EnvironmentVisualPlannerTests.cs`

**Interfaces:**
- Produces: `enum EnvironmentElement { Floor, WallStraight, WallCorner, WallEnd, WallJunction, DoorFrame, LowCover, LowCoverLong, Pillar, Crate, Terminal, LightFixture }`
- Produces: `readonly struct VisualPlacement { EnvironmentElement Element; Vector3 Position; float YawDegrees; Vector2Int Tile; }`
- Produces: `static List<VisualPlacement> EnvironmentVisualPlanner.Plan(MissionLayout layout)`
- Produces: `static ulong VisualVariants.Hash(int seed, int salt, EnvironmentElement element, int x, int y)`
- Consumes: `MissionLayout` (`Boxes`, `Width`, `Height`, `Seed`, `IsFloor`, `TileCenter`, `ToWorld`, `FloorTileCount`), `MissionBox`, `MissionBoxKind`.

Planner rules (all positions y = 0, tile centres via `layout.TileCenter`):
- Floor: one per floor tile, yaw 0.
- Wall mask = tiles of every `Wall` and `Baffle` box (union). Neighbour bits N=1 (y+1), E=2 (x+1), S=4 (y-1), W=8 (x-1). 0 neighbours -> `Pillar`; 1 -> `WallEnd`, yaw = direction index (N0 E1 S2 W3) x 90 toward the neighbour; 2 opposite -> `WallStraight` (yaw 0 for E+W, 90 for N+S); 2 adjacent -> `WallCorner` (N+E 0, E+S 90, S+W 180, W+N 270); 3 -> `WallJunction`, yaw = (missingDirIndex x 90 + 180) % 360; 4 -> `WallJunction` yaw 0.
- A `WallEnd` whose tile belongs to a `Wall` box (the ring, not a `Baffle`) also yields a `DoorFrame` at the same position and yaw.
- `Pillar` box tile -> `Pillar`. `Crate` box: one `Crate` per tile.
- `LowWall` box: if both dimensions > 1, one `LowCover` per tile. Otherwise along the longer axis (X when width >= height, yaw 0; else yaw 90): pairs of tiles become one `LowCoverLong` centred between the two tiles, then the remainder single `LowCover` tiles.
- `WallStraight` tiles with exactly one perpendicular side being a floor tile, and `VisualVariants.Hash(layout.Seed, 0, LightFixture, x, y) % 4 == 0`, also yield a `LightFixture` at the tile, yaw = index of that floor side x 90 (the fixture is authored on its +Z face).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class EnvironmentVisualPlannerTests
    {
        static MissionBox Box(MissionBoxKind kind, int x, int y, int w, int h, float height = 3f) =>
            new MissionBox($"{kind}_{x}_{y}", kind, new RectInt(x, y, w, h), height);

        // A width x height grid; floorMask decides which tiles are floor (default: all).
        static MissionLayout Layout(int width, int height, System.Func<int, int, bool> floorMask, params MissionBox[] boxes)
        {
            var floor = new bool[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    floor[y * width + x] = floorMask == null || floorMask(x, y);
            return new MissionLayout(1, 0, width, height, floor) { Boxes = boxes };
        }

        static VisualPlacement At(MissionLayout layout, EnvironmentElement element, int x, int y) =>
            EnvironmentVisualPlanner.Plan(layout).Single(p => p.Element == element && p.Tile == new Vector2Int(x, y));

        [Test]
        public void StraightRun_EastWest_HasEndsFacingTheNeighbour_AndAStraightMiddle()
        {
            var layout = Layout(7, 7, null, Box(MissionBoxKind.Wall, 2, 3, 3, 1));

            Assert.That(At(layout, EnvironmentElement.WallEnd, 2, 3).YawDegrees, Is.EqualTo(90f), "neighbour to the east");
            Assert.That(At(layout, EnvironmentElement.WallEnd, 4, 3).YawDegrees, Is.EqualTo(270f), "neighbour to the west");
            Assert.That(At(layout, EnvironmentElement.WallStraight, 3, 3).YawDegrees, Is.EqualTo(0f));
            Assert.That(At(layout, EnvironmentElement.WallStraight, 3, 3).Position, Is.EqualTo(layout.TileCenter(new Vector2Int(3, 3))));
        }

        [Test]
        public void StraightRun_NorthSouth_IsRotatedNinetyDegrees()
        {
            var layout = Layout(7, 7, null, Box(MissionBoxKind.Wall, 3, 2, 1, 3));

            Assert.That(At(layout, EnvironmentElement.WallStraight, 3, 3).YawDegrees, Is.EqualTo(90f));
            Assert.That(At(layout, EnvironmentElement.WallEnd, 3, 2).YawDegrees, Is.EqualTo(0f), "neighbour to the north");
            Assert.That(At(layout, EnvironmentElement.WallEnd, 3, 4).YawDegrees, Is.EqualTo(180f), "neighbour to the south");
        }

        [TestCase(1, 1, 0f)]     // L opening to north + east
        [TestCase(1, 3, 90f)]    // east + south
        [TestCase(3, 3, 180f)]   // south + west
        [TestCase(3, 1, 270f)]   // west + north
        public void Corners_TakeTheYawOfTheirTwoNeighbours(int cornerX, int cornerY, float yaw)
        {
            var layout = Layout(7, 7, null,
                Box(MissionBoxKind.Wall, 1, cornerY, 3, 1),
                Box(MissionBoxKind.Wall, cornerX, 1, 1, 3));

            Assert.That(At(layout, EnvironmentElement.WallCorner, cornerX, cornerY).YawDegrees, Is.EqualTo(yaw));
        }

        [Test]
        public void TJunction_PointsItsMissingSide_AndACrossIsAJunctionToo()
        {
            var tee = Layout(7, 7, null, Box(MissionBoxKind.Wall, 1, 3, 5, 1), Box(MissionBoxKind.Wall, 3, 1, 1, 3));
            Assert.That(At(tee, EnvironmentElement.WallJunction, 3, 3).YawDegrees, Is.EqualTo(180f), "missing north");

            var cross = Layout(7, 7, null, Box(MissionBoxKind.Wall, 1, 3, 5, 1), Box(MissionBoxKind.Wall, 3, 1, 1, 5));
            Assert.That(At(cross, EnvironmentElement.WallJunction, 3, 3).YawDegrees, Is.EqualTo(0f));
        }

        [Test]
        public void AnIsolatedWallTile_AndAPillarBox_AreBothPillars()
        {
            var layout = Layout(7, 7, null, Box(MissionBoxKind.Wall, 1, 1, 1, 1), Box(MissionBoxKind.Pillar, 4, 4, 1, 1));

            Assert.That(At(layout, EnvironmentElement.Pillar, 1, 1), Is.Not.Null);
            Assert.That(At(layout, EnvironmentElement.Pillar, 4, 4).Position, Is.EqualTo(layout.TileCenter(new Vector2Int(4, 4))));
        }

        [Test]
        public void DoorFrames_AppearOnlyOnRingWallEnds_NotBaffleEnds()
        {
            var ring = Layout(9, 9, null, Box(MissionBoxKind.Wall, 1, 1, 3, 1));
            var baffle = Layout(9, 9, null, Box(MissionBoxKind.Baffle, 1, 1, 3, 1));

            Assert.That(EnvironmentVisualPlanner.Plan(ring).Count(p => p.Element == EnvironmentElement.DoorFrame), Is.EqualTo(2));
            Assert.That(EnvironmentVisualPlanner.Plan(baffle).Count(p => p.Element == EnvironmentElement.DoorFrame), Is.EqualTo(0));
            Assert.That(EnvironmentVisualPlanner.Plan(baffle).Count(p => p.Element == EnvironmentElement.WallEnd), Is.EqualTo(2));
        }

        [Test]
        public void ALowWallOfThree_IsOneLongModuleAndOneShort_AlongItsAxis()
        {
            var east = Layout(9, 9, null, Box(MissionBoxKind.LowWall, 2, 2, 3, 1, 1f));
            var placements = EnvironmentVisualPlanner.Plan(east);
            var longModule = placements.Single(p => p.Element == EnvironmentElement.LowCoverLong);
            var shortModule = placements.Single(p => p.Element == EnvironmentElement.LowCover);
            Assert.That(longModule.Position, Is.EqualTo(east.ToWorld(3f, 2.5f)), "centred between tiles 2 and 3");
            Assert.That(longModule.YawDegrees, Is.EqualTo(0f));
            Assert.That(shortModule.Tile, Is.EqualTo(new Vector2Int(4, 2)));

            var north = Layout(9, 9, null, Box(MissionBoxKind.LowWall, 2, 2, 1, 3, 1f));
            var vertical = EnvironmentVisualPlanner.Plan(north).Single(p => p.Element == EnvironmentElement.LowCoverLong);
            Assert.That(vertical.YawDegrees, Is.EqualTo(90f));
            Assert.That(vertical.Position, Is.EqualTo(north.ToWorld(2.5f, 3f)));
        }

        [Test]
        public void ACrate_IsOneModulePerTile()
        {
            var layout = Layout(9, 9, null, Box(MissionBoxKind.Crate, 4, 4, 1, 1, 1f));

            Assert.That(At(layout, EnvironmentElement.Crate, 4, 4).Position, Is.EqualTo(layout.TileCenter(new Vector2Int(4, 4))));
        }

        [Test]
        public void Floor_HasOneModulePerFloorTile_AtGroundLevel()
        {
            var layout = Layout(6, 6, (x, y) => x < 4);
            var floors = EnvironmentVisualPlanner.Plan(layout).Where(p => p.Element == EnvironmentElement.Floor).ToList();

            Assert.That(floors, Has.Count.EqualTo(layout.FloorTileCount));
            Assert.That(floors.All(p => p.Position.y == 0f), Is.True);
        }

        [Test]
        public void LightFixtures_SitOnStraightWallsAndFaceTheFloorSide()
        {
            // Floor only at y >= 4, a straight wall along y = 3 below it: the floor side is north.
            var layout = Layout(24, 8, (x, y) => y >= 4, Box(MissionBoxKind.Wall, 1, 3, 22, 1));
            var lights = EnvironmentVisualPlanner.Plan(layout).Where(p => p.Element == EnvironmentElement.LightFixture).ToList();

            Assert.That(lights, Is.Not.Empty);
            Assert.That(lights.All(p => p.Tile.y == 3 && p.YawDegrees == 0f), Is.True);
        }

        [Test]
        public void WallsWithFloorOnBothSides_GetNoLightFixture()
        {
            var layout = Layout(24, 8, null, Box(MissionBoxKind.Wall, 1, 3, 22, 1));

            Assert.That(EnvironmentVisualPlanner.Plan(layout).Count(p => p.Element == EnvironmentElement.LightFixture), Is.Zero);
        }

        [Test]
        public void Planning_IsDeterministic_AndDoesNotTouchTheLayout()
        {
            var result = MissionGenerator.Generate(new MissionSettings { seed = 12345 });
            var layout = result.Layout;
            var hash = layout.Hash;

            var first = EnvironmentVisualPlanner.Plan(layout);
            var second = EnvironmentVisualPlanner.Plan(layout);

            Assert.That(second, Is.EqualTo(first));
            Assert.That(layout.Hash, Is.EqualTo(hash));
            Assert.That(MissionGenerator.Generate(new MissionSettings { seed = 12345 }).Layout.Hash, Is.EqualTo(hash));
        }

        [Test]
        public void GeneratedMissions_GetExactlyOneWallModulePerWallTile_AndNoneElsewhere()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = MissionGenerator.Generate(new MissionSettings { seed = seed }).Layout;
                var tiles = new System.Collections.Generic.HashSet<Vector2Int>();
                foreach (var box in layout.Boxes.Where(b => b.Kind == MissionBoxKind.Wall || b.Kind == MissionBoxKind.Baffle))
                    for (var y = box.Footprint.yMin; y < box.Footprint.yMax; y++)
                        for (var x = box.Footprint.xMin; x < box.Footprint.xMax; x++)
                            tiles.Add(new Vector2Int(x, y));
                var pillarBoxTiles = layout.Boxes.Count(b => b.Kind == MissionBoxKind.Pillar);

                var wallModules = EnvironmentVisualPlanner.Plan(layout).Where(p =>
                    p.Element == EnvironmentElement.WallStraight || p.Element == EnvironmentElement.WallCorner ||
                    p.Element == EnvironmentElement.WallEnd || p.Element == EnvironmentElement.WallJunction ||
                    p.Element == EnvironmentElement.Pillar).ToList();

                Assert.That(wallModules, Has.Count.EqualTo(tiles.Count + pillarBoxTiles), $"seed {seed}");
                Assert.That(wallModules.Select(p => p.Tile).Distinct().Count(), Is.EqualTo(wallModules.Count), $"seed {seed}: no tile twice");
            }
        }
    }
}
```

Note: a `Pillar` box does not overlap wall tiles (clearance 2), so the count equation holds. If the generator ever yields an isolated one-tile wall, it is counted in `tiles`, still one module.

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode EnvironmentVisualPlannerTests`
Expected: EXIT=1, `error CS0246` for `EnvironmentVisualPlanner` / `EnvironmentElement`.

- [ ] **Step 3: Implement**

`EnvironmentElement.cs`:

```csharp
namespace Blackglass
{
    /// <summary>
    /// What a generated piece of environment is, as gameplay and the layout see it. The generator and planner speak only in
    /// these; an <see cref="EnvironmentTheme"/> decides which prefab represents each one.
    /// </summary>
    public enum EnvironmentElement
    {
        Floor,
        WallStraight,
        WallCorner,
        WallEnd,
        WallJunction,
        DoorFrame,
        LowCover,
        LowCoverLong,
        Pillar,
        Crate,
        Terminal,
        LightFixture,
    }
}
```

`VisualPlacement.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>One visual module to place: what it is, where (world, base on the ground) and its yaw. Plain data.</summary>
    public readonly struct VisualPlacement
    {
        public VisualPlacement(EnvironmentElement element, Vector3 position, float yawDegrees, Vector2Int tile)
        {
            Element = element;
            Position = position;
            YawDegrees = yawDegrees;
            Tile = tile;
        }

        public EnvironmentElement Element { get; }
        public Vector3 Position { get; }
        public float YawDegrees { get; }
        /// <summary>The tile this module belongs to (the first tile of a multi-tile module). Keys variant selection.</summary>
        public Vector2Int Tile { get; }

        public override string ToString() => $"{Element} {Tile} yaw {YawDegrees}";
    }
}
```

`VisualVariants.cs` (hash only for now):

```csharp
using System;

namespace Blackglass
{
    /// <summary>
    /// Stateless deterministic choices for visual decoration. Nothing here draws from a random stream, so a visual choice can
    /// be added or changed without shifting the layout, the objectives or any other visual choice.
    /// </summary>
    public static class VisualVariants
    {
        const ulong Gamma = 0x9E3779B97F4A7C15UL;

        public static ulong Hash(int seed, int salt, EnvironmentElement element, int x, int y)
        {
            unchecked
            {
                var h = Mix(0xD1B54A32D192ED03UL);
                h = Mix(h + Gamma + (ulong)(uint)seed);
                h = Mix(h + Gamma + (ulong)(uint)salt);
                h = Mix(h + Gamma + (ulong)(uint)(int)element);
                h = Mix(h + Gamma + (ulong)(uint)x);
                h = Mix(h + Gamma + (ulong)(uint)y);
                return h;
            }
        }

        static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
```

`EnvironmentVisualPlanner.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Pure: turns a layout into the visual modules that represent it. Knows gameplay concepts only (floor, wall tiles by how
    /// they join, low cover, crates); knows nothing about prefabs, themes or randomness. Order is fixed (floor, walls by row,
    /// props), so the result is reproducible. See Docs/EnvironmentAssetGuide.md for the yaw convention.
    /// </summary>
    public static class EnvironmentVisualPlanner
    {
        const int LightOneIn = 4;
        const int North = 1, East = 2, South = 4, West = 8;
        // Direction index 0..3 = N, E, S, W; yaw = index * 90.
        static readonly Vector2Int[] Directions = { new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0) };

        public static List<VisualPlacement> Plan(MissionLayout layout)
        {
            var result = new List<VisualPlacement>();
            PlanFloor(layout, result);
            PlanWalls(layout, result);
            PlanProps(layout, result);
            return result;
        }

        static void PlanFloor(MissionLayout layout, List<VisualPlacement> result)
        {
            for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                    if (layout.IsFloor(x, y))
                        result.Add(new VisualPlacement(EnvironmentElement.Floor, layout.TileCenter(new Vector2Int(x, y)), 0f, new Vector2Int(x, y)));
        }

        static void PlanWalls(MissionLayout layout, List<VisualPlacement> result)
        {
            var width = layout.Width;
            var height = layout.Height;
            var wall = new bool[width * height];
            var ring = new bool[width * height];
            foreach (var box in layout.Boxes)
            {
                if (box.Kind != MissionBoxKind.Wall && box.Kind != MissionBoxKind.Baffle)
                    continue;
                for (var y = box.Footprint.yMin; y < box.Footprint.yMax; y++)
                {
                    for (var x = box.Footprint.xMin; x < box.Footprint.xMax; x++)
                    {
                        wall[y * width + x] = true;
                        if (box.Kind == MissionBoxKind.Wall)
                            ring[y * width + x] = true;
                    }
                }
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!wall[y * width + x])
                        continue;
                    var mask = 0;
                    for (var d = 0; d < 4; d++)
                    {
                        var nx = x + Directions[d].x;
                        var ny = y + Directions[d].y;
                        if (nx >= 0 && ny >= 0 && nx < width && ny < height && wall[ny * width + nx])
                            mask |= 1 << d;
                    }
                    Classify(mask, out var element, out var yaw);
                    var tile = new Vector2Int(x, y);
                    var position = layout.TileCenter(tile);
                    result.Add(new VisualPlacement(element, position, yaw, tile));
                    if (element == EnvironmentElement.WallEnd && ring[y * width + x])
                        result.Add(new VisualPlacement(EnvironmentElement.DoorFrame, position, yaw, tile));
                    if (element == EnvironmentElement.WallStraight && TryLightFacing(layout, tile, yaw, out var lightYaw))
                        result.Add(new VisualPlacement(EnvironmentElement.LightFixture, position, lightYaw, tile));
                }
            }
        }

        static void Classify(int mask, out EnvironmentElement element, out float yaw)
        {
            var count = 0;
            for (var d = 0; d < 4; d++)
                if ((mask & (1 << d)) != 0)
                    count++;
            switch (count)
            {
                case 0:
                    element = EnvironmentElement.Pillar;
                    yaw = 0f;
                    return;
                case 1:
                    element = EnvironmentElement.WallEnd;
                    yaw = IndexOf(mask) * 90f;
                    return;
                case 2:
                    if (mask == (North | South)) { element = EnvironmentElement.WallStraight; yaw = 90f; return; }
                    if (mask == (East | West)) { element = EnvironmentElement.WallStraight; yaw = 0f; return; }
                    element = EnvironmentElement.WallCorner;
                    yaw = mask == (North | East) ? 0f : mask == (East | South) ? 90f : mask == (South | West) ? 180f : 270f;
                    return;
                case 3:
                    element = EnvironmentElement.WallJunction;
                    var missing = IndexOf(~mask & 15);
                    yaw = (missing * 90f + 180f) % 360f;
                    return;
                default:
                    element = EnvironmentElement.WallJunction;
                    yaw = 0f;
                    return;
            }
        }

        static int IndexOf(int singleBit)
        {
            for (var d = 0; d < 4; d++)
                if (singleBit == 1 << d)
                    return d;
            return 0;
        }

        // A straight wall along X (yaw 0) has its sides north and south; along Z (yaw 90) east and west.
        static bool TryLightFacing(MissionLayout layout, Vector2Int tile, float straightYaw, out float lightYaw)
        {
            lightYaw = 0f;
            var sideA = straightYaw == 0f ? 0 : 1;   // N or E
            var sideB = straightYaw == 0f ? 2 : 3;   // S or W
            var floorA = layout.IsFloor(tile.x + Directions[sideA].x, tile.y + Directions[sideA].y);
            var floorB = layout.IsFloor(tile.x + Directions[sideB].x, tile.y + Directions[sideB].y);
            if (floorA == floorB)
                return false;
            if (VisualVariants.Hash(layout.Seed, 0, EnvironmentElement.LightFixture, tile.x, tile.y) % LightOneIn != 0)
                return false;
            lightYaw = (floorA ? sideA : sideB) * 90f;
            return true;
        }

        static void PlanProps(MissionLayout layout, List<VisualPlacement> result)
        {
            foreach (var box in layout.Boxes)
            {
                var rect = box.Footprint;
                switch (box.Kind)
                {
                    case MissionBoxKind.Pillar:
                    case MissionBoxKind.Crate:
                        var element = box.Kind == MissionBoxKind.Pillar ? EnvironmentElement.Pillar : EnvironmentElement.Crate;
                        for (var y = rect.yMin; y < rect.yMax; y++)
                            for (var x = rect.xMin; x < rect.xMax; x++)
                                result.Add(new VisualPlacement(element, layout.TileCenter(new Vector2Int(x, y)), 0f, new Vector2Int(x, y)));
                        break;
                    case MissionBoxKind.LowWall:
                        PlanLowWall(layout, rect, result);
                        break;
                }
            }
        }

        static void PlanLowWall(MissionLayout layout, RectInt rect, List<VisualPlacement> result)
        {
            if (rect.width > 1 && rect.height > 1)
            {
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        result.Add(new VisualPlacement(EnvironmentElement.LowCover, layout.TileCenter(new Vector2Int(x, y)), 0f, new Vector2Int(x, y)));
                return;
            }
            var alongX = rect.width >= rect.height;
            var length = alongX ? rect.width : rect.height;
            var yaw = alongX ? 0f : 90f;
            var i = 0;
            for (; i + 2 <= length; i += 2)
            {
                var centre = alongX ? layout.ToWorld(rect.xMin + i + 1f, rect.yMin + 0.5f) : layout.ToWorld(rect.xMin + 0.5f, rect.yMin + i + 1f);
                var tile = alongX ? new Vector2Int(rect.xMin + i, rect.yMin) : new Vector2Int(rect.xMin, rect.yMin + i);
                result.Add(new VisualPlacement(EnvironmentElement.LowCoverLong, centre, yaw, tile));
            }
            for (; i < length; i++)
            {
                var tile = alongX ? new Vector2Int(rect.xMin + i, rect.yMin) : new Vector2Int(rect.xMin, rect.yMin + i);
                result.Add(new VisualPlacement(EnvironmentElement.LowCover, layout.TileCenter(tile), yaw, tile));
            }
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode EnvironmentVisualPlannerTests`
Expected: EXIT=0, all new tests pass. If `LightFixtures_SitOnStraightWallsAndFaceTheFloorSide` finds none, the hash selected no tile in 20 straight tiles: change `LightOneIn` to 3, re-run, and report it (the choice is arbitrary but must stay a constant).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Environment Assets/_Project/Tests/EditMode/EnvironmentVisualPlannerTests.cs Assets/_Project/Tests/EditMode/EnvironmentVisualPlannerTests.cs.meta
git commit -m "Add the pure environment visual planner: layout to semantic placements

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```
(`Assets/_Project/Scripts/Environment.meta` and the new files' `.meta` are included by the directory add.)

---

### Task 2: Variant picking and the theme asset

**Files:**
- Modify: `Assets/_Project/Scripts/Environment/VisualVariants.cs` (add `Pick`)
- Create: `Assets/_Project/Scripts/Environment/EnvironmentTheme.cs`
- Test: `Assets/_Project/Tests/EditMode/VisualVariantsTests.cs`, `Assets/_Project/Tests/EditMode/EnvironmentThemeTests.cs`

**Interfaces:**
- Produces: `static int VisualVariants.Pick(int seed, int salt, EnvironmentElement element, Vector2Int tile, int count)` (returns -1 when `count <= 0`, else `0..count-1`).
- Produces: `EnvironmentTheme : ScriptableObject` with `[Serializable] struct Entry { EnvironmentElement element; GameObject[] variants; }`, `int Salt`, `bool Has(EnvironmentElement)`, `GameObject Resolve(EnvironmentElement, Vector2Int tile, int seed)` (null when missing/empty), `static EnvironmentTheme Create(int salt, Entry[] entries)` (for tooling and tests).

- [ ] **Step 1: Write the failing tests**

`VisualVariantsTests.cs`:

```csharp
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class VisualVariantsTests
    {
        [Test]
        public void Pick_IsDeterministic_AndInRange()
        {
            for (var x = 0; x < 20; x++)
            {
                var a = VisualVariants.Pick(5, 1, EnvironmentElement.Crate, new Vector2Int(x, 3), 3);
                var b = VisualVariants.Pick(5, 1, EnvironmentElement.Crate, new Vector2Int(x, 3), 3);
                Assert.That(a, Is.EqualTo(b));
                Assert.That(a, Is.InRange(0, 2));
            }
        }

        [Test]
        public void Pick_WithNoVariants_IsMinusOne() =>
            Assert.That(VisualVariants.Pick(5, 1, EnvironmentElement.Crate, Vector2Int.zero, 0), Is.EqualTo(-1));

        [Test]
        public void Pick_UsesEveryVariant_OverManyTiles()
        {
            var picks = Enumerable.Range(0, 300).Select(i =>
                VisualVariants.Pick(9, 0, EnvironmentElement.LowCover, new Vector2Int(i % 20, i / 20), 3)).Distinct().ToList();

            Assert.That(picks, Is.EquivalentTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void Pick_ChangesWithSaltSeedAndElement_ButNeverWithCallOrder()
        {
            var tiles = Enumerable.Range(0, 200).Select(i => new Vector2Int(i, i * 7 % 13)).ToList();
            int[] Picks(int seed, int salt, EnvironmentElement e) => tiles.Select(t => VisualVariants.Pick(seed, salt, e, t, 4)).ToArray();

            var baseline = Picks(1, 0, EnvironmentElement.Crate);
            Assert.That(Picks(1, 1, EnvironmentElement.Crate), Is.Not.EqualTo(baseline), "salt");
            Assert.That(Picks(2, 0, EnvironmentElement.Crate), Is.Not.EqualTo(baseline), "seed");
            Assert.That(Picks(1, 0, EnvironmentElement.LowCover), Is.Not.EqualTo(baseline), "element");
            Assert.That(Picks(1, 0, EnvironmentElement.Crate), Is.EqualTo(baseline), "repeatable after other calls");
        }
    }
}
```

`EnvironmentThemeTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class EnvironmentThemeTests
    {
        GameObject a, b;
        EnvironmentTheme theme;

        [SetUp]
        public void SetUp()
        {
            a = new GameObject("A");
            b = new GameObject("B");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
            if (theme != null)
                Object.DestroyImmediate(theme);
        }

        [Test]
        public void Resolve_ReturnsAVariantOfTheElement_Deterministically()
        {
            theme = EnvironmentTheme.Create(3, new[]
            {
                new EnvironmentTheme.Entry { element = EnvironmentElement.Crate, variants = new[] { a, b } },
            });

            var first = theme.Resolve(EnvironmentElement.Crate, new Vector2Int(4, 5), 77);

            Assert.That(first, Is.AnyOf(a, b));
            Assert.That(theme.Resolve(EnvironmentElement.Crate, new Vector2Int(4, 5), 77), Is.SameAs(first));
            Assert.That(theme.Has(EnvironmentElement.Crate), Is.True);
        }

        [Test]
        public void MissingEmptyAndNullEntries_CountAsMissing()
        {
            theme = EnvironmentTheme.Create(0, new[]
            {
                new EnvironmentTheme.Entry { element = EnvironmentElement.Crate, variants = new GameObject[0] },
                new EnvironmentTheme.Entry { element = EnvironmentElement.Floor, variants = null },
                new EnvironmentTheme.Entry { element = EnvironmentElement.Pillar, variants = new GameObject[] { null } },
            });

            foreach (var element in new[] { EnvironmentElement.Crate, EnvironmentElement.Floor, EnvironmentElement.Pillar, EnvironmentElement.Terminal })
            {
                Assert.That(theme.Has(element), Is.False, element.ToString());
                Assert.That(theme.Resolve(element, Vector2Int.zero, 1) == null, Is.True, element.ToString());
            }
        }

        [Test]
        public void ADifferentSalt_ChangesTheChoices_NotTheVariantSet()
        {
            var entries = new[] { new EnvironmentTheme.Entry { element = EnvironmentElement.LowCover, variants = new[] { a, b } } };
            var one = EnvironmentTheme.Create(1, entries);
            var two = EnvironmentTheme.Create(2, entries);
            try
            {
                var differs = false;
                for (var x = 0; x < 50; x++)
                    differs |= one.Resolve(EnvironmentElement.LowCover, new Vector2Int(x, 0), 5) != two.Resolve(EnvironmentElement.LowCover, new Vector2Int(x, 0), 5);
                Assert.That(differs, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(one);
                Object.DestroyImmediate(two);
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `Tools/run-tests.sh EditMode Visual`
Expected: EXIT=1, `error CS0117` / `CS0246` for `Pick` and `EnvironmentTheme`.

- [ ] **Step 3: Implement**

Add to `VisualVariants` (before `Mix`):

```csharp
        /// <summary>Which of `count` variants this element at this tile uses; -1 when there are none.</summary>
        public static int Pick(int seed, int salt, EnvironmentElement element, UnityEngine.Vector2Int tile, int count)
        {
            if (count <= 0)
                return -1;
            return (int)(Hash(seed, salt, element, tile.x, tile.y) % (ulong)count);
        }
```

`EnvironmentTheme.cs`:

```csharp
using System;
using System.Linq;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One visual theme: which prefab variants represent each semantic element. A prefab here is a pure visual (the builder
    /// removes any collider on it). `salt` makes two themes choose differently for the same seed. A second theme is a new
    /// asset; the generator never changes.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Environment Theme", fileName = "EnvironmentTheme")]
    public sealed class EnvironmentTheme : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public EnvironmentElement element;
            public GameObject[] variants;
        }

        [SerializeField] int salt;
        [SerializeField] Entry[] entries = new Entry[0];

        public int Salt => salt;

        /// <summary>Builds a theme in memory (editor tooling and tests).</summary>
        public static EnvironmentTheme Create(int salt, Entry[] entries)
        {
            var theme = CreateInstance<EnvironmentTheme>();
            theme.salt = salt;
            theme.entries = entries;
            return theme;
        }

        public bool Has(EnvironmentElement element) => Variants(element).Length > 0;

        /// <summary>The variant for this element at this tile of this seed, or null when the theme has none.</summary>
        public GameObject Resolve(EnvironmentElement element, Vector2Int tile, int seed)
        {
            var variants = Variants(element);
            var index = VisualVariants.Pick(seed, salt, element, tile, variants.Length);
            return index < 0 ? null : variants[index];
        }

        GameObject[] Variants(EnvironmentElement element)
        {
            foreach (var entry in entries)
            {
                if (entry.element != element || entry.variants == null)
                    continue;
                return entry.variants.Where(v => v != null).ToArray();
            }
            return Array.Empty<GameObject>();
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `Tools/run-tests.sh EditMode Visual` then `Tools/run-tests.sh EditMode EnvironmentTheme`
Expected: EXIT=0.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Environment Assets/_Project/Tests/EditMode/VisualVariantsTests.cs* Assets/_Project/Tests/EditMode/EnvironmentThemeTests.cs*
git commit -m "Add the environment theme asset and stateless variant selection

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The visual builder

**Files:**
- Create: `Assets/_Project/Scripts/Environment/EnvironmentVisualBuilder.cs`
- Create: `Assets/_Project/Tests/PlayMode/TestSupport/TestTheme.cs`
- Test: `Assets/_Project/Tests/PlayMode/EnvironmentVisualsPlayModeTests.cs`

**Interfaces:**
- Consumes: `EnvironmentVisualPlanner.Plan`, `EnvironmentTheme.Resolve/Has`.
- Produces: `public const string EnvironmentVisualBuilder.RootName = "Visuals"`; `public static Transform EnvironmentVisualBuilder.Build(MissionLayout layout, EnvironmentTheme theme, Transform parent)` (creates `Visuals` under `parent`, one child group per element, returns it); `internal static void StripColliders(GameObject instance)`; `internal static GameObject Placeholder(string label)` (magenta unlit-ish cube, no collider).
- Produces (tests): `TestTheme.Create(bool withColliders = true, params EnvironmentElement[] omit)` returns a theme whose every prefab is an active cube (with a `BoxCollider` when requested); `TestTheme.Dispose(theme)` destroys the theme and its prefab sources.

- [ ] **Step 1: Write the failing test**

`TestTheme.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>A theme for tests: every element is a plain cube "prefab" (an ordinary scene object used as an Instantiate source).</summary>
    internal static class TestTheme
    {
        static readonly Dictionary<EnvironmentTheme, List<GameObject>> Sources = new Dictionary<EnvironmentTheme, List<GameObject>>();

        public static EnvironmentTheme Create(bool withColliders = true, params EnvironmentElement[] omit)
        {
            var sources = new List<GameObject>();
            var entries = new List<EnvironmentTheme.Entry>();
            foreach (EnvironmentElement element in System.Enum.GetValues(typeof(EnvironmentElement)))
            {
                if (omit.Contains(element))
                    continue;
                var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
                source.name = "Test_" + element;
                source.transform.position = new Vector3(0f, -1000f, 0f);
                if (!withColliders)
                    Object.DestroyImmediate(source.GetComponent<Collider>());
                sources.Add(source);
                entries.Add(new EnvironmentTheme.Entry { element = element, variants = new[] { source } });
            }
            var theme = EnvironmentTheme.Create(1, entries.ToArray());
            Sources[theme] = sources;
            return theme;
        }

        public static void Dispose(EnvironmentTheme theme)
        {
            if (theme == null)
                return;
            if (Sources.TryGetValue(theme, out var sources))
            {
                foreach (var source in sources)
                    Object.DestroyImmediate(source);
                Sources.Remove(theme);
            }
            Object.DestroyImmediate(theme);
        }
    }
}
#endif
```

`EnvironmentVisualsPlayModeTests.cs` (new file; later tasks append tests to it):

```csharp
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EnvironmentVisualsPlayModeTests
    {
        GameObject root;
        EnvironmentTheme theme;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
                Object.DestroyImmediate(root);
            TestTheme.Dispose(theme);
        }

        static MissionLayout Layout(int seed) => MissionGenerator.Generate(new MissionSettings { seed = seed }).Layout;

        [UnityTest]
        public IEnumerator Build_InstantiatesOneModulePerPlacement_UnderAVisualsRoot_WithNoColliders()
        {
            var layout = Layout(12345);
            theme = TestTheme.Create(withColliders: true);
            root = new GameObject("Root");

            var visuals = EnvironmentVisualBuilder.Build(layout, theme, root.transform);
            yield return null;

            Assert.That(visuals.name, Is.EqualTo(EnvironmentVisualBuilder.RootName));
            Assert.That(visuals.parent, Is.EqualTo(root.transform));
            var expected = EnvironmentVisualPlanner.Plan(layout);
            Assert.That(visuals.GetComponentsInChildren<MeshRenderer>(), Has.Length.EqualTo(expected.Count));
            Assert.That(visuals.GetComponentsInChildren<Collider>(true), Is.Empty, "visuals never collide");
        }

        [UnityTest]
        public IEnumerator Build_PlacesAModuleAtItsPlannedPositionAndYaw()
        {
            var layout = Layout(12345);
            theme = TestTheme.Create();
            root = new GameObject("Root");
            var wall = EnvironmentVisualPlanner.Plan(layout).First(p => p.Element == EnvironmentElement.WallEnd);

            var visuals = EnvironmentVisualBuilder.Build(layout, theme, root.transform);
            yield return null;

            var group = visuals.Find(EnvironmentElement.WallEnd.ToString());
            var match = group.Cast<Transform>().First(t => (t.position - wall.Position).sqrMagnitude < 1e-6f
                && Mathf.Approximately(Mathf.DeltaAngle(t.eulerAngles.y, wall.YawDegrees), 0f));
            Assert.That(match, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator AThemeMissingAnElement_GetsAPlaceholderAndOneWarning_AndStillBuilds()
        {
            var layout = Layout(12345);
            theme = TestTheme.Create(true, EnvironmentElement.Crate, EnvironmentElement.Floor);
            root = new GameObject("Root");
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Crate"));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Floor"));

            var visuals = EnvironmentVisualBuilder.Build(layout, theme, root.transform);
            yield return null;

            Assert.That(visuals.GetComponentsInChildren<MeshRenderer>(), Has.Length.EqualTo(EnvironmentVisualPlanner.Plan(layout).Count));
            Assert.That(visuals.GetComponentsInChildren<Collider>(true), Is.Empty);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode EnvironmentVisualsPlayModeTests`
Expected: EXIT=1, `CS0103`/`CS0246` for `EnvironmentVisualBuilder`.

- [ ] **Step 3: Implement**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Instantiates the planned visual modules from a theme under one "Visuals" node. The node lives under the mission root,
    /// so tearing the mission down removes every visual. Prefabs are visuals only: any collider on an instance is removed
    /// before the NavMesh bake, so imported art can never change navigation, cover or LOS.
    /// </summary>
    public static class EnvironmentVisualBuilder
    {
        public const string RootName = "Visuals";

        static Material placeholderMaterial;

        public static Transform Build(MissionLayout layout, EnvironmentTheme theme, Transform parent)
        {
            var visuals = new GameObject(RootName).transform;
            visuals.SetParent(parent, false);
            var groups = new Dictionary<EnvironmentElement, Transform>();
            var warned = new HashSet<EnvironmentElement>();
            foreach (var placement in EnvironmentVisualPlanner.Plan(layout))
            {
                if (!groups.TryGetValue(placement.Element, out var group))
                {
                    group = new GameObject(placement.Element.ToString()).transform;
                    group.SetParent(visuals, false);
                    groups[placement.Element] = group;
                }
                var prefab = theme.Resolve(placement.Element, placement.Tile, layout.Seed);
                GameObject instance;
                if (prefab != null)
                {
                    instance = Object.Instantiate(prefab, group);
                    instance.name = prefab.name;
                    if (!instance.activeSelf)
                        instance.SetActive(true);
                }
                else
                {
                    if (warned.Add(placement.Element))
                        Debug.LogWarning($"Environment theme '{theme.name}' has no prefab for {placement.Element}; using a placeholder.");
                    instance = Placeholder(placement.Element.ToString());
                    instance.transform.SetParent(group, false);
                }
                instance.transform.SetPositionAndRotation(placement.Position, Quaternion.Euler(0f, placement.YawDegrees, 0f));
                StripColliders(instance);
            }
            return visuals;
        }

        internal static void StripColliders(GameObject instance)
        {
            foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(collider);
        }

        // A 1 m magenta block: obviously wrong, never invisible, never solid.
        internal static GameObject Placeholder(string label)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Missing_" + label;
            Object.DestroyImmediate(cube.GetComponent<Collider>());
            cube.transform.localScale = Vector3.one;
            if (placeholderMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
                placeholderMaterial = new Material(shader) { name = "EnvironmentPlaceholder", hideFlags = HideFlags.HideAndDontSave };
                placeholderMaterial.SetColor("_BaseColor", Color.magenta);
                placeholderMaterial.color = Color.magenta;
            }
            cube.GetComponent<Renderer>().sharedMaterial = placeholderMaterial;
            return cube;
        }
    }
}
```

Note: `Shader.Find(...) ??` is on a `Shader` (UnityEngine.Object). Replace with an explicit null check to follow the project rule:

```csharp
var shader = Shader.Find("Universal Render Pipeline/Unlit");
if (shader == null)
    shader = Shader.Find("Sprites/Default");
```
(Use this form in the file; the `??` line above must not be committed.)

The placeholder is placed so its base sits on the ground: a cube centred on its position would sink half into the floor. Offset it: after `Placeholder(...)` creation set `cube.transform.localScale = new Vector3(1f, 1f, 1f)` and parent it under an empty child so the cube is raised 0.5 m. Do that inside `Placeholder`: create `var holder = new GameObject("Missing_" + label)`, parent the cube to it at local `(0, 0.5, 0)`, return `holder`. Adjust the test accordingly: it only counts `MeshRenderer`s and colliders, so it is unaffected.

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode EnvironmentVisualsPlayModeTests`
Expected: EXIT=0, 3 passed.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Environment Assets/_Project/Tests/PlayMode/TestSupport/TestTheme.cs* Assets/_Project/Tests/PlayMode/EnvironmentVisualsPlayModeTests.cs*
git commit -m "Add the environment visual builder: themed modules under one Visuals node

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: MissionBuilder uses the visual layer

**Files:**
- Modify: `Assets/_Project/Scripts/Mission/MissionBuilder.cs`
- Test: `Assets/_Project/Tests/PlayMode/EnvironmentVisualsPlayModeTests.cs` (append)

**Interfaces:**
- Consumes: `EnvironmentVisualBuilder.Build`, `EnvironmentTheme`.
- Produces: `MissionBuilder.Build(MissionLayout layout, Material groundMaterial, Material obstacleMaterial, Action<Transform> addContent = null, EnvironmentTheme theme = null)`; `GeneratedMission.Visuals` (Transform, null without a theme); `GeneratedMission.SetVisualsVisible(bool visible)`.

Behaviour: with a theme, floor and box cubes keep collider / `CoverSurface` / `NavMeshModifier` but `MeshRenderer.enabled = false`; `Visuals` is built (inside the existing try/catch so a throw cleans the root) before `Physics.SyncTransforms()`. Without a theme nothing changes. `SetVisualsVisible(false)` hides `Visuals` and enables the gameplay renderers (debug view); `true` reverses it; a mission without `Visuals` ignores it.

- [ ] **Step 1: Write the failing tests** (append to `EnvironmentVisualsPlayModeTests`; add `using System.Collections.Generic; using UnityEngine.AI; using Unity.AI.Navigation;` at the top)

```csharp
        TestWorld world;
        GeneratedMission mission;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        void DestroyMission()
        {
            if (mission == null || mission.Root == null)
                return;
            var data = mission.Surface != null ? mission.Surface.navMeshData : null;
            Object.DestroyImmediate(mission.Root);
            if (data != null)
                Object.DestroyImmediate(data);
            mission = null;
        }

        static float NavArea()
        {
            var t = NavMesh.CalculateTriangulation();
            var area = 0f;
            for (var i = 0; i < t.indices.Length; i += 3)
                area += Vector3.Cross(t.vertices[t.indices[i + 1]] - t.vertices[t.indices[i]],
                    t.vertices[t.indices[i + 2]] - t.vertices[t.indices[i]]).magnitude * 0.5f;
            return area;
        }

        System.Collections.Generic.List<string> CoverSignature(GeneratedMission built)
        {
            var registry = world.CreateRegistry();
            var discovery = world.Track(new GameObject("Discovery")).AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
            discovery.Discover(built.Geometry, MissionNavigation.ReachableFrom(built.Layout.TileCenter(built.Layout.FriendlySpawns[0])));
            return registry.Points.Select(p => $"{p.Placement}/{p.Height}/{p.Position.x:F2},{p.Position.z:F2}").OrderBy(s => s).ToList();
        }

        [UnityTest]
        public IEnumerator WithATheme_NavMeshAndCoverAreIdenticalToWithout_EvenWhenPrefabsCarryColliders([ValueSource(nameof(Seeds))] int seed)
        {
            var layout = Layout(seed);
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;
            var plainArea = NavArea();
            var plainCover = CoverSignature(mission);
            DestroyMission();

            theme = TestTheme.Create(withColliders: true);
            mission = MissionBuilder.Build(layout, null, null, null, theme);
            yield return null;

            Assert.That(mission.Visuals, Is.Not.Null);
            Assert.That(mission.Visuals.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(NavArea(), Is.EqualTo(plainArea).Within(0.01f), $"seed {seed}: NavMesh area");
            Assert.That(CoverSignature(mission), Is.EqualTo(plainCover), $"seed {seed}: cover");
        }

        static readonly int[] Seeds = { 12345, 7, 31 };

        [UnityTest]
        public IEnumerator WithATheme_TheGameplayCubesStayAsCollidersButAreNotRendered()
        {
            var layout = Layout(12345);
            theme = TestTheme.Create();
            mission = MissionBuilder.Build(layout, null, null, null, theme);
            yield return null;

            Assert.That(mission.Geometry.childCount, Is.EqualTo(layout.FloorRects.Count + layout.Boxes.Count));
            foreach (Transform child in mission.Geometry)
            {
                Assert.That(child.GetComponent<BoxCollider>(), Is.Not.Null, child.name);
                Assert.That(child.GetComponent<MeshRenderer>().enabled, Is.False, child.name);
            }
            Assert.That(mission.Geometry.GetComponentsInChildren<CoverSurface>().Length, Is.EqualTo(layout.Boxes.Count));
        }

        [UnityTest]
        public IEnumerator WithoutATheme_NothingChanges_NoVisualsRoot_CubesStayVisible()
        {
            mission = MissionBuilder.Build(Layout(12345), null, null);
            yield return null;

            Assert.That(mission.Visuals == null, Is.True);
            Assert.That(mission.Root.transform.Find(EnvironmentVisualBuilder.RootName) == null, Is.True);
            Assert.That(mission.Geometry.GetComponentsInChildren<MeshRenderer>().All(r => r.enabled), Is.True);
            mission.SetVisualsVisible(false);   // harmless without visuals
            Assert.That(mission.Geometry.GetComponentsInChildren<MeshRenderer>().All(r => r.enabled), Is.True);
        }

        [UnityTest]
        public IEnumerator HidingTheVisuals_ShowsTheGameplayCubes_AndShowingThemReverses()
        {
            theme = TestTheme.Create();
            mission = MissionBuilder.Build(Layout(12345), null, null, null, theme);
            yield return null;

            mission.SetVisualsVisible(false);
            Assert.That(mission.Visuals.gameObject.activeSelf, Is.False);
            Assert.That(mission.Geometry.GetComponentsInChildren<MeshRenderer>().All(r => r.enabled), Is.True);

            mission.SetVisualsVisible(true);
            Assert.That(mission.Visuals.gameObject.activeSelf, Is.True);
            Assert.That(mission.Geometry.GetComponentsInChildren<MeshRenderer>().All(r => !r.enabled), Is.True);
        }

        [UnityTest]
        public IEnumerator DestroyingTheRoot_RemovesEveryVisual()
        {
            theme = TestTheme.Create();
            mission = MissionBuilder.Build(Layout(12345), null, null, null, theme);
            yield return null;
            var visuals = mission.Visuals.gameObject;

            DestroyMission();
            yield return null;

            Assert.That(visuals == null, Is.True);
            Assert.That(GameObject.Find(GeneratedMission.RootName) == null, Is.True);
        }
```

Also extend `TearDown` with `DestroyMission(); world.Dispose();` placed first (before the theme dispose), keeping the existing lines.

Caveat: `CoverSignature` uses `world.CreateRegistry()` — its `TestWorld` tracks objects for disposal. A mission destroyed and rebuilt in one test creates two registries/discoveries; that is fine.

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode EnvironmentVisualsPlayModeTests`
Expected: EXIT=1, `CS1739`/`CS1061` (`theme` parameter, `Visuals`, `SetVisualsVisible` missing).

- [ ] **Step 3: Implement**

In `MissionBuilder.cs`:

1. `GeneratedMission`: add

```csharp
        /// <summary>The themed visual modules, or null when the mission was built without a theme (the cubes are then the look).</summary>
        public Transform Visuals { get; internal set; }

        /// <summary>
        /// Debug view: false hides the themed visuals and shows the gameplay cubes (the colliders, NavMesh and cover geometry
        /// that decide everything); true restores the themed look. Ignored when there are no visuals.
        /// </summary>
        public void SetVisualsVisible(bool visible)
        {
            if (Visuals == null)
                return;
            Visuals.gameObject.SetActive(visible);
            foreach (Transform child in Geometry)
                if (child.TryGetComponent<MeshRenderer>(out var renderer))
                    renderer.enabled = !visible;
        }
```

2. `Build` signature gets `, EnvironmentTheme theme = null` after `addContent`. Pass `visible: theme == null` into `Cube(...)` (new `bool visible` parameter: after material assignment `if (!visible) cube.GetComponent<MeshRenderer>().enabled = false;`). Keep the update to the doc comment: obstacle cubes are still the gameplay objects.
3. Inside the existing `try`, after `addContent?.Invoke(geometry.transform);` add:

```csharp
                if (theme != null)
                    visuals = EnvironmentVisualBuilder.Build(layout, theme, root.transform);
```
with `Transform visuals = null;` declared before the `try`. Set `Visuals = visuals` in the returned `GeneratedMission`.

Wait for the second `foreach (Transform child in Geometry)` in `SetVisualsVisible`: when the terminal is added later (Task 5) its root has no `MeshRenderer`, so it is skipped by `TryGetComponent`.

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode EnvironmentVisualsPlayModeTests` then `Tools/run-tests.sh PlayMode MissionBuilderPlayModeTests`
Expected: EXIT=0 for both.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Mission/MissionBuilder.cs Assets/_Project/Tests/PlayMode/EnvironmentVisualsPlayModeTests.cs
git commit -m "Build themed visuals in MissionBuilder; gameplay cubes keep colliders but stop rendering

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The terminal gets a swappable VisualRoot

**Files:**
- Modify: `Assets/_Project/Scripts/Mission/MissionContent.cs`
- Modify: `Assets/_Project/Scripts/Mission/ObjectiveMarker.cs`
- Modify: `Assets/_Project/Tests/PlayMode/ObjectiveMarkerPlayModeTests.cs` (terminal test reads the child renderer)
- Test: `Assets/_Project/Tests/PlayMode/EnvironmentVisualsPlayModeTests.cs` (append)

**Interfaces:**
- Produces: `public const string MissionContent.VisualRootName = "VisualRoot"`; `MissionContent.AddTerminal(Transform geometry, ObjectivePlan plan, MissionLayout layout, MissionSettings settings, Material material, EnvironmentTheme theme = null)`; `ObjectiveMarker.Bind(Func<Color> source, Renderer[] targets = null)` (when `targets` is given only those renderers are tinted).
- Structure: `Terminal` is an unscaled empty root at the same world position as before (tile centre, y = 0.6) with a `BoxCollider` (size 0.8 x 1.2 x 0.8, centre 0), `NavMeshModifier` (Not Walkable), `MissionInteractable`, `ObjectiveMarker`, and a child `VisualRoot` at local (0, -0.6, 0) (so a base-centre-pivot visual stands on the floor). Visual = `theme.Resolve(Terminal, plan.TerminalTile, layout.Seed)` instance with colliders stripped; with no theme or no Terminal entry, a collider-free placeholder cube (0.8 x 1.2 x 0.8) using `material`. The marker tints renderers on GameObjects named `Display` under VisualRoot, or all VisualRoot renderers when there are none.

- [ ] **Step 1: Write the failing tests** (append)

```csharp
        static MissionSettings Settings() => new MissionSettings();

        MissionInteractable Terminal(EnvironmentTheme withTheme, out MissionLayout layout)
        {
            MissionLayout built = null;
            ObjectivePlan plan = null;
            for (var seed = 1; seed < 40 && plan == null; seed++)
            {
                built = Layout(seed);
                ObjectivePlacer.TryPlace(built, Settings(), out plan, out _);
            }
            layout = built;
            MissionInteractable terminal = null;
            var holder = new GameObject("Geometry");
            holder.transform.SetParent(world.Track(new GameObject("TerminalRoot")).transform, false);
            terminal = MissionContent.AddTerminal(holder.transform, plan, built, Settings(), null, withTheme);
            return terminal;
        }

        [Test]
        public void TheTerminal_KeepsItsBoxColliderAndNavModifier_OnAnUnscaledRoot_WithAVisualRoot()
        {
            theme = TestTheme.Create();
            var terminal = Terminal(theme, out _);

            Assert.That(terminal.transform.localScale, Is.EqualTo(Vector3.one));
            var box = terminal.GetComponent<BoxCollider>();
            Assert.That(box.size, Is.EqualTo(new Vector3(0.8f, 1.2f, 0.8f)));
            Assert.That(terminal.GetComponent<Unity.AI.Navigation.NavMeshModifier>().area, Is.EqualTo(1));
            var visualRoot = terminal.transform.Find(MissionContent.VisualRootName);
            Assert.That(visualRoot, Is.Not.Null);
            Assert.That(visualRoot.localPosition, Is.EqualTo(new Vector3(0f, -0.6f, 0f)));
            Assert.That(visualRoot.childCount, Is.EqualTo(1));
            Assert.That(visualRoot.GetComponentsInChildren<Collider>(true), Is.Empty, "the visual never collides");
            Assert.That(terminal.GetComponent<MeshRenderer>() == null, Is.True);
        }

        [Test]
        public void WithoutATheme_TheTerminalShowsAPlaceholderCube_ThatStillTintsWithItsState()
        {
            var terminal = Terminal(null, out _);
            var visualRoot = terminal.transform.Find(MissionContent.VisualRootName);

            Assert.That(visualRoot.GetComponentInChildren<MeshRenderer>(), Is.Not.Null);
            Assert.That(visualRoot.GetComponentsInChildren<Collider>(true), Is.Empty);
        }

        [Test]
        public void TheMarker_TintsOnlyDisplayRenderers_WhenTheVisualHasThem_AndEverythingOtherwise()
        {
            var host = world.Track(new GameObject("Host"));
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.transform.SetParent(host.transform, false);
            var display = GameObject.CreatePrimitive(PrimitiveType.Cube);
            display.name = "Display";
            display.transform.SetParent(host.transform, false);
            var marker = host.AddComponent<ObjectiveMarker>();

            marker.Bind(() => Color.red, new[] { display.GetComponent<Renderer>() });

            var block = new MaterialPropertyBlock();
            display.GetComponent<Renderer>().GetPropertyBlock(block);
            Assert.That(block.GetColor("_BaseColor").r, Is.EqualTo(1f).Within(0.01f));
            body.GetComponent<Renderer>().GetPropertyBlock(block);
            Assert.That(block.isEmpty, Is.True, "the body is not tinted");
        }
```

The fallback "all renderers when there is no Display" is covered by the placeholder test (the placeholder cube has no `Display` name and is tinted); add one assertion to it: after `ObjectiveMarker` update the `MaterialPropertyBlock` of the placeholder renderer is not empty:

```csharp
            var block = new MaterialPropertyBlock();
            visualRoot.GetComponentInChildren<MeshRenderer>().GetPropertyBlock(block);
            Assert.That(block.isEmpty, Is.False);
```

(`Bind` calls `Apply` immediately, so no frame wait is needed.)

`ObjectiveMarkerPlayModeTests.TheTerminal_ShowsItsState_...`: change `terminal.GetComponent<Renderer>()` to `terminal.GetComponentInChildren<Renderer>()`.

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode EnvironmentVisualsPlayModeTests`
Expected: EXIT=1 (`AddTerminal` has no `theme` parameter, `VisualRootName`, `Bind` overload).

- [ ] **Step 3: Implement**

`ObjectiveMarker`: change `public void Bind(Func<Color> source)` to

```csharp
        public void Bind(Func<Color> source, Renderer[] targets = null)
        {
            colour = source;
            renderers = targets;
            Apply();
        }
```
(`Apply` already does `renderers ??= GetComponentsInChildren<Renderer>()`.)

`MissionContent.AddTerminal`:

```csharp
        public const string VisualRootName = "VisualRoot";
        const string DisplayName = "Display";

        /// <summary>
        /// Adds the terminal under `geometry`. Call it from the builder's pre-bake hook. The root is a plain gameplay object
        /// (collider, Not Walkable modifier, interactable, marker); what the player sees is a child VisualRoot, so the look can
        /// change without touching objective behaviour.
        /// </summary>
        public static MissionInteractable AddTerminal(Transform geometry, ObjectivePlan plan, MissionLayout layout,
            MissionSettings settings, Material material, EnvironmentTheme theme = null)
        {
            var terminal = new GameObject("Terminal");
            terminal.transform.SetParent(geometry, false);
            terminal.transform.position = layout.TileCenter(plan.TerminalTile) + Vector3.up * (TerminalHeight * 0.5f);
            var box = terminal.AddComponent<BoxCollider>();
            box.size = new Vector3(TerminalSize, TerminalHeight, TerminalSize);
            // Not walkable, like every obstacle: no island forms on top and the mesh has a hole around it.
            var modifier = terminal.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = NotWalkableArea;
            var interactable = terminal.AddComponent<MissionInteractable>();
            interactable.Initialize(InteractionRange, settings.interactionSeconds);

            var visualRoot = new GameObject(VisualRootName).transform;
            visualRoot.SetParent(terminal.transform, false);
            visualRoot.localPosition = new Vector3(0f, -TerminalHeight * 0.5f, 0f);
            var tint = AddTerminalVisual(visualRoot, plan, layout, theme, material);
            terminal.AddComponent<ObjectiveMarker>().Bind(() => ObjectiveMarker.TerminalColour(interactable), tint);
            return interactable;
        }

        static Renderer[] AddTerminalVisual(Transform visualRoot, ObjectivePlan plan, MissionLayout layout, EnvironmentTheme theme, Material material)
        {
            GameObject instance = null;
            var prefab = theme != null ? theme.Resolve(EnvironmentElement.Terminal, plan.TerminalTile, layout.Seed) : null;
            if (prefab != null)
            {
                instance = Object.Instantiate(prefab, visualRoot);
                instance.name = prefab.name;
                if (!instance.activeSelf)
                    instance.SetActive(true);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
            }
            else
            {
                instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
                instance.name = "Placeholder";
                instance.transform.SetParent(visualRoot, false);
                instance.transform.localPosition = new Vector3(0f, TerminalHeight * 0.5f, 0f);
                instance.transform.localScale = new Vector3(TerminalSize, TerminalHeight, TerminalSize);
                if (material != null)
                    instance.GetComponent<Renderer>().sharedMaterial = material;
            }
            EnvironmentVisualBuilder.StripColliders(instance);
            var all = instance.GetComponentsInChildren<Renderer>();
            var displays = all.Where(r => r.gameObject.name == DisplayName).ToArray();
            return displays.Length > 0 ? displays : all;
        }
```
Add `using System.Linq;` to the file. Remove the old cube-based body.

- [ ] **Step 4: Run to verify it passes, then the touched suites**

Run: `Tools/run-tests.sh PlayMode EnvironmentVisualsPlayModeTests`, `PlayMode ObjectiveMarkerPlayModeTests`, `PlayMode MissionContentPlayModeTests`, `PlayMode MissionObjectivesSceneTests`.
Expected: EXIT=0. If a test fails only because it assumed the terminal root is a scaled cube (e.g. reads `localScale` or `GetComponent<Renderer>()`), update that assertion to the new structure and mention it in the report; do not weaken what it verifies.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Mission/MissionContent.cs Assets/_Project/Scripts/Mission/ObjectiveMarker.cs Assets/_Project/Tests/PlayMode
git commit -m "Give the terminal a swappable VisualRoot; the marker tints only its display

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Director wiring, debug toggle and the F8 developer action

**Files:**
- Modify: `Assets/_Project/Scripts/Mission/MissionDirector.cs`
- Modify: `Assets/_Project/Scripts/Mission/MissionDeveloperInput.cs`
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/MissionRig.cs`
- Test: `Assets/_Project/Tests/PlayMode/EnvironmentVisualsPlayModeTests.cs` (append), `Assets/_Project/Tests/PlayMode/MissionDeveloperInputTests.cs` (append)

**Interfaces:**
- Produces: `MissionDirector` serialized field `environmentTheme`; `Initialize(..., bool randomSeedAtStart = false, EnvironmentTheme theme = null)`; `bool VisualsVisible`; `void SetVisualsVisible(bool)`; `void ToggleVisuals()`. The visible flag survives regeneration and is applied to each new mission after `mission.Terminal` is assigned.
- Produces: `MissionDeveloperInput.Initialize(director, same, fresh, InputActionReference toggleVisuals = null)` and serialized field `toggleVisualsAction`; action `Developer/ToggleVisuals` bound to `<Keyboard>/f8`, group `KeyboardMouse`.
- Produces (tests): `MissionRig(MissionSettings settings = null, bool withCamera = false, EnvironmentTheme theme = null)`; every `Director.Initialize` call inside the rig passes `theme`.

- [ ] **Step 1: Write the failing tests**

Append to `EnvironmentVisualsPlayModeTests.cs`:

```csharp
        [UnityTest]
        public IEnumerator TheDirector_BuildsWithTheTheme_AndRegeneratingLeavesNothingBehind()
        {
            theme = TestTheme.Create();
            using (var rig = new MissionRig(null, false, theme))
            {
                yield return rig.Generate(12345);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
                Assert.That(rig.Director.Current.Visuals, Is.Not.Null);
                var firstRoot = rig.Director.Current.Root;
                var baselineRenderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

                for (var i = 0; i < 3; i++)
                {
                    rig.Director.RegenerateSame();
                    yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);
                    yield return null;
                }

                Assert.That(firstRoot == null, Is.True, "old root destroyed");
                Assert.That(Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                    Is.EqualTo(baselineRenderers), "no stale renderers after repeated regeneration");
                Assert.That(Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.Zero);
                Assert.That(Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Count(g => g.name == GeneratedMission.RootName), Is.EqualTo(1));
            }
        }

        [UnityTest]
        public IEnumerator TheDebugToggle_SurvivesRegeneration()
        {
            theme = TestTheme.Create();
            using (var rig = new MissionRig(null, false, theme))
            {
                yield return rig.Generate(12345);
                rig.Director.ToggleVisuals();
                Assert.That(rig.Director.VisualsVisible, Is.False);
                Assert.That(rig.Director.Current.Visuals.gameObject.activeSelf, Is.False);

                rig.Director.RegenerateSame();
                yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

                Assert.That(rig.Director.Current.Visuals.gameObject.activeSelf, Is.False, "the new mission starts in the debug view");
                rig.Director.ToggleVisuals();
                Assert.That(rig.Director.Current.Visuals.gameObject.activeSelf, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator ThemedMissions_KeepTheirLayoutHash_AndTheSameObjectiveTerminalTile()
        {
            theme = TestTheme.Create();
            ulong plainHash;
            Vector3 plainTerminal;
            using (var rig = new MissionRig())
            {
                yield return rig.Generate(31);
                plainHash = rig.Director.Report.LayoutHash;
                plainTerminal = rig.Director.Current.Terminal.Position;
            }
            using (var rig = new MissionRig(null, false, theme))
            {
                yield return rig.Generate(31);
                Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(plainHash));
                Assert.That(rig.Director.Current.Terminal.Position, Is.EqualTo(plainTerminal));
            }
        }
```

Append to `MissionDeveloperInputTests.cs` a test:

```csharp
        [UnityTest]
        public IEnumerator F8_TogglesTheVisuals()
        {
            Assert.That(rig.Director.VisualsVisible, Is.True);

            Press(keyboard.f8Key);
            yield return null;
            Release(keyboard.f8Key);

            Assert.That(rig.Director.VisualsVisible, Is.False);
        }
```
and in its `Setup()` change the `input.Initialize(...)` call to also pass `TestControls.Ref(actions, "Developer/ToggleVisuals")` as the fourth argument.

- [ ] **Step 2: Run to verify they fail**

Run: `Tools/run-tests.sh PlayMode EnvironmentVisualsPlayModeTests`
Expected: EXIT=1 (`MissionRig` has no `theme` parameter, no `ToggleVisuals`).

- [ ] **Step 3: Implement**

`MissionDirector.cs`:
- Add field `[SerializeField] EnvironmentTheme environmentTheme;` after `obstacleMaterial`, and `bool visualsVisible = true;`.
- `Initialize(..., bool randomSeedAtStart = false, EnvironmentTheme theme = null)` sets `environmentTheme = theme;`.
- In `TryAttempt`: `MissionContent.AddTerminal(geometry, plan, layout, request, obstacleMaterial, environmentTheme)` and `MissionBuilder.Build(layout, groundMaterial, obstacleMaterial, addTerminal, environmentTheme)`.
- After `mission.Terminal = terminal;` add `mission.SetVisualsVisible(visualsVisible);`.
- Add:

```csharp
        public bool VisualsVisible => visualsVisible;

        /// <summary>Debug view: false shows the gameplay cubes instead of the themed visuals. Kept across regenerations.</summary>
        public void SetVisualsVisible(bool visible)
        {
            visualsVisible = visible;
            Current?.SetVisualsVisible(visible);
        }

        public void ToggleVisuals() => SetVisualsVisible(!visualsVisible);
```

`MissionDeveloperInput.cs`: add `[SerializeField] InputActionReference toggleVisualsAction;`, a fourth optional `Initialize` parameter assigned to it, include it in `SetEnabled(true/false, ...)` calls, subscribe/unsubscribe `performed += OnToggleVisuals` (null-checked like the others), and `void OnToggleVisuals(InputAction.CallbackContext context) => director.ToggleVisuals();`. Update the class summary to mention F8.

`BlackglassControls.inputactions`, `Developer` map: add after the `RegenerateNew` action (comma after it):

```json
                { "name": "ToggleVisuals", "type": "Button", "id": "3c8f1a57-9d24-4e6b-8a13-5b7e0d2c4f66", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
```
and a binding after the F7 binding (comma after it):

```json
                { "name": "", "id": "7a2e9b04-6c15-4d38-b9f2-0e4a8c1d3b77", "path": "<Keyboard>/f8", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "ToggleVisuals", "isComposite": false, "isPartOfComposite": false }
```

`MissionRig.cs`: add `readonly EnvironmentTheme theme;`, constructor `MissionRig(MissionSettings settings = null, bool withCamera = false, EnvironmentTheme theme = null)` storing it, and pass `theme: theme` / the trailing argument in the three `Director.Initialize(...)` calls (`SetHostileSlots`, `SetFriendlySlots`, `GenerateAtStart`, and the constructor's). The `Initialize` call is positional: append `, theme: theme` (named) as the last argument; for the constructor call that is `..., ground, obstacle, generateAtStart: false, theme: theme`.

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode EnvironmentVisualsPlayModeTests`, `PlayMode MissionDeveloperInputTests`, `PlayMode MissionDirectorPlayModeTests`, then `Tools/run-tests.sh EditMode` (input asset tests).
Expected: EXIT=0. If an EditMode input-asset test fails because the new `ToggleVisuals` action lacks a binding per control scheme or similar, read the test and make the minimum adjustment that matches the existing precedent for `RegenerateSame` (developer keys are keyboard-only).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Mission Assets/_Project/Input/BlackglassControls.inputactions Assets/_Project/Tests/PlayMode
git commit -m "Wire the theme into the director; F8 toggles visuals for debugging

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: The prototype kit: materials, prefabs, theme, scene wiring, lighting

**Files:**
- Create: `Assets/_Project/Editor/EnvironmentKitBuilder.cs`
- Create (generated by running the builder): `Assets/_Project/Environment/Materials/*.mat`, `Assets/_Project/Environment/Prefabs/*.prefab`, `Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset`
- Modify (by the builder): `Assets/_Project/Scenes/ProceduralMission.unity`
- Test: `Assets/_Project/Tests/PlayMode/EnvironmentKitTests.cs`

**Interfaces:**
- Produces: `EnvironmentKitBuilder.Build(bool overwrite)` (materials, prefabs, theme; skips existing assets unless `overwrite`), `EnvironmentKitBuilder.WireScene()` (assigns theme and the F8 action, sets lighting), `ThemePath` const, menu items under `Blackglass/Environment/`, and batch entry `EnvironmentKitBuilder.BuildAndWireFromCommandLine`.
- Conventions the prefabs must follow (also the content of the asset guide): 1 unit = 1 m; wall-class modules occupy exactly 1 x 3 x 1 m with the pivot at bottom-centre of the tile; floor tile 1 x 0.2 x 1 with the pivot at the centre of the top face (top at y = 0); props pivot at base-centre; authored front = +Z; Straight runs along X; End connects toward +Z (free faces -Z, +/-X); Corner connects +Z and +X; Junction connects +X, -X and +Z (free face -Z); LowCoverLong is 2 x 1 x 1 running along X, pivot at its centre on the ground; LightFixture is authored on the +Z face of its wall tile; Terminal 0.8 x 1.2 x 0.8 base-centre, screens named `Display` on +Z and -Z. Prefabs contain **no colliders**.

- [ ] **Step 1: Write the failing test**

`EnvironmentKitTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EnvironmentKitTests
    {
        const string ThemePath = "Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset";

        static EnvironmentTheme Theme() => AssetDatabase.LoadAssetAtPath<EnvironmentTheme>(ThemePath);

        [Test]
        public void TheTheme_HasAPrefabForEveryElement_AndTwoVariantsOfLowCover()
        {
            var theme = Theme();
            Assert.That(theme, Is.Not.Null);
            foreach (EnvironmentElement element in System.Enum.GetValues(typeof(EnvironmentElement)))
                Assert.That(theme.Has(element), Is.True, element.ToString());
            var variants = Enumerable.Range(0, 200).Select(i => theme.Resolve(EnvironmentElement.LowCover, new Vector2Int(i, 0), 1)).Distinct().Count();
            Assert.That(variants, Is.EqualTo(2));
        }

        [Test]
        public void EveryPrefab_HasNoCollider_AndTheMaterialsAreShared()
        {
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/Environment/Prefabs" });
            Assert.That(guids.Length, Is.InRange(10, 20));
            var materials = new System.Collections.Generic.HashSet<Material>();
            foreach (var guid in guids)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty, prefab.name);
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials)
                        materials.Add(material);
            }
            Assert.That(materials.Count, Is.LessThanOrEqualTo(8), "a modest palette");
            Assert.That(materials.All(m => AssetDatabase.GetAssetPath(m).StartsWith("Assets/_Project/Environment/Materials")), Is.True);
        }

        [Test]
        public void ModuleBounds_MatchTheGridConvention()
        {
            Bounds BoundsOf(string name)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Project/Environment/Prefabs/{name}.prefab");
                var renderers = prefab.GetComponentsInChildren<Renderer>();
                var b = renderers[0].bounds;
                foreach (var r in renderers.Skip(1))
                    b.Encapsulate(r.bounds);
                return b;
            }

            foreach (var wall in new[] { "WallStraight", "WallCorner", "WallEnd", "WallJunction", "Pillar" })
            {
                var b = BoundsOf(wall);
                Assert.That(b.min.y, Is.EqualTo(0f).Within(0.01f), wall + " base on the ground");
                Assert.That(b.max.y, Is.EqualTo(3f).Within(0.02f), wall + " is 3 m tall");
                Assert.That(b.size.x, Is.InRange(0.99f, 1.1f), wall);
                Assert.That(b.size.z, Is.InRange(0.99f, 1.1f), wall);
            }
            var floor = BoundsOf("Floor");
            Assert.That(floor.max.y, Is.EqualTo(0f).Within(0.01f), "floor top at y = 0");
            Assert.That(floor.size.x, Is.EqualTo(1f).Within(0.01f));
            Assert.That(BoundsOf("LowCover_A").max.y, Is.EqualTo(1f).Within(0.02f));
            Assert.That(BoundsOf("LowCoverLong").size.x, Is.EqualTo(2f).Within(0.02f));
            Assert.That(BoundsOf("Terminal").size.y, Is.EqualTo(1.2f).Within(0.05f));
        }

        [UnityTest]
        public IEnumerator ARealThemedMission_HasTheSameNavMeshAndCoverAsTheCubes_AndEveryOpeningStaysConnected()
        {
            using (var rig = new MissionRig())
            {
                yield return rig.Generate(12345);
                var plainHash = rig.Director.Report.LayoutHash;
                var plainCover = rig.Registry.Points.Count;
                var plainPaths = rig.Director.Hostiles.Count;

                var themed = new MissionRig(null, false, Theme());
                try
                {
                    yield return themed.Generate(12345);
                    Assert.That(themed.Director.State, Is.EqualTo(MissionState.Ready));
                    Assert.That(themed.Director.Report.LayoutHash, Is.EqualTo(plainHash));
                    Assert.That(themed.Registry.Points.Count, Is.EqualTo(plainCover));
                    Assert.That(themed.Director.Hostiles.Count, Is.EqualTo(plainPaths));
                }
                finally
                {
                    themed.Dispose();
                }
            }
        }
    }
}
#endif
```
Note: the last test creates two rigs in sequence; the first rig's generated mission stays alive while the second generates (two NavMesh surfaces). To avoid overlap, restructure: generate with the plain rig, record numbers, `rig.Dispose()` explicitly, then build the themed rig. Write it that way (no `using` around the first rig; call `rig.Dispose()` before creating `themed`).

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode EnvironmentKitTests`
Expected: EXIT=2, failures (theme asset null / no prefabs). Compiles fine.

- [ ] **Step 3: Implement `EnvironmentKitBuilder.cs`**

```csharp
#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Creates the prototype Corporate environment kit from Unity primitives: six shared materials, the module prefabs and the
    /// theme asset. Existing assets are kept unless `overwrite` is set, so replacing a prefab with real art is not undone by
    /// running the menu again. Conventions: Docs/EnvironmentAssetGuide.md.
    /// </summary>
    public static class EnvironmentKitBuilder
    {
        const string Root = "Assets/_Project/Environment";
        const string MaterialDir = Root + "/Materials";
        const string PrefabDir = Root + "/Prefabs";
        const string ThemeDir = Root + "/Themes/CorporatePrototype";
        public const string ThemePath = ThemeDir + "/CorporatePrototype.asset";
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static bool overwrite;
        static Material concrete, metal, floorMat, prop, accent, display;

        [MenuItem("Blackglass/Environment/Create Prototype Kit (keeps existing assets)")]
        public static void CreateKit() => Build(false);

        [MenuItem("Blackglass/Environment/Rebuild Prototype Kit (overwrites)")]
        public static void RebuildKit() => Build(true);

        [MenuItem("Blackglass/Environment/Wire ProceduralMission Scene")]
        public static void WireSceneMenu() => WireScene();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildAndWireFromCommandLine()
        {
            Build(false);
            WireScene();
        }

        public static void Build(bool force)
        {
            overwrite = force;
            Directory.CreateDirectory(MaterialDir);
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(ThemeDir);
            AssetDatabase.Refresh();

            concrete = Mat("BW_Concrete", new Color(0.14f, 0.15f, 0.17f), 0f, 0.25f, null);
            metal = Mat("BW_Metal", new Color(0.07f, 0.08f, 0.09f), 0.7f, 0.45f, null);
            floorMat = Mat("BW_Floor", new Color(0.09f, 0.10f, 0.11f), 0f, 0.35f, null);
            prop = Mat("BW_Prop", new Color(0.30f, 0.32f, 0.35f), 0f, 0.3f, null);
            accent = Mat("BW_Accent", new Color(0.05f, 0.6f, 0.8f), 0f, 0.5f, new Color(0.1f, 1.0f, 1.4f));
            display = Mat("BW_Display", new Color(0.05f, 0.2f, 0.3f), 0f, 0.6f, new Color(0f, 0.5f, 0.8f));

            var floor = Prefab("Floor", r =>
            {
                Part(r, "Slab", new Vector3(0f, -0.1f, 0f), new Vector3(1f, 0.2f, 1f), floorMat);
                Part(r, "Panel", new Vector3(0f, 0.002f, 0f), new Vector3(0.9f, 0.004f, 0.9f), metal);
            });
            var straight = Prefab("WallStraight", r =>
            {
                WallBody(r, concrete);
                Part(r, "ConduitN", new Vector3(0f, 2.35f, 0.52f), new Vector3(1f, 0.08f, 0.08f), metal);
                Part(r, "ConduitS", new Vector3(0f, 2.35f, -0.52f), new Vector3(1f, 0.08f, 0.08f), metal);
                Part(r, "StripN", new Vector3(0f, 0.9f, 0.505f), new Vector3(0.5f, 0.04f, 0.01f), accent);
                Part(r, "StripS", new Vector3(0f, 0.9f, -0.505f), new Vector3(0.5f, 0.04f, 0.01f), accent);
            });
            var end = Prefab("WallEnd", r =>
            {
                WallBody(r, concrete);
                Part(r, "EndPlate", new Vector3(0f, 1.6f, -0.51f), new Vector3(0.8f, 2.4f, 0.02f), metal);
            });
            var corner = Prefab("WallCorner", r =>
            {
                WallBody(r, concrete);
                Part(r, "Post", new Vector3(-0.47f, 1.6f, -0.47f), new Vector3(0.12f, 2.64f, 0.12f), metal);
            });
            var junction = Prefab("WallJunction", r =>
            {
                WallBody(r, concrete);
                Part(r, "ConduitFree", new Vector3(0f, 2.35f, -0.52f), new Vector3(1f, 0.08f, 0.08f), metal);
            });
            var doorFrame = Prefab("DoorFrame", r =>
            {
                Part(r, "JambE", new Vector3(0.51f, 1.2f, -0.35f), new Vector3(0.04f, 2.4f, 0.3f), metal);
                Part(r, "JambW", new Vector3(-0.51f, 1.2f, -0.35f), new Vector3(0.04f, 2.4f, 0.3f), metal);
                Part(r, "Header", new Vector3(0f, 2.4f, -0.51f), new Vector3(0.8f, 0.05f, 0.02f), accent);
                Part(r, "StripE", new Vector3(0.4f, 1.2f, -0.515f), new Vector3(0.04f, 2.0f, 0.01f), accent);
                Part(r, "StripW", new Vector3(-0.4f, 1.2f, -0.515f), new Vector3(0.04f, 2.0f, 0.01f), accent);
            });
            var pillar = Prefab("Pillar", r =>
            {
                WallBody(r, metal);
                Part(r, "BandLow", new Vector3(0f, 0.9f, 0f), new Vector3(1.02f, 0.06f, 1.02f), accent);
                Part(r, "BandHigh", new Vector3(0f, 2.2f, 0f), new Vector3(1.02f, 0.06f, 1.02f), accent);
            });
            var lowA = Prefab("LowCover_A", r => LowBody(r, 1f, false));
            var lowB = Prefab("LowCover_B", r => LowBody(r, 1f, true));
            var lowLong = Prefab("LowCoverLong", r => LowBody(r, 2f, true));
            var crate = Prefab("Crate", r =>
            {
                Part(r, "Body", new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f), prop);
                Part(r, "BandLow", new Vector3(0f, 0.1f, 0f), new Vector3(1.02f, 0.08f, 1.02f), metal);
                Part(r, "BandHigh", new Vector3(0f, 0.9f, 0f), new Vector3(1.02f, 0.08f, 1.02f), metal);
            });
            var cabinet = Prefab("Cabinet", r =>
            {
                Part(r, "Body", new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f), metal);
                Part(r, "DoorStrip", new Vector3(0f, 0.55f, 0.505f), new Vector3(0.02f, 0.7f, 0.01f), accent);
                Part(r, "Top", new Vector3(0f, 0.97f, 0f), new Vector3(1f, 0.06f, 1f), prop);
            });
            var terminal = Prefab("Terminal", r =>
            {
                Part(r, "Base", new Vector3(0f, 0.45f, 0f), new Vector3(0.8f, 0.9f, 0.8f), metal);
                Part(r, "Top", new Vector3(0f, 1.15f, 0f), new Vector3(0.8f, 0.1f, 0.8f), prop);
                Part(r, "Neck", new Vector3(0f, 0.97f, 0f), new Vector3(0.7f, 0.25f, 0.7f), concrete);
                Part(r, "Display", new Vector3(0f, 1.0f, 0.36f), new Vector3(0.6f, 0.3f, 0.06f), display);
                Part(r, "Display", new Vector3(0f, 1.0f, -0.36f), new Vector3(0.6f, 0.3f, 0.06f), display);
            });
            var light = Prefab("LightFixture", r =>
            {
                Part(r, "Housing", new Vector3(0f, 2.45f, 0.55f), new Vector3(0.5f, 0.12f, 0.1f), metal);
                Part(r, "Lens", new Vector3(0f, 2.45f, 0.605f), new Vector3(0.44f, 0.05f, 0.02f), accent);
            });

            var theme = AssetDatabase.LoadAssetAtPath<EnvironmentTheme>(ThemePath);
            if (theme == null || overwrite)
            {
                var fresh = EnvironmentTheme.Create(1, new[]
                {
                    Entry(EnvironmentElement.Floor, floor),
                    Entry(EnvironmentElement.WallStraight, straight),
                    Entry(EnvironmentElement.WallEnd, end),
                    Entry(EnvironmentElement.WallCorner, corner),
                    Entry(EnvironmentElement.WallJunction, junction),
                    Entry(EnvironmentElement.DoorFrame, doorFrame),
                    Entry(EnvironmentElement.Pillar, pillar),
                    Entry(EnvironmentElement.LowCover, lowA, lowB),
                    Entry(EnvironmentElement.LowCoverLong, lowLong),
                    Entry(EnvironmentElement.Crate, crate, cabinet),
                    Entry(EnvironmentElement.Terminal, terminal),
                    Entry(EnvironmentElement.LightFixture, light),
                });
                if (theme == null)
                    AssetDatabase.CreateAsset(fresh, ThemePath);
                else
                {
                    EditorUtility.CopySerialized(fresh, theme);
                    Object.DestroyImmediate(fresh);
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static EnvironmentTheme.Entry Entry(EnvironmentElement element, params GameObject[] variants) =>
            new EnvironmentTheme.Entry { element = element, variants = variants };

        static void WallBody(GameObject root, Material body)
        {
            Part(root, "Plinth", new Vector3(0f, 0.15f, 0f), new Vector3(1f, 0.3f, 1f), metal);
            Part(root, "Body", new Vector3(0f, 1.61f, 0f), new Vector3(1f, 2.62f, 1f), body);
            Part(root, "Cap", new Vector3(0f, 2.96f, 0f), new Vector3(1f, 0.08f, 1f), prop);
        }

        // Low cover: 1 m tall in total, `length` long along X, light top so it reads from the gameplay camera.
        static void LowBody(GameObject root, float length, bool stripe)
        {
            Part(root, "Body", new Vector3(0f, 0.45f, 0f), new Vector3(length, 0.9f, 1f), prop);
            Part(root, "Top", new Vector3(0f, 0.95f, 0f), new Vector3(length, 0.1f, 1f), metal);
            if (!stripe)
                return;
            Part(root, "StripN", new Vector3(0f, 0.5f, 0.505f), new Vector3(length * 0.6f, 0.04f, 0.01f), accent);
            Part(root, "StripS", new Vector3(0f, 0.5f, -0.505f), new Vector3(length * 0.6f, 0.04f, 0.01f), accent);
        }

        static GameObject Part(GameObject parent, string name, Vector3 position, Vector3 size, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent.transform, false);
            part.transform.localPosition = position;
            part.transform.localScale = size;
            part.GetComponent<Renderer>().sharedMaterial = material;
            return part;
        }

        static GameObject Prefab(string name, System.Action<GameObject> build)
        {
            var path = $"{PrefabDir}/{name}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null && !overwrite)
                return existing;
            var root = new GameObject(name);
            build(root);
            var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        static Material Mat(string name, Color colour, float metallic, float smoothness, Color? emission)
        {
            var path = $"{MaterialDir}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null && !overwrite)
                return existing;
            var material = existing != null ? existing : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", colour);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            if (emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission.Value);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            if (existing == null)
                AssetDatabase.CreateAsset(material, path);
            else
                EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>Assigns the theme and the F8 action in ProceduralMission, and darkens the lighting a little.</summary>
        public static void WireScene()
        {
            var theme = AssetDatabase.LoadAssetAtPath<EnvironmentTheme>(ThemePath);
            if (theme == null)
                throw new System.InvalidOperationException("Run Build first: no theme at " + ThemePath);
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var director = Object.FindFirstObjectByType<MissionDirector>();
            var directorObject = new SerializedObject(director);
            directorObject.FindProperty("environmentTheme").objectReferenceValue = theme;
            directorObject.ApplyModifiedPropertiesWithoutUndo();

            var toggle = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.name == "ToggleVisuals");
            var developer = Object.FindFirstObjectByType<MissionDeveloperInput>();
            if (toggle != null && developer != null)
            {
                var inputObject = new SerializedObject(developer);
                inputObject.FindProperty("toggleVisualsAction").objectReferenceValue = toggle;
                inputObject.ApplyModifiedPropertiesWithoutUndo();
            }
            else
                Debug.LogWarning("ToggleVisuals action reference or MissionDeveloperInput not found; F8 is not wired.");

            // Minimal lighting pass: a dimmer cool key light and a flat dark-blue ambient. Not final lighting.
            foreach (var sun in Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Where(l => l.type == LightType.Directional))
            {
                sun.color = new Color(0.78f, 0.84f, 1f);
                sun.intensity = 0.9f;
                EditorUtility.SetDirty(sun);
            }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.20f, 0.22f, 0.28f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
    }
}
#endif
```

Implementation notes for the executor:
- Prefab pivots: every `Part` position above is relative to the prefab root (root = pivot). Verify a few visually only if cheap; `ModuleBounds_MatchTheGridConvention` enforces the numbers.
- `LowCover_A/B` names must match the test (`LowCover_A`); the `Crate` theme entry has `Crate` and `Cabinet` variants.
- The `Terminal` prefab has two child objects named `Display`; that is intended (`MissionContent` tints all `Display` renderers).
- `Object.FindFirstObjectByType` on a `MonoBehaviour` is fine; do not use `??` on Unity objects.
- If a `Directional Light` object in the scene was serialised in the Prefab-instance form, `SetDirty` still saves it; if the scene has no directional light, skip silently.

- [ ] **Step 4: Run the builder (Editor closed)**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "F:/Programs/ProjectBlackglass" -executeMethod Blackglass.EditorTools.EnvironmentKitBuilder.BuildAndWireFromCommandLine -logFile Logs/KitBuild.log
echo EXIT=$?
grep -n "error\|Exception\|ToggleVisuals action" Logs/KitBuild.log | head
```
Expected: EXIT=0, no errors. If the builder logs the `ToggleVisuals` warning, the `InputActionReference` sub-asset is missing: open the project once, select `BlackglassControls.inputactions` and confirm the action appears (reference sub-assets generate on import), then re-run `WireScene` via the same `-executeMethod BuildAndWireFromCommandLine`.

Run: `Tools/run-tests.sh PlayMode EnvironmentKitTests`
Expected: EXIT=0. Fix any bound that is off by editing the prefab numbers in the builder and re-running `-executeMethod` with Rebuild (`EnvironmentKitBuilder.RebuildKit` via a one-off `-executeMethod Blackglass.EditorTools.EnvironmentKitBuilder.RebuildKit`) followed by `WireScene`; do not loosen the test bounds.

- [ ] **Step 5: Look at it**

Open the `ProceduralMission` scene is not possible headless; instead take the checks from Task 9's manual list. For this task confirm only: `git status` shows the new Environment assets and a modified `ProceduralMission.unity` (the diff should be only the two serialized fields, the light and the ambient settings: inspect `git diff Assets/_Project/Scenes/ProceduralMission.unity | head -80`). If the diff contains unrelated churn (re-serialisation of unrelated objects), report it and keep only the intended lines by reverting the scene and applying the field assignments by hand in YAML.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Editor Assets/_Project/Editor.meta Assets/_Project/Environment Assets/_Project/Environment.meta Assets/_Project/Scenes/ProceduralMission.unity Assets/_Project/Tests/PlayMode/EnvironmentKitTests.cs*
git commit -m "Add the Corporate prototype kit: materials, primitive prefabs, theme and scene wiring

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Documentation

**Files:**
- Create: `Docs/EnvironmentAssetGuide.md`
- Modify: `docs/Decisions.md` (append decision 035)
- Modify: `docs/superpowers/specs/2026-10-07-environment-visuals-design.md` (amendments)

(Existing decisions live in `docs/Decisions.md`; the guide goes next to it as `docs/EnvironmentAssetGuide.md`: use `docs/`, not `Docs/`, to match the repository.)

- [ ] **Step 1: Write `docs/EnvironmentAssetGuide.md`** containing exactly these sections, filled with the numbers below (no placeholders):

1. **Units and grid.** 1 Unity unit = 1 m; the mission grid is 1 m tiles; Y up; export from Blender with Apply Scale and `-Z Forward, Y Up` set so the Unity import has scale 1 and no rotation.
2. **Module table** (name, element, footprint, height, pivot, authored orientation): Floor (1 x 0.2 x 1, centre of top face, top at y=0); WallStraight (1 x 3 x 1, bottom-centre, runs along X); WallEnd (connects toward +Z, free faces -Z and sides); WallCorner (connects +Z and +X); WallJunction (connects +X, -X, +Z; free face -Z); DoorFrame (overlay on a WallEnd, same footprint, same orientation); Pillar (1 x 3 x 1); LowCover (1 x 1 x 1, bottom-centre); LowCoverLong (2 x 1 x 1 along X, centre on the ground); Crate/Cabinet (<= 1 x 1 x 1, base-centre, 1 m tall); Terminal (0.8 x 1.2 x 0.8, base-centre, front +Z, screens in objects named `Display`); LightFixture (authored on the +Z face of a wall tile, within 0.1 m of it).
3. **Forward and yaw.** Front = +Z; the generator rotates in 90 degree steps clockwise from above; never rely on a rotation in code: fix orientation inside the prefab (put the mesh under a child transform and rotate that child).
4. **Naming.** `<Element>[_Variant]` for prefabs (e.g. `WallStraight`, `LowCover_B`), meshes `SM_<Element>_<Variant>`, materials `BW_<Name>`.
5. **Materials.** Reuse the six `BW_` materials where possible; URP Lit; one material per renderer where possible; never create materials at runtime; emissive via `_EmissionColor` only.
6. **Colliders.** Prefabs have none. The generated gameplay object under `Geometry` carries the one BoxCollider, `CoverSurface` and `NavMeshModifier`. Any collider left on a prefab is removed at build time. Decorative detail may protrude at most 0.1 m outside the tile footprint and never changes cover or navigation.
7. **How to turn a mesh into a module.** Numbered: model/generate; clean in Blender (apply transforms, pivot per table, scale 1, front +Z); export FBX; import in `Assets/_Project/Environment/` (or a theme folder), scale 1, no mesh colliders; create a prefab with the mesh under a child; open `Themes/<Theme>/*.asset` and put the prefab in the element's `variants` list (append for a new variant, replace to swap); press Play in `ProceduralMission` and use F8 to compare against the gameplay cubes.
8. **Variants.** More than one prefab in the list means deterministic variation by seed and tile; variant never changes gameplay.
9. **New theme.** Duplicate the theme asset, change `salt` and prefabs, assign it to the `MissionDirector.environmentTheme` field.
10. **Checklist before import** (6 one-line items: scale, pivot, front, no colliders, shared materials, height 3 m for wall-class modules).

- [ ] **Step 2: Append decision 035** to `docs/Decisions.md` in the file's existing style (read the tail of decision 034 first for the format): decided (2026-10-07): visual layer = planner + theme + builder; gameplay cubes remain the sole collider/cover/nav carriers with renderers disabled when themed; per-tile wall modules classified by neighbours; variant selection by stateless hash with theme salt (no random stream); terminal split into gameplay root + `VisualRoot`; F8 debug toggle; alternatives rejected: stretched single wall meshes (cannot carry trim), putting colliders on visual prefabs (trim would create cover/nav faults), drawing variants from the layout random stream (any visual change would reshuffle the map), a decal/clutter framework (out of scope); implications: replacing art needs no generator change; a new theme is a new asset; `DoorFrame` overlays ring wall ends; floors are per-tile modules (instance count grows with mission size; static batching is the first optimisation if profiling asks).

- [ ] **Step 3: Amend the spec** in a short "Amendments (implementation)" section at the end: `WallPillar` folded into `Pillar`; `DoorFrame` is a jamb overlay on ring `WallEnd`s (openings are 3-5 tiles wide, so no per-gap piece exists); `LowCover`/`LowCoverLong` split (2-tile modules plus remainder); the debug view shows the gameplay cubes with their existing placeholder materials (not translucent); the terminal stays visible in the debug view; `LightFixture` is an emissive prop only (no real lights).

- [ ] **Step 4: Commit**

```bash
git add docs/EnvironmentAssetGuide.md docs/Decisions.md docs/superpowers/specs/2026-10-07-environment-visuals-design.md
git commit -m "Document the environment visual layer: decision 035 and the external asset guide

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Whole-suite validation, scene smoke check, report

**Files:** none new (fixes only if a test fails).

- [ ] **Step 1: Full suites**

Run: `Tools/run-tests.sh EditMode` then `Tools/run-tests.sh PlayMode`
Expected: EXIT=0 for both. Totals must be BASE_EDIT + the new EditMode tests and BASE_PLAY + the new PlayMode tests from Tasks 1-7 (count them from the files; report both numbers). Any failure: use superpowers:systematic-debugging; do not skip or weaken tests.

- [ ] **Step 2: Scene smoke test (headless)**

Run a short batch that loads `ProceduralMission`, generates 5 seeds, and logs errors. If a PlayMode scene test already does this (`ProceduralMissionSceneTests`, `MissionObjectivesSceneTests`), confirm they passed in Step 1 and that the scene now has the theme assigned: `grep -n "environmentTheme" Assets/_Project/Scenes/ProceduralMission.unity` must show a non-zero guid reference. Check `Logs/TestRun-PlayMode.log` for `Error`/`Exception` lines that are not from tests that expect them: `grep -n "Exception\|error" Logs/TestRun-PlayMode.log | grep -vi "expected" | head`.

- [ ] **Step 3: Determinism and regression spot checks (record the results)**

Run the existing mission tests that prove the layout hash for a fixed seed (they are in the suites above); confirm they passed unmodified. Confirm `git diff main --stat -- Assets/_Project/Scripts/Mission/MissionGenerator.cs Assets/_Project/Scripts/Mission/ObjectivePlacer.cs Assets/_Project/Scripts/Cover` is empty (generator, placer and cover code untouched).

- [ ] **Step 4: Prepare the manual test list** for the final report (the owner runs it):
  1. Open `ProceduralMission`, press Play; generate several seeds with F7; confirm walls, floors, corners, wall ends, door jambs and pillars line up (no gaps or overlaps at seams) and that no magenta placeholder appears.
  2. F6 regenerates the same seed: identical layout and identical visual variants; F8 toggles to the raw gameplay cubes and back; hidden state persists across F6/F7.
  3. Walk characters through doorways and around corners, check no snagging on trim; use low cover and tall/corner cover (cover markers still where they were).
  4. Fight around cover; use abilities; pause (tactical pause) and issue queued orders.
  5. Interact with the terminal (colour tint on the screens only), extract.
  6. Check Darius' scale against doors (2.4 m clear), cover (1 m) and the terminal (1.2 m); check camera framing and that walkable space, walls, low cover, openings, terminal, extraction and unit teams are distinguishable.
  7. Keyboard/mouse and a controller (Xbox / PlayStation / Nintendo layouts) for all of the above.
  8. Regenerate 10 times; check the Hierarchy has a single `GeneratedMissionRoot` and the Console is clean.

- [ ] **Step 5: Request review and finish**

Use superpowers:requesting-code-review for the whole branch (`git diff main...HEAD`), fix what it finds, then superpowers:finishing-a-development-branch. Do not merge or push without the owner's decision. Report: files created/changed, semantic representation, theme architecture, conventions, modules, materials, collision, navigation and cover interaction, deterministic strategy, terminal separation, regeneration cleanup, asset workflow, manual tests, known limitations (per-tile instance count; terminal faces +/-Z only; debug view uses existing placeholder materials; no static batching yet; `DoorFrame` overlay only on ring wall ends), and architecture concerns before Phase 10.

---

## Self-review (completed)

- **Spec coverage:** planner (T1), variants/theme/missing-prefab handling (T2), builder, ownership, collider stripping (T3), gameplay-vs-visual split and debug toggle (T4, T6), terminal VisualRoot (T5), kit/materials/lighting/conventions/scene (T7), docs and asset guide (T8), validation list items 1-30 via T4/T6/T7 tests, existing suites in T9 and the manual list (T9). Spec items consciously changed are recorded as amendments in T8 Step 3.
- **Placeholders:** none; all code is given. Two instructions ask the executor to adjust tests that assumed a primitive terminal; they are bounded by "do not weaken".
- **Type consistency:** `EnvironmentElement`, `VisualPlacement(Element, Position, YawDegrees, Tile)`, `EnvironmentVisualPlanner.Plan`, `VisualVariants.Hash/Pick`, `EnvironmentTheme.Create/Has/Resolve/Entry`, `EnvironmentVisualBuilder.Build/RootName/StripColliders/Placeholder`, `GeneratedMission.Visuals/SetVisualsVisible`, `MissionContent.AddTerminal(..., EnvironmentTheme theme = null)/VisualRootName`, `ObjectiveMarker.Bind(Func<Color>, Renderer[] = null)`, `MissionDirector.VisualsVisible/SetVisualsVisible/ToggleVisuals`, `MissionRig(settings, withCamera, theme)` are used identically across tasks.
