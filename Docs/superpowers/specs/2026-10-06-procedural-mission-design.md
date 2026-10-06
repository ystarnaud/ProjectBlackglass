# Phase 8 — First procedurally generated combat mission: design

Date: 2026-10-06. Status: implemented on branch procedural-mission (see Docs/Decisions.md record 026). Where the build differs from this text, record 026 is authoritative: low cover may stand 1 tile from a room wall and no obstacle stands within 2 tiles of a corridor (section 4, step 5); a spawn region is its room shrunk by 1 tile, not 2 (step 7); teardown also destroys each bake's `NavMeshData` and `Clear()`/`OnDisable` cancel a running generation (section 8).

## 1. Goal and scope

Prove the whole runtime pipeline with one small combat mission:

Seed → generate layout → validate connectivity → build geometry → build NavMesh → validate navigation → discover cover → choose spawns → spawn friendly squad → spawn hostiles → start encounter → play the existing combat systems.

The mission must be structurally reliable, deterministic for a seed, fully playable with every existing system (direct control, tactical pause, command queues, companions, enemy AI, line of sight, cover including tall-wall corners, abilities, mouse/keyboard and controller) and regenerable in Play Mode without leftovers.

Success is the owner's Phase 8 validation list (36 items). Out of scope (owner list): final procedural system, biomes, themes, procedural textures/story/quests, loot, inventory, progression, objectives beyond the existing win condition, elevation, destructibles, minimap, fog of war, save/load, multiplayer, third-party packages. Phase 9 is not started.

Owner decisions taken in brainstorming (2026-10-06):

- **The mission lives in a new scene, `ProceduralMission`.** `Prototype` and its scene tests are left untouched as the hand-built regression arena.
- **Layout model:** rooms on a coarse grid, rasterised to a 1 m floor mask, walls derived from the mask boundary.
- **Developer controls:** F6 regenerates the same seed, F7 a new random seed; a specific seed is set in the Inspector in Play Mode, then F6. (Update, see record 026: at Play the scene starts on a fresh clock-based seed, flag `newSeedAtStart`.) No clickable debug buttons and no typed-seed box (a click would also reach `PlayerCommandInput` as an order; digits would fire abilities 1–4).

Assumptions I made (say so if wrong):

- Defaults: 4×3 grid of 14 m cells (56×42 m), 6 rooms, 3 friendlies, 3 hostiles.
- Walls are 3 m high and 1 m thick; low cover is 1 m high; every footprint is a whole number of metres.
- The generator configuration is a plain `[Serializable]` class on the director (like `CoverGenerationSettings`), not a ScriptableObject.
- The friendly squad is **respawned** on every generation (fresh health, cooldowns, positions), from the existing `FriendlyUnit` prefab with the Phase 7 archetypes and abilities.
- Hostiles use the existing `HostileUnit` prefab and, optionally, one of the three existing archetype assets per slot; a slot without an archetype keeps the prefab's inline stats. Enemy AI does not use abilities (unchanged from Phase 7).
- A failed regeneration leaves an empty scene (teardown happens first); there is no "keep the old mission" path.

## 2. Architecture

```
MissionDirector (persistent, scene)        coroutine, owns the sequence and the state
  ├─ MissionGenerator   (pure)   seed + MissionSettings → MissionLayout (or failure)   [EditMode-tested]
  ├─ MissionBuilder     (scene)  MissionLayout → GeneratedMissionRoot/Geometry (cubes, CoverSurface, NavMeshModifier)
  ├─ NavMeshSurface     (package) runtime build on the Geometry node + path validation
  ├─ CoverDiscovery.Discover(root, reachable)   existing system, scoped
  └─ MissionSpawner     (scene)  spawn points → units under GeneratedMissionRoot/Actors, wired to the persistent systems
Persistent (never generated): input, TacticalPause, TacticalCameraController, PrototypeHud, controller/cursor,
  Encounter, UnitSelection, ActiveCharacter, CoverRegistry, CoverDiscovery, AbilityTargeting, debug views
Per mission (destroyed with the root): floor, walls, cover boxes, NavMesh data, friendly and hostile units
```

