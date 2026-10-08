# Phase 11: Battlefield uncertainty and intelligence gathering: design

Status: design approved in conversation 2026-10-08 (with one owner override: a hostile that fires reveals itself); this
file is the written spec for review.

## Goal
Separate what exists in a mission from what the player knows. The mission (layout, NavMesh, colliders, objectives, AI)
is unchanged. A new knowledge layer sits between it and everything the player sees or can target:

```
Actual mission state  ->  Intelligence (knowledge) layer  ->  Player-visible tactical information
```

Knowledge is gained from exploration, friendly line of sight, a hackable camera network and a Recon Scan ability, all
through one reveal API. Hiding is presentation and targeting only. Nothing unknown is destroyed, disabled or moved, and
enemy AI keeps its own perception. With fog off (the code default) the game behaves exactly as at Phase 10.

## Non-goals
Stealth, noise, vision cones as stealth, patrol routes, electronic warfare, false intel, a production minimap, a
production drone, hacking minigames, alarms, turrets, door networks, inventory, loot, campaign, save/load, production UI
or final VFX/audio, new packages, a second level representation, new controller architecture, clearing a last-known
marker by looking at its spot (see Limitations). Phase 12 is not started.

## Understanding (what was asked, what is assumed)
Asked: unknown / discovered / observed states; per-mission starting knowledge (full, partial, none; known objectives with
unknown enemies; known layout with unknown objectives; briefing-known spawn, extraction, one objective, partial layout,
an enemy marker); region discovery through openings by range and line of sight, walls revealing nothing; live enemy
observation with a last-known marker; objective existence separate from knowledge; full usability under tactical pause;
two recon sources (camera + control terminal, Recon Scan ability) behind one API; a prototype map overlay; a complete
leak audit; deterministic generation; reset on regeneration; keyboard/mouse and controller parity; truth-vs-player debug.

Assumed (all confirmed in the design conversation unless marked): `ProceduralMission` ships with a fog preset while the
code default is full knowledge (confirmed); Recon Scan belongs to Kestrel (confirmed); the scan ignores walls
(confirmed); camera intel stays live after the hack, configurable (confirmed); unknown objectives are listed vaguely
under a briefing flag (confirmed); ground abilities may target undiscovered ground (decision below); a firing hostile
is exposed (owner override).

## Facts the design rests on (current code)
- `MissionDirector.TryAttempt` runs teardown -> layout -> objectives -> build (with a pre-bake content callback) -> NavMesh
  validate -> cover discovery -> spawn -> runtime -> camera. `MissionSystems` holds the persistent, per-mission-refilled
  systems; `ResetSystems()` clears them. Everything generated lives under `GeneratedMissionRoot`.
- `MissionLayout` has `Rooms` (index, cell, rect), `Connections` (RoomA, RoomB, `Strip` RectInt), `FloorRects`, `Boxes`,
  spawns and a golden `Hash`. `ObjectivePlan` uses a salted second `SeededRandom` stream so layout hashes never move.
- `MissionObjective` already has `IsKnown/SetKnown` (decision 033); `MissionHudText.Panel` and `MarkerLabel` already gate on
  it. `EliminateHostilesObjective.Describe` prints "(living/total)", a count leak.
- `PointerTargetResolver.Resolve` does one raycast and classifies friendly -> any alive `Health` hostile -> interactable ->
  cover (with an `acceptAny` predicate hook) -> ground. `TacticalCursor` snaps with `NearestHostile`,
  `interactables.NearestAvailable`, `CycleTarget`, `PickAttackTarget`, `SoftTarget`, all over `encounter.Hostiles`.
- `AbilityDefinition` is immutable SO data; the `Effect` getter forces `Damage` for Ground mode. Ground abilities already
  work in direct control, queueing, pause and controller (`SnapTo=Ground`, range ring, aim ring).
