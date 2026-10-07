# Phase 9.5B: Modular environment visual layer: design

Status: approved in conversation 2026-10-07; this file is the written spec.

## Goal
Replace "generated missions are raw developer cubes" with a visual-module layer. The generator keeps reasoning in
gameplay concepts; swappable visual prefabs decorate gameplay geometry. Deliver the pipeline plus a ~12-piece primitive kit
in one prototype theme ("Corporate Prototype": dark near-future corporate/urban). Not a production-art phase.

## Non-goals
Multiple themes, final art/textures/lighting/post-processing, clutter framework, decals, new gameplay, new player input,
render-pipeline change, new packages, character changes, layout algorithm changes.

## Facts the design rests on (current code)
- `MissionLayout`: `FloorRects`, `Boxes` (`MissionBox{Kind Wall|Baffle|Pillar|LowWall|Crate, Footprint (tiles), Height}`),
  1 m tiles; wall 3 m, low 1 m, floor 0.2 m thick. Walls = 1-tile-thick maximal runs overlapping at junctions (decision
  032). Doorways are gaps in the wall ring. Layout/objective randomness uses independent `SeededRandom` streams.
- `MissionBuilder.Build` makes cube primitives that are render mesh and collider at once (CoverSurface +
  NavMeshModifier(NotWalkable) for obstacles), bakes the NavMesh from child colliders. `MissionContent.AddTerminal` does the
  same for the terminal. Everything lives under `GeneratedMissionRoot`; the director destroys that root on regeneration.
- The scale (1 m grid, 3 m walls, 1 m low cover) stays. Nothing is rescaled.

## Architecture
```
MissionLayout (pure, unchanged)
   -> EnvironmentVisualPlanner (pure)      -> List<VisualPlacement>
   -> EnvironmentTheme (ScriptableObject)  -> element -> prefab variants[]
   -> EnvironmentVisualBuilder (scene)     -> instances under Root/Visuals
Gameplay colliders (Root/Geometry) are separate and authoritative.
```

### Gameplay layer (authoritative, no art)
`MissionBuilder` still creates one simple object per floor rect / box under `Geometry` with a `BoxCollider`,
`CoverSurface` and `NavMeshModifier`, exactly as now, but with **no renderer/mesh filter**. Nav, cover and LOS read only
these. Visual prefabs never carry colliders, so trim cannot create cover points or NavMesh holes. A debug mode shows these
gameplay boxes (translucent) while visuals are hidden.

### Semantic elements
`EnvironmentElement` enum: `Floor, WallStraight, WallCorner, WallEnd, WallJunction, WallPillar, DoorFrame, LowCover,
Pillar, Crate, Terminal, LightFixture`. (Tall cover = wall tiles, as the generator's Baffle/Wall are tall boxes; "tall-wall
end" = `WallEnd`.)

### Planner (pure, EditMode-tested)
Rasterise all Wall/Baffle boxes to a tile mask; for each tile classify by 4-neighbours: 1 neighbour -> WallEnd, 2 opposite
-> WallStraight, 2 adjacent -> WallCorner, 3/4 -> WallJunction, 0 -> WallPillar. Yaw is the multiple of 90 degrees that maps
the module's authored orientation to the neighbour pattern. Pillar box -> Pillar; LowWall box -> one LowCover per tile
(runs of >= 2 may use the "long" variant key); Crate -> Crate; floor tiles -> Floor per tile; DoorFrame where a wall-ring
gap (floor tile flanked on both sides by wall tiles along a gap axis) occurs; LightFixture on a deterministic subset of
wall tiles facing a floor tile (cap one per room-sized area). Output is plain data (element, world position, yaw, tile,
variantKey); no UnityEngine objects, no randomness.

### Theme and resolver
`EnvironmentTheme : ScriptableObject` holds `List<Entry{element, GameObject[] variants}>` and a `salt`. `Resolve(element,
tile, seed)` returns a variant. Missing entry: a generated magenta placeholder cube + one warning per element. A second theme
is a new asset; no generator change.

### Deterministic variants
`variantIndex = Hash(seed, theme.salt, element, tile.x, tile.y) % variants.Length` (stateless SplitMix finisher). No shared
stream, no draw order dependence: adding a decorative choice never shifts any other, and layout/objective streams are not
touched. Variant choice never changes gameplay meaning (colliders come from the gameplay layer).

### Builder and ownership
`MissionBuilder.Build` gains a theme parameter (null = no visuals; raw gameplay boxes get the old placeholder material so
existing tests/tools still work). Visuals instantiate under `Root/Visuals`, grouped by element, so regeneration cleanup
stays the existing root destroy. Lights are children of the same root. `GeneratedMission` gains `Visuals` (Transform).

### Terminal
`Terminal` GameObject keeps the box collider, NavMeshModifier, `MissionInteractable`, `ObjectiveMarker` (no renderer).
Child `VisualRoot` holds the instantiated `Terminal` prefab, so the look can be swapped without touching objective code.
Existing sizes (0.8 x 1.2 x 0.8 m) are kept as the collider; the visual fits that footprint.

### Debug view
`MissionVisualToggle` (director property + developer input action on the existing developer map) hides `Visuals` and shows
the gameplay boxes translucent. Not gameplay input.

## Conventions (documented in the asset guide)
- 1 unit = 1 m, Y up, +Z = module front (the face looking into the room for wall details/terminal).
- Floor tile 1 x 0.2 x 1, pivot top-centre of tile (top surface at y=0 after placement, centre in XZ).
- Wall tile 1 x 3 x 1, pivot bottom-centre; corner/junction/pillar the same footprint, pivot bottom-centre of the tile.
  Straight authored running along X; end cap authored open toward +Z; corner authored joining +Z and +X.
- Low cover 1 x 1 x 1 per tile (short = 1 tile, long variant = 1 x 1 x 2 for runs); pivot bottom-centre.
- Crate/cabinet <= 1 x 1.2 x 1; terminal 0.8 x 1.2 x 0.8, pivot base-centre; doorway clear width 1.0 m+ and clear
  height 2.4 m; light fixture mounts at 2.6 m on wall face.
- Corrective rotation/offset is done inside the prefab (child pivot), never in generator code.

## Kit (primitive prefabs generated by an editor menu script, then freely overwritable)
Floor tile; wall straight, corner, end, junction; wall pillar; door frame; pillar; low cover short and long (2 variants for
short); crate/cabinet; terminal; light fixture (emissive strip). 6 shared URP materials (concrete, dark metal, floor,
prop, emissive accent, display). Lighting: dim directional + ambient tweak in the ProceduralMission scene; emissive accents,
no per-wall real lights (at most one small point light per room, optional/off by default).
Folders: `Assets/_Project/Environment/{Themes/CorporatePrototype, Materials, Prefabs}`.

## Validation
EditMode: planner classification (every neighbour pattern), counts vs layout, no-visual-randomness (planner/theme leave
layout hash and objective plan unchanged; same seed gives identical placements and variants; different theme salt changes
variants only), orientation table. PlayMode: build with theme -> renderer-less gameplay boxes, Visuals have no colliders,
cover count and NavMesh area identical to the no-theme build, doorways traversable, terminal VisualRoot present and
interaction still works, regeneration leaves no stale objects (root count, lights, renderers). Full existing suite must
stay green. Manual procedure and completion report as specified in the phase brief.

## Risks
- ~1-2k wall/floor instances per mission; shared materials, no per-instance materials; static-batch if profiling asks.
- Per-tile wall modules on 1 m thick walls make a chunky look; accepted for the prototype.
- Existing tests that look for primitive renderers or materials on geometry boxes may need small updates.