The pure half (`MissionGenerator`, `MissionLayout`, `SeededRandom`, `MissionSettings`) has no `UnityEngine` object dependencies beyond value types (`Vector2Int`, `RectInt`, `Bounds`) so procedural decisions never tangle with combat, input, abilities, cover evaluation or AI. New files live in `Scripts/Mission/`.

## 3. Seed and determinism

- `SeededRandom` is a small SplitMix64 struct (`NextInt(max)`, `NextInt(min, max)`, `NextFloat`, `Chance(p)`, `Shuffle`). It is used instead of `System.Random` so a seed means the same mission after any runtime or Unity upgrade.
- Attempt *n* of seed *s* uses the sub-seed `Mix(s, n)`. The final layout for a seed is therefore the same however many attempts failed before it, and a failed attempt count is reproducible.
- All layout decisions are integers (tile coordinates); floats appear only when the builder converts tiles to world positions. Unordered collections are never iterated for decisions.
- Generation never calls `UnityEngine.Random`. A test seeds `UnityEngine.Random`, generates, and checks the global sequence is unchanged, so combat randomness (`UnitAttacker.RollHit`) is isolated.
- `MissionLayout.Hash` is an FNV-1a hash over mask, rooms, connections, boxes, spawn regions and spawn points. Tests compare "same seed twice", "different seeds differ", and pin the hashes of a few golden seeds (updated deliberately when the algorithm changes).
- `MissionSettings` is validated and clamped in one place (`Validate()`), and printed in full (`Describe()`) in every failure message, so a seed plus that line reproduces a layout.

## 4. Layout generation (pure)

Coordinates are tiles of 1 m; the mission rectangle is centred on the world origin by the builder.

1. **Choose rooms.** From a `gridColumns × gridRows` grid of `cellSize` cells, grow a connected set of `roomCount` cells from a seeded start cell (add a random neighbour of the set each step). Inside each chosen cell, the room is a rectangle of 7–10 tiles per side, inset at least 2 tiles from the cell edge (so the gutter between neighbouring rooms is at least 4 m) and placed at a seeded integer offset.
2. **Connect.** A spanning tree over the adjacent chosen cells, plus up to `extraLoops` additional adjacent pairs. Each connection is a corridor strip `corridorWidth` wide that runs between the two rooms across the gutter, placed at a seeded offset inside the rooms' overlap on the shared axis. Both ends sit fully inside the rooms, so the room wall gets a door gap exactly as wide as the corridor. A pair whose overlap is smaller than `corridorWidth` is not a legal connection; if the tree cannot be completed the attempt fails.
3. **Rasterise.** Floor mask = union of rooms and corridor strips.
4. **Tall obstacles.** Per room, up to `bafflesPerRoom` free-standing tall wall segments (3–5 tiles long, 1 thick, axis-aligned) and up to one pillar (1×1, tall). A candidate is placed only if it keeps a clear gap of at least 2 tiles to every wall, door gap and other obstacle, and if the walkable area stays connected (below). Tries are bounded per room.
5. **Low cover.** Total target = `lowCoverDensity` per 100 m² of floor, spread over rooms in seeded order: low walls 3×1 and crates 1×1, height 1 m, same clearance and connectivity rules, never in a door gap or corridor.
6. **Walls.** Wall tiles are the void tiles 8-adjacent to a floor tile. They are decomposed into boxes by a deterministic greedy rectangle cover (maximal runs first, merged when stacked with identical extent), so the room frame becomes a few long boxes. A door gap therefore ends the wall in a clean end face, and free-standing baffles and corridor sides give more ends. Those ends are exactly what the cover generator turns into corner locations.
7. **Spawn regions.** Build the room graph; BFS distances give the room pair with the greatest graph distance (ties by the seeded RNG): the friendly room and the main hostile room. The friendly region is that room's interior shrunk by 2 tiles; hostiles fill the farthest room's region first and then the next-farthest room's (at most two rooms). Spawn tiles are shuffled by the RNG and accepted if at least 1 tile from every obstacle and inside the room walls by 1 tile (the region), at least 2.5 m from every already chosen unit, and at least `minTeamSeparation` (16 m) straight-line from every unit of the other team. If a region cannot supply all its points, the attempt fails.
8. **Output.** `MissionLayout`: settings echo, seed, attempt, mask, rooms, connections, wall boxes, obstacle boxes (each tagged tall or low), spawn regions, friendly and hostile spawn tiles, bounds (`Bounds`, world space) and `Hash`.