- `UnitAttacker` raises `Attacked(Health target)` on a hit and `Missed(Health target)` on a miss; `AttackLineView` and the
  animation driver already subscribe.
- Hostile-position leaks today: `PrototypeHud.DrawUnitLabels`/`DescribeSides`, `AttackLineView`, `CoverView` (claimed
  locations), `TacticalCursorView.Describe`, `MissionDebugText`/`MissionGizmoView`, the hostile collider hit in the pointer
  resolver, `CompanionAI.ChooseAssistTarget`, `EliminateHostilesObjective.Describe`.
- Developer keys: F6 same seed, F7 new seed, F8 visuals, F9 operatives, F10 +XP, F11 reset (the `Developer` input map).

## Architecture
```
IntelligenceSettings (per mission, in MissionSettings)
        |
RegionMap (pure, from MissionLayout)    SecurityPlan (pure, salted RNG)
        |                                        |
   MissionIntelligence  <-- reveal API --  sources, evaluated each pass by IntelligenceService:
   (pure model: region, enemy,               friendly observers | compromised cameras
    device, objective knowledge)             Recon Scan pulses  | hostile exposure (firing)
        |                                    exploration (standing in a region)
        +--> read-only queries --> targeting, cursor, HUD, labels, cover view, overlay, presenters
```

New folder `Assets/_Project/Scripts/Intelligence/`, flat namespace `Blackglass`. Small files, one purpose each.

### Knowledge model
- **`KnowledgeState`**: `Unknown`, `Discovered`, `Observed`. One enum for regions, devices and enemies. Meaning: regions and
  devices: Discovered = known and permanent, Observed = in sight right now. Enemies: Discovered = last known (a marker),
  Observed = live.