**Connectivity (grid level).** A 4-connected flood fill over floor tiles not covered by an obstacle must reach every floor tile from a friendly spawn tile, every room must contain a reachable tile, and every hostile spawn tile must be reachable. It runs after each obstacle placement (rejecting the candidate) and once more on the final layout (failing the attempt). The 2-tile clearance rule keeps paths at least 2 m wide; `corridorWidth` is clamped to at least 3.

**Bounded retry.** `MissionGenerator.Generate(seed, settings)` tries attempts `1..maxAttempts` (default 20, clamped 1–100) and returns a `MissionGenerationResult` (layout or a failure with the reason of each failed attempt). It never loops unbounded.

## 5. Geometry and navigation

`MissionBuilder` creates `GeneratedMissionRoot` with two children:

- `Geometry`: the floor (merged floor rectangles, 0.2 m thick, top at y = 0, shared ground material), walls and obstacles (cubes with a `BoxCollider` and a `CoverSurface`, named `Wall_n`, `Baffle_n`, `Pillar_n`, `LowWall_n`, `Crate_n`, shared obstacle material), and a `NavMeshModifier` set to Not Walkable on every obstacle so no walkable island forms on a wall or crate top. Floor and obstacles use `GameObject.CreatePrimitive` (placeholder only).
- `Actors`: parent of every spawned unit.

A `NavMeshSurface` (collect objects: Children, agent type 0, the project's agent radius 0.5 and height) sits on `Geometry` and `BuildNavMesh()` runs synchronously after `Physics.SyncTransforms()`. `Blackglass.asmdef` gains a reference to `Unity.AI.Navigation` (the package is already in the manifest; no new package).

**Navigation validation** runs after the build: the surface has data; the NavMesh triangulation area is at least a fraction (30%) of the floor area; a complete path (`NavMeshPathStatus.PathComplete`) exists from the friendly spawn point to the centre of every room and to every hostile spawn point; every spawn point snaps to the NavMesh within 1 m. A failure ends the attempt (the root is torn down) and the next attempt starts.

## 6. Cover

`CoverDiscovery` gains `Discover(Transform root, Func<Vector3, bool> isReachable = null)`. It collects `CoverSurface`s under `root` only (the no-argument `Discover()` is unchanged, so the arena and its tests are unaffected), runs the existing `CoverGenerator` and feeds the walkable predicate with the existing `SamplePosition` test **and** the optional reachability test. The mission passes "a complete path exists from the friendly spawn" so unreachable locations never reach the registry.

No second cover representation exists. AI, `MoveToCover`, the cover view and the controller cursor consume the same `CoverLocation`s. Teardown calls `registry.Rebuild(empty)` before destroying anything: every old location is retired, its claim dropped, and it reads as invalid.

## 7. Spawning and wiring

`MissionSpawner` instantiates units under `Actors` at the chosen points (`NavMeshAgent` placed with `Warp` if `isOnNavMesh` is false after instantiation), facing the other team's centre. Before spawning it checks each point with `Physics.CheckCapsule` (unit radius, floor excluded by layer or by testing only against `Geometry` obstacles) so nobody starts inside geometry; units are separated by the layout's 2.5 m rule.

Wiring uses narrow internal `Wire(...)` methods so prefab-tuned values are not overwritten by `Initialize` defaults:

- friendly: `CompanionAI` (`ActiveCharacter`, `Encounter`), `UnitCover` (`CoverRegistry`), `UnitAbilities` (`Encounter`, definitions from the squad slot), `UnitAttacker.ApplyArchetype`;
- hostile: `EnemyAI` (`Encounter`, `CoverRegistry`), `UnitCover`, optional archetype.

The persistent systems are then updated: `Encounter.Initialize(friendlies, hostiles)` (existing, internal), `UnitSelection` roster replaced and selection cleared, `ActiveCharacter.SetUnit(first friendly)`, takeover and follow flags reset to their defaults, pause resumed.

`MissionSlot` data on the director (serialized): friendly slots (`prefab`, `archetype`, `abilities[]`, with the Prototype's trio: Melee, Marksman, Ranged, all with Aimed Shot, Blast, Mend) and hostile slots (`prefab`, optional `archetype`); hostile `i` uses slot `i mod slots`.

## 8. Lifecycle and regeneration

`MissionDirector.Generate(int seed)` (public; also `RegenerateSame()` and `GenerateNew()`) starts a coroutine unless one is running (a second request while `Generating` logs a warning and is ignored). `MissionState`: `Idle`, `Generating`, `Ready`, `Failed`.

1. **Teardown** (also runs first on the very first call, harmlessly): retire cover (`Rebuild(empty)`), clear `Encounter` sides, `UnitSelection` roster and selection, `ActiveCharacter` unit, destroy each tracked unit's `DeathMarker.LastMarker` (death markers are root objects that outlive their unit), destroy `GeneratedMissionRoot` (its `NavMeshSurface` removes its NavMesh data), resume pause. Then wait one frame so `Destroy` completes.
2. **Layout** via `MissionGenerator` (pure, bounded).
3. **Build** geometry, `Physics.SyncTransforms()`, build NavMesh, validate navigation; on failure tear down and continue with the next attempt (the same bounded counter covers pure-layout and navigation failures).
4. **Cover** `Discover(root, reachable)`.
5. **Spawn** squad and hostiles, wire them, update the persistent systems.
6. **Camera** `SetBounds` to the mission bounds plus a margin and snap to the squad.
7. **State = Ready**; the encounter runs under the existing rules (hostiles wait for detection, line of sight and reachability).

On exhausting the attempts: `State = Failed`, one `Debug.LogError` with the seed, every attempt's failure reason and `settings.Describe()`; no units are spawned; the pause service is left running. There are no static registries or singletons, so nothing else can go stale. `TacticalCameraController` gains `SetBounds(Rect)` (the clamp uses it instead of the fixed ±25) and `FocusOn(Vector3)` (a snap, no glide).

**Developer input.** A `Developer` action map with `RegenerateSame` (F6) and `RegenerateNew` (F7), keyboard only, read by `MissionDeveloperInput`, which calls the director and nothing else. The input asset tests use an explicit list of pad actions, so the keyboard-only `Developer` map needs no test change. A new-seed request uses `Environment.TickCount`-derived seed (not `UnityEngine.Random`), then writes it to the director's `seed` field so it shows in the Inspector and HUD.

## 9. Debug

`MissionDebugView` (IMGUI, like `PrototypeHud`; debug only) shows: seed, attempt number and `maxAttempts`, state (and the failure text), rooms, connections, floor area, nav status (built, area ratio, paths valid), cover total / low / tall / corner, friendly and hostile spawn counts and the separation, mission bounds, and the key hints F6/F7. A `MissionGizmoView` draws, with the existing line-material approach, room rectangles, the room connection graph, the two spawn regions and the mission bounds, toggled by a serialized flag (default on). No production UI.

## 10. Configuration (`MissionSettings`)

| Field | Default | Note |
|---|---|---|
| `seed` | 12345 | mission seed (also edited in the Inspector) |
| `gridColumns` / `gridRows` | 4 / 3 | clamped 2–6 |
| `cellSize` | 14 | metres, clamped 12–20 |
| `roomCount` | 6 | clamped 2–`columns×rows` |
| `corridorWidth` | 3 | clamped 3–5 |
| `extraLoops` | 1 | extra connections beyond the tree |
| `bafflesPerRoom` | 1 | max tall wall segments per room |
| `lowCoverDensity` | 3 | low-cover objects per 100 m² of floor |
| `friendlyCount` | 3 | set by the director from the number of friendly slots; clamped 1–6 |
| `hostileCount` | 3 | clamped 1–8 |
| `minTeamSeparation` | 16 | metres, straight line, between teams |
| `maxAttempts` | 20 | clamped 1–100 |

Fixed constants, not exposed: tile 1 m, wall height 3 m and thickness 1 m, low-cover height 1 m, clearance 2 tiles, spawn spacing 2.5 m, minimum walkable area ratio 0.3 (a sanity check against an empty or broken build, not a quality bar).

## 11. Testing

EditMode (pure): `SeededRandom` (sequence, ranges, determinism); `MissionSettings.Validate`; layout determinism (same seed twice equal hash, different seeds differ over a sample of seeds, golden hashes for retained seeds); invariants over many seeds (every floor tile connected, every room reachable, spawns reachable, spawns separated and clear of obstacles, bounds contain everything, no obstacle in a door gap, wall boxes do not overlap floor or each other); wall decomposition (door gap ends; baffles have four ends); bounded failure (an impossible configuration returns a failure with `maxAttempts` reasons, never hangs); the `UnityEngine.Random` isolation test.

PlayMode (real mission in a test-built world and in the `ProceduralMission` scene): the director reaches `Ready` for several seeds and the NavMesh exists; the same seed twice gives the same layout hash and the same cover count; tall-wall ends yield `Corner` locations and low cover yields `Low` face locations; every registry location is reachable from the friendly spawn; units stand on the NavMesh, outside geometry and apart; the teams start at least `minTeamSeparation` apart; regeneration leaves exactly the new units, no old death markers, an empty-then-new registry, retired old locations with no claims, a fresh encounter (full sides, `Ongoing`), no armed ability and no selection; a failed generation (impossible settings) leaves `Failed`, an error with the seed, no units; Move, Attack, MoveToCover, an ability, queue and pause on a generated mission; enemy AI and companions act; victory and defeat resolve; the camera bounds equal the mission bounds. The `Prototype` scene tests must stay green.

Baseline totals are measured by a real run before the plan's totals table is written.

## 12. Rejected alternatives

- BSP rooms with routed corridors (more code and degenerate corridors for no prototype value); hand-made room templates (not procedural).
- `System.Random` or `UnityEngine.Random` for layout (not stable across runtimes, or global state shared with combat).
- Converting `Prototype` in place, or a toggled arena inside it (rewrites ~36 tests, or two worlds in one scene).
- A second cover representation for generated missions (violates decision 023; the generator reads the same boxes).
- NavMesh baked into the scene or `NavMeshBuilder` low-level API (not runtime-generated; the package surface is the simplest supported route).
- Clickable IMGUI debug buttons and an IMGUI seed text field (clicks and keystrokes reach gameplay input).
- Keeping the old mission when generation fails (extra state for a prototype).
- A ScriptableObject for the settings (no sharing need yet).

## 13. Known limitations (expected)

- Axis-aligned, whole-metre geometry only; one floor level; no doors that open or close.
- One mission shape family (rooms, corridors, baffles); no templates, objectives or hazards.
- Enemy placement is spatial only (farthest rooms), with no encounter director.
- Failure leaves an empty scene; the seed must be re-entered to retry.
- Corner cover still has no peek behaviour (decision 023); hostiles still choose only low cover.
- The camera's fixed rig has no minimap and may show the void around the mission.
- The Phase 7 limitation that enemy AI does not use abilities carries over.

## 14. Manual checks for the owner (summary; the full sequence is in the completion report)

Same seed twice; several random seeds; a seed with many tall-wall corners; a seed with significant low cover; controller play (cursor, cover selection, ground and unit ability targeting, camera); keyboard and mouse play; F6/F7 regeneration during combat and while paused.