- **`MissionIntelligence`** (plain C#, no UnityEngine objects held beyond `Health` keys, no randomness): holds region
  states, enemy intel (`Health` -> state, last-known position, time of last sighting), device states and the objective
  knowledge writer (it is the only caller of `MissionObjective.SetKnown`).
- **Reveal API** (the only way knowledge changes): `RevealRegion(int)`, `RevealArea(Vector3 centre, float radius)`,
  `RevealDevice(device)`, `RevealObjective(objective)`, `ObserveEnemy(Health, Vector3 position)`. Permanent facts
  (regions, devices, objectives) only ever go up. Enemy state is rebuilt each pass: the pass calls `BeginPass()`, sources
  call `ObserveEnemy`/`MarkRegionObserved`, `EndPass()` demotes every enemy and region not marked this pass (enemy ->
  Discovered with its last position and stops updating; region Observed -> Discovered). Dead hostiles leave the table.
- Queries: `StateOfRegion(int)`, `StateOfPoint(Vector3)`, `StateOfEnemy(Health)`, `LastKnownPosition(Health, out pos)`,
  `StateOfDevice(device)`, `IsTargetable(Health)` (== Observed), plus a `Changed` event for presenters.

### Region map
`RegionMap` is derived from `MissionLayout`, not a second representation. Regions are the rooms plus the connection strips
(a strip is the opening between rooms). It gives tile -> region, region rects/centres, adjacency, and **sample points**
(region centre plus a coarse grid of floor points, about every 3 tiles, at floor height + 0.5 m). Wall tiles map to the set
of adjacent regions.

Region discovery rule per pass: an observer's own region is Observed. Any other region whose bounds intersect the
observer's range circle is Observed when at least one sample point is within range and passes the line-of-sight test
(combat's blocker mask). A closed wall therefore reveals nothing behind it, and a door or corridor mouth reveals the part
of the next region the aperture allows. Observed regions are Discovered permanently.

### Sources (each gives regions, enemies, devices)
1. **Friendly observers**: every living friendly, 360 degrees, `observationRange` (default 14 m, above the 12 m enemy
   detection range so the player is not out-seen by default), LOS for enemies and sample points.
2. **Camera network**: after the control terminal is used, each camera observes by position + facing + half-angle + range +
   LOS (camera lens point to target; no unit eye offset). Stays live while the network is compromised
   (`cameraStaysLive`, default true; false = one snapshot at the moment of the hack).
3. **Recon Scan**: a ground-targeted ability with a new `Reveal` effect. At the target point it discovers every region
   intersecting the radius, discovers devices inside it, and observes hostiles inside it for `revealSeconds`; the pulse ignores
   walls (a scan is not sight), then hostiles drop to last known.
4. **Exposure by firing** (owner override): each `UnitAttacker.Attacked`/`Missed` event of a hostile exposes that hostile
   (hit or miss) for `exposureSeconds` (default 3 s of simulation time). It is Observed, with a live position, for that
   long wherever it is, then becomes last-known at its last position. Subscribed by the service when a hostile is
   registered, so there is no combat change.
5. **Briefing** (starting knowledge, applied once at mission start through the same API).

### Service and cadence
`IntelligenceService` (MonoBehaviour on the scene's systems object, like `Encounter`) owns the model and the pass.
Passes run every 0.2 s of simulation time (`Time.time`; paused time never advances them) and immediately at mission
start and after a scan. The service exposes a read-only `Knowledge` view. **Every consumer takes the service as an optional
serialized reference; null means everything known**, which keeps the `Prototype` scene and all existing tests unchanged.
The director creates/refills it in `TryAttempt` and clears it in `ResetSystems()`, like the other systems.

### Configuration
`IntelligenceSettings` (serializable, in `MissionSettings`, deep-cloned in `Validated()`, summarised in `Describe()`):
`fogEnabled` (default false = nothing hidden); `map` {Full, Partial, None}; `objectives` {All, ExtractionOnly, None};
`showUnknownObjectives` (vague listing); `enemyMarkersAtStart` (count of hostiles that start as last-known markers);
`observationRange`; `cameraCount` (default 0); `cameraRange`; `cameraFov`; `cameraStaysLive`; `exposureSeconds`.
Partial map = friendly spawn region(s) + extraction region + regions one connection hop from the spawn. No random choice,
so determinism is untouched. Static presets: **Full** (default), **Blind** (no map, extraction only, hostiles hidden,
cameras 3; the scene default), **Layout known** (map Full, objectives None), **Objectives known** (map None, objectives All,
enemies hidden), **Briefed** (Partial, one known objective, one enemy marker). The Developer map gets a key to cycle
presets and regenerate the same seed.

### Security devices
- **`SecurityPlan`** (pure data + pinned hash) from a new `SecurityPlacer` on its own salted `SeededRandom` stream, run
  after the objective plan. It never touches the layout or objective hash. Contents: one camera-control terminal tile and
  `cameraCount` camera mounts (tile on a free wall-adjacent floor tile, facing into its region). The terminal avoids spawn
  tiles, the objective terminal and the extraction zone; cameras are never placed in a friendly spawn room, so they
  cover rooms and corridors the player has not entered.
- **Control terminal**: a second `MissionInteractable`, built pre-bake by generalising `MissionContent.AddTerminal` (solid
  cube + NavMesh modifier). `MissionInteractable` gains a `Completed` event (today only `IsCompleted` polling). It is
  available from mission start but, as a device, is Unknown until discovered (unknown terminals cannot be targeted or
  interacted with). Completion calls `CameraNetwork.Compromise()`. No hacking minigame.
- **Cameras**: non-collider wall mounts (a small dark housing + lens; no NavMesh effect), discoverable devices with a
  `KnowledgeState`. Before compromise they are inert; after, they observe as in Sources.
- **`CameraNetwork`** (plain C#): the cameras, the compromised flag, the per-pass `Observe(intelligence)` call.

### Recon Scan
`AbilityEffect` gains `Reveal`; `AbilityDefinition` gains `revealSeconds`. `Reveal` requires Ground target mode; the `Effect`
getter, `OnValidate` and `Create` rules are extended accordingly; `AbilityDescriptions` describes it. `UnitAbilities` gets
the intelligence service (set by `MissionSpawner`, null = effect does nothing); `Apply` routes `Reveal` to the service's
scan call. Because it is a Ground ability, direct control, queueing, tactical pause, range ring, aim ring, keyboard/mouse
and controller all work through the existing path with no new input. Asset: *Recon Scan* (range 18 m, radius 12 m,
cooldown 25 s, `revealSeconds` 6, no LOS needed), added to Kestrel as her second ability (Darius or Sable play it by Tab).

### Presentation
- **Fog**: `FogPresenter` builds, from `RegionMap` and the layout, collider-free volumes with an unlit dark material: one
  per region rect and one per wall box not inside a region. Region volume by state: Unknown -> opaque; Discovered -> a
  translucent veil (visible-but-not-live); Observed -> hidden. A wall volume is opaque unless any adjacent region is
  Discovered or Observed. Theme-independent (works with the modular visuals and with gameplay cubes), no change to the
  environment planner/builder. Volumes sit under `GeneratedMissionRoot`, so regeneration destroys them. If the translucent
  URP material proves fragile, Discovered falls back to hidden (documented) and the map overlay carries the difference.
- **Hostiles**: `HostilePresenter` on each hostile (added by the spawner) toggles `Renderer.forceRenderingOff` and its
  line/label children from `StateOfEnemy` (not `enabled`, which other code toggles), and shows a simple last-known
  marker (flat ring + "last seen" label at the stored position) for Discovered hostiles. Colliders stay.
- **Tactical map overlay**: `IntelMapView` (IMGUI, scaled to a corner, toggle key; works while paused) draws region rects
  coloured by state (unknown dark, discovered grey, observed bright), friendlies, observed enemies, last-known markers,
  discovered devices and known objective/extraction markers.
- **Objectives**: unknown objectives no longer vanish; with `showUnknownObjectives` the HUD lists their vague title
  ("Locate the data terminal") with no location, marker or progress. `MissionObjective` gains a `VagueTitle`. An Interact
  objective becomes known when its region is Observed (seen, so a mapped layout keeps it unknown); Eliminate when the first hostile is observed; extraction per
  settings (default known). `EliminateHostilesObjective.Describe` no longer prints living/total while intel is on: it
  shows kills ("Eliminate hostiles (3 down)"). Mission completion logic never reads knowledge.

### Targeting and anti-leak rules
1. An enemy is a target iff `Observed`. Last-known markers are never targets. Single-target abilities, Attack, the soft
   target, cursor snap, target cycling and controller snapping all go through one predicate
   (`HostileTargets` gains a filter; `TacticalCursor.NearestHostile/CycleTarget/PickAttackTarget/SoftTarget`,
   `AbilityTargeting.TargetHealth/Check` and `CompanionAI.ChooseAssistTarget` use it).
2. `PointerTargetResolver` stops using a single raycast result: it casts `RaycastAll`, skips hits on non-targetable
   hostile colliders, undiscovered/unknown devices, and cover in an Unknown region (via the `acceptAny` hook), and takes the
   first remaining hit. Clicking where an unseen enemy stands resolves to ground/whatever is behind.
3. `interactables.NearestAvailable` and `PlayerCommandInput.TryFindReachableTerminal` skip unknown devices.
4. **Ground abilities may target any point within normal range rules, including undiscovered ground** (decision): Recon Scan
   exists to look into the unknown, and a damaging ground ability into the dark is blind fire. Previews (`AreaHits` via
   `AbilityDescriptions.Preview`) list only observed hostiles; the real effect still hits every hostile the rules
   allow.
5. Gated presentation: unit labels (health, AI state, LOS, cover) for non-observed hostiles, hostile counts in
   `DescribeSides`, `AttackLineView` lines whose shooter is not observed (a firing hostile is exposed, so its line shows),
   `CoverView` locations in Unknown regions and claims by non-observed hostiles, `TacticalCursorView` text for hidden
   things, `MissionDebugText`/`MissionGizmoView` (only in truth view), objective markers (already gated).
6. Enemy AI is not fogged; companions share squad intel (their sight feeds the same model).

### Lifecycle, pause, determinism, input
- **Regeneration**: the director replaces the model, `RegionMap`, `SecurityPlan`, camera network, fog volumes and
  presenters with each mission; `ResetSystems()` clears the service. No intel survives F6/F7.
- **Pause**: passes, exposure timers and scan pulses use scaled time; nothing advances while paused; all queries, the cursor,
  the overlay and the debug views keep working.
- **Determinism**: intel has no randomness; the security plan has its own salted stream; layout and objective hashes are
  unchanged and re-asserted by tests; hostile positions and spawns do not depend on fog.
- **Input**: no new input framework. Scan uses the existing ability slots; targeting gates sit in `TacticalCursor`/resolver
  shared by mouse and controller. New developer actions only: truth view, preset cycle, overlay toggle.

### Debug and truth view
`TruthView` (a service flag, default off, toggled by a Developer-map key, F12) is display-only: it shows everything as it is
with state tags (unknown/discovered/observed on regions, enemies and devices), camera cones, the scan radius and the
actual-vs-known table; targeting stays restricted so truth view never changes play. The existing mission debug text and
gizmos are shown only with truth view when fog is on. None of this is reachable in normal play.

## Testing
- **EditMode (pure)**: `RegionMap` from golden layouts (tile->region, adjacency, sample points); `MissionIntelligence`
  reveal/demotion rules, last-known behaviour, exposure expiry; `ObservationRules` (range, cone, circle); `SecurityPlacer`
  determinism + pinned hash + hashes of layout/objective plans unchanged; settings clone/validate/presets;
  `AbilityDefinition` Reveal rules; objective knowledge rules.
- **PlayMode (real walls, doors, NavMesh)**: a closed wall does not reveal, an open door reveals partially; enemy observed
  then lost -> marker; firing hostile beyond range is exposed then lost; pointer resolver and cursor/cycle/soft target skip
  unobserved; controller snap path; camera hack grants intel; scan ability through the existing queue/pause path;
  regeneration reset; same seed with fog on/off identical mission; truth view display-only; labels/counts gated.
- **Regressions**: the existing suites stay green (the two procedural scene suites opt into full knowledge via a helper
  where they click enemies or count interactables). Baseline to be measured before totals are written: EditMode 788 and
  PlayMode 729 were green at Phase 10.
- **Manual**: `Docs/Phase11-ManualTests.md` (full vs no knowledge, exploring, LOS reveal, last-known, target-cycling leak
  test, camera hack, scan, unknown/revealed objectives, pause with intel, controller vs keyboard targeting, regeneration,
  truth vs normal).

## Documentation
Decision record **037** in `Docs/Decisions.md`: knowledge-state model, region discovery, last-known behaviour, objective
knowledge separation, hacked-camera behaviour, recon-tool behaviour, firing exposure, anti-leak rules, ground-ability
targeting choice.

## Limitations and concerns to report
- A unit that is already chasing a target keeps chasing it after it leaves observation (its movement can hint at the
  target's position). Not fixed in this phase.
- Last-known markers never clear by looking at their spot; they only update when the enemy is re-observed.
- Passes run every 0.2 s, so a newly seen enemy appears up to 0.2 s late.
- Death marker discs are not gated.
- The translucent "discovered" veil depends on a runtime URP transparent material (fallback described above).
- The scan ignores walls and so can place a marker on an enemy behind several walls, by design.
- Companion assist acts only on observed enemies, so a shooter beyond sight is answered only after it exposes itself.
  `AutoRetaliate` is not gated: it reacts to damage taken, not to knowledge.
