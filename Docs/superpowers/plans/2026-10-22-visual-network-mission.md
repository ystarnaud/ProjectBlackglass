# Visually Playable Local Network Mission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Status: **approved by the owner with binding amendments (2026-10-22, "Owner rulings" below); executing Native.** Base: `main` at
`c061ff9` (the merged loopback fixture checkpoint).
Branch at execution time: `phase13-visual-net-mission`.

**Goal:** A human with mouse and keyboard plays fixture 03 in a separate Windows client process against the local fixture server
over WebSockets. The client shows the real map and the objectives; the player finds the hostile, engages it, walks around the
visible obstacles to extraction, and sees the mission finish. No server-log coordinates, harness, warp or scripted completion.

**Architecture:** The server builds one explicit, bounded, whitelisted projection of the public map: grid size, floor
rectangles, wall and obstacle boxes, and closed-container tiles. It also projects the known objectives (titles, states, and the
extraction point). The projection is sent only when the mission's intelligence configuration is fog on, `MapKnowledge.Full`
and `ObjectiveKnowledge.All`, with no terminal and no cameras. It goes only to welcomed connections that own units. The server
also relays the mission end, and each resolved shot to connections that already receive both units' avatars.

A new client scene, `NetPlayClient`, rebuilds a seedless `MissionLayout` from the projection. It draws it with the existing
environment theme (`EnvironmentVisualBuilder`), placeholder cubes and the existing character visual. It commands through the
existing four requests with offline-style controls and the offline `TacticalCameraController`. The scripted net-test client,
its harness and every offline scene stay as they are.

**Tech Stack:** Unity 6000.3.25f1, C#, FishNet broadcasts over the Unity Transport WebSocket transport, the Input System
(`BlackglassControls.inputactions`), URP, NUnit EditMode and PlayMode tests.

**Spec:** the owner's brief "Project Blackglass — Next Priority: Visually Playable Network Mission" (2026-10-22). Its ten
requirements are quoted in "Requirements traceability" below. The design part of this document (sections 1 to 6) argues from
it. It also builds on decision 047 and `Docs/superpowers/specs/2026-10-12-hidden-information-design.md` (§3.8, §3.12).

---

## 0. Decisions for the owner (please confirm or change before execution)

| # | Decision | Recommendation |
|---|---|---|
| D1 | The map/objective projection is ordinary protocol, not fixture-only. A release server build also sends it, but only when the mission is fog on + Full + All with no terminal and no cameras. Every other mission gets nothing. | Yes. It is the intended online behaviour for a Full-knowledge mission. Fixture-guarding it would only make the release build less useful. |
| D2 | Closed loot containers are sent as crate obstacles, each a tile and nothing else (no index, no contents, no item). The offline player sees the same crates under map Full, and the crates block movement, so leaving them out would make units path around invisible objects. | Yes. |
| D3 | **Seed-fingerprint risk.** The full geometry is a deterministic function of the seed, and the client build contains the generator. So anyone holding the geometry can brute-force the 31-bit seed offline, then re-derive the hostile spawns and the loot rolls. Sending the map makes the seed recoverable in practice even though the seed itself is never sent. This is the "geometry secrecy" item the project already deferred (spec §3.12, decision 047, CLAUDE.md out-of-scope list). | Accept for this local, loopback-only, development slice (fixture seed 1 is public anyway). Record it in decision 052 as an open blocker before any public or player-facing online use. The likely fix is to derive hidden placements (spawns, loot rolls) from a second backend secret that does not shape the geometry. That fix is not in this plan. |
| D4 | Four new server-to-client messages: `NetMapMessage`, `NetObjectivesMessage`, `NetMissionEndMessage`, `NetShotMessage`. There is no new client-to-server message, and no change to the gate, admission, budgets, tokens or replicated avatar fields. | Yes. |
| D5 | Avatars use the existing `EnemyUnit_Visual` model with the existing rifle (`RifleVisual.asset`) for every unit. A coloured ground ring tells sides apart (blue own, red hostile), and animation is driven from replicated state. Operative names are not on the wire, so the HUD says "Operative 1", "Operative 2". | Yes. Placeholder-grade but recognisable. Names can come with Stage 13's loadout data. |
| D6 | A separate scene and player, `NetPlayClient` (`Tools/build-netplay-client.sh`). `NetTestClient`, `NetClientView`, `NetTestControls` and the harness are unchanged. The one exception: the harness's allowed-message list gains the four new message names. | Yes. The scripted checkpoint and its tests keep working untouched. |
| D7 | Controls follow the offline scheme: left click selects, moves or attacks; WASD pans; Q/E or right-drag rotates; the wheel zooms; Shift queues; X stops; Tab cycles operatives; Ctrl+A selects all. F follows the selected operative, and while following, WASD steers it (`NetMoveIntent`); Esc ends follow. There is no tactical pause online, so Space does nothing. | Yes. |

## Owner rulings (2026-10-22, binding; they override any task text below that disagrees)

The owner approved the direction with these amendments. Execution: **Native, sequential, test-first**; no parallel Unity
instances and no parallel implementers on overlapping files. Branch `phase13-visual-net-mission` from `c061ff9`.

- **D1 — modified: local-development-only.** Every server-side emission of the four new messages (map, objectives, mission end,
  shots) is compiled only under `#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE` (the existing fixture-build define), **and** needs
  the approved runtime profile: fog on, `MapKnowledge.Full`, `ObjectiveKnowledge.All`, no terminal, no cameras, and a welcomed
  connection that owns at least one operative. An ordinary release server never emits them, whatever its mission profile; there
  is no runtime flag that can turn them on. Implementation: `NetMapProjection` and `NetShotRelay` are whole-file guarded; every
  briefing, objectives, end and shot field and method in `NetServer` sits inside the guard. `NetMapBuildGuardTests` (EditMode,
  source audit) fails if any of those files or regions loses its guard, or if a non-guarded line of `NetServer` mentions them.
  The client side (types, validation, wire structs, decoding, renderer) is not guarded: a release server simply never sends.
  Ruling (implementer): the shot relay and the end message count as part of the visual feature, so they are guarded too.
- **D2 — approved.** Closed crates as geometry only: never contents, item definitions, quantities, instance ids or provenance.
- **D3 — approved for local development only.** Decision 052 records that the fixed-salt deterministic streams derive layout,
  objective, security and loot information from one seed, so a full map may enable seed recovery and prediction of hidden
  information. Accepted only for the local fixture playtest; a **mandatory blocker** before public multiplayer or distribution to
  untrusted clients. Leaving the numeric seed out of the message is not a security solution. The production generator, backend
  schema and B25 contracts are not modified.
- **D4 — approved.** Exactly four new server-to-client messages; no client-to-server request; the gate is not bypassed.
- **D5 — approved.** Existing model and rifle; friendly, hostile and selected states visibly distinct; the model is cosmetic and
  never determines authoritative equipment.
- **D6 — approved.** Separate `NetPlayClient` scene and Windows player; `NetTestClient` and its harness preserved.
- **D7 — approved.** Click select/move/attack; WASD pan; Q/E rotate; wheel zoom; F toggles follow with direct steering; no
  tactical pause online. Camera input and operative movement stay clearly distinct when follow changes: WASD pans the camera
  unless follow is on, and then it only steers (camera pan disabled); leaving follow releases the intent before pan resumes.
  `NetPlayControlsPlayTests.Wasd_pans_or_steers_never_both` pins it.

**Mandatory additions to the tasks:**
- **Versioned payloads.** All four messages carry a `Format` byte (1). The client refuses any other format and draws nothing.
  `NetMapWireTests` covers an unknown format for each message.
- **Largest map over the real transport.** `NetMapSamples.Largest()` builds a valid map at every protocol limit (128 x 128 grid,
  1024 floors, 2048 boxes, 64 containers). `NetBriefingPlayTests.The_largest_map_crosses_the_websocket_transport` sends it over
  the in-process loopback (WebSocket) and checks the client decodes it equal. `NetPlayTwoProcessPlayTests` hosts the server in the
  test runner and launches the built `NetPlayClient.exe` as a **separate process** (real two-process WebSocket); the client's log
  must report the largest map accepted. It needs the built client, so it is ignored unless `BLACKGLASS_NETPLAY_CLIENT_EXE` is set;
  `Tools/check-netplay-two-process.sh` sets it and runs it, and Task 10 records the run. The server takes the synthetic map through
  an internal test hook that exists only under the D1 guard (never a command-line option).
- **No protected payload to the wrong connections.** `NetBriefingPlayTests` proves a connected-but-not-hello'd connection, a
  welcomed connection that owns no operative, and a refused hello each receive no map, objectives, end or shot message, over a
  window that includes an objective change, a shot and the mission end.
- **Review checkpoint 1.** After Tasks 1 to 4 (protocol, serialization, build guards, projection, shot relay): an independent
  security review on Opus (high effort). Every Critical and Important finding is closed before Task 5 connects the renderer.
- **Review checkpoint 2.** After the full client: complete EditMode and PlayMode suites sequentially, the existing networking
  harness, the new visual tests, and a fresh independent final review.
- **Final acceptance is the owner's** manual, player-driven run (local Windows server plus `NetPlayClient`). Automated tests are
  never reported as a substitute. No log-coordinate assistance, scripted completion, warps or hidden-information cheats.
- No mission VM, Stage 10 agent, backend deployment, B24 work, Stage 13 or Stage 14 work. The owner's two Unity settings files and
  uncommitted `.claude` content stay untouched. No merge or push without owner review.

## Requirements traceability

| Owner requirement | Where |
|---|---|
| 1. Preserve the server, FishNet, gate, visibility, snapshot operatives, B25, manifests | §2 "Unchanged"; Global Constraints; Task 3 and Task 4 touch only sending code; Task 9's unassisted test asserts the frozen Success manifest. |
| 2. A player-facing client, separate or cleanly layered | D6; Task 8 (scene, bootstrap, build). |
| 3. Render floors, rooms, walls, corridors, obstacles at correct coordinates, reusing MissionLayout semantics and assets | §3.3; Task 2 (`NetMapGeometry.ToLayout`), Task 5 (`NetMapView` with `EnvironmentVisualBuilder`); PlayMode cube-by-cube match against the server's geometry. |
| 4. Full/All only; an explicit bounded projection; never the seed, hidden hostiles, loot details, undiscovered intelligence or raw MissionLayout | §3.1 and §3.2; Task 1 (bounds), Task 2 (whitelist and refusals), Task 3 (participants only); D3. |
| 5. Server-controlled hostile visibility, frozen last-known; no client simulation | §3.5; avatars are only what FishNet replicates; last-known markers come from `NetKnowledgeMessage`; the client has no NavMesh, no colliders and no game rules. |
| 6. Pan, zoom, controlled follow, visible selection, commands relative to the displayed environment | Task 7 (`TacticalCameraController`, `NetPick`, `NetSelection`, follow). |
| 7. Avatars, health, attack feedback, objectives, extraction, success/failure | Task 6 (avatars, tracers, hit and miss), Task 5 (extraction zone), Task 8 (HUD and end banner). |
| 8. Only MoveIntent, MoveTo, Attack, Stop | Task 7 sends only these; Global Constraints. |
| 9. Two local Windows processes, human, unassisted | Task 10 (builds, `Tools/run-local-netplay.sh`, the owner's playtest); Task 9 automates the same path without a human. |
| 10. Tests: geometry match, no disclosure, invalid/excessive payloads fail safely, offline unchanged | Tasks 1, 2, 3, 5 and 9 (see each task's tests); `OfflineSceneGuardTests`. |

## Global Constraints

- Unity 6000.3.25f1. Unity processes run one at a time; this PC is low on memory. After a low-memory reap, do not restart the killed command on your own.
- Preserve: `ServerCommandGate`, `NetSessionHost`, `NetAdmission`, `NetBudget`, `NetVisibility`, `NetAttackGuard`, `NetUnitState`'s SyncVars, `AttemptSession`, the B25 container fill, `ManifestBuilder`, the token file format.
- Client-to-server requests stay exactly `NetMoveIntent`, `NetMoveTo`, `NetAttack`, `NetStop`. No item, ability or interaction request (those are Stage 14).
- Never send or log an attempt's seed. No message or struct field may be named after `seed`, `plan`, `spawn` or `layout` (`NetMessageShapeTests`).
- `Assets/_Project/Scripts/Net/Core` references no networking library (`NetSourceAuditTests`).
- Offline scenes (`ProceduralMission`, `Prototype`), their input, HUD and gameplay are unchanged. No file under `Scripts/Mission`, `Scripts/Environment`, `Scripts/CameraControl`, `Scripts/Units`, `Scripts/Hud` or `Scripts/Presentation` is modified.
- Do not modify `Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset` or anything under `.claude/`.
- No Linux mission VM, no backend deployment, no Stage 10 change, no B24 work, no production network exposure. The server binds `127.0.0.1` only (unchanged).
- Placeholder art is acceptable. Do not import new assets.
- Stage explicit paths; never `git commit -a`. Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. No merge or push without the owner's approval.

## Review Focus

These failure modes are implied by the brief but easy to miss. Each has a test in the task named.

1. **A click on a HUD panel also commands the world.** A reasonable player expects a click on the objectives or squad panel to do nothing in the world. Test: `NetPlayTextTests.HudPanels_CoverTheirScreenRects` and `NetPlayControlsPlayTests.A_click_on_a_hud_panel_sends_nothing` (Task 8).
2. **The server goes away mid-mission.** The client must show "Disconnected" and stop sending. `NetClient.Send` would throw on a null manager. Test: `NetPlayControlsPlayTests.Nothing_is_sent_unless_the_client_is_ready` (Task 7).
3. **A rotated or zoomed camera.** A click must still land where the cursor points on the drawn floor. Test: `NetPlayControlsPlayTests.A_click_on_open_floor_moves_every_selected_operative_there` runs at two yaws and two zooms (Task 7).
4. **A selected or followed operative dies.** It must leave the selection, follow must end, and no request may name it. Test: `NetSelectionTests.Retain_DropsTheDead` and `NetPlayControlsPlayTests.A_dead_operative_is_dropped_and_never_ordered` (Task 7).
5. **The player keeps clicking after the mission ended.** After the end message, no request may leave the client. Test: `NetPlayControlsPlayTests.Nothing_is_sent_after_the_mission_ended` (Task 7).

---

## 1. What exists (inspected at `c061ff9`)

- **Server.** `ServerMission` scene; `ServerBootstrap` with `-attemptFixture` (Editor and fixture builds) then `-net`. `NetServer` builds `NetUnits`, `NetVisibility`, `NetAttackGuard`, `ServerCommandGate` and `NetSessionHost`, and spawns one ownerless `NetUnitState` avatar per unit. Each frame its visibility pass rebuilds observers and sends `NetKnowledgeMessage`s. The fixture mission (`ServerFixture.Settings()`) is fog on, `MapKnowledge.Full`, `ObjectiveKnowledge.All`, with no cameras and no terminal: a 2x2 grid, 3 rooms, one hostile, eliminate-hostiles, and extraction needing one living operative.
- **Client.** `NetTestClient` scene: `NetClient` (connect, hello, requests, `NetClientRecord`), `NetClientView` (capsules, grey last-known discs, an orthographic camera framing the avatars), `NetTestControls` (WASD steer, click move or attack, 1 and 2 pick), `NetHarness`.
- **Not on the wire today:** geometry, objectives, extraction point, mission end, shots. That is why the checkpoint run needed the operator's log hints.
- **Reusable as is:**
  - `MissionLayout` (its internal constructor and setters are reachable from the `Blackglass` assembly).
  - `EnvironmentVisualPlanner` and `EnvironmentVisualBuilder`. The builder is visual-only: it strips colliders and NavMesh modifiers.
  - `EnvironmentTheme` `CorporatePrototype`.
  - `ObjectiveMarker.ZoneColour`.
  - `MissionContent.LootSize` and `LootHeight`, and `MissionConstants`.
  - `TacticalCameraController` (`Initialize`, `SetBounds`, `FocusOn`, `Yaw`; it works with no `ActiveCharacter`).
  - `UnitWeaponVisual` (presentation-only), `RifleVisual.asset`, `EnemyUnit_Visual.prefab` (Animator with `Speed` float, `Dead` bool, `Fire` and `Hit` triggers), `TracerRifle.prefab` with `TracerView.Launch` and `TracerStyle.Create`.
  - `NetIntentSender`, `NetClientOptions`.
- **Seed use in visuals.** `EnvironmentVisualBuilder` picks theme variants and `EnvironmentVisualPlanner` places light fixtures from `layout.Seed`. The client layout's seed is 0, so its variants and lights are deterministic and cosmetic, and differ from what the server would pick. Geometry does not depend on it.

## 2. Unchanged (proved by tests and by the diff)

- Request handling: the gate, the session host, admission, budgets and refusals; the four request kinds and their wire form.
- What is replicated on avatars: the eight SyncVars and the hostile filtering in `NetAvatars`.
- Visibility: `NetVisibility` decides; the new code only asks `Visibility.Replicates`.
- The attempt: `AttemptSession`, the ledger, B25, the manifest, `outcomeSequence`. No new gameplay event is raised or counted.
- Offline scenes and the code they run. `OfflineSceneGuardTests` pins both scene files' bytes. The plan modifies no file in the offline folders listed in Global Constraints.
- The scripted checkpoint: `NetTestClient`, `NetHarness` roles, `AttemptLoopbackPlayTests`.

## 3. Design

### 3.1 The map projection (server, `NetMapProjection`)

Input: the mission's `MissionLayout`, the container tiles (`GeneratedMission.Loot.Containers[i].Tile`), the mission's
`IntelligenceSettings`, and whether the mission has a terminal or cameras. Output: a `NetMap`, or null with a refusal.

**Refusals (fail closed, nothing is sent):**
- fog off;
- `map != Full`;
- `objectives != All`;
- a terminal (its object would be an undrawn obstacle, and interacting with it is Stage 14);
- cameras (devices are intelligence content);
- a map over the limits.

The server logs one line, `[Net] map projection off: <reason>` or `[Net] map projection on floors=<n> boxes=<n> containers=<n>`.
It never logs a coordinate.

**Whitelist — the only fields copied:**

| Field | Source | Why it is public under Full |
|---|---|---|
| `Width`, `Height` | `layout.Width/Height` | the grid frame; needed to place tiles in world space (`ToWorld`) |
| `Floors` (tile rectangles) | `layout.FloorRects` | the floor the player sees; `RectCover.Decompose` gives an exact non-overlapping cover of the floor mask |
| `Boxes` (kind, footprint, rise) | `layout.Boxes` | every wall, baffle, pillar, low wall and crate; solid, visible objects |
| `Containers` (tiles) | `Loot.Containers[i].Tile` | closed crates, visible offline under Full (D2) |

**Never copied:**
- `Seed`, `Attempt`, `Hash`;
- `Rooms`, `Connections`;
- `FriendlyRoom`, `HostileRooms`, `FriendlyRegion`, `HostileRegions`, `FriendlySpawns`, `HostileSpawns`;
- `UsedTiles` (recomputed by the client);
- container index, room, items or contents;
- the objective plan, the security plan, the loot table, cover points, the NavMesh.

Box names are not copied either; the client names boxes by kind and order.

**Limits** (`NetMap`):

| Limit | Value |
|---|---|
| Grid side | 1..128 (the generator's maximum is 6 × 20 = 120) |
| Floors | 1..1024 |
| Boxes | ≤ 2048 |
| Containers | ≤ 64 |
| Box rise | in (0, 4] m |

The same `NetMapValidation.Problem` runs on the server before sending and on the client after decoding. It checks counts
before touching any element, so its work is bounded, and it rejects:
- rectangles outside the grid;
- empty or overlapping floor rectangles;
- unknown box kinds;
- non-finite or out-of-range rises;
- containers that are off the floor.

### 3.2 Objectives, mission end and shots

- **Objectives (`NetMapProjection.Objectives`).** Taken from `MissionRuntime.Objectives` (the goals, then extraction).
  - Only objectives with `IsKnown` are included, and only when the same configuration refusal passes.
  - Each carries its kind, its state and its `Title`, clipped to 48 printable characters. It never carries `Describe()`, which
    can include the hostile count (decision 037).
  - The extraction objective also carries its point (`ReachZoneObjective.Center`) and `Radius`. No other kind carries a point.
  - Sent to each participant at welcome, then again after any `ObjectiveChanged`, coalesced to at most one send per frame. Each
    send is the whole list (at most 8 entries).
- **Mission end.** On `MissionDirector.MissionFinished(Success|Failure)`, each participant gets `NetMissionEndMessage`
  (1 Success, 2 Failure). A participant welcomed after the end gets it at welcome. The client accepts only the first valid one.
- **Shots (`NetShotRelay`).**
  - `UnitAttacker.Fired` on every registered unit queues `(attackerId, targetId, hit)` for basic attacks. Ability records and
    untargeted records are skipped, since abilities are Stage 14 on the wire.
  - After the frame's visibility pass, each queued shot goes to every welcomed connection for which
    `Visibility.Replicates(connection, attacker) && Visibility.Replicates(connection, target)`; then the queue is emptied.
  - A shot is judged once, at the first visibility pass after it was fired (at most one frame later; checkpoint 1 review M1).
    So a shot from a hostile unobserved at that pass is never sent; its victim's health still drops
    through the replicated SyncVar, as today.
  - `Fired` is raised after `Attacked`/`Missed`, and those are what put a firing hostile's exposure in force. So the exposure
    reveal usually lands in the same pass and the shot is shown.
  - The queue is capped at 256 per frame; extra shots are dropped and counted.

**Participants.** Map, objectives and end go only to welcomed connections that own at least one unit (`ownedCount > 0`). A
welcomed connection that owns none (a spectator, or the visibility tests' account-b) receives none of them. No briefing
message is ever sent before the welcome.

### 3.3 Client map (`NetMapGeometry`, `NetMapView`)

- `NetMapGeometry.ToLayout(map)` builds a client `MissionLayout`:
  - seed 0 and attempt 0 (the real ones are never known);
  - the floor mask rebuilt from the floor rectangles;
  - `FloorRects` as sent;
  - boxes named `<Kind>_<n>`;
  - `UsedTiles` computed exactly like `MissionGenerator.UsedRect` (floor bounds plus one tile);
  - rooms, connections, spawns and regions left empty.
- Tests prove, for many seeds and the largest settings, that the floor mask, boxes, `UsedTiles`, `WorldBounds` and
  `EnvironmentVisualPlanner.Plan` (minus seed-picked light fixtures) all equal the server's.
- **Cube placement.** `NetMapGeometry.FloorBounds`, `BoxBounds` and `ContainerBounds` use the same formulas as
  `MissionBuilder.Build` and `MissionContent.AddLootContainers`. A PlayMode test compares every client cube with the server's
  real cube or crate transform in the same process, so any divergence fails.
- **`NetMapView`** draws the first valid map once, under a `ClientMap` root:
  - the environment theme through `EnvironmentVisualBuilder.Build`, which is visual-only;
  - gameplay-shaped cubes, whose renderers show only when there is no theme;
  - crates in the offline loot colour;
  - the extraction disc and beacon at the projected point, coloured by state with `ObjectiveMarker.ZoneColour`.
- **No physics on the client.** The view creates no collider, no NavMesh and no cover component. In host-mode tests it
  therefore cannot disturb the server's physics, NavMesh or line of sight, which share the scene.

### 3.4 Client avatars and combat feedback (`NetPlayAvatars`, `NetAvatarView`)

For each replicated avatar this client was sent:
- a child `EnemyUnit_Visual` instance with the rifle, colliders stripped;
- a side ring (blue own, red hostile) and a selection ring.

The Animator is driven from replicated facts only:
- `Speed` from the interpolated position (with `UnitAnimationDriver`'s 40 m/s teleport guard);
- `Dead` from `Alive`;
- `Fire` on a relayed shot;
- `Hit` on a health drop.

Each relayed shot draws a `TracerRifle` streak from the muzzle to the target's chest, and a miss floats "MISS". A health drop
floats "-N". Last-known records draw a red translucent disc with a "?" label at the frozen position. A hostile that is not
replicated draws nothing.

### 3.5 Client authority

The client never moves an avatar, never decides a hit, never changes health, inventory or objectives, and runs no AI.
- It sends only the four requests.
- It reads only:
  - what FishNet replicates to it;
  - `NetKnowledgeMessage`;
  - the four new messages.
- Its map is drawn from the projection and nothing else. Without a valid map it shows "No map authorized for this mission"
  and still plays with a ground plane, as the net-test client does today.

### 3.6 Controls, camera, HUD

- **Camera.** `TacticalCameraController` wired to the `Camera` map of `BlackglassControls`: WASD pan, Q/E rotate, right-drag
  rotate and tilt, wheel zoom. Bounds come from the client layout's `WorldBounds`. It frames the squad once when the map and
  the own avatars are both present.
- **Commands** (left click, `Commands/Command`, resolved by pure math with no physics, in `NetPick`):
  - on an own operative: select it (Ctrl adds or removes);
  - on a replicated living hostile: `NetAttack` from each selected living operative;
  - on open floor: `NetMoveTo` for each selected operative, with Shift appending;
  - on an obstacle: nothing, with feedback "Blocked — pick open floor";
  - off the map: nothing.
- **Keys.** X `NetStop`s the selection. Tab cycles one operative; Ctrl+A selects all. F toggles follow: the camera stays on the
  primary operative and WASD steers it (`NetMoveIntent` through `NetIntentSender`, camera-relative), the way offline takeover
  works. Esc ends follow.
- **No requests** while the client is not Ready, after the mission end, or for a dead or unowned unit.
- **HUD** (IMGUI, placeholder):
  - connection and map status;
  - the objectives panel;
  - squad cards with health bars and orders;
  - health bars over visible units;
  - floating combat text;
  - the extraction label;
  - the last refused request and the click feedback;
  - a controls help line;
  - a large end banner ("MISSION SUCCESS" or "MISSION FAILED").

  Clicks inside a HUD panel never command the world.

### 3.7 Security implications

- **New disclosure, all bounded and whitelisted:**
  - the public geometry of a Full-knowledge mission;
  - closed-container tiles;
  - known objective titles and states;
  - the extraction point;
  - the mission result;
  - shots between units the connection already observes.

  No hidden hostile position, count or order; no container content; no seed, attempt number, spawn, region, room graph or hash.
- **Seed fingerprint (D3).** The geometry identifies the seed, and from the seed the hidden placements follow. This is
  acceptable only for this local development slice, and is recorded as a blocker for public or player-facing online use.
- **Protocol surface.** Four server-to-client broadcasts. No client-to-server message is added, so the gate, admission,
  budgets, abuse disconnects and the 60 req/s budget see exactly what they see today. The client validates every payload and
  draws nothing from an invalid one. FishNet's own reader limits apply below that, and a malformed packet is FishNet's error,
  never a partial draw.
- **Release builds.** Never emit (owner ruling D1): the server-side code is compiled only in Editor and fixture builds, and no
  runtime flag can turn it on.
- **Logging.** The server logs counts only. The client logs refusal reasons, never coordinates or tokens.
- **Binding.** Unchanged: `127.0.0.1` only; local Windows processes only; nothing deployed.

---

## 4. File structure

| File | Responsibility |
|---|---|
| Create `Assets/_Project/Scripts/Net/Core/NetMap.cs` | Plain data: `NetMap`, `NetMapBox`, `NetObjective`, `NetObjectiveKind`, `NetMissionResult`, `NetShot`, and the limits. |
| Create `Assets/_Project/Scripts/Net/Core/NetMapValidation.cs` | `Problem(NetMap)` and `Problem(IReadOnlyList<NetObjective>)`: bounded checks shared by server and client. |
| Create `Assets/_Project/Scripts/Net/Core/NetMapProjection.cs` | Server side: the configuration refusal, the map whitelist, the known-objective list. |
| Create `Assets/_Project/Scripts/Net/Core/NetMapGeometry.cs` | Client side: seedless `MissionLayout` from a `NetMap`, cube bounds, used rectangle. |
| Create `Assets/_Project/Scripts/Net/Core/NetShotRelay.cs` | Per-frame shot queue filtered by replication. |
| Modify `Assets/_Project/Scripts/Net/Core/NetClientRecord.cs` | Store the decoded map, objectives and result. |
| Modify `Assets/_Project/Scripts/Net/Adapter/NetMessages.cs` | Four broadcasts and their wire structs. |
| Create `Assets/_Project/Scripts/Net/Adapter/NetMapWire.cs` | Conversion between the core types and the wire, with decode-time checks. |
| Modify `Assets/_Project/Scripts/Net/Adapter/NetServer.cs` | Build the projection once; send briefing at welcome; relay objectives, end and shots. |
| Modify `Assets/_Project/Scripts/Net/Adapter/NetClient.cs` | Register and decode the four messages; `ShotReceived` event. |
| Modify `Assets/_Project/Scripts/Net/Adapter/NetHarnessVisibility.cs` | Allow the four new message types in the harness's type check. |
| Create `Assets/_Project/Scripts/Net/Play/NetMapView.cs` | Draw the map, crates and extraction zone; expose layout, obstacles, cubes. |
| Create `Assets/_Project/Scripts/Net/Play/NetAvatarView.cs` | One avatar's visual: model, rifle, rings, Animator. |
| Create `Assets/_Project/Scripts/Net/Play/NetPlayAvatars.cs` | Avatar visuals, last-known markers, tracers, floating-text events. |
| Create `Assets/_Project/Scripts/Net/Play/NetSelection.cs` | Pure selection model. |
| Create `Assets/_Project/Scripts/Net/Play/NetPick.cs` | Pure ray picking against the drawn map and avatars. |
| Create `Assets/_Project/Scripts/Net/Play/NetPlayControls.cs` | Input to requests; follow and steer; camera framing. |
| Create `Assets/_Project/Scripts/Net/Play/NetPlayText.cs` | Pure HUD text and panel rectangles. |
| Create `Assets/_Project/Scripts/Net/Play/NetPlayHud.cs` | IMGUI HUD. |
| Create `Assets/_Project/Scripts/Net/Play/NetPlayBootstrap.cs` | Command line, connect. |
| Create `Assets/_Project/Editor/Net/NetPlayClientSceneBuilder.cs` | Generates `Assets/_Project/Scenes/NetPlayClient.unity`. |
| Create `Assets/_Project/Editor/Net/NetPlayClientBuild.cs` | Windows player build to `Builds/NetPlayClient/`. |
| Create `Tools/build-netplay-client.sh` | Batch build wrapper. |
| Create `Tools/run-local-netplay.sh` | Local two-process demonstration launcher. |
| Create `Assets/_Project/Scenes/NetPlayClient.unity` | Generated by the builder; committed. |
| Tests, EditMode | `NetMapValidationTests`, `NetMapProjectionTests`, `NetMapGeometryTests`, `NetMapWireTests`, `NetShotRelayTests`, `NetClientRecordBriefingTests`, `NetSelectionTests`, `NetPickTests`, `NetPlayTextTests`, `OfflineSceneGuardTests`; modify `NetMessageShapeTests`. |
| Tests, PlayMode | `NetBriefingPlayTests`, `NetMapViewPlayTests`, `NetPlayAvatarsPlayTests`, `NetPlayControlsPlayTests`, `NetPlayClientScenePlayTests`, `NetPlayUnassistedPlayTests`; support `TestSupport/NetPlayKit.cs`. |
| Docs | `Docs/Decisions.md` (052), `Docs/DedicatedServer.md`, this plan's "As built", `CLAUDE.md` progress line. |

Test commands (Unity closed, run from the repository root in Git Bash):
`Tools/run-tests.sh EditMode "<filter>"` and `Tools/run-tests.sh PlayMode "<filter>"`. Results are in
`Logs/TestResults-<platform>.xml`, and the script prints the summary.

---

### Task 1: Decision 052 and the bounded map types

**Files:**
- Modify: `Docs/Decisions.md` (append decision 052)
- Create: `Assets/_Project/Scripts/Net/Core/NetMap.cs`
- Create: `Assets/_Project/Scripts/Net/Core/NetMapValidation.cs`
- Test: `Assets/_Project/Tests/EditMode/NetMapValidationTests.cs`

**Interfaces:**
- Produces:
  - `NetMap(int width, int height, IList<RectInt> floors, IList<NetMapBox> boxes, IList<Vector2Int> containers)` with
    `Width`, `Height`, `Floors`, `Boxes`, `Containers` and the constants `Format = 1`, `MaxSide = 128`, `MaxFloors = 1024`,
    `MaxBoxes = 2048`, `MaxContainers = 64`, `MaxRise = 4f`;
  - `NetMapBox(MissionBoxKind kind, RectInt footprint, float rise)`;
  - `NetObjectiveKind { Eliminate = 1, Interact = 2, Extraction = 3 }`;
  - `NetObjective(NetObjectiveKind kind, ObjectiveState state, string title, bool hasPoint, Vector3 point, float radius)` with
    `MaxTitle = 48`, `MaxCount = 8`, `MaxRadius = 10f`;
  - `NetMissionResult { None = 0, Success = 1, Failure = 2 }`;
  - `NetShot(int attackerId, int targetId, bool hit)`;
  - `NetMapValidation.Problem(NetMap)` and `NetMapValidation.Problem(IReadOnlyList<NetObjective>)`, each returning `string`
    (null when valid).

- [ ] **Step 1: Append decision 052 to `Docs/Decisions.md`.**

```markdown
## 052. Server-authorized public map projection for the visual network client (2026-10-22)

**Decided.** For a mission with fog on, map knowledge Full and objective knowledge All, and with no terminal and no cameras, the
server sends each welcomed connection that owns units one bounded, whitelisted projection of the public map: the grid size, the
floor as tile rectangles, every wall and obstacle box (kind, footprint, height), and the tiles of the closed containers. It also
sends the known objectives (kind, state, title, and the extraction point and radius), the mission result, and each resolved shot
between units the connection already receives. Every other configuration receives nothing. The client rebuilds a seedless
MissionLayout from the projection and draws it with the existing environment theme; it has no collider, NavMesh or game rule.
No client-to-server message is added.

**Why.** A human cannot evaluate gameplay without the environment, the objectives and the result (owner, 2026-10-22). A whitelist
keeps the seed, spawns, regions, room graph, hashes, container contents and hostile information off the wire by construction.

**Local development only (owner ruling).** The server-side emission is compiled only in Editor and fixture builds
(`BLACKGLASS_NET_FIXTURE`) and also needs the approved profile; an ordinary release server never sends it, and no runtime flag can
enable it. All four messages carry a format version and are validated on both sides.

**Rejected.** Sending MissionLayout wholesale (it carries the seed and the spawns). Sending the seed so the client regenerates
(it reveals everything). Layout streaming by discovery (out of scope; needed only for MapKnowledge Partial or None). Emission
from release servers (owner ruling: not until the seed-recovery blocker below is resolved).

**Implications.** Spec §3.12 changes for development builds only: the network client there receives geometry. **Mandatory
blocker before public multiplayer or distribution to untrusted clients:** the existing fixed-salt deterministic streams derive
layout, objective, security and loot information from the same seed, the geometry is a fingerprint of that 31-bit seed, and the
client build contains the generator. A full map may therefore enable seed recovery and prediction of otherwise hidden
information (hostile spawns, security, loot rolls). Leaving the numeric seed out of the message is not a security solution.
Accepted only for the local fixture playtest. This slice changes neither the production generator, nor the backend schema, nor
the B25 contracts. MapKnowledge Partial and None, terminals and cameras need their own projection rules
later.
```

- [ ] **Step 2: Write the failing tests** in `Assets/_Project/Tests/EditMode/NetMapValidationTests.cs`.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The bounded checks every map and objective list passes before it is sent and after it is received (decision 052).</summary>
    public class NetMapValidationTests
    {
        static NetMap Map(Action<List<RectInt>, List<NetMapBox>, List<Vector2Int>> change = null, int width = 10, int height = 8)
        {
            var floors = new List<RectInt> { new RectInt(1, 1, 4, 3), new RectInt(5, 1, 3, 3) };
            var boxes = new List<NetMapBox>
            {
                new NetMapBox(MissionBoxKind.Wall, new RectInt(0, 0, 10, 1), 3f),
                new NetMapBox(MissionBoxKind.Crate, new RectInt(2, 2, 1, 1), 1f),
            };
            var containers = new List<Vector2Int> { new Vector2Int(6, 2) };
            change?.Invoke(floors, boxes, containers);
            return new NetMap(width, height, floors, boxes, containers);
        }

        static List<NetObjective> Objectives() => new List<NetObjective>
        {
            new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "Eliminate security team", false, Vector3.zero, 0f),
            new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Inactive, "Extraction", true, new Vector3(-9.5f, 0f, 4.5f), 2f),
        };

        [Test] public void AWellFormedMap_HasNoProblem() => Assert.That(NetMapValidation.Problem(Map()), Is.Null);
        [Test] public void NoMap_IsAProblem() => Assert.That(NetMapValidation.Problem((NetMap)null), Is.Not.Null);

        [TestCase(0, 8)] [TestCase(10, 0)] [TestCase(129, 8)] [TestCase(10, 129)]
        public void AGridOutsideOneTo128_IsRefused(int width, int height) =>
            StringAssert.Contains("grid", NetMapValidation.Problem(Map(width: width, height: height)));

        [Test] public void AMapWithNoFloor_IsRefused() => StringAssert.Contains("floor", NetMapValidation.Problem(Map((f, b, c) => { f.Clear(); c.Clear(); })));
        [Test] public void AFloorOutsideTheGrid_IsRefused() => StringAssert.Contains("outside", NetMapValidation.Problem(Map((f, b, c) => f.Add(new RectInt(8, 6, 3, 3)))));
        [Test] public void AnEmptyFloorRectangle_IsRefused() => StringAssert.Contains("outside", NetMapValidation.Problem(Map((f, b, c) => f.Add(new RectInt(1, 5, 0, 2)))));
        [Test] public void OverlappingFloors_AreRefused() => StringAssert.Contains("overlaps", NetMapValidation.Problem(Map((f, b, c) => f.Add(new RectInt(2, 2, 1, 1)))));

        [Test]
        public void MoreThan1024Floors_AreRefusedBeforeAnyIsRead()
        {
            var floors = Enumerable.Range(0, NetMap.MaxFloors + 1).Select(i => new RectInt(i % 128, i / 128, 1, 1)).ToList();
            StringAssert.Contains("floors", NetMapValidation.Problem(new NetMap(128, 128, floors, new List<NetMapBox>(), new List<Vector2Int>())));
        }

        [Test] public void ABoxOutsideTheGrid_IsRefused() => StringAssert.Contains("box", NetMapValidation.Problem(Map((f, b, c) => b.Add(new NetMapBox(MissionBoxKind.Pillar, new RectInt(9, 7, 2, 2), 3f)))));
        [Test] public void AnUnknownBoxKind_IsRefused() => StringAssert.Contains("kind", NetMapValidation.Problem(Map((f, b, c) => b.Add(new NetMapBox((MissionBoxKind)9, new RectInt(3, 3, 1, 1), 1f)))));

        [TestCase(0f)] [TestCase(-1f)] [TestCase(4.5f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void ABoxRiseOutsideZeroToFour_IsRefused(float rise) =>
            StringAssert.Contains("rise", NetMapValidation.Problem(Map((f, b, c) => b.Add(new NetMapBox(MissionBoxKind.Crate, new RectInt(3, 3, 1, 1), rise)))));

        [Test]
        public void MoreThan2048Boxes_AreRefused() =>
            StringAssert.Contains("boxes", NetMapValidation.Problem(Map((f, b, c) => b.AddRange(Enumerable.Repeat(new NetMapBox(MissionBoxKind.Crate, new RectInt(2, 2, 1, 1), 1f), NetMap.MaxBoxes)))));

        [Test] public void AContainerOffTheFloor_IsRefused() => StringAssert.Contains("container", NetMapValidation.Problem(Map((f, b, c) => c.Add(new Vector2Int(0, 0)))));
        [Test] public void AContainerOutsideTheGrid_IsRefused() => StringAssert.Contains("container", NetMapValidation.Problem(Map((f, b, c) => c.Add(new Vector2Int(40, 2)))));
        [Test] public void MoreThan64Containers_AreRefused() => StringAssert.Contains("containers", NetMapValidation.Problem(Map((f, b, c) => c.AddRange(Enumerable.Repeat(new Vector2Int(6, 2), NetMap.MaxContainers)))));

        [Test] public void WellFormedObjectives_HaveNoProblem() => Assert.That(NetMapValidation.Problem(Objectives()), Is.Null);
        [Test] public void NoObjectiveList_IsAProblem() => Assert.That(NetMapValidation.Problem((IReadOnlyList<NetObjective>)null), Is.Not.Null);

        [Test]
        public void MoreThanEightObjectives_AreRefused() =>
            StringAssert.Contains("objectives", NetMapValidation.Problem(Enumerable.Repeat(Objectives()[0], NetObjective.MaxCount + 1).ToList()));

        [TestCase((NetObjectiveKind)0)] [TestCase((NetObjectiveKind)9)]
        public void AnUnknownObjectiveKind_IsRefused(NetObjectiveKind kind) =>
            StringAssert.Contains("kind", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(kind, ObjectiveState.Active, "x", false, Vector3.zero, 0f) }));

        [Test]
        public void AnUnknownObjectiveState_IsRefused() =>
            StringAssert.Contains("state", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Eliminate, (ObjectiveState)7, "x", false, Vector3.zero, 0f) }));

        [TestCase("")] [TestCase("line\nbreak")] [TestCase("0123456789012345678901234567890123456789012345678")]
        public void ATitleThatIsEmptyTooLongOrUnprintable_IsRefused(string title) =>
            StringAssert.Contains("title", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, title, false, Vector3.zero, 0f) }));

        [Test]
        public void AnExtractionPointThatIsNotFiniteOrOffTheMap_IsRefused()
        {
            foreach (var point in new[] { new Vector3(float.NaN, 0f, 0f), new Vector3(300f, 0f, 0f), new Vector3(0f, 50f, 0f) })
                StringAssert.Contains("point", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Active, "Extraction", true, point, 2f) }));
        }

        [TestCase(0f)] [TestCase(11f)] [TestCase(float.NaN)]
        public void AnExtractionRadiusOutsideZeroToTen_IsRefused(float radius) =>
            StringAssert.Contains("radius", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Active, "Extraction", true, Vector3.zero, radius) }));

        [Test]
        public void OnlyExtractionCarriesAPoint() =>
            StringAssert.Contains("point", NetMapValidation.Problem(new List<NetObjective> { new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "x", true, Vector3.zero, 0f) }));
    }
}
```

- [ ] **Step 3: Run them to verify they fail.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetMapValidationTests"`
Expected: compilation fails with `error CS0246: The type or namespace name 'NetMap' could not be found`.

- [ ] **Step 4: Write `Assets/_Project/Scripts/Net/Core/NetMap.cs`.**

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One upright obstacle of the public map: its kind, its tile footprint and how high it rises, in metres.</summary>
    public readonly struct NetMapBox
    {
        public NetMapBox(MissionBoxKind kind, RectInt footprint, float rise)
        {
            Kind = kind;
            Footprint = footprint;
            Rise = rise;
        }

        public MissionBoxKind Kind { get; }
        public RectInt Footprint { get; }
        public float Rise { get; }
    }

    /// <summary>
    /// The public map of one mission (decision 052): what a client may draw under map knowledge Full and nothing else. The grid
    /// size, the floor as non-overlapping tile rectangles, every wall and obstacle box, and the tiles of the closed containers.
    /// No seed, attempt number, room graph, spawn, region, hash or container content exists here. Immutable.
    /// </summary>
    public sealed class NetMap
    {
        public const byte Format = 1;
        public const int MaxSide = 128;
        public const int MaxFloors = 1024;
        public const int MaxBoxes = 2048;
        public const int MaxContainers = 64;
        public const float MaxRise = 4f;

        public NetMap(int width, int height, IList<RectInt> floors, IList<NetMapBox> boxes, IList<Vector2Int> containers)
        {
            Width = width;
            Height = height;
            Floors = new ReadOnlyCollection<RectInt>(new List<RectInt>(floors ?? throw new ArgumentNullException(nameof(floors))));
            Boxes = new ReadOnlyCollection<NetMapBox>(new List<NetMapBox>(boxes ?? throw new ArgumentNullException(nameof(boxes))));
            Containers = new ReadOnlyCollection<Vector2Int>(new List<Vector2Int>(containers ?? throw new ArgumentNullException(nameof(containers))));
        }

        public int Width { get; }
        public int Height { get; }
        public IReadOnlyList<RectInt> Floors { get; }
        public IReadOnlyList<NetMapBox> Boxes { get; }
        public IReadOnlyList<Vector2Int> Containers { get; }
    }

    /// <summary>The kind of a known objective on the wire; 0 is never valid.</summary>
    public enum NetObjectiveKind : byte
    {
        Eliminate = 1,
        Interact = 2,
        Extraction = 3,
    }

    /// <summary>
    /// One objective the player knows (decision 052): its kind, state and title (never the counted description), and for
    /// extraction only, the zone's centre and radius.
    /// </summary>
    public readonly struct NetObjective
    {
        public const int MaxTitle = 48;
        public const int MaxCount = 8;
        public const float MaxRadius = 10f;

        public NetObjective(NetObjectiveKind kind, ObjectiveState state, string title, bool hasPoint, Vector3 point, float radius)
        {
            Kind = kind;
            State = state;
            Title = title;
            HasPoint = hasPoint;
            Point = point;
            Radius = radius;
        }

        public NetObjectiveKind Kind { get; }
        public ObjectiveState State { get; }
        public string Title { get; }
        public bool HasPoint { get; }
        public Vector3 Point { get; }
        public float Radius { get; }
    }

    /// <summary>The mission's terminal result as a client learns it; None until the end message.</summary>
    public enum NetMissionResult : byte
    {
        None = 0,
        Success = 1,
        Failure = 2,
    }

    /// <summary>One resolved basic attack between two units the receiving connection already observes.</summary>
    public readonly struct NetShot
    {
        public NetShot(int attackerId, int targetId, bool hit)
        {
            AttackerId = attackerId;
            TargetId = targetId;
            Hit = hit;
        }

        public int AttackerId { get; }
        public int TargetId { get; }
        public bool Hit { get; }
    }
}
```

- [ ] **Step 5: Write `Assets/_Project/Scripts/Net/Core/NetMapValidation.cs`.**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The checks a map and an objective list pass before the server sends them and after the client decodes them (decision
    /// 052). Counts are checked before any element, so the work is bounded by the limits whatever arrives. Null means valid;
    /// otherwise the first problem, which names no coordinate.
    /// </summary>
    public static class NetMapValidation
    {
        public static string Problem(NetMap map)
        {
            if (map == null)
                return "no map";
            if (map.Width < 1 || map.Height < 1 || map.Width > NetMap.MaxSide || map.Height > NetMap.MaxSide)
                return FormattableString.Invariant($"grid {map.Width}x{map.Height} outside 1..{NetMap.MaxSide}");
            if (map.Floors.Count < 1 || map.Floors.Count > NetMap.MaxFloors)
                return FormattableString.Invariant($"{map.Floors.Count} floors outside 1..{NetMap.MaxFloors}");
            if (map.Boxes.Count > NetMap.MaxBoxes)
                return FormattableString.Invariant($"{map.Boxes.Count} boxes above {NetMap.MaxBoxes}");
            if (map.Containers.Count > NetMap.MaxContainers)
                return FormattableString.Invariant($"{map.Containers.Count} containers above {NetMap.MaxContainers}");
            var floor = new bool[map.Width * map.Height];
            for (var i = 0; i < map.Floors.Count; i++)
            {
                var rect = map.Floors[i];
                if (!Inside(map, rect))
                    return FormattableString.Invariant($"floor {i} lies outside the grid");
                for (var y = rect.yMin; y < rect.yMax; y++)
                {
                    for (var x = rect.xMin; x < rect.xMax; x++)
                    {
                        if (floor[y * map.Width + x])
                            return FormattableString.Invariant($"floor {i} overlaps another");
                        floor[y * map.Width + x] = true;
                    }
                }
            }
            for (var i = 0; i < map.Boxes.Count; i++)
            {
                var box = map.Boxes[i];
                if (!Enum.IsDefined(typeof(MissionBoxKind), box.Kind))
                    return FormattableString.Invariant($"box {i} has an unknown kind");
                if (!Inside(map, box.Footprint))
                    return FormattableString.Invariant($"box {i} lies outside the grid");
                if (!(box.Rise > 0f && box.Rise <= NetMap.MaxRise))
                    return FormattableString.Invariant($"box {i} rise outside (0, {NetMap.MaxRise}]");
            }
            for (var i = 0; i < map.Containers.Count; i++)
            {
                var tile = map.Containers[i];
                if (tile.x < 0 || tile.y < 0 || tile.x >= map.Width || tile.y >= map.Height || !floor[tile.y * map.Width + tile.x])
                    return FormattableString.Invariant($"container {i} is not on a floor tile");
            }
            return null;
        }

        public static string Problem(IReadOnlyList<NetObjective> objectives)
        {
            if (objectives == null)
                return "no objective list";
            if (objectives.Count > NetObjective.MaxCount)
                return FormattableString.Invariant($"{objectives.Count} objectives above {NetObjective.MaxCount}");
            for (var i = 0; i < objectives.Count; i++)
            {
                var objective = objectives[i];
                if (!Enum.IsDefined(typeof(NetObjectiveKind), objective.Kind))
                    return FormattableString.Invariant($"objective {i} has an unknown kind");
                if (!Enum.IsDefined(typeof(ObjectiveState), objective.State))
                    return FormattableString.Invariant($"objective {i} has an unknown state");
                if (!Printable(objective.Title))
                    return FormattableString.Invariant($"objective {i} title is empty, longer than {NetObjective.MaxTitle} or unprintable");
                var extraction = objective.Kind == NetObjectiveKind.Extraction;
                if (objective.HasPoint != extraction)
                    return FormattableString.Invariant($"objective {i}: only extraction carries a point");
                if (!extraction)
                    continue;
                var p = objective.Point;
                if (!(Finite(p.x) && Finite(p.y) && Finite(p.z)) || Mathf.Abs(p.x) > NetMap.MaxSide || Mathf.Abs(p.z) > NetMap.MaxSide || Mathf.Abs(p.y) > 10f)
                    return FormattableString.Invariant($"objective {i} point is not finite or lies off the map");
                if (!(objective.Radius > 0f && objective.Radius <= NetObjective.MaxRadius))
                    return FormattableString.Invariant($"objective {i} radius outside (0, {NetObjective.MaxRadius}]");
            }
            return null;
        }

        static bool Inside(NetMap map, RectInt rect) =>
            rect.width >= 1 && rect.height >= 1 && rect.xMin >= 0 && rect.yMin >= 0 && rect.xMax <= map.Width && rect.yMax <= map.Height;

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static bool Printable(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > NetObjective.MaxTitle)
                return false;
            foreach (var c in text)
            {
                if (char.IsControl(c))
                    return false;
            }
            return true;
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetMapValidationTests"`
Expected: PASS, every test in the class.

- [ ] **Step 7: Run the networking audit** (Core must stay free of FishNet).
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetSourceAuditTests"`
Expected: PASS.

- [ ] **Step 8: Commit.**

```bash
git add Docs/Decisions.md Assets/_Project/Scripts/Net/Core/NetMap.cs Assets/_Project/Scripts/Net/Core/NetMap.cs.meta \
  Assets/_Project/Scripts/Net/Core/NetMapValidation.cs Assets/_Project/Scripts/Net/Core/NetMapValidation.cs.meta \
  Assets/_Project/Tests/EditMode/NetMapValidationTests.cs Assets/_Project/Tests/EditMode/NetMapValidationTests.cs.meta
git commit -m "Decision 052 and the bounded public-map types: NetMap, NetObjective, shared validation" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

(Unity writes the `.meta` files on the first test run. Stage them with their sources.)

---

### Task 2: The server's projection and the client's seedless layout

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetMapProjection.cs`
- Create: `Assets/_Project/Scripts/Net/Core/NetMapGeometry.cs`
- Test: `Assets/_Project/Tests/EditMode/NetMapProjectionTests.cs`, `Assets/_Project/Tests/EditMode/NetMapGeometryTests.cs`

**Interfaces:**
- Consumes: Task 1's `NetMap`, `NetMapBox`, `NetObjective`, `NetMapValidation`.
- Produces:
  - `NetMapProjection.Problem(IntelligenceSettings settings, bool hasTerminal, bool hasCameras) : string`;
  - `NetMapProjection.Map(MissionLayout layout, IReadOnlyList<Vector2Int> containerTiles, IntelligenceSettings settings, bool hasTerminal, bool hasCameras, out string refusal) : NetMap`;
  - `NetMapProjection.Objectives(IReadOnlyList<MissionObjective> objectives, IntelligenceSettings settings, bool hasTerminal, bool hasCameras) : List<NetObjective>`;
  - `NetMapGeometry.ToLayout(NetMap map) : MissionLayout`;
  - `NetMapGeometry.FloorBounds(MissionLayout layout, RectInt rect) : Bounds`;
  - `NetMapGeometry.BoxBounds(MissionLayout layout, MissionBox box) : Bounds`;
  - `NetMapGeometry.ContainerBounds(MissionLayout layout, Vector2Int tile) : Bounds`.

- [ ] **Step 1: Write the failing projection tests** in `Assets/_Project/Tests/EditMode/NetMapProjectionTests.cs`.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The server's whitelist projection of the public map and the known objectives (decision 052).</summary>
    public class NetMapProjectionTests
    {
        static MissionLayout FixtureLayout()
        {
            Assert.That(MissionGenerator.TryAttempt(ServerFixture.Settings().Validated(), 1, out var layout, out var reason), Is.True, reason);
            return layout;
        }

        static IntelligenceSettings Fixture() => ServerFixture.Settings().intelligence;

        [Test]
        public void TheFixtureMission_IsProjected_WithItsFloorsBoxesAndContainersCopiedExactly()
        {
            var layout = FixtureLayout();
            var tiles = new[] { layout.FriendlySpawns[0] };   // any floor tile stands in for a container here
            var map = NetMapProjection.Map(layout, tiles, Fixture(), false, false, out var refusal);
            Assert.That(refusal, Is.Null);
            Assert.That(map.Width, Is.EqualTo(layout.Width));
            Assert.That(map.Height, Is.EqualTo(layout.Height));
            Assert.That(map.Floors, Is.EqualTo(layout.FloorRects));
            Assert.That(map.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise)), Is.EqualTo(layout.Boxes.Select(b => (b.Kind, b.Footprint, b.Height))));
            Assert.That(map.Containers, Is.EqualTo(tiles));
        }

        [TestCase(MapKnowledge.Partial)] [TestCase(MapKnowledge.None)]
        public void AnyMapKnowledgeButFull_IsNotProjected(MapKnowledge knowledge)
        {
            var settings = Fixture();
            settings.map = knowledge;
            Assert.That(NetMapProjection.Map(FixtureLayout(), Array.Empty<Vector2Int>(), settings, false, false, out var refusal), Is.Null);
            StringAssert.Contains("map knowledge", refusal);
        }

        [TestCase(ObjectiveKnowledge.ExtractionAndTerminal)] [TestCase(ObjectiveKnowledge.ExtractionOnly)] [TestCase(ObjectiveKnowledge.None)]
        public void AnyObjectiveKnowledgeButAll_ProjectsNeitherMapNorObjectives(ObjectiveKnowledge knowledge)
        {
            var settings = Fixture();
            settings.objectives = knowledge;
            Assert.That(NetMapProjection.Map(FixtureLayout(), Array.Empty<Vector2Int>(), settings, false, false, out var refusal), Is.Null);
            StringAssert.Contains("objective knowledge", refusal);
            Assert.That(NetMapProjection.Objectives(Goals(out _, out _), settings, false, false), Is.Empty);
        }

        [Test]
        public void FogOff_IsNotProjected()
        {
            var settings = Fixture();
            settings.fogEnabled = false;
            StringAssert.Contains("fog", NetMapProjection.Problem(settings, false, false));
        }

        [Test]
        public void ATerminalOrCameras_AreNotProjectedInThisSlice()
        {
            StringAssert.Contains("terminal", NetMapProjection.Problem(Fixture(), true, false));
            StringAssert.Contains("camera", NetMapProjection.Problem(Fixture(), false, true));
        }

        [Test]
        public void AMapOverTheLimits_IsRefusedBeforeItIsSent()
        {
            var layout = new MissionLayout(0, 1, NetMap.MaxSide + 1, 4, new bool[(NetMap.MaxSide + 1) * 4])
            {
                FloorRects = new[] { new RectInt(0, 0, 1, 1) },
            };
            Assert.That(NetMapProjection.Map(layout, Array.Empty<Vector2Int>(), Fixture(), false, false, out var refusal), Is.Null);
            StringAssert.Contains("grid", refusal);
        }

        [Test]
        public void TheProjectedTypes_HaveNoMemberForSeedSpawnRegionRoomHashAttemptOrContents()
        {
            var forbidden = new[] { "seed", "spawn", "region", "room", "hash", "attempt", "plan", "layout", "hostile", "item", "loot", "content" };
            foreach (var type in new[] { typeof(NetMap), typeof(NetMapBox), typeof(NetObjective) })
            {
                var names = type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(m => m.Name);
                foreach (var name in names)
                    foreach (var bad in forbidden)
                        Assert.That(name.IndexOf(bad, StringComparison.OrdinalIgnoreCase), Is.LessThan(0), type.Name + "." + name);
            }
        }

        static List<MissionObjective> Goals(out EliminateHostilesObjective eliminate, out ReachZoneObjective extraction)
        {
            eliminate = new EliminateHostilesObjective("eliminate", "Eliminate security team", new List<Health>());
            extraction = new ReachZoneObjective("extract", "Extraction", new Vector3(-9.5f, 0f, 4.5f), MissionContent.ZoneRadius, 1, new List<Health>(), isRequired: false);
            return new List<MissionObjective> { eliminate, extraction };
        }

        [Test]
        public void Objectives_CarryTitlesNotCounts_AndAPointOnlyForExtraction()
        {
            var list = NetMapProjection.Objectives(Goals(out _, out _), Fixture(), false, false);
            Assert.That(list.Count, Is.EqualTo(2));
            Assert.That(list[0].Kind, Is.EqualTo(NetObjectiveKind.Eliminate));
            Assert.That(list[0].Title, Is.EqualTo("Eliminate security team"));
            Assert.That(list[0].HasPoint, Is.False);
            Assert.That(list[1].Kind, Is.EqualTo(NetObjectiveKind.Extraction));
            Assert.That(list[1].Point, Is.EqualTo(new Vector3(-9.5f, 0f, 4.5f)));
            Assert.That(list[1].Radius, Is.EqualTo(MissionContent.ZoneRadius));
            Assert.That(NetMapValidation.Problem(list), Is.Null);
        }

        [Test]
        public void AnUnknownObjective_IsLeftOut()
        {
            var goals = Goals(out var eliminate, out _);
            eliminate.SetKnown(false);
            Assert.That(NetMapProjection.Objectives(goals, Fixture(), false, false).Select(o => o.Kind), Is.EqualTo(new[] { NetObjectiveKind.Extraction }));
        }

        [Test]
        public void ALongTitle_IsClippedToTheLimit()
        {
            var goals = new List<MissionObjective> { new EliminateHostilesObjective("e", new string('x', 80), new List<Health>()) };
            Assert.That(NetMapProjection.Objectives(goals, Fixture(), false, false)[0].Title.Length, Is.EqualTo(NetObjective.MaxTitle));
        }
    }
}
```

- [ ] **Step 2: Write the failing geometry tests** in `Assets/_Project/Tests/EditMode/NetMapGeometryTests.cs`.

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The client's seedless layout rebuilt from the projection equals the server's geometry (decision 052).</summary>
    public class NetMapGeometryTests
    {
        static IEnumerable<TestCaseData> Layouts()
        {
            for (var seed = 1; seed <= 12; seed++)
            {
                var s = ServerFixture.Settings();
                s.seed = seed;
                yield return new TestCaseData(s).SetName("Fixture settings, seed " + seed);
            }
            for (var seed = 1; seed <= 3; seed++)
            {
                var big = ServerFixture.Settings();
                big.seed = seed;
                big.gridColumns = 6;
                big.gridRows = 6;
                big.cellSize = 20;
                big.roomCount = 20;
                big.extraLoops = 4;
                big.bafflesPerRoom = 3;
                big.lowCoverDensity = 10f;
                yield return new TestCaseData(big).SetName("Largest settings, seed " + seed);
            }
        }

        static MissionLayout Generate(MissionSettings settings)
        {
            var request = settings.Validated();
            for (var attempt = 1; attempt <= request.maxAttempts; attempt++)
                if (MissionGenerator.TryAttempt(request, attempt, out var layout, out _))
                    return layout;
            Assert.Fail("no attempt generated");
            return null;
        }

        static MissionLayout RoundTrip(MissionLayout server)
        {
            var map = NetMapProjection.Map(server, new Vector2Int[0], ServerFixture.Settings().intelligence, false, false, out var refusal);
            Assert.That(refusal, Is.Null, "the projection fits the limits");
            return NetMapGeometry.ToLayout(map);
        }

        [TestCaseSource(nameof(Layouts))]
        public void TheClientLayout_HasTheServersFloorBoxesUsedTilesAndBounds(MissionSettings settings)
        {
            var server = Generate(settings);
            var client = RoundTrip(server);
            Assert.That(client.Width, Is.EqualTo(server.Width));
            Assert.That(client.Height, Is.EqualTo(server.Height));
            for (var y = -1; y <= server.Height; y++)
                for (var x = -1; x <= server.Width; x++)
                    Assert.That(client.IsFloor(x, y), Is.EqualTo(server.IsFloor(x, y)), "tile " + x + "," + y);
            Assert.That(client.FloorTileCount, Is.EqualTo(server.FloorTileCount));
            Assert.That(client.FloorRects, Is.EqualTo(server.FloorRects));
            Assert.That(client.Boxes.Select(b => (b.Kind, b.Footprint, b.Height)), Is.EqualTo(server.Boxes.Select(b => (b.Kind, b.Footprint, b.Height))));
            Assert.That(client.UsedTiles, Is.EqualTo(server.UsedTiles));
            Assert.That(client.WorldBounds, Is.EqualTo(server.WorldBounds));
        }

        [TestCaseSource(nameof(Layouts))]
        public void TheClientLayout_KnowsNoSeedAttemptRoomSpawnOrRegion(MissionSettings settings)
        {
            var client = RoundTrip(Generate(settings));
            Assert.That(client.Seed, Is.Zero);
            Assert.That(client.Attempt, Is.Zero);
            Assert.That(client.Hash, Is.Zero);
            Assert.That(client.Rooms, Is.Empty);
            Assert.That(client.Connections, Is.Empty);
            Assert.That(client.FriendlySpawns, Is.Empty);
            Assert.That(client.HostileSpawns, Is.Empty);
            Assert.That(client.HostileRooms, Is.Empty);
            Assert.That(client.HostileRegions, Is.Empty);
            Assert.That(client.FriendlyRoom, Is.EqualTo(-1));
        }

        [TestCaseSource(nameof(Layouts))]
        public void TheThemedVisualPlan_IsTheServersExceptTheSeedPickedLights(MissionSettings settings)
        {
            var server = Generate(settings);
            var client = RoundTrip(server);
            string Key(VisualPlacement p) => p.Element + "@" + p.Tile + ":" + p.Position.ToString("F3") + "/" + p.YawDegrees;
            var expected = EnvironmentVisualPlanner.Plan(server).Where(p => p.Element != EnvironmentElement.LightFixture).Select(Key);
            var actual = EnvironmentVisualPlanner.Plan(client).Where(p => p.Element != EnvironmentElement.LightFixture).Select(Key);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void CubeBounds_AreMissionBuildersFormulas()
        {
            var layout = RoundTrip(Generate(ServerFixture.Settings()));
            var rect = layout.FloorRects[0];
            var floor = NetMapGeometry.FloorBounds(layout, rect);
            Assert.That(floor.center, Is.EqualTo(layout.RectCenter(rect) + Vector3.down * (MissionConstants.FloorThickness * 0.5f)));
            Assert.That(floor.size, Is.EqualTo(new Vector3(rect.width, MissionConstants.FloorThickness, rect.height)));
            var box = layout.Boxes[0];
            var b = NetMapGeometry.BoxBounds(layout, box);
            Assert.That(b.center, Is.EqualTo(layout.RectCenter(box.Footprint) + Vector3.up * (box.Height * 0.5f)));
            Assert.That(b.size, Is.EqualTo(new Vector3(box.Footprint.width, box.Height, box.Footprint.height)));
            var tile = new Vector2Int(rect.xMin, rect.yMin);
            var crate = NetMapGeometry.ContainerBounds(layout, tile);
            Assert.That(crate.center, Is.EqualTo(layout.TileCenter(tile) + Vector3.up * (MissionContent.LootHeight * 0.5f)));
            Assert.That(crate.size, Is.EqualTo(new Vector3(MissionContent.LootSize, MissionContent.LootHeight, MissionContent.LootSize)));
        }
    }
}
```

`VisualPlacement` exposes `Element`, `Position`, `YawDegrees` and `Tile` (`Scripts/Environment/VisualPlacement.cs`).

- [ ] **Step 3: Run both to verify they fail.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetMapProjectionTests|Blackglass.Tests.NetMapGeometryTests"`
Expected: compilation fails, because `NetMapProjection` and `NetMapGeometry` are not defined.

- [ ] **Step 4: Write `Assets/_Project/Scripts/Net/Core/NetMapProjection.cs`.**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The server's projection of what a client may draw and know about a mission (decision 052). First slice: only fog on,
    /// map knowledge Full and objective knowledge All, with no terminal and no cameras; anything else projects nothing. The map is
    /// a whitelist copy (grid size, floor rectangles, boxes, container tiles); the objectives are the known ones, by title.
    /// </summary>
    public static class NetMapProjection
    {
        public static string Problem(IntelligenceSettings settings, bool hasTerminal, bool hasCameras)
        {
            if (settings == null)
                return "no intelligence settings";
            if (!settings.fogEnabled)
                return "fog is off";
            if (settings.map != MapKnowledge.Full)
                return "map knowledge " + settings.map + " is not projected (only Full)";
            if (settings.objectives != ObjectiveKnowledge.All)
                return "objective knowledge " + settings.objectives + " is not projected (only All)";
            if (hasTerminal)
                return "a terminal is not projected in this slice";
            if (hasCameras)
                return "security cameras are not projected in this slice";
            return null;
        }

        public static NetMap Map(MissionLayout layout, IReadOnlyList<Vector2Int> containerTiles, IntelligenceSettings settings,
            bool hasTerminal, bool hasCameras, out string refusal)
        {
            refusal = layout == null ? "no layout" : Problem(settings, hasTerminal, hasCameras);
            if (refusal != null)
                return null;
            var boxes = new List<NetMapBox>(layout.Boxes.Count);
            foreach (var box in layout.Boxes)
                boxes.Add(new NetMapBox(box.Kind, box.Footprint, box.Height));
            var tiles = new List<Vector2Int>();
            if (containerTiles != null)
                tiles.AddRange(containerTiles);
            var map = new NetMap(layout.Width, layout.Height, new List<RectInt>(layout.FloorRects), boxes, tiles);
            refusal = NetMapValidation.Problem(map);
            return refusal == null ? map : null;
        }

        /// <summary>The known objectives in runtime order (goals, then extraction); empty when the configuration is not projected.</summary>
        public static List<NetObjective> Objectives(IReadOnlyList<MissionObjective> objectives, IntelligenceSettings settings,
            bool hasTerminal, bool hasCameras)
        {
            var result = new List<NetObjective>();
            if (objectives == null || Problem(settings, hasTerminal, hasCameras) != null)
                return result;
            foreach (var objective in objectives)
            {
                if (objective == null || !objective.IsKnown || result.Count == NetObjective.MaxCount)
                    continue;
                var title = objective.Title.Length > NetObjective.MaxTitle ? objective.Title.Substring(0, NetObjective.MaxTitle) : objective.Title;
                if (objective is ReachZoneObjective zone)
                    result.Add(new NetObjective(NetObjectiveKind.Extraction, objective.State, title, true, zone.Center, zone.Radius));
                else
                    result.Add(new NetObjective(objective.Type == ObjectiveType.Interact ? NetObjectiveKind.Interact : NetObjectiveKind.Eliminate,
                        objective.State, title, false, Vector3.zero, 0f));
            }
            return result;
        }
    }
}
```

- [ ] **Step 5: Write `Assets/_Project/Scripts/Net/Core/NetMapGeometry.cs`.**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The client's side of the projection (decision 052): a MissionLayout rebuilt from a validated NetMap, and the world bounds
    /// of each cube MissionBuilder and MissionContent build for it. Seed 0 and attempt 0, because the real ones are never known;
    /// rooms, connections, spawns and regions stay empty.
    /// </summary>
    public static class NetMapGeometry
    {
        /// <summary>The map must already pass NetMapValidation.</summary>
        public static MissionLayout ToLayout(NetMap map)
        {
            if (map == null)
                throw new ArgumentNullException(nameof(map));
            var floor = new bool[map.Width * map.Height];
            foreach (var rect in map.Floors)
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        floor[y * map.Width + x] = true;
            var boxes = new List<MissionBox>(map.Boxes.Count);
            for (var i = 0; i < map.Boxes.Count; i++)
            {
                var box = map.Boxes[i];
                boxes.Add(new MissionBox(box.Kind + "_" + (i + 1), box.Kind, box.Footprint, box.Rise));
            }
            return new MissionLayout(0, 0, map.Width, map.Height, floor)
            {
                FloorRects = new List<RectInt>(map.Floors).AsReadOnly(),
                Boxes = boxes.AsReadOnly(),
                UsedTiles = UsedRect(floor, map.Width, map.Height),
            };
        }

        /// <summary>As MissionBuilder places a floor cube.</summary>
        public static Bounds FloorBounds(MissionLayout layout, RectInt rect) =>
            new Bounds(layout.RectCenter(rect) + Vector3.down * (MissionConstants.FloorThickness * 0.5f),
                new Vector3(rect.width, MissionConstants.FloorThickness, rect.height));

        /// <summary>As MissionBuilder places an obstacle cube.</summary>
        public static Bounds BoxBounds(MissionLayout layout, MissionBox box) =>
            new Bounds(layout.RectCenter(box.Footprint) + Vector3.up * (box.Height * 0.5f),
                new Vector3(box.Footprint.width, box.Height, box.Footprint.height));

        /// <summary>As MissionContent places a loot container's collider.</summary>
        public static Bounds ContainerBounds(MissionLayout layout, Vector2Int tile) =>
            new Bounds(layout.TileCenter(tile) + Vector3.up * (MissionContent.LootHeight * 0.5f),
                new Vector3(MissionContent.LootSize, MissionContent.LootHeight, MissionContent.LootSize));

        /// <summary>The generator's used rectangle (MissionGenerator.UsedRect): the floor's bounds plus one tile all round.</summary>
        internal static RectInt UsedRect(bool[] floor, int width, int height)
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

`MissionLayout.FloorRects`, `Boxes` and `UsedTiles` have `internal set`, and the constructor is internal. `Blackglass`
code reaches them, and the test assemblies do too through `InternalsVisibleTo`.

- [ ] **Step 6: Run both test classes to verify they pass.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetMapProjectionTests|Blackglass.Tests.NetMapGeometryTests"`
Expected: PASS. In particular, all 15 layouts round-trip, and the largest settings fit the limits.

- [ ] **Step 7: Commit.**

```bash
git add Assets/_Project/Scripts/Net/Core/NetMapProjection.cs* Assets/_Project/Scripts/Net/Core/NetMapGeometry.cs* \
  Assets/_Project/Tests/EditMode/NetMapProjectionTests.cs* Assets/_Project/Tests/EditMode/NetMapGeometryTests.cs*
git commit -m "Map projection whitelist (Full/All only) and the client's seedless layout, equal to the server's geometry" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Wire messages and the briefing (map, objectives, mission end)

**Files:**
- Modify: `Assets/_Project/Scripts/Net/Adapter/NetMessages.cs` (append)
- Create: `Assets/_Project/Scripts/Net/Adapter/NetMapWire.cs`
- Modify: `Assets/_Project/Scripts/Net/Core/NetClientRecord.cs`
- Modify: `Assets/_Project/Scripts/Net/Adapter/NetClient.cs`
- Modify: `Assets/_Project/Scripts/Net/Adapter/NetServer.cs`
- Modify: `Assets/_Project/Scripts/Net/Adapter/NetHarnessVisibility.cs:17-21`
- Modify: `Assets/_Project/Tests/EditMode/NetMessageShapeTests.cs`
- Test: `Assets/_Project/Tests/EditMode/NetMapWireTests.cs`, `Assets/_Project/Tests/EditMode/NetClientRecordBriefingTests.cs`, `Assets/_Project/Tests/PlayMode/NetBriefingPlayTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces:
  - wire structs `NetRectWire`, `NetBoxWire`, `NetTileWire`, `NetObjectiveWire`;
  - broadcasts `NetMapMessage`, `NetObjectivesMessage`, `NetMissionEndMessage`, and `NetShotMessage` (declared here, used in Task 4);
  - `NetMapWire.ToMessage(NetMap) : NetMapMessage`, `NetMapWire.FromMessage(NetMapMessage, out string) : NetMap`;
  - `NetMapWire.ToMessage(IReadOnlyList<NetObjective>) : NetObjectivesMessage`, `NetMapWire.FromMessage(NetObjectivesMessage, out string) : List<NetObjective>`;
  - `NetClientRecord` members: `ReceiveMap(double, NetMap, string)`, `ReceiveObjectives(double, IReadOnlyList<NetObjective>, string)`, `ReceiveEnd(double, byte)`, `Map`, `MapProblem`, `MapMessages`, `Objectives`, `ObjectivesProblem`, `ObjectivesMessages`, `Result`, `EndMessages`.

- [ ] **Step 1: Write the failing wire and record tests.** First `Assets/_Project/Tests/EditMode/NetMapWireTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The projection survives the wire unchanged, and every malformed payload decodes to nothing with a reason.</summary>
    public class NetMapWireTests
    {
        static NetMap FixtureMap()
        {
            Assert.That(MissionGenerator.TryAttempt(ServerFixture.Settings().Validated(), 1, out var layout, out var reason), Is.True, reason);
            return NetMapProjection.Map(layout, new[] { layout.FriendlySpawns[0] }, ServerFixture.Settings().intelligence, false, false, out _);
        }

        [Test]
        public void TheFixtureMap_RoundTripsExactly()
        {
            var map = FixtureMap();
            var back = NetMapWire.FromMessage(NetMapWire.ToMessage(map), out var problem);
            Assert.That(problem, Is.Null);
            Assert.That((back.Width, back.Height), Is.EqualTo((map.Width, map.Height)));
            Assert.That(back.Floors, Is.EqualTo(map.Floors));
            Assert.That(back.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise)), Is.EqualTo(map.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise))));
            Assert.That(back.Containers, Is.EqualTo(map.Containers));
        }

        [Test]
        public void AnotherFormat_DecodesToNothing()
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Format = 2;
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("format", problem);
        }

        [Test]
        public void MissingArrays_DecodeToNothing()
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Boxes = null;
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("missing", problem);
        }

        [Test]
        public void OversizedArrays_AreRefusedBeforeDecoding()
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Floors = new NetRectWire[NetMap.MaxFloors + 1];
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("too large", problem);
        }

        [TestCase((byte)0)] [TestCase((byte)6)]
        public void AnUnknownBoxKindByte_DecodesToNothing(byte kind)
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Boxes[0].Kind = kind;
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("kind", problem);
        }

        [Test]
        public void AFloorOutsideTheGrid_DecodesToNothing()
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Floors[0].X = 500;
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("outside", problem);
        }

        [Test]
        public void Objectives_RoundTrip_AndBadBytesDecodeToNothing()
        {
            var list = new List<NetObjective>
            {
                new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "Eliminate security team", false, Vector3.zero, 0f),
                new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Inactive, "Extraction", true, new Vector3(-9.5f, 0f, 4.5f), 2f),
            };
            var message = NetMapWire.ToMessage(list);
            var back = NetMapWire.FromMessage(message, out var problem);
            Assert.That(problem, Is.Null);
            Assert.That(back.Select(o => (o.Kind, o.State, o.Title, o.HasPoint, o.Point, o.Radius)), Is.EqualTo(list.Select(o => (o.Kind, o.State, o.Title, o.HasPoint, o.Point, o.Radius))));
            message.Objectives[0].State = 9;
            Assert.That(NetMapWire.FromMessage(message, out problem), Is.Null);
            StringAssert.Contains("state", problem);
            message.Objectives = null;
            Assert.That(NetMapWire.FromMessage(message, out problem), Is.Null);
            StringAssert.Contains("missing", problem);
        }
    }
}
```

Then `Assets/_Project/Tests/EditMode/NetClientRecordBriefingTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>What the client keeps of the briefing: the first valid map for good, the latest valid objectives, the first valid result.</summary>
    public class NetClientRecordBriefingTests
    {
        static NetMap Map(int width) => new NetMap(width, 4, new List<RectInt> { new RectInt(0, 0, 2, 2) }, new List<NetMapBox>(), new List<Vector2Int>());

        [Test]
        public void TheFirstValidMap_StandsForTheWholeAttempt()
        {
            var record = new NetClientRecord();
            record.ReceiveMap(1d, null, "map format 2 is not 1");
            Assert.That(record.Map, Is.Null);
            Assert.That(record.MapProblem, Does.Contain("format"));
            var first = Map(4);
            record.ReceiveMap(2d, first, null);
            record.ReceiveMap(3d, Map(5), null);
            Assert.That(record.Map, Is.SameAs(first));
            Assert.That(record.MapProblem, Is.Null);
            Assert.That(record.MapMessages, Is.EqualTo(3));
        }

        [Test]
        public void AnInvalidObjectiveList_KeepsThePreviousOne()
        {
            var record = new NetClientRecord();
            var list = new List<NetObjective> { new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "Eliminate", false, Vector3.zero, 0f) };
            record.ReceiveObjectives(1d, list, null);
            record.ReceiveObjectives(2d, null, "objective 0 has an unknown state");
            Assert.That(record.Objectives.Count, Is.EqualTo(1));
            Assert.That(record.ObjectivesProblem, Does.Contain("state"));
        }

        [Test]
        public void TheFirstValidResult_IsFinal_AndUnknownBytesAreIgnored()
        {
            var record = new NetClientRecord();
            record.ReceiveEnd(1d, 7);
            Assert.That(record.Result, Is.EqualTo(NetMissionResult.None));
            record.ReceiveEnd(2d, (byte)NetMissionResult.Failure);
            record.ReceiveEnd(3d, (byte)NetMissionResult.Success);
            Assert.That(record.Result, Is.EqualTo(NetMissionResult.Failure));
            Assert.That(record.EndMessages, Is.EqualTo(3));
        }

        [Test]
        public void Clear_ForgetsTheBriefing()
        {
            var record = new NetClientRecord();
            record.ReceiveMap(1d, Map(4), null);
            record.ReceiveEnd(2d, (byte)NetMissionResult.Success);
            record.Clear();
            Assert.That(record.Map, Is.Null);
            Assert.That(record.Result, Is.EqualTo(NetMissionResult.None));
            Assert.That(record.Objectives, Is.Empty);
            Assert.That(record.MapMessages, Is.Zero);
        }
    }
}
```

Then update `Assets/_Project/Tests/EditMode/NetMessageShapeTests.cs`. Replace the expected list in
`TheBroadcasts_AreExactlyTheKnownMessages` with:

```csharp
                "NetContainerMessage", "NetFixtureMessage", "NetFixtureStepMessage", "NetHelloMessage", "NetKnowledgeMessage",
                "NetMapMessage", "NetMissionEndMessage", "NetObjectivesMessage", "NetRefusedMessage", "NetRequestMessage",
                "NetResultMessage", "NetShotMessage", "NetWelcomeMessage",
```

and add this test to the class:

```csharp
        [Test]
        public void TheBriefingMessages_NameNoRoomRegionHashAttemptItemLootOrContent()
        {
            var forbidden = new[] { "room", "region", "hash", "attempt", "item", "loot", "content", "seed", "spawn", "plan", "layout" };
            foreach (var type in new[] { typeof(NetMapMessage), typeof(NetRectWire), typeof(NetBoxWire), typeof(NetTileWire),
                         typeof(NetObjectivesMessage), typeof(NetObjectiveWire), typeof(NetMissionEndMessage), typeof(NetShotMessage) })
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    foreach (var bad in forbidden)
                        Assert.That(field.Name.IndexOf(bad, StringComparison.OrdinalIgnoreCase), Is.LessThan(0), type.Name + "." + field.Name);
        }
```

- [ ] **Step 2: Run them to verify they fail.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetMapWireTests|Blackglass.Tests.NetClientRecordBriefingTests|Blackglass.Tests.NetMessageShapeTests"`
Expected: compilation fails, because `NetMapWire`, `NetMapMessage` and `ReceiveMap` are not defined.

- [ ] **Step 3: Append the messages to `Assets/_Project/Scripts/Net/Adapter/NetMessages.cs`** (inside the namespace, after `NetContainerMessage`).

```csharp
    /// <summary>One floor rectangle of the public map, in tiles (decision 052).</summary>
    public struct NetRectWire
    {
        public short X;
        public short Y;
        public short Width;
        public short Height;
    }

    /// <summary>One wall or obstacle box of the public map: Kind is MissionBoxKind + 1 (0 is never valid); Rise in metres.</summary>
    public struct NetBoxWire
    {
        public byte Kind;
        public short X;
        public short Y;
        public short Width;
        public short Height;
        public float Rise;
    }

    /// <summary>One tile of the public map (a closed container).</summary>
    public struct NetTileWire
    {
        public short X;
        public short Y;
    }

    /// <summary>
    /// Server to a connection that owns units, once after its welcome, only for a Full/All mission (decision 052): the public
    /// map. Nothing else of the mission's generation travels.
    /// </summary>
    public struct NetMapMessage : IBroadcast
    {
        public byte Format;
        public short Width;
        public short Height;
        public NetRectWire[] Floors;
        public NetBoxWire[] Boxes;
        public NetTileWire[] Containers;
    }

    /// <summary>One known objective: Kind is a NetObjectiveKind, State an ObjectiveState; Point and Radius for extraction only.</summary>
    public struct NetObjectiveWire
    {
        public byte Kind;
        public byte State;
        public string Title;
        public bool HasPoint;
        public Vector3 Point;
        public float Radius;
    }

    /// <summary>Server to a connection that owns units: the whole known-objective list, at welcome and after every change.</summary>
    public struct NetObjectivesMessage : IBroadcast
    {
        public NetObjectiveWire[] Objectives;
    }

    /// <summary>Server to a connection that owns units: the mission ended (a NetMissionResult value, 1 or 2).</summary>
    public struct NetMissionEndMessage : IBroadcast
    {
        public byte Result;
    }

    /// <summary>Server to a connection that receives both units' avatars: one resolved basic attack (Hit false: cover turned it away).</summary>
    public struct NetShotMessage : IBroadcast
    {
        public int AttackerId;
        public int TargetId;
        public bool Hit;
    }
```

- [ ] **Step 4: Write `Assets/_Project/Scripts/Net/Adapter/NetMapWire.cs`.**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Conversion between the projection's core types and the wire (decision 052). Decoding never throws: a missing, oversized,
    /// unknown or invalid payload decodes to null with a reason, so a client draws nothing from it.
    /// </summary>
    public static class NetMapWire
    {
        public static NetMapMessage ToMessage(NetMap map)
        {
            var floors = new NetRectWire[map.Floors.Count];
            for (var i = 0; i < floors.Length; i++)
            {
                var r = map.Floors[i];
                floors[i] = new NetRectWire { X = (short)r.x, Y = (short)r.y, Width = (short)r.width, Height = (short)r.height };
            }
            var boxes = new NetBoxWire[map.Boxes.Count];
            for (var i = 0; i < boxes.Length; i++)
            {
                var b = map.Boxes[i];
                boxes[i] = new NetBoxWire
                {
                    Kind = (byte)((int)b.Kind + 1), X = (short)b.Footprint.x, Y = (short)b.Footprint.y,
                    Width = (short)b.Footprint.width, Height = (short)b.Footprint.height, Rise = b.Rise,
                };
            }
            var containers = new NetTileWire[map.Containers.Count];
            for (var i = 0; i < containers.Length; i++)
                containers[i] = new NetTileWire { X = (short)map.Containers[i].x, Y = (short)map.Containers[i].y };
            return new NetMapMessage { Format = NetMap.Format, Width = (short)map.Width, Height = (short)map.Height, Floors = floors, Boxes = boxes, Containers = containers };
        }

        public static NetMap FromMessage(NetMapMessage message, out string problem)
        {
            try
            {
                if (message.Format != NetMap.Format)
                {
                    problem = "map format " + message.Format + " is not " + NetMap.Format;
                    return null;
                }
                if (message.Floors == null || message.Boxes == null || message.Containers == null)
                {
                    problem = "map arrays missing";
                    return null;
                }
                if (message.Floors.Length > NetMap.MaxFloors || message.Boxes.Length > NetMap.MaxBoxes || message.Containers.Length > NetMap.MaxContainers)
                {
                    problem = "map too large";
                    return null;
                }
                var floors = new List<RectInt>(message.Floors.Length);
                foreach (var f in message.Floors)
                    floors.Add(new RectInt(f.X, f.Y, f.Width, f.Height));
                var boxes = new List<NetMapBox>(message.Boxes.Length);
                for (var i = 0; i < message.Boxes.Length; i++)
                {
                    var b = message.Boxes[i];
                    if (b.Kind < 1 || b.Kind > 5)
                    {
                        problem = "box " + i + " has an unknown kind";
                        return null;
                    }
                    boxes.Add(new NetMapBox((MissionBoxKind)(b.Kind - 1), new RectInt(b.X, b.Y, b.Width, b.Height), b.Rise));
                }
                var containers = new List<Vector2Int>(message.Containers.Length);
                foreach (var c in message.Containers)
                    containers.Add(new Vector2Int(c.X, c.Y));
                var map = new NetMap(message.Width, message.Height, floors, boxes, containers);
                problem = NetMapValidation.Problem(map);
                return problem == null ? map : null;
            }
            catch (Exception exception)
            {
                problem = "map unreadable: " + exception.GetType().Name;
                return null;
            }
        }

        public static NetObjectivesMessage ToMessage(IReadOnlyList<NetObjective> objectives)
        {
            var wire = new NetObjectiveWire[objectives.Count];
            for (var i = 0; i < wire.Length; i++)
            {
                var o = objectives[i];
                wire[i] = new NetObjectiveWire { Kind = (byte)o.Kind, State = (byte)o.State, Title = o.Title, HasPoint = o.HasPoint, Point = o.Point, Radius = o.Radius };
            }
            return new NetObjectivesMessage { Objectives = wire };
        }

        public static List<NetObjective> FromMessage(NetObjectivesMessage message, out string problem)
        {
            try
            {
                if (message.Objectives == null)
                {
                    problem = "objective array missing";
                    return null;
                }
                if (message.Objectives.Length > NetObjective.MaxCount)
                {
                    problem = "objectives too many";
                    return null;
                }
                var list = new List<NetObjective>(message.Objectives.Length);
                foreach (var o in message.Objectives)
                    list.Add(new NetObjective((NetObjectiveKind)o.Kind, (ObjectiveState)o.State, o.Title, o.HasPoint, o.Point, o.Radius));
                problem = NetMapValidation.Problem(list);
                return problem == null ? list : null;
            }
            catch (Exception exception)
            {
                problem = "objectives unreadable: " + exception.GetType().Name;
                return null;
            }
        }
    }
}
```

- [ ] **Step 5: Extend `Assets/_Project/Scripts/Net/Core/NetClientRecord.cs`.**
  - Add the fields and properties below next to the existing ones.
  - Add the three methods after `Step`.
  - In `Clear()`, reset them: `map = null; MapProblem = null; MapMessages = 0; objectives = EmptyObjectives; ObjectivesProblem = null; ObjectivesMessages = 0; Result = NetMissionResult.None; EndMessages = 0;`.
  - In the class summary, add "and the briefing (map, objectives, result; decision 052)".

```csharp
        static readonly IReadOnlyList<NetObjective> EmptyObjectives = new NetObjective[0];
        NetMap map;
        IReadOnlyList<NetObjective> objectives = EmptyObjectives;

        /// <summary>The first valid map this connection received, for the whole attempt; null before one.</summary>
        public NetMap Map => map;
        /// <summary>Why the last map message was not used, or null.</summary>
        public string MapProblem { get; private set; }
        public int MapMessages { get; private set; }
        /// <summary>The latest valid known-objective list; empty before one.</summary>
        public IReadOnlyList<NetObjective> Objectives => objectives;
        public string ObjectivesProblem { get; private set; }
        public int ObjectivesMessages { get; private set; }
        /// <summary>The first valid mission result; None before the end message.</summary>
        public NetMissionResult Result { get; private set; }
        public int EndMessages { get; private set; }

        public void ReceiveMap(double time, NetMap decoded, string problem)
        {
            MapMessages++;
            Add(new Decoded(time, "NetMapMessage", false, Vector3.zero));
            if (map != null)
                return;
            if (decoded != null)
            {
                map = decoded;
                MapProblem = null;
            }
            else
            {
                MapProblem = problem ?? "unreadable";
            }
        }

        public void ReceiveObjectives(double time, IReadOnlyList<NetObjective> decoded, string problem)
        {
            ObjectivesMessages++;
            Add(new Decoded(time, "NetObjectivesMessage", false, Vector3.zero));
            if (decoded == null)
            {
                ObjectivesProblem = problem ?? "unreadable";
                return;
            }
            objectives = new System.Collections.ObjectModel.ReadOnlyCollection<NetObjective>(new List<NetObjective>(decoded));
            ObjectivesProblem = null;
        }

        public void ReceiveEnd(double time, byte result)
        {
            EndMessages++;
            Add(new Decoded(time, "NetMissionEndMessage", false, Vector3.zero));
            if (Result == NetMissionResult.None && (result == (byte)NetMissionResult.Success || result == (byte)NetMissionResult.Failure))
                Result = (NetMissionResult)result;
        }
```

The objective point is deliberately recorded without a position (`hasPosition: false`). The harness's "no position before
observation" check concerns hostiles, and the extraction point is not one.

- [ ] **Step 6: Run the EditMode tests to verify they pass.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetMapWireTests|Blackglass.Tests.NetClientRecordBriefingTests|Blackglass.Tests.NetMessageShapeTests|Blackglass.Tests.NetClientRecordTests"`
Expected: PASS.

- [ ] **Step 7: Write the failing PlayMode tests** in `Assets/_Project/Tests/PlayMode/NetBriefingPlayTests.cs`.

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The briefing over the real transport in host mode (decision 052): the map and the known objectives reach a connection that
    /// owns units, after its welcome and never before; a connection that owns none receives nothing; objective changes and the
    /// mission end follow. These tests damage units directly to drive the relay; that is not the acceptance path.
    /// </summary>
    public class NetBriefingPlayTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [UnityTest]
        public IEnumerator A_squad_owner_receives_the_public_map_and_known_objectives_after_its_welcome()
        {
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var record = rig.Client.Record;
            yield return TestWorld.WaitUntil(() => record.Map != null && record.Objectives.Count > 0, 10f);
            Assert.That(record.MapMessages, Is.EqualTo(1));
            var layout = rig.Director.Current.Layout;
            Assert.That(record.Map.Floors, Is.EqualTo(layout.FloorRects));
            Assert.That(record.Map.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise)), Is.EqualTo(layout.Boxes.Select(b => (b.Kind, b.Footprint, b.Height))));
            Assert.That(record.Map.Containers, Is.EqualTo(rig.Director.Current.Loot.Containers.Select(c => c.Tile)));
            var types = record.DecodedMessages.Select(d => d.Type).ToList();
            Assert.That(types.IndexOf("NetWelcomeMessage"), Is.LessThan(types.IndexOf("NetMapMessage")), "nothing of the briefing before the welcome");
            Assert.That(record.Objectives.Select(o => (o.Kind, o.State)),
                Is.EqualTo(new[] { (NetObjectiveKind.Eliminate, ObjectiveState.Active), (NetObjectiveKind.Extraction, ObjectiveState.Inactive) }));
            var zone = rig.Director.Current.ExtractionZone.position;
            Assert.That(Vector3.Distance(record.Objectives[1].Point, zone), Is.LessThan(1e-4f));
            Assert.That(rig.Logs.Any(l => l.Contains("[Net] map projection on floors=")), Is.True);
        }

        [UnityTest]
        public IEnumerator A_connection_that_owns_no_unit_receives_no_map_objectives_end_or_shot()
        {
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenB);
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Ready));
            yield return new WaitForSecondsRealtime(2f);
            var record = rig.Client.Record;
            Assert.That(record.MapMessages + record.ObjectivesMessages + record.EndMessages, Is.Zero);
            Assert.That(record.DecodedMessages.Any(d => d.Type == "NetShotMessage"), Is.False);
        }

        [UnityTest]
        public IEnumerator Objective_changes_and_the_mission_end_reach_the_owner()
        {
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var record = rig.Client.Record;
            yield return TestWorld.WaitUntil(() => record.Objectives.Count == 2, 10f);
            rig.Director.Hostiles[0].GetComponent<Health>().TakeDamage(100000);
            yield return TestWorld.WaitUntil(() => record.Objectives.Count == 2 && record.Objectives[0].State == ObjectiveState.Completed
                && record.Objectives[1].State == ObjectiveState.Active, 10f);
            foreach (var friendly in rig.Director.Friendlies)
                friendly.GetComponent<Health>().TakeDamage(100000);
            yield return TestWorld.WaitUntil(() => record.Result != NetMissionResult.None, 10f);
            Assert.That(record.Result, Is.EqualTo(NetMissionResult.Failure));
            Assert.That(record.EndMessages, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator A_mission_whose_map_knowledge_is_not_full_sends_no_map_and_says_why()
        {
            rig.Director.Settings.intelligence.map = MapKnowledge.Partial;
            rig.Director.RegenerateSame();
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 60f);
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return new WaitForSecondsRealtime(2f);
            Assert.That(rig.Client.Record.MapMessages, Is.Zero);
            Assert.That(rig.Client.Record.ObjectivesMessages, Is.Zero);
            Assert.That(rig.Logs.Any(l => l.Contains("[Net] map projection off: map knowledge Partial")), Is.True);
        }
    }
}
#endif
```

The rig's `RegenerateSame` changes the loaded scene's director settings in memory only; the scene asset is never saved.
`rig.Stop` unloads the scene.

- [ ] **Step 8: Run them to verify they fail.**
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetBriefingPlayTests"`
Expected: FAIL. The first test times out waiting for `record.Map`, because nothing is sent yet.

- [ ] **Step 9: Make `NetClient` decode the messages.** In `Assets/_Project/Scripts/Net/Adapter/NetClient.cs`:
  - add `using System;`;
  - add the event below;
  - register the four broadcasts in `Connect` beside the existing ones, and unregister them in `Disconnect`;
  - add the four handlers next to `OnContainer`.

```csharp
        /// <summary>A relayed shot (decision 052): both units are avatars this client was sent. Presentation reads it; nothing else.</summary>
        public event Action<NetShot> ShotReceived;
```

```csharp
            client.RegisterBroadcast<NetMapMessage>(OnMap);
            client.RegisterBroadcast<NetObjectivesMessage>(OnObjectives);
            client.RegisterBroadcast<NetMissionEndMessage>(OnMissionEnd);
            client.RegisterBroadcast<NetShotMessage>(OnShot);
```

```csharp
                    client.UnregisterBroadcast<NetMapMessage>(OnMap);
                    client.UnregisterBroadcast<NetObjectivesMessage>(OnObjectives);
                    client.UnregisterBroadcast<NetMissionEndMessage>(OnMissionEnd);
                    client.UnregisterBroadcast<NetShotMessage>(OnShot);
```

```csharp
        void OnMap(NetMapMessage message, Channel channel)
        {
            var map = NetMapWire.FromMessage(message, out var problem);
            record.ReceiveMap(Now, map, problem);
            if (map == null)
                Debug.LogWarning("[NetClient] map refused: " + problem);
        }

        void OnObjectives(NetObjectivesMessage message, Channel channel)
        {
            var list = NetMapWire.FromMessage(message, out var problem);
            record.ReceiveObjectives(Now, list, problem);
            if (list == null)
                Debug.LogWarning("[NetClient] objectives refused: " + problem);
        }

        void OnMissionEnd(NetMissionEndMessage message, Channel channel) => record.ReceiveEnd(Now, message.Result);

        void OnShot(NetShotMessage message, Channel channel)
        {
            record.Note(Now, nameof(NetShotMessage));
            if (message.AttackerId > 0 && message.TargetId > 0)
                ShotReceived?.Invoke(new NetShot(message.AttackerId, message.TargetId, message.Hit));
        }
```

- [ ] **Step 10: Make `NetServer` send the briefing.** In `Assets/_Project/Scripts/Net/Adapter/NetServer.cs`:
  - Add `using System.Linq;`.
  - Add fields beside the others:

```csharp
        readonly HashSet<NetConnection> participants = new HashSet<NetConnection>();
        MissionDirector mission;
        NetMap map;
        bool hasTerminal;
        bool hasCameras;
        bool objectivesDirty;
```

  - In `Begin`, after `Host = new NetSessionHost(...)` and before `if (options.NetQuiet)`:

```csharp
            // The public map and known objectives (decision 052): built once from the ready mission, or refused with a reason.
            mission = director;
            var current = director.Current;
            hasTerminal = current.Terminal != null;
            hasCameras = current.Security.HasTerminal || current.Security.Cameras.Count > 0;
            map = NetMapProjection.Map(current.Layout, current.Loot.Containers.Select(c => c.Tile).ToList(), director.Settings.intelligence,
                hasTerminal, hasCameras, out var mapRefusal);
            Debug.Log(Prefix + (map != null
                ? FormattableString.Invariant($"map projection on floors={map.Floors.Count} boxes={map.Boxes.Count} containers={map.Containers.Count}")
                : "map projection off: " + mapRefusal));
            if (director.Runtime != null)
                director.Runtime.ObjectiveChanged += OnObjectiveChanged;
            director.MissionFinished += OnMissionFinished;
```

  - In `Stop()`, before `manager = null;`:

```csharp
            if (mission != null)
            {
                if (mission.Runtime != null)
                    mission.Runtime.ObjectiveChanged -= OnObjectiveChanged;
                mission.MissionFinished -= OnMissionFinished;
            }
            mission = null;
            map = null;
            participants.Clear();
            objectivesDirty = false;
```

  - In `Update()`, after the `Guard.Tick();` line:

```csharp
            if (objectivesDirty)
            {
                objectivesDirty = false;
                var objectives = NetMapWire.ToMessage(CurrentObjectives());
                foreach (var participant in participants)
                    if (welcomed.TryGetValue(participant, out var target))
                        manager.ServerManager.Broadcast(target, objectives, false);
            }
```

  - In `OnRemoteState`, inside `if (leaving != null) { ... }`, add `participants.Remove(leaving);`.
  - In `OnHello`, after `manager.ServerManager.Objects.RebuildObservers(connection);`:

```csharp
            if (ownedCount > 0)
            {
                participants.Add(joined);
                SendBriefing(connection);
            }
```

  - Add the methods:

```csharp
        List<NetObjective> CurrentObjectives() =>
            mission != null && mission.Runtime != null
                ? NetMapProjection.Objectives(mission.Runtime.Objectives, mission.Settings.intelligence, hasTerminal, hasCameras)
                : new List<NetObjective>();

        // Only after the welcome and only to a connection that owns units: the map, the known objectives, and the result if over.
        void SendBriefing(NetworkConnection connection)
        {
            if (map != null)
                manager.ServerManager.Broadcast(connection, NetMapWire.ToMessage(map), false);
            var objectives = CurrentObjectives();
            if (objectives.Count > 0)
                manager.ServerManager.Broadcast(connection, NetMapWire.ToMessage(objectives), false);
            var phase = mission != null ? mission.Phase : MissionPhase.Inactive;
            if (phase == MissionPhase.Success || phase == MissionPhase.Failure)
                manager.ServerManager.Broadcast(connection, new NetMissionEndMessage { Result = (byte)(phase == MissionPhase.Success ? NetMissionResult.Success : NetMissionResult.Failure) }, false);
        }

        void OnObjectiveChanged(MissionObjective objective) => objectivesDirty = true;

        void OnMissionFinished(MissionPhase phase)
        {
            objectivesDirty = true;
            var end = new NetMissionEndMessage { Result = (byte)(phase == MissionPhase.Success ? NetMissionResult.Success : NetMissionResult.Failure) };
            foreach (var participant in participants)
                if (welcomed.TryGetValue(participant, out var target))
                    manager.ServerManager.Broadcast(target, end, false);
            Debug.Log(Prefix + FormattableString.Invariant($"mission end {phase} sent to {participants.Count} client(s)"));
        }
```

- [ ] **Step 11: Allow the new types in the harness.** In `Assets/_Project/Scripts/Net/Adapter/NetHarnessVisibility.cs`,
  extend `AllowedMessages` to:

```csharp
            nameof(NetWelcomeMessage), nameof(NetResultMessage), nameof(NetKnowledgeMessage), nameof(NetFixtureStepMessage),
            nameof(NetContainerMessage), nameof(NetMapMessage), nameof(NetObjectivesMessage), nameof(NetMissionEndMessage),
            nameof(NetShotMessage),
```

- [ ] **Step 12: Run the PlayMode tests to verify they pass, then the existing networking PlayMode classes.**
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetBriefingPlayTests"`
Expected: PASS, 4 tests.
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetLoopback|Blackglass.Tests.NetHarness|Blackglass.Tests.AttemptNetPlayTests|Blackglass.Tests.NetSquadIntelPlayModeTests"`
Expected: PASS, same counts as on `main`. Record the before and after counts in the ledger.

- [ ] **Step 13: Commit.**

```bash
git add Assets/_Project/Scripts/Net/Adapter/NetMessages.cs Assets/_Project/Scripts/Net/Adapter/NetMapWire.cs* \
  Assets/_Project/Scripts/Net/Core/NetClientRecord.cs Assets/_Project/Scripts/Net/Adapter/NetClient.cs \
  Assets/_Project/Scripts/Net/Adapter/NetServer.cs Assets/_Project/Scripts/Net/Adapter/NetHarnessVisibility.cs \
  Assets/_Project/Tests/EditMode/NetMapWireTests.cs* Assets/_Project/Tests/EditMode/NetClientRecordBriefingTests.cs* \
  Assets/_Project/Tests/EditMode/NetMessageShapeTests.cs Assets/_Project/Tests/PlayMode/NetBriefingPlayTests.cs*
git commit -m "Briefing on the wire: the public map, known objectives and mission end, to unit owners only, after the welcome" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Shot relay, filtered by replication

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetShotRelay.cs`
- Modify: `Assets/_Project/Scripts/Net/Adapter/NetServer.cs`
- Test: `Assets/_Project/Tests/EditMode/NetShotRelayTests.cs`

**Interfaces:**
- Consumes: `NetShot` (Task 1), `NetShotMessage` and `NetClient.ShotReceived` (Task 3), `NetVisibility.Replicates(NetConnection, int)`.
- Produces: `NetShotRelay.Add(NetShot)`, `NetShotRelay.Flush(IEnumerable<NetConnection>, Func<NetConnection, int, bool>, Action<NetConnection, NetShot>)`, `NetShotRelay.Clear()`, `NetShotRelay.Pending`, `NetShotRelay.Dropped`, `NetShotRelay.MaxPending = 256`.

- [ ] **Step 1: Write the failing tests** in `Assets/_Project/Tests/EditMode/NetShotRelayTests.cs`.

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>A shot reaches a connection only while that connection receives both units' avatars (decision 052).</summary>
    public class NetShotRelayTests
    {
        const int FriendA = 1;
        const int FriendB = 2;
        const int Hostile = 3;

        sealed class Intel : INetIntelSource
        {
            public readonly HashSet<(NetConnection, int)> Seen = new HashSet<(NetConnection, int)>();
            public bool Observes(NetConnection connection, int hostileId) => Seen.Contains((connection, hostileId));
        }

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<(NetConnection, NetShot)> sent = new List<(NetConnection, NetShot)>();
        Intel intel;
        NetVisibility visibility;
        NetConnection a;
        NetConnection b;
        NetShotRelay relay;

        [SetUp]
        public void SetUp()
        {
            var units = new NetUnits();
            units.Register(NetTestKit.MakeUnit(hosts, "FriendlyUnit_1"), false);
            units.Register(NetTestKit.MakeUnit(hosts, "FriendlyUnit_2"), false);
            units.Register(NetTestKit.MakeUnit(hosts, "HostileUnit_1"), true);
            intel = new Intel();
            visibility = new NetVisibility(units, intel);
            a = new NetConnection("account-a", new[] { FriendA, FriendB });
            b = new NetConnection("account-b", new int[0]);
            visibility.Add(a);
            visibility.Add(b);
            relay = new NetShotRelay();
            sent.Clear();
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        void Flush()
        {
            visibility.Update(new List<NetKnowledgeChange>());
            relay.Flush(new[] { a, b }, visibility.Replicates, (c, s) => sent.Add((c, s)));
        }

        [Test]
        public void AShotFromAnUnobservedHostile_IsSentToNobody()
        {
            relay.Add(new NetShot(Hostile, FriendA, true));
            Flush();
            Assert.That(sent, Is.Empty);
            Assert.That(relay.Pending, Is.Zero, "a shot never waits for a later frame");
        }

        [Test]
        public void AShotBetweenAnObservedHostileAndAnOwnUnit_IsSentToTheOwnerOnly()
        {
            intel.Seen.Add((a, Hostile));
            relay.Add(new NetShot(Hostile, FriendA, false));
            relay.Add(new NetShot(FriendB, Hostile, true));
            Flush();
            Assert.That(sent.Select(s => s.Item1), Is.All.SameAs(a));
            Assert.That(sent.Select(s => (s.Item2.AttackerId, s.Item2.TargetId, s.Item2.Hit)), Is.EqualTo(new[] { (Hostile, FriendA, false), (FriendB, Hostile, true) }));
        }

        [Test]
        public void ASendThatThrows_StillEmptiesTheQueue()
        {
            intel.Seen.Add((a, Hostile));
            relay.Add(new NetShot(Hostile, FriendA, true));
            visibility.Update(new List<NetKnowledgeChange>());
            Assert.Throws<System.InvalidOperationException>(() => relay.Flush(new[] { a }, visibility.Replicates, (c, s) => throw new System.InvalidOperationException()));
            Assert.That(relay.Pending, Is.Zero);
        }

        [Test]
        public void MoreThanTheCapInOneFrame_AreDroppedAndCounted()
        {
            for (var i = 0; i < NetShotRelay.MaxPending + 5; i++)
                relay.Add(new NetShot(FriendA, Hostile, true));
            Assert.That(relay.Pending, Is.EqualTo(NetShotRelay.MaxPending));
            Assert.That(relay.Dropped, Is.EqualTo(5));
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetShotRelayTests"`
Expected: compilation fails, because `NetShotRelay` is not defined.

- [ ] **Step 3: Write `Assets/_Project/Scripts/Net/Core/NetShotRelay.cs`.**

```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// The frame's resolved basic attacks, relayed after the visibility pass (decision 052): each goes to every connection that
    /// receives both units' avatars at that moment, and then the queue is empty. A shot never waits for a later frame, so a shot
    /// from a hostile a connection does not observe never reaches it. Holds no networking type.
    /// </summary>
    public sealed class NetShotRelay
    {
        public const int MaxPending = 256;

        readonly List<NetShot> pending = new List<NetShot>();

        public int Pending => pending.Count;
        /// <summary>Shots dropped because a frame queued more than MaxPending (diagnostics).</summary>
        public int Dropped { get; private set; }

        public void Add(NetShot shot)
        {
            if (pending.Count >= MaxPending)
            {
                Dropped++;
                return;
            }
            pending.Add(shot);
        }

        public void Flush(IEnumerable<NetConnection> connections, Func<NetConnection, int, bool> replicates, Action<NetConnection, NetShot> send)
        {
            try
            {
                foreach (var shot in pending)
                    foreach (var connection in connections)
                        if (replicates(connection, shot.AttackerId) && replicates(connection, shot.TargetId))
                            send(connection, shot);
            }
            finally
            {
                pending.Clear();
            }
        }

        public void Clear() => pending.Clear();
    }
}
```

- [ ] **Step 4: Hook it into `NetServer`.**
  - Add the fields:

```csharp
        readonly NetShotRelay shots = new NetShotRelay();
        readonly List<(UnitAttacker Attacker, Action<ShotRecord> Handler)> shotHandlers = new List<(UnitAttacker, Action<ShotRecord>)>();
```

  - In `Begin`, after the `director.MissionFinished += OnMissionFinished;` line from Task 3:

```csharp
            for (var id = 1; id <= Units.Count; id++)
            {
                if (!Units.TryGet(id, out var unit) || !unit.TryGetComponent<UnitAttacker>(out var attacker))
                    continue;
                var attackerId = id;   // the loop variable is shared by every iteration's closure
                Action<ShotRecord> handler = record => OnFired(attackerId, record);
                attacker.Fired += handler;
                shotHandlers.Add((attacker, handler));
            }
```

  - In `Stop()`, beside the Task 3 cleanup:

```csharp
            foreach (var (attacker, handler) in shotHandlers)
                if (attacker != null)
                    attacker.Fired -= handler;
            shotHandlers.Clear();
            shots.Clear();
```

  - In `Update()`, after the visibility `finally` block that publishes changes (so this frame's exposure is in force), and before `Guard.Tick();`:

```csharp
            shots.Flush(welcomed.Keys, Visibility.Replicates, (connection, shot) =>
                manager.ServerManager.Broadcast(welcomed[connection], new NetShotMessage { AttackerId = shot.AttackerId, TargetId = shot.TargetId, Hit = shot.Hit }, false));
```

  - Add the handler:

```csharp
        // A resolved basic attack between two registered units; ability shots stay off the wire (Stage 14).
        void OnFired(int attackerId, ShotRecord record)
        {
            if (record.Ability != null || record.Target == null || !record.Target.TryGetComponent<CommandableUnit>(out var target))
                return;
            var targetId = Units.IdOf(target);
            if (targetId != 0)
                shots.Add(new NetShot(attackerId, targetId, record.Outcome == ShotOutcome.Hit));
        }
```

- [ ] **Step 5: Run the tests.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetShotRelayTests|Blackglass.Tests.NetSourceAuditTests"`
Expected: PASS.
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetBriefingPlayTests|Blackglass.Tests.AttemptLoopbackPlayTests"`
Expected: PASS. `AttemptLoopbackPlayTests` is the full scripted run; the harness now tolerates `NetShotMessage`.

- [ ] **Step 6: Commit.**

```bash
git add Assets/_Project/Scripts/Net/Core/NetShotRelay.cs* Assets/_Project/Scripts/Net/Adapter/NetServer.cs \
  Assets/_Project/Tests/EditMode/NetShotRelayTests.cs*
git commit -m "Shot relay: resolved basic attacks to connections that receive both avatars, after the visibility pass" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Client map view

**Files:**
- Create: `Assets/_Project/Scripts/Net/Play/NetMapView.cs`
- Create: `Assets/_Project/Tests/PlayMode/TestSupport/NetPlayKit.cs`
- Test: `Assets/_Project/Tests/PlayMode/NetMapViewPlayTests.cs`

**Interfaces:**
- Consumes: `NetClient.Record.Map`, `NetClient.Record.Objectives`, `NetMapGeometry`, `NetMapValidation`.
- Produces:
  - `NetMapView.Initialize(NetClient, EnvironmentTheme, Material ground, Material obstacle)`;
  - `NetMapView.Build(NetMap) : bool`;
  - `Layout : MissionLayout`, `Built : bool`, `Obstacles : IReadOnlyList<Bounds>`, `Cubes : IReadOnlyList<Transform>`
    (floors in order, then boxes, then containers), `Extraction : Transform`, `RootName = "ClientMap"`;
  - `NetPlayKit.AddMapView(GameObject host, NetClient client, bool themed) : NetMapView`.

- [ ] **Step 1: Write the test support** in `Assets/_Project/Tests/PlayMode/TestSupport/NetPlayKit.cs`.

```csharp
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Adds the play client's components to a test's NetClient host, wired to the project's real assets.</summary>
    internal static class NetPlayKit
    {
        public const string ThemePath = "Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset";

        public static T Load<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert(asset != null, "missing " + path);
            return asset;
        }

        public static NetMapView AddMapView(GameObject host, NetClient client, bool themed)
        {
            var view = host.AddComponent<NetMapView>();
            view.Initialize(client, themed ? Load<EnvironmentTheme>(ThemePath) : null,
                Load<Material>("Assets/_Project/Materials/Ground.mat"), Load<Material>("Assets/_Project/Materials/Obstacle.mat"));
            return view;
        }

        static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new System.InvalidOperationException(message);
        }
    }
}
#endif
```

- [ ] **Step 2: Write the failing tests** in `Assets/_Project/Tests/PlayMode/NetMapViewPlayTests.cs`.

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The client draws exactly the server's geometry (decision 052): every floor, wall, obstacle and crate cube the client builds
    /// sits where the server's real cube or crate collider is, in the same process; the client builds no collider, NavMesh or
    /// cover; a refused map draws nothing.
    /// </summary>
    public class NetMapViewPlayTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            var root = GameObject.Find(NetMapView.RootName);
            if (root != null)
                Object.Destroy(root);
            yield return rig.Stop();
        }

        // MissionBuilder adds the floor cubes, then one cube per box, as Geometry's first children, in layout order (content such as
        // the crates comes after them); the crates are read from the mission's own list.
        static List<(string Name, Vector3 Position, Vector3 Size)> ServerShapes(GeneratedMission mission)
        {
            var result = new List<(string, Vector3, Vector3)>();
            var layout = mission.Layout;
            var cubes = layout.FloorRects.Count + layout.Boxes.Count;
            for (var i = 0; i < cubes; i++)
            {
                var cube = mission.Geometry.GetChild(i);
                result.Add((cube.name, cube.position, cube.localScale));
            }
            foreach (var container in mission.LootContainers)
            {
                var collider = container.GetComponent<BoxCollider>();
                result.Add((container.name, container.transform.position, collider.size));
            }
            return result;
        }

        [UnityTest]
        public IEnumerator Every_client_cube_sits_where_the_servers_geometry_is([Values(false, true)] bool themed)
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var view = NetPlayKit.AddMapView(rig.Client.gameObject, rig.Client, themed);
            yield return TestWorld.WaitUntil(() => view.Built, 10f);
            var server = ServerShapes(rig.Director.Current);
            Assert.That(view.Cubes.Count, Is.EqualTo(server.Count));
            for (var i = 0; i < server.Count; i++)
            {
                Assert.That(Vector3.Distance(view.Cubes[i].position, server[i].Position), Is.LessThan(1e-4f), server[i].Name + " position");
                Assert.That(Vector3.Distance(view.Cubes[i].localScale, server[i].Size), Is.LessThan(1e-4f), server[i].Name + " size");
            }
            Assert.That(view.Layout.WorldBounds, Is.EqualTo(rig.Director.Current.Layout.WorldBounds));
            Assert.That(Vector3.Distance(view.Extraction.position, rig.Director.Current.ExtractionZone.position), Is.LessThan(1e-4f));
        }

        [UnityTest]
        public IEnumerator The_client_map_has_no_collider_navmesh_or_cover()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var view = NetPlayKit.AddMapView(rig.Client.gameObject, rig.Client, true);
            yield return TestWorld.WaitUntil(() => view.Built, 10f);
            var root = GameObject.Find(NetMapView.RootName);
            Assert.That(root.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<Unity.AI.Navigation.NavMeshSurface>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<CoverSurface>(true), Is.Empty);
        }

        [UnityTest]
        public IEnumerator An_invalid_map_draws_nothing()
        {
            var host = new GameObject("MapViewOnly");
            var view = NetPlayKit.AddMapView(host, null, false);
            var bad = new NetMap(10, 10, new List<RectInt> { new RectInt(0, 0, 4, 4), new RectInt(2, 2, 4, 4) }, new List<NetMapBox>(), new List<Vector2Int>());
            Assert.That(view.Build(bad), Is.False);
            Assert.That(view.Built, Is.False);
            Assert.That(GameObject.Find(NetMapView.RootName), Is.Null);
            Object.Destroy(host);
            yield return null;
        }
    }
}
#endif
```

- [ ] **Step 3: Run them to verify they fail.**
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetMapViewPlayTests"`
Expected: compilation fails, because `NetMapView` is not defined.

- [ ] **Step 4: Write `Assets/_Project/Scripts/Net/Play/NetMapView.cs`.**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Draws the public map the server projected (decision 052) once, under one ClientMap root:
    /// - the environment theme's modules (EnvironmentVisualBuilder, visual-only);
    /// - the gameplay-shaped cubes, whose renderers show only without a theme;
    /// - the closed crates;
    /// - the extraction disc and beacon, coloured by the objective's state.
    /// It creates no collider, NavMesh or cover component, so it cannot change any simulation, even in a host-mode test where
    /// the server's mission shares the scene. A refused map draws nothing.
    /// </summary>
    public sealed class NetMapView : MonoBehaviour
    {
        public const string RootName = "ClientMap";
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Color CrateColour = new Color(0.85f, 0.6f, 0.15f);   // MissionContent's loot colour

        [SerializeField] NetClient client;
        [SerializeField] EnvironmentTheme theme;
        [SerializeField] Material groundMaterial;
        [SerializeField] Material obstacleMaterial;

        readonly List<Bounds> obstacles = new List<Bounds>();
        readonly List<Transform> cubes = new List<Transform>();
        readonly List<Renderer> zoneRenderers = new List<Renderer>();
        Transform root;
        MaterialPropertyBlock block;

        internal void Initialize(NetClient netClient, EnvironmentTheme environmentTheme, Material ground, Material obstacle)
        {
            client = netClient;
            theme = environmentTheme;
            groundMaterial = ground;
            obstacleMaterial = obstacle;
        }

        public MissionLayout Layout { get; private set; }
        public bool Built => Layout != null;
        /// <summary>Every box and crate, as world bounds: what a click ray can be stopped by.</summary>
        public IReadOnlyList<Bounds> Obstacles => obstacles;
        /// <summary>The cubes in the server's order: floors, then boxes, then crates.</summary>
        public IReadOnlyList<Transform> Cubes => cubes;
        /// <summary>The extraction zone's visual, or null before the objective arrives.</summary>
        public Transform Extraction { get; private set; }

        void LateUpdate()
        {
            if (client == null)
                return;
            if (!Built && client.Record.Map != null)
                Build(client.Record.Map);
            if (Built)
                DrawExtraction(client.Record.Objectives);
        }

        void OnDestroy()
        {
            if (root != null)
                Destroy(root.gameObject);
        }

        /// <summary>Draws the map once; false (and nothing drawn) for an invalid map or when one is already drawn.</summary>
        internal bool Build(NetMap map)
        {
            if (Built || NetMapValidation.Problem(map) != null)
                return false;
            var layout = NetMapGeometry.ToLayout(map);
            root = new GameObject(RootName).transform;
            var showCubes = theme == null;
            for (var i = 0; i < layout.FloorRects.Count; i++)
                Cube("Floor_" + (i + 1), NetMapGeometry.FloorBounds(layout, layout.FloorRects[i]), groundMaterial, showCubes, false, null);
            foreach (var box in layout.Boxes)
                Cube(box.Name, NetMapGeometry.BoxBounds(layout, box), obstacleMaterial, showCubes, true, null);
            for (var i = 0; i < map.Containers.Count; i++)
                Cube("LootContainer_" + (i + 1), NetMapGeometry.ContainerBounds(layout, map.Containers[i]), obstacleMaterial, true, true, CrateColour);
            if (theme != null)
                EnvironmentVisualBuilder.Build(layout, theme, root);
            Layout = layout;
            return true;
        }

        void Cube(string objectName, Bounds bounds, Material material, bool visible, bool obstacle, Color? colour)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = objectName;
            DestroyImmediate(cube.GetComponent<Collider>());   // at once: not even one physics step may see it
            cube.transform.SetParent(root, false);
            cube.transform.position = bounds.center;
            cube.transform.localScale = bounds.size;
            var renderer = cube.GetComponent<Renderer>();
            if (material != null)
                renderer.sharedMaterial = material;
            renderer.enabled = visible;
            if (colour.HasValue)
                Tint(renderer, colour.Value);
            if (obstacle)
                obstacles.Add(bounds);
            cubes.Add(cube.transform);
        }

        void DrawExtraction(IReadOnlyList<NetObjective> objectives)
        {
            foreach (var objective in objectives)
            {
                if (objective.Kind != NetObjectiveKind.Extraction || !objective.HasPoint)
                    continue;
                if (Extraction == null)
                    Extraction = CreateZone(objective.Radius);
                Extraction.position = objective.Point;
                var colour = ObjectiveMarker.ZoneColour(objective.State);
                foreach (var renderer in zoneRenderers)
                    Tint(renderer, colour);
                return;
            }
        }

        // MissionContent.CreateZone's look: a flat disc of the zone's radius and a thin beacon, with no collider.
        Transform CreateZone(float radius)
        {
            var zone = new GameObject("ExtractionZone").transform;
            zone.SetParent(root, false);
            ZonePart(zone, "Disc", new Vector3(radius * 2f, 0.02f, radius * 2f), 0.03f);
            ZonePart(zone, "Beacon", new Vector3(0.25f, 1.5f, 0.25f), 1.5f);
            return zone;
        }

        void ZonePart(Transform zone, string partName, Vector3 scale, float height)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            part.name = partName;
            DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(zone, false);
            part.transform.localPosition = new Vector3(0f, height, 0f);
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            if (obstacleMaterial != null)
                renderer.sharedMaterial = obstacleMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            zoneRenderers.Add(renderer);
        }

        void Tint(Renderer renderer, Color colour)
        {
            if (block == null)
                block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, colour);
            renderer.SetPropertyBlock(block);
        }
    }
}
```

The crate colour duplicates `MissionContent.LootColour`, which is private. That is deliberate: no offline file is modified.

- [ ] **Step 5: Run the tests to verify they pass.**
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetMapViewPlayTests"`
Expected: PASS, 4 tests (the themed case runs twice).

- [ ] **Step 6: Commit.**

```bash
git add Assets/_Project/Scripts/Net/Play.meta Assets/_Project/Scripts/Net/Play/NetMapView.cs* \
  Assets/_Project/Tests/PlayMode/TestSupport/NetPlayKit.cs* Assets/_Project/Tests/PlayMode/NetMapViewPlayTests.cs*
git commit -m "Client map view: the projected map drawn with the environment theme, cube-for-cube where the server's geometry is, no physics" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Avatars and combat feedback

**Files:**
- Create: `Assets/_Project/Scripts/Net/Play/NetAvatarView.cs`
- Create: `Assets/_Project/Scripts/Net/Play/NetPlayAvatars.cs`
- Test: `Assets/_Project/Tests/PlayMode/NetPlayAvatarsPlayTests.cs` (it includes the pure speed test, since `NetAvatarView` is a `MonoBehaviour`)

**Interfaces:**
- Consumes: `NetClient.VisibleStates()`, `NetClient.ShotReceived`, `NetClient.Record.LastKnown`, `NetUnitState`.
- Produces:
  - `NetAvatarView.Speed(Vector3 previous, Vector3 current, float deltaTime) : float` (static);
  - `NetAvatarView.MuzzlePoint`, `ChestPoint`, `Fire()`, `Hit()`;
  - `NetPlayAvatars.Initialize(NetClient, Func<int, bool> isSelected, GameObject visualPrefab, WeaponVisualAsset weapon, GameObject tracerPrefab, Material ringMaterial, Material markerMaterial)`;
  - `NetPlayAvatars.ViewOf(int unitId) : NetAvatarView`;
  - `event Action<int, string> Floater` (unit id, text) and `IsOwned(NetUnitState)`.

- [ ] **Step 1: Write the failing tests** in `Assets/_Project/Tests/PlayMode/NetPlayAvatarsPlayTests.cs`.

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>Each avatar this client was sent gets the character visual with no collider; the animation is driven from replicated state.</summary>
    public class NetPlayAvatarsPlayTests
    {
        const string VisualPath = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Visual.prefab";
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true);
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [Test]
        public void Speed_IsFlatMetresPerSecond_AndAWarpReadsAsStanding()
        {
            Assert.That(NetAvatarView.Speed(Vector3.zero, new Vector3(0.05f, 3f, 0f), 0.01f), Is.EqualTo(5f).Within(1e-4f));
            Assert.That(NetAvatarView.Speed(Vector3.zero, new Vector3(10f, 0f, 0f), 0.01f), Is.Zero);
            Assert.That(NetAvatarView.Speed(Vector3.zero, Vector3.one, 0f), Is.Zero);
        }

        [UnityTest]
        public IEnumerator Every_own_avatar_gets_the_character_visual_without_a_collider()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var avatars = rig.Client.gameObject.AddComponent<NetPlayAvatars>();
            avatars.Initialize(rig.Client, _ => false, NetPlayKit.Load<GameObject>(VisualPath),
                NetPlayKit.Load<WeaponVisualAsset>("Assets/_Project/Data/Items/Visuals/RifleVisual.asset"),
                NetPlayKit.Load<GameObject>("Assets/_Project/Presentation/Prefabs/TracerRifle.prefab"),
                NetPlayKit.Load<Material>("Assets/_Project/Materials/SelectionRing.mat"), NetPlayKit.Load<Material>("Assets/_Project/Materials/IntelMarker.mat"));
            yield return TestWorld.WaitUntil(() => rig.Client.OwnedCount > 0 && NetClient.VisibleStates().Count(s => avatars.ViewOf(s.UnitId) != null) == rig.Client.OwnedCount, 10f);
            foreach (var state in NetClient.VisibleStates())
            {
                var view = avatars.ViewOf(state.UnitId);
                Assert.That(view.GetComponentInChildren<Animator>(), Is.Not.Null, "#" + state.UnitId + " has the animated model");
                Assert.That(view.GetComponentsInChildren<Collider>(true), Is.Empty, "#" + state.UnitId + " has no collider");
                Assert.That(view.GetComponentInChildren<UnitWeaponVisual>().Current, Is.Not.Null, "#" + state.UnitId + " holds the rifle");
            }
            Assert.That(NetClient.VisibleStates().Any(s => s.Hostile), Is.False, "the quiet hostile is not observed from spawn");
        }
    }
}
#endif
```

- [ ] **Step 2: Run them to verify they fail.**
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetPlayAvatarsPlayTests"`
Expected: compilation fails, because `NetAvatarView` and `NetPlayAvatars` are not defined.

- [ ] **Step 3: Write `Assets/_Project/Scripts/Net/Play/NetAvatarView.cs`.**

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One replicated avatar's look in the play client: the character model with its rifle (colliders stripped), a side ring
    /// (blue own, red hostile) and a selection ring. Its Animator is driven only from what was replicated: Speed from the
    /// interpolated position, Dead from Alive, Fire on a relayed shot, Hit on a health drop. It decides nothing.
    /// </summary>
    public sealed class NetAvatarView : MonoBehaviour
    {
        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int DeadId = Animator.StringToHash("Dead");
        static readonly int FireId = Animator.StringToHash("Fire");
        static readonly int HitId = Animator.StringToHash("Hit");
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Color OwnColour = new Color(0.2f, 0.45f, 0.95f);
        static readonly Color HostileColour = new Color(0.9f, 0.2f, 0.2f);
        static readonly Color OtherColour = new Color(0.25f, 0.75f, 0.3f);
        static readonly Color SelectedColour = new Color(1f, 0.85f, 0.2f);
        // UnitAnimationDriver's guard: a move this fast in one frame is a placement, not walking.
        const float TeleportSpeed = 40f;
        const float SpeedDamping = 0.1f;

        Animator animator;
        UnitWeaponVisual weapon;
        Renderer sideRing;
        Renderer selectionRing;
        MaterialPropertyBlock block;
        Vector3 last;
        bool started;

        internal static NetAvatarView Create(NetUnitState state, GameObject visualPrefab, WeaponVisualAsset weaponAsset, Material ringMaterial)
        {
            var holder = new GameObject("PlayVisual_" + state.UnitId);
            holder.transform.SetParent(state.transform, false);
            var view = holder.AddComponent<NetAvatarView>();
            if (visualPrefab != null)
            {
                var model = Instantiate(visualPrefab, holder.transform);
                EnvironmentVisualBuilder.StripColliders(model);
                view.animator = model.GetComponentInChildren<Animator>();
                view.weapon = model.AddComponent<UnitWeaponVisual>();
                view.weapon.Show(weaponAsset);
            }
            else
            {
                var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                DestroyImmediate(capsule.GetComponent<Collider>());
                capsule.transform.SetParent(holder.transform, false);
                capsule.transform.localPosition = new Vector3(0f, 1f, 0f);
            }
            view.sideRing = Ring(holder.transform, "SideRing", 1.1f, 0.02f, ringMaterial);
            view.selectionRing = Ring(holder.transform, "SelectionRing", 1.5f, 0.01f, ringMaterial);
            return view;
        }

        static Renderer Ring(Transform parent, string ringName, float diameter, float height, Material material)
        {
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = ringName;
            DestroyImmediate(ring.GetComponent<Collider>());
            ring.transform.SetParent(parent, false);
            ring.transform.localPosition = new Vector3(0f, height, 0f);
            ring.transform.localScale = new Vector3(diameter, 0.005f, diameter);
            var renderer = ring.GetComponent<Renderer>();
            if (material != null)
                renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return renderer;
        }

        /// <summary>Flat metres per second between two positions; 0 for no time or for a jump faster than TeleportSpeed.</summary>
        internal static float Speed(Vector3 previous, Vector3 current, float deltaTime)
        {
            if (deltaTime <= 0f)
                return 0f;
            var flat = current - previous;
            flat.y = 0f;
            var speed = flat.magnitude / deltaTime;
            return speed > TeleportSpeed ? 0f : speed;
        }

        internal Vector3 MuzzlePoint => weapon != null && weapon.Muzzle != null ? weapon.Muzzle.position : transform.position + Vector3.up * 1.4f;
        internal Vector3 ChestPoint => transform.position + Vector3.up * 1.2f;

        internal void Tick(NetUnitState state, bool owned, bool selected, float deltaTime)
        {
            var position = transform.position;
            if (animator != null)
            {
                if (started)
                    animator.SetFloat(SpeedId, Speed(last, position, deltaTime), SpeedDamping, deltaTime);
                animator.SetBool(DeadId, !state.Alive);
            }
            last = position;
            started = true;
            Tint(sideRing, state.Hostile ? HostileColour : owned ? OwnColour : OtherColour);
            sideRing.enabled = state.Alive;
            Tint(selectionRing, SelectedColour);
            selectionRing.enabled = selected && state.Alive;
        }

        internal void Fire()
        {
            if (animator != null)
                animator.SetTrigger(FireId);
        }

        internal void Hit()
        {
            if (animator != null)
                animator.SetTrigger(HitId);
        }

        void Tint(Renderer renderer, Color colour)
        {
            if (block == null)
                block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, colour);
            renderer.SetPropertyBlock(block);
        }
    }
}
```

- [ ] **Step 4: Write `Assets/_Project/Scripts/Net/Play/NetPlayAvatars.cs`.**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The play client's units:
    /// - an NetAvatarView on every avatar this client was sent;
    /// - a red disc where a hostile was last seen (the server's frozen record; it never moves);
    /// - a tracer for every relayed shot;
    /// - floating-text events for the HUD ("-N" on a health drop, "MISS" on a shot cover turned away).
    /// Reads only replicated state and the messages; decides nothing.
    /// </summary>
    public sealed class NetPlayAvatars : MonoBehaviour
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] NetClient client;
        [SerializeField] GameObject visualPrefab;
        [SerializeField] WeaponVisualAsset weapon;
        [SerializeField] GameObject tracerPrefab;
        [SerializeField] Material ringMaterial;
        [SerializeField] Material markerMaterial;

        readonly Dictionary<NetUnitState, NetAvatarView> views = new Dictionary<NetUnitState, NetAvatarView>();
        readonly Dictionary<int, NetAvatarView> byId = new Dictionary<int, NetAvatarView>();
        readonly Dictionary<int, int> lastHealth = new Dictionary<int, int>();
        readonly Dictionary<int, GameObject> markers = new Dictionary<int, GameObject>();
        readonly List<int> stale = new List<int>();
        Func<int, bool> isSelected = _ => false;
        TracerStyle tracerStyle;
        NetClient subscribed;

        /// <summary>A unit id and a short text to float above it (the HUD draws it).</summary>
        public event Action<int, string> Floater;

        internal void Initialize(NetClient netClient, Func<int, bool> selected, GameObject visual, WeaponVisualAsset weaponAsset,
            GameObject tracer, Material ring, Material marker)
        {
            client = netClient;
            isSelected = selected ?? (_ => false);
            visualPrefab = visual;
            weapon = weaponAsset;
            tracerPrefab = tracer;
            ringMaterial = ring;
            markerMaterial = marker;
            Subscribe();
        }

        internal void SetSelection(Func<int, bool> selected) => isSelected = selected ?? (_ => false);

        void OnEnable() => Subscribe();

        void OnDisable()
        {
            if (subscribed != null)
                subscribed.ShotReceived -= OnShot;
            subscribed = null;
        }

        void Subscribe()
        {
            if (client == null || subscribed == client || !isActiveAndEnabled)
                return;
            client.ShotReceived += OnShot;
            subscribed = client;
        }

        public bool IsOwned(NetUnitState state) =>
            client != null && !state.Hostile && state.UnitId >= client.FirstOwnedId && state.UnitId < client.FirstOwnedId + client.OwnedCount;

        public NetAvatarView ViewOf(int unitId) => byId.TryGetValue(unitId, out var view) && view != null && view.isActiveAndEnabled ? view : null;

        void LateUpdate()
        {
            if (client == null)
                return;
            foreach (var state in NetClient.VisibleStates())
            {
                if (!views.TryGetValue(state, out var view) || view == null)
                    views[state] = view = NetAvatarView.Create(state, visualPrefab, weapon, ringMaterial);
                view.gameObject.SetActive(true);
                byId[state.UnitId] = view;
                view.Tick(state, IsOwned(state), isSelected(state.UnitId), Time.deltaTime);
                if (lastHealth.TryGetValue(state.UnitId, out var before) && state.Health < before)
                {
                    view.Hit();
                    Floater?.Invoke(state.UnitId, "-" + (before - state.Health));
                }
                lastHealth[state.UnitId] = state.Health;
            }
            // Host mode keeps the server's avatars in the scene; one this client is no longer sent must not stay drawn.
            foreach (var pair in views)
                if (pair.Key != null && pair.Value != null && !pair.Key.IsClientInitialized)
                    pair.Value.gameObject.SetActive(false);
            DrawLastKnown();
        }

        void OnShot(NetShot shot)
        {
            var attacker = ViewOf(shot.AttackerId);
            var target = ViewOf(shot.TargetId);
            if (attacker == null || target == null)
                return;
            attacker.Fire();
            if (!shot.Hit)
                Floater?.Invoke(shot.TargetId, "MISS");
            if (tracerPrefab == null)
                return;
            var tracer = Instantiate(tracerPrefab);
            var line = tracer.GetComponent<TracerView>();
            if (line == null)
            {
                Destroy(tracer);
                return;
            }
            if (tracerStyle == null)
                tracerStyle = TracerStyle.Create(tracerPrefab);
            Destroy(tracer, line.Launch(attacker.MuzzlePoint, target.ChestPoint, tracerStyle) + 0.1f);
        }

        void DrawLastKnown()
        {
            foreach (var pair in client.Record.LastKnown)
            {
                if (!markers.TryGetValue(pair.Key, out var marker) || marker == null)
                    markers[pair.Key] = marker = CreateMarker(pair.Key);
                marker.transform.position = pair.Value + new Vector3(0f, 0.05f, 0f);
            }
            stale.Clear();
            foreach (var pair in markers)
                if (!client.Record.LastKnown.ContainsKey(pair.Key))
                    stale.Add(pair.Key);
            foreach (var id in stale)
            {
                if (markers[id] != null)
                    Destroy(markers[id]);
                markers.Remove(id);
            }
        }

        GameObject CreateMarker(int unitId)
        {
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "LastKnown_" + unitId;
            DestroyImmediate(disc.GetComponent<Collider>());
            disc.transform.localScale = new Vector3(1.2f, 0.02f, 1.2f);
            var renderer = disc.GetComponent<Renderer>();
            if (markerMaterial != null)
                renderer.sharedMaterial = markerMaterial;
            var block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, new Color(0.9f, 0.25f, 0.25f, 0.5f));
            renderer.SetPropertyBlock(block);
            return disc;
        }

        /// <summary>Where the HUD draws a last-known "?" label: the frozen records, as given.</summary>
        public IReadOnlyDictionary<int, Vector3> LastKnown => client != null ? client.Record.LastKnown : new Dictionary<int, Vector3>();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass.**
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetPlayAvatarsPlayTests"`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit.**

```bash
git add Assets/_Project/Scripts/Net/Play/NetAvatarView.cs* Assets/_Project/Scripts/Net/Play/NetPlayAvatars.cs* \
  Assets/_Project/Tests/PlayMode/NetPlayAvatarsPlayTests.cs*
git commit -m "Play client avatars: character visual with rifle, side and selection rings, replicated-state animation, tracers, last-known discs" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Selection, picking, controls and camera

**Files:**
- Create: `Assets/_Project/Scripts/Net/Play/NetSelection.cs`
- Create: `Assets/_Project/Scripts/Net/Play/NetPick.cs`
- Create: `Assets/_Project/Scripts/Net/Play/NetPlayControls.cs`
- Test: `Assets/_Project/Tests/EditMode/NetSelectionTests.cs`, `Assets/_Project/Tests/EditMode/NetPickTests.cs`, `Assets/_Project/Tests/PlayMode/NetPlayControlsPlayTests.cs`

**Interfaces:**
- Consumes: `NetMapView.Layout`, `NetMapView.Obstacles`, `NetMapView.Built` (Task 5); `NetClient`; `NetIntentSender`; `TacticalCameraController.SetBounds/FocusOn/Yaw`.
- Produces:
  - `NetSelection`: `Selected`, `Count`, `Primary`, `Contains`, `Only`, `Toggle`, `All`, `Next`, `Retain`;
  - `NetPick.Ground(Ray, MissionLayout, IReadOnlyList<Bounds>, out Vector3) : NetPick.GroundHit` (`None`, `Floor`, `Blocked`);
  - `NetPick.Unit(Ray, IReadOnlyList<(int Id, Vector3 Position)>) : int`;
  - `NetPlayControls.Initialize(NetClient, Camera, TacticalCameraController, NetMapView, InputActionReference command, InputActionReference pointer, InputActionReference queue, InputActionReference stop, InputActionReference cancel, InputActionReference pan)`;
  - `NetPlayControls.CommandAtScreen(Vector2 screen, bool append, bool toggle)`;
  - `NetPlayControls.MoveSelected(Vector3 point, bool append)`, `AttackWithSelected(int hostileId)`, `StopSelected()`, `SelectAllLiving()`;
  - `Selection`, `Following`, `LastFeedback`, `IsSelected(int)`, `internal Func<NetRequest, uint> Send`, `internal Func<Vector2, bool> Blocks` (Task 8 sets it to the HUD's panel test).

- [ ] **Step 1: Write the failing pure tests.** First `Assets/_Project/Tests/EditMode/NetSelectionTests.cs`:

```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetSelectionTests
    {
        [Test]
        public void Only_ReplacesTheSelection_AndToggleAddsOrRemoves()
        {
            var s = new NetSelection();
            s.Only(2);
            s.Toggle(1);
            Assert.That(s.Selected, Is.EqualTo(new[] { 2, 1 }));
            Assert.That(s.Primary, Is.EqualTo(2));
            s.Toggle(2);
            Assert.That(s.Selected, Is.EqualTo(new[] { 1 }));
            s.Only(1);
            Assert.That(s.Selected, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void Next_CyclesOneOperativeInIdOrder()
        {
            var s = new NetSelection();
            s.Next(new[] { 1, 2 });
            Assert.That(s.Selected, Is.EqualTo(new[] { 1 }));
            s.Next(new[] { 1, 2 });
            Assert.That(s.Selected, Is.EqualTo(new[] { 2 }));
            s.Next(new[] { 1, 2 });
            Assert.That(s.Selected, Is.EqualTo(new[] { 1 }));
            s.Next(new int[0]);
            Assert.That(s.Count, Is.Zero);
        }

        [Test]
        public void All_SelectsEveryGivenId()
        {
            var s = new NetSelection();
            s.All(new[] { 2, 1 });
            Assert.That(s.Selected, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void Retain_DropsTheDead()
        {
            var s = new NetSelection();
            s.All(new[] { 1, 2 });
            s.Retain(id => id == 2);
            Assert.That(s.Selected, Is.EqualTo(new[] { 2 }));
            Assert.That(s.Primary, Is.EqualTo(2));
            s.Retain(id => false);
            Assert.That(s.Primary, Is.Zero);
        }
    }
}
```

Then `Assets/_Project/Tests/EditMode/NetPickTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Picking against the drawn map with no physics: open floor, an obstacle in the way, off the map, and units.</summary>
    public class NetPickTests
    {
        static MissionLayout Layout(out List<Bounds> obstacles)
        {
            var map = new NetMap(10, 10, new List<RectInt> { new RectInt(1, 1, 8, 8) },
                new List<NetMapBox> { new NetMapBox(MissionBoxKind.Pillar, new RectInt(4, 4, 2, 2), 3f) }, new List<Vector2Int>());
            var layout = NetMapGeometry.ToLayout(map);
            obstacles = new List<Bounds> { NetMapGeometry.BoxBounds(layout, layout.Boxes[0]) };
            return layout;
        }

        static Ray Down(Vector3 at) => new Ray(at + Vector3.up * 20f, Vector3.down);

        [Test]
        public void ARayOntoOpenFloor_GivesThatGroundPoint()
        {
            var layout = Layout(out var obstacles);
            var target = layout.TileCenter(new Vector2Int(2, 2));
            Assert.That(NetPick.Ground(Down(target), layout, obstacles, out var point), Is.EqualTo(NetPick.GroundHit.Floor));
            Assert.That(Vector3.Distance(point, target), Is.LessThan(1e-4f));
        }

        [Test]
        public void ARayThatMeetsAnObstacleFirst_IsBlocked()
        {
            var layout = Layout(out var obstacles);
            Assert.That(NetPick.Ground(Down(layout.TileCenter(new Vector2Int(4, 4))), layout, obstacles, out _), Is.EqualTo(NetPick.GroundHit.Blocked));
        }

        [Test]
        public void AnObliqueRayPastAWall_StopsAtTheWallNotTheFloorBehindIt()
        {
            var layout = Layout(out var obstacles);
            // Aimed at a point inside the pillar from the west and above: the ray meets the pillar's west face before it could
            // reach the open floor beyond.
            var inside = layout.ToWorld(4.5f, 5f) + Vector3.up * 1.5f;
            var origin = inside + new Vector3(-10f, 5f, 0f);
            Assert.That(NetPick.Ground(new Ray(origin, (inside - origin).normalized), layout, obstacles, out _), Is.EqualTo(NetPick.GroundHit.Blocked));
        }

        [Test]
        public void ARayOffTheFloor_IsNothing()
        {
            var layout = Layout(out var obstacles);
            Assert.That(NetPick.Ground(Down(layout.TileCenter(new Vector2Int(0, 0))), layout, obstacles, out _), Is.EqualTo(NetPick.GroundHit.None));
            Assert.That(NetPick.Ground(new Ray(Vector3.up, Vector3.up), layout, obstacles, out _), Is.EqualTo(NetPick.GroundHit.None));
        }

        [Test]
        public void Unit_PicksTheNearestUnitAlongTheRay_OrNone()
        {
            var units = new List<(int, Vector3)> { (1, new Vector3(0f, 0f, 0f)), (2, new Vector3(0f, 0f, 5f)) };
            var ray = new Ray(new Vector3(0f, 1f, -10f), Vector3.forward);
            Assert.That(NetPick.Unit(ray, units), Is.EqualTo(1));
            Assert.That(NetPick.Unit(new Ray(new Vector3(3f, 1f, -10f), Vector3.forward), units), Is.Zero);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetSelectionTests|Blackglass.Tests.NetPickTests"`
Expected: compilation fails, because `NetSelection` and `NetPick` are not defined.

- [ ] **Step 3: Write `Assets/_Project/Scripts/Net/Play/NetSelection.cs`.**

```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>Which own operatives the player has selected, by net id. Pure; the first selected is the primary (follow, steer).</summary>
    public sealed class NetSelection
    {
        readonly List<int> selected = new List<int>();

        public IReadOnlyList<int> Selected => selected;
        public int Count => selected.Count;
        public int Primary => selected.Count > 0 ? selected[0] : 0;

        public bool Contains(int id) => selected.Contains(id);

        public void Only(int id)
        {
            selected.Clear();
            selected.Add(id);
        }

        public void Toggle(int id)
        {
            if (!selected.Remove(id))
                selected.Add(id);
        }

        public void All(IEnumerable<int> ids)
        {
            selected.Clear();
            selected.AddRange(ids);
            selected.Sort();
        }

        /// <summary>One operative: the next living id after the current primary, wrapping; nothing when none lives.</summary>
        public void Next(IReadOnlyList<int> living)
        {
            if (living.Count == 0)
            {
                selected.Clear();
                return;
            }
            var ordered = new List<int>(living);
            ordered.Sort();
            var at = ordered.IndexOf(Primary);
            Only(ordered[(at + 1) % ordered.Count]);
        }

        /// <summary>Drops every id the predicate rejects (dead, unknown or no longer owned).</summary>
        public void Retain(Func<int, bool> keep) => selected.RemoveAll(id => !keep(id));
    }
}
```

- [ ] **Step 4: Write `Assets/_Project/Scripts/Net/Play/NetPick.cs`.**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What a click ray meets in the drawn world, by plain geometry (the play client has no colliders): the nearest obstacle box,
    /// or the floor plane on a floor tile, or a unit's upright box. Pure.
    /// </summary>
    public static class NetPick
    {
        public enum GroundHit
        {
            None,
            Floor,
            Blocked,
        }

        const float UnitRadius = 0.5f;
        const float UnitHeight = 1.9f;

        public static GroundHit Ground(Ray ray, MissionLayout layout, IReadOnlyList<Bounds> obstacles, out Vector3 point)
        {
            point = default;
            var nearestObstacle = float.PositiveInfinity;
            foreach (var bounds in obstacles)
                if (bounds.IntersectRay(ray, out var distance) && distance >= 0f && distance < nearestObstacle)
                    nearestObstacle = distance;
            if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var floorDistance) && floorDistance <= nearestObstacle)
            {
                var hit = ray.GetPoint(floorDistance);
                var tileX = Mathf.FloorToInt(hit.x + layout.Width * 0.5f);
                var tileY = Mathf.FloorToInt(hit.z + layout.Height * 0.5f);
                if (!layout.IsFloor(tileX, tileY))
                    return float.IsPositiveInfinity(nearestObstacle) ? GroundHit.None : GroundHit.Blocked;
                point = new Vector3(hit.x, 0f, hit.z);
                return GroundHit.Floor;
            }
            return float.IsPositiveInfinity(nearestObstacle) ? GroundHit.None : GroundHit.Blocked;
        }

        /// <summary>The id of the nearest unit whose upright box the ray enters, or 0.</summary>
        public static int Unit(Ray ray, IReadOnlyList<(int Id, Vector3 Position)> units)
        {
            var best = 0;
            var bestDistance = float.PositiveInfinity;
            foreach (var (id, position) in units)
            {
                var box = new Bounds(position + Vector3.up * (UnitHeight * 0.5f), new Vector3(UnitRadius * 2f, UnitHeight, UnitRadius * 2f));
                if (box.IntersectRay(ray, out var distance) && distance < bestDistance)
                {
                    best = id;
                    bestDistance = distance;
                }
            }
            return best;
        }
    }
}
```

- [ ] **Step 5: Run the pure tests to verify they pass.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetSelectionTests|Blackglass.Tests.NetPickTests"`
Expected: PASS.

- [ ] **Step 6: Write the failing PlayMode tests** in `Assets/_Project/Tests/PlayMode/NetPlayControlsPlayTests.cs`.

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Clicks become exactly the existing requests, relative to what is drawn: open floor moves every selected operative to the
    /// point under the cursor, at any camera yaw or zoom; an obstacle or a HUD panel sends nothing; nothing is sent unless the
    /// client is ready, after the mission ended, or for a dead operative.
    /// </summary>
    public class NetPlayControlsPlayTests
    {
        NetLoopbackRig rig;
        NetPlayControls controls;
        NetMapView view;
        TacticalCameraController cameraRig;
        Camera viewCamera;
        readonly List<NetRequest> sent = new List<NetRequest>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            view = NetPlayKit.AddMapView(rig.Client.gameObject, rig.Client, false);
            var rigObject = new GameObject("CameraRig");
            viewCamera = new GameObject("Camera").AddComponent<Camera>();
            viewCamera.transform.SetParent(rigObject.transform, false);
            cameraRig = rigObject.AddComponent<TacticalCameraController>();
            cameraRig.Initialize(viewCamera, null, null, null, null, null);
            controls = rig.Client.gameObject.AddComponent<NetPlayControls>();
            controls.Initialize(rig.Client, viewCamera, cameraRig, view, null, null, null, null, null, null);
            controls.Send = r => { sent.Add(r); return (uint)sent.Count; };
            yield return TestWorld.WaitUntil(() => view.Built && NetClient.VisibleStates().Count == rig.Client.OwnedCount, 10f);
            yield return null;
            sent.Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(cameraRig.gameObject);
            yield return rig.Stop();
        }

        // A floor tile 3..10 m from the squad with every obstacle at least 3 m away, so no wall can stand between a 55-degree
        // camera ray and it, and no unit stands on it.
        Vector3 OpenFloorPoint()
        {
            var layout = view.Layout;
            var own = NetClient.VisibleStates().Select(s => s.transform.position).ToList();
            for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                {
                    var c = layout.TileCenter(new Vector2Int(x, y));
                    if (layout.IsFloor(x, y) && view.Obstacles.All(b => b.SqrDistance(c) > 9f)
                        && own.All(p => Vector3.Distance(c, p) > 3f) && own.Any(p => Vector3.Distance(c, p) < 10f))
                        return c;
                }
            Assert.Fail("no open floor tile near the squad");
            return default;
        }

        void Zoom(float distance)
        {
            var serialized = new UnityEditor.SerializedObject(cameraRig);
            serialized.FindProperty("distance").floatValue = distance;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [UnityTest]
        public IEnumerator A_click_on_open_floor_moves_every_selected_operative_there([Values(0f, 135f)] float yaw, [Values(8f, 30f)] float distance)
        {
            var target = OpenFloorPoint();
            cameraRig.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            Zoom(distance);
            cameraRig.FocusOn(target);   // places the camera at the new yaw and distance at once
            yield return null;
            Assert.That(NetPick.Ground(viewCamera.ScreenPointToRay(viewCamera.WorldToScreenPoint(target)), view.Layout, view.Obstacles, out _),
                Is.EqualTo(NetPick.GroundHit.Floor), "precondition: the camera sees the target tile");
            controls.SelectAllLiving();
            controls.CommandAtScreen(viewCamera.WorldToScreenPoint(target), false, false);
            var moves = sent.OfType<NetMoveTo>().ToList();
            Assert.That(moves.Select(m => m.UnitId), Is.EquivalentTo(Enumerable.Range(rig.Client.FirstOwnedId, rig.Client.OwnedCount)));
            foreach (var move in moves)
            {
                Assert.That(Vector3.Distance(move.Destination, target), Is.LessThan(0.05f));
                Assert.That(move.Append, Is.False);
            }
            Assert.That(sent.All(r => r is NetMoveTo || r is NetMoveIntent), Is.True, "only the existing requests");
        }

        [UnityTest]
        public IEnumerator A_click_on_an_obstacle_sends_nothing_and_says_so()
        {
            var box = rig.Director.Current.Layout.Boxes.First(b => b.Kind == MissionBoxKind.Wall);
            var top = NetMapGeometry.BoxBounds(view.Layout, view.Layout.Boxes.First(b => b.Footprint == box.Footprint));
            cameraRig.FocusOn(top.center);
            yield return null;
            controls.SelectAllLiving();
            controls.CommandAtScreen(viewCamera.WorldToScreenPoint(top.center + Vector3.up * (top.extents.y - 0.01f)), false, false);
            Assert.That(sent, Is.Empty);
            StringAssert.Contains("Blocked", controls.LastFeedback);
        }

        [UnityTest]
        public IEnumerator A_click_on_an_own_operative_selects_it_and_sends_nothing()
        {
            var own = NetClient.VisibleStates().OrderBy(s => s.UnitId).Last();
            cameraRig.FocusOn(own.transform.position);
            yield return null;
            controls.CommandAtScreen(viewCamera.WorldToScreenPoint(own.transform.position + Vector3.up * 1f), false, false);
            Assert.That(controls.Selection.Selected, Is.EqualTo(new[] { own.UnitId }));
            Assert.That(sent, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Nothing_is_sent_unless_the_client_is_ready()
        {
            controls.SelectAllLiving();
            rig.Client.Disconnect();
            yield return TestWorld.WaitUntil(() => rig.Client.State == NetClientState.Disconnected, 5f);
            controls.MoveSelected(OpenFloorPoint(), false);
            controls.StopSelected();
            Assert.That(sent, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Nothing_is_sent_after_the_mission_ended()
        {
            controls.SelectAllLiving();
            rig.Client.Record.ReceiveEnd(0d, (byte)NetMissionResult.Success);
            controls.MoveSelected(OpenFloorPoint(), false);
            controls.StopSelected();
            yield return null;
            Assert.That(sent, Is.Empty);
        }

        [UnityTest]
        public IEnumerator A_dead_operative_is_dropped_and_never_ordered()
        {
            controls.SelectAllLiving();
            var dead = rig.Director.Friendlies[0];
            var deadId = rig.Server.Units.IdOf(dead);
            dead.GetComponent<Health>().TakeDamage(100000);
            yield return TestWorld.WaitUntil(() => NetClient.VisibleStates().Any(s => s.UnitId == deadId && !s.Alive), 5f);
            yield return null;
            Assert.That(controls.Selection.Contains(deadId), Is.False);
            controls.MoveSelected(OpenFloorPoint(), false);
            Assert.That(sent.OfType<NetMoveTo>().Any(m => m.UnitId == deadId), Is.False);
        }
    }
}
#endif
```

`TacticalCameraController.Initialize` takes null action references; its Update reads them through `InputActionUtility`,
whose `Read` and `IsPressed` return no input for a null reference, and `SetEnabled` skips it. `FocusOn` applies the camera pose at once, so the yaw and zoom set just before it take effect in that frame.
The camera's own bounds (±25 m by default) contain the fixture map.

- [ ] **Step 7: Run them to verify they fail.**
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetPlayControlsPlayTests"`
Expected: compilation fails, because `NetPlayControls` is not defined.

- [ ] **Step 8: Write `Assets/_Project/Scripts/Net/Play/NetPlayControls.cs`.**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// The play client's controls (Input System; decision 052, D7). A left click on:
    /// - an own operative selects it (Ctrl adds or removes);
    /// - a living hostile this client is sent attacks it with every selected operative;
    /// - open floor moves them there (Shift queues);
    /// - an obstacle does nothing and says so.
    /// X stops; Tab cycles one operative; Ctrl+A selects all. F follows the primary operative, and while following, WASD steers it
    /// (camera-relative NetMoveIntent); Esc ends follow. Input only requests: every order is one of the four existing
    /// NetRequests. Nothing leaves while the client is not Ready, after the mission ended, or for a dead or unowned unit.
    /// </summary>
    public sealed class NetPlayControls : MonoBehaviour
    {
        [SerializeField] NetClient client;
        [SerializeField] Camera viewCamera;
        [SerializeField] TacticalCameraController cameraRig;
        [SerializeField] NetMapView mapView;
        [SerializeField] InputActionReference commandAction;
        [SerializeField] InputActionReference pointerAction;
        [SerializeField] InputActionReference queueAction;
        [SerializeField] InputActionReference stopAction;
        [SerializeField] InputActionReference cancelAction;
        [SerializeField] InputActionReference panAction;

        readonly NetSelection selection = new NetSelection();
        readonly NetIntentSender sender = new NetIntentSender();
        readonly List<(int Id, Vector3 Position)> pickable = new List<(int, Vector3)>();
        InputAction cycle;
        InputAction selectAll;
        InputAction follow;
        bool framed;

        /// <summary>Where requests go (tests replace it). Never called unless the client is Ready.</summary>
        internal Func<NetRequest, uint> Send;
        /// <summary>True for a screen point a HUD panel covers; such a click commands nothing (the HUD sets it).</summary>
        internal Func<Vector2, bool> Blocks = _ => false;

        public NetSelection Selection => selection;
        public bool Following { get; private set; }
        /// <summary>The last click's explanation when it did nothing ("Blocked — pick open floor"), for the HUD.</summary>
        public string LastFeedback { get; private set; }

        internal void Initialize(NetClient netClient, Camera camera, TacticalCameraController rig, NetMapView map,
            InputActionReference command, InputActionReference pointer, InputActionReference queue,
            InputActionReference stop, InputActionReference cancel, InputActionReference pan)
        {
            client = netClient;
            viewCamera = camera;
            cameraRig = rig;
            mapView = map;
            commandAction = command;
            pointerAction = pointer;
            queueAction = queue;
            stopAction = stop;
            cancelAction = cancel;
            panAction = pan;
        }

        public bool IsSelected(int unitId) => selection.Contains(unitId);

        void OnEnable()
        {
            cycle = new InputAction("CycleOperative", InputActionType.Button, "<Keyboard>/tab");
            selectAll = new InputAction("SelectAll", InputActionType.Button);
            selectAll.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", "<Keyboard>/ctrl").With("Button", "<Keyboard>/a");
            follow = new InputAction("Follow", InputActionType.Button, "<Keyboard>/f");
            cycle.Enable();
            selectAll.Enable();
            follow.Enable();
            InputActionUtility.SetEnabled(true, commandAction, pointerAction, queueAction, stopAction, cancelAction, panAction);
            if (commandAction != null)
                commandAction.action.performed += OnCommand;
        }

        void OnDisable()
        {
            if (commandAction != null)
                commandAction.action.performed -= OnCommand;
            foreach (var action in new[] { cycle, selectAll, follow })
            {
                if (action == null)
                    continue;
                action.Disable();
                action.Dispose();
            }
        }

        bool CanOrder => client != null && client.State == NetClientState.Ready && client.Record.Result == NetMissionResult.None;

        uint Request(NetRequest request) => Send != null ? Send(request) : client.Send(request);

        void Update()
        {
            if (client == null)
                return;
            selection.Retain(IsLivingOwn);
            if (!CanOrder)
            {
                Following = false;
                return;
            }
            FrameOnce();
            if (cycle.WasPressedThisFrame())
                selection.Next(LivingOwnIds());
            if (selectAll.WasPressedThisFrame())
                SelectAllLiving();
            if (follow.WasPressedThisFrame())
                SetFollowing(!Following && selection.Primary != 0);
            if (cancelAction != null && cancelAction.action.WasPressedThisFrame())
                SetFollowing(false);
            if (stopAction != null && stopAction.action.WasPressedThisFrame())
                StopSelected();
            if (Following && selection.Primary == 0)
                SetFollowing(false);
            if (Following)
            {
                var axes = panAction != null ? panAction.action.ReadValue<Vector2>() : Vector2.zero;
                var yaw = cameraRig != null ? cameraRig.Yaw : 0f;
                var direction = Quaternion.Euler(0f, yaw, 0f) * new Vector3(axes.x, 0f, axes.y);
                if (sender.Next(direction, Time.realtimeSinceStartupAsDouble, out var send))
                    Request(new NetMoveIntent(selection.Primary, send));
            }
        }

        void LateUpdate()
        {
            if (!Following || cameraRig == null)
                return;
            var primary = StateOf(selection.Primary);
            if (primary != null)
                cameraRig.FocusOn(primary.transform.position);
        }

        void SetFollowing(bool value)
        {
            if (Following && !value && CanOrder && sender.ReleaseBeforeOrder(out var release))
                Request(new NetMoveIntent(selection.Primary, release));
            Following = value;
        }

        void OnCommand(InputAction.CallbackContext context)
        {
            if (pointerAction == null)
                return;
            var keyboard = Keyboard.current;
            CommandAtScreen(pointerAction.action.ReadValue<Vector2>(),
                queueAction != null && queueAction.action.IsPressed(),
                keyboard != null && keyboard.ctrlKey.isPressed);
        }

        /// <summary>One left click at a screen point (the input callback and tests call this).</summary>
        internal void CommandAtScreen(Vector2 screen, bool append, bool toggle)
        {
            LastFeedback = null;
            if (!CanOrder || viewCamera == null || Blocks(screen))
                return;
            var ray = viewCamera.ScreenPointToRay(screen);
            pickable.Clear();
            foreach (var state in NetClient.VisibleStates())
                if (state.Alive && (state.Hostile || IsOwn(state.UnitId)))
                    pickable.Add((state.UnitId, state.transform.position));
            var unitId = NetPick.Unit(ray, pickable);
            if (unitId != 0)
            {
                if (IsOwn(unitId))
                {
                    if (toggle)
                        selection.Toggle(unitId);
                    else
                        selection.Only(unitId);
                    return;
                }
                AttackWithSelected(unitId);
                return;
            }
            if (selection.Count == 0)
            {
                LastFeedback = "Select an operative first (click one, Tab or Ctrl+A)";
                return;
            }
            if (mapView == null || !mapView.Built)
            {
                // No map authorized: the ground plane, as the net-test client does.
                if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var distance))
                    MoveSelected(ray.GetPoint(distance), append);
                return;
            }
            switch (NetPick.Ground(ray, mapView.Layout, mapView.Obstacles, out var point))
            {
                case NetPick.GroundHit.Floor:
                    MoveSelected(point, append);
                    break;
                case NetPick.GroundHit.Blocked:
                    LastFeedback = "Blocked — pick open floor";
                    break;
                default:
                    LastFeedback = "Off the map";
                    break;
            }
        }

        internal void MoveSelected(Vector3 point, bool append)
        {
            if (!CanOrder)
                return;
            if (sender.ReleaseBeforeOrder(out var release) && selection.Primary != 0)
                Request(new NetMoveIntent(selection.Primary, release));
            foreach (var id in selection.Selected)
                Request(new NetMoveTo(id, point, append));
        }

        internal void AttackWithSelected(int hostileId)
        {
            if (!CanOrder)
                return;
            if (selection.Count == 0)
            {
                LastFeedback = "Select an operative first (click one, Tab or Ctrl+A)";
                return;
            }
            foreach (var id in selection.Selected)
                Request(new NetAttack(id, hostileId));
        }

        internal void StopSelected()
        {
            if (!CanOrder)
                return;
            foreach (var id in selection.Selected)
                Request(new NetStop(id));
        }

        internal void SelectAllLiving() => selection.All(LivingOwnIds());

        // Once the map and the squad are both here: bound the camera to the map and put it over the squad, primary selected.
        void FrameOnce()
        {
            if (framed || cameraRig == null || mapView == null || !mapView.Built)
                return;
            var own = LivingOwnIds();
            if (own.Count == 0)
                return;
            var bounds = mapView.Layout.WorldBounds;
            cameraRig.SetBounds(new Rect(bounds.min.x, bounds.min.z, bounds.size.x, bounds.size.z));
            var centre = Vector3.zero;
            foreach (var id in own)
                centre += StateOf(id).transform.position;
            cameraRig.FocusOn(centre / own.Count);
            if (selection.Count == 0)
                selection.Only(own[0]);
            framed = true;
        }

        bool IsOwn(int unitId) => client != null && unitId >= client.FirstOwnedId && unitId < client.FirstOwnedId + client.OwnedCount;

        bool IsLivingOwn(int unitId)
        {
            if (!IsOwn(unitId))
                return false;
            var state = StateOf(unitId);
            return state != null && state.Alive;
        }

        List<int> LivingOwnIds()
        {
            var ids = new List<int>();
            foreach (var state in NetClient.VisibleStates())
                if (!state.Hostile && state.Alive && IsOwn(state.UnitId))
                    ids.Add(state.UnitId);
            ids.Sort();
            return ids;
        }

        static NetUnitState StateOf(int unitId)
        {
            foreach (var state in NetClient.VisibleStates())
                if (state.UnitId == unitId)
                    return state;
            return null;
        }
    }
}
```

`InputActionUtility.SetEnabled` is the existing helper `TacticalCameraController` uses. It skips null references.

- [ ] **Step 9: Run the tests to verify they pass.**
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetPlayControlsPlayTests"`
Expected: PASS, 9 tests (4 click cases plus 5).

- [ ] **Step 10: Commit.**

```bash
git add Assets/_Project/Scripts/Net/Play/NetSelection.cs* Assets/_Project/Scripts/Net/Play/NetPick.cs* \
  Assets/_Project/Scripts/Net/Play/NetPlayControls.cs* Assets/_Project/Tests/EditMode/NetSelectionTests.cs* \
  Assets/_Project/Tests/EditMode/NetPickTests.cs* Assets/_Project/Tests/PlayMode/NetPlayControlsPlayTests.cs*
git commit -m "Play client controls: offline-style select, move, attack, stop, follow-and-steer; picking against the drawn map" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: HUD, bootstrap, scene, build and run scripts

**Files:**
- Create: `Assets/_Project/Scripts/Net/Play/NetPlayText.cs`
- Create: `Assets/_Project/Scripts/Net/Play/NetPlayHud.cs`
- Create: `Assets/_Project/Scripts/Net/Play/NetPlayBootstrap.cs`
- Create: `Assets/_Project/Editor/Net/NetPlayClientSceneBuilder.cs`
- Create: `Assets/_Project/Editor/Net/NetPlayClientBuild.cs`
- Create: `Tools/build-netplay-client.sh`, `Tools/run-local-netplay.sh`
- Create (generated): `Assets/_Project/Scenes/NetPlayClient.unity`
- Test: `Assets/_Project/Tests/EditMode/NetPlayTextTests.cs`, `Assets/_Project/Tests/PlayMode/NetPlayClientScenePlayTests.cs`; add one case to `NetPlayControlsPlayTests`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `NetPlayText.ObjectiveLine(NetObjective) : string`, `NetPlayText.ResultBanner(NetMissionResult) : string`, `NetPlayText.MapStatus(NetClientRecord) : string`;
  - `NetPlayText.ObjectivesPanel` and `NetPlayText.SquadPanel(int squadCount, float screenHeight)` (IMGUI `Rect`s), `NetPlayText.Covers(Vector2 guiPoint, int squadCount, float screenHeight) : bool`;
  - `NetPlayHud.Initialize(NetClient, NetPlayControls, NetPlayAvatars, NetMapView, Camera)`;
  - `NetPlayBootstrap.Initialize(NetClient)`;
  - `NetPlayClientSceneBuilder.ScenePath = "Assets/_Project/Scenes/NetPlayClient.unity"`;
  - `NetPlayClientBuild.OutputPath = "Builds/NetPlayClient/NetPlayClient.exe"`.

- [ ] **Step 1: Write the failing text tests** in `Assets/_Project/Tests/EditMode/NetPlayTextTests.cs`.

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetPlayTextTests
    {
        [Test]
        public void ObjectiveLines_SayTheStateInWords()
        {
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "Eliminate security team", false, Vector3.zero, 0f)),
                Is.EqualTo("[ ] Eliminate security team"));
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Completed, "Eliminate security team", false, Vector3.zero, 0f)),
                Is.EqualTo("[x] Eliminate security team"));
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Inactive, "Extraction", true, Vector3.zero, 2f)),
                Is.EqualTo("[ ] Extraction (locked)"));
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Active, "Extraction", true, Vector3.zero, 2f)),
                Is.EqualTo("[ ] Extraction (open: reach the green zone)"));
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Failed, "X", false, Vector3.zero, 0f)),
                Is.EqualTo("[!] X (failed)"));
        }

        [Test]
        public void TheBanner_NamesTheResult()
        {
            Assert.That(NetPlayText.ResultBanner(NetMissionResult.Success), Is.EqualTo("MISSION SUCCESS"));
            Assert.That(NetPlayText.ResultBanner(NetMissionResult.Failure), Is.EqualTo("MISSION FAILED"));
            Assert.That(NetPlayText.ResultBanner(NetMissionResult.None), Is.Null);
        }

        [Test]
        public void MapStatus_ExplainsAMissingOrRefusedMap()
        {
            var record = new NetClientRecord();
            Assert.That(NetPlayText.MapStatus(record), Is.EqualTo("No map authorized for this mission"));
            record.ReceiveMap(0d, null, "map too large");
            Assert.That(NetPlayText.MapStatus(record), Is.EqualTo("Map refused: map too large"));
        }

        [Test]
        public void HudPanels_CoverTheirScreenRects()
        {
            Assert.That(NetPlayText.Covers(NetPlayText.ObjectivesPanel.center, 2, 800f), Is.True);
            var squad = NetPlayText.SquadPanel(2, 800f);
            Assert.That(NetPlayText.Covers(squad.center, 2, 800f), Is.True);
            Assert.That(NetPlayText.Covers(new Vector2(640f, 400f), 2, 800f), Is.False, "the middle of the screen is the world");
        }
    }
}
```

Add this case to `NetPlayControlsPlayTests` (Review Focus 1):

```csharp
        [UnityTest]
        public IEnumerator A_click_on_a_hud_panel_sends_nothing()
        {
            var target = OpenFloorPoint();
            cameraRig.FocusOn(target);
            yield return null;
            controls.SelectAllLiving();
            var screen = (Vector2)viewCamera.WorldToScreenPoint(target);
            controls.Blocks = p => p == screen;
            controls.CommandAtScreen(screen, false, false);
            Assert.That(sent, Is.Empty);
        }
```

- [ ] **Step 2: Run them to verify they fail.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetPlayTextTests"`
Expected: compilation fails, because `NetPlayText` is not defined.

- [ ] **Step 3: Write `Assets/_Project/Scripts/Net/Play/NetPlayText.cs`.**

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>The play HUD's words and panel rectangles (IMGUI screen space: origin top left). Pure.</summary>
    public static class NetPlayText
    {
        public const float SquadRowHeight = 26f;
        public static readonly Rect ObjectivesPanel = new Rect(10f, 40f, 420f, 130f);

        public static Rect SquadPanel(int squadCount, float screenHeight)
        {
            var height = 30f + SquadRowHeight * Mathf.Max(1, squadCount);
            return new Rect(10f, screenHeight - height - 40f, 420f, height);
        }

        /// <summary>True when a click at this IMGUI point lands on a HUD panel and must not command the world.</summary>
        public static bool Covers(Vector2 guiPoint, int squadCount, float screenHeight) =>
            ObjectivesPanel.Contains(guiPoint) || SquadPanel(squadCount, screenHeight).Contains(guiPoint);

        public static string ObjectiveLine(NetObjective objective)
        {
            switch (objective.State)
            {
                case ObjectiveState.Completed:
                    return "[x] " + objective.Title;
                case ObjectiveState.Failed:
                    return "[!] " + objective.Title + " (failed)";
                case ObjectiveState.Inactive:
                    return "[ ] " + objective.Title + (objective.Kind == NetObjectiveKind.Extraction ? " (locked)" : " (not yet)");
                default:
                    return "[ ] " + objective.Title + (objective.Kind == NetObjectiveKind.Extraction ? " (open: reach the green zone)" : string.Empty);
            }
        }

        public static string ResultBanner(NetMissionResult result) =>
            result == NetMissionResult.Success ? "MISSION SUCCESS" : result == NetMissionResult.Failure ? "MISSION FAILED" : null;

        public static string MapStatus(NetClientRecord record) =>
            record.Map != null ? null : record.MapProblem != null ? "Map refused: " + record.MapProblem : "No map authorized for this mission";

        public const string Help =
            "Left click: select / move / attack   Shift: queue   X: stop   Tab: next   Ctrl+A: all   F: follow (WASD steers)   Esc: free camera   " +
            "WASD: pan   Q/E or right-drag: rotate   Wheel: zoom";
    }
}
```

- [ ] **Step 4: Write `Assets/_Project/Scripts/Net/Play/NetPlayHud.cs`.**

```csharp
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The play client's placeholder HUD (IMGUI):
    /// - connection and map status;
    /// - the known objectives;
    /// - squad cards with health bars;
    /// - health bars over visible units;
    /// - floating combat text;
    /// - the extraction and last-known labels;
    /// - the last refused request or click feedback;
    /// - the controls;
    /// - the end banner.
    /// It shows only what the client was sent. Clicks on its panels never reach the world (NetPlayControls.Blocks).
    /// </summary>
    public sealed class NetPlayHud : MonoBehaviour
    {
        const float FloatSeconds = 1.2f;

        [SerializeField] NetClient client;
        [SerializeField] NetPlayControls controls;
        [SerializeField] NetPlayAvatars avatars;
        [SerializeField] NetMapView mapView;
        [SerializeField] Camera viewCamera;

        readonly List<(int Id, string Text, float Born)> floaters = new List<(int, string, float)>();
        GUIStyle banner;
        GUIStyle label;

        internal void Initialize(NetClient netClient, NetPlayControls playControls, NetPlayAvatars playAvatars, NetMapView map, Camera camera)
        {
            client = netClient;
            controls = playControls;
            avatars = playAvatars;
            mapView = map;
            viewCamera = camera;
        }

        void OnEnable()
        {
            if (avatars != null)
                avatars.Floater += OnFloater;
            if (controls != null)
                controls.Blocks = screen => NetPlayText.Covers(new Vector2(screen.x, Screen.height - screen.y), client != null ? client.OwnedCount : 0, Screen.height);
        }

        void OnDisable()
        {
            if (avatars != null)
                avatars.Floater -= OnFloater;
        }

        void OnFloater(int unitId, string text) => floaters.Add((unitId, text, Time.time));

        void OnGUI()
        {
            if (client == null)
                return;
            if (label == null)
                label = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = false };
            GUI.Label(new Rect(10f, 10f, 900f, 24f), StatusLine(), label);
            DrawObjectives();
            DrawSquad();
            DrawWorldLabels();
            GUI.Label(new Rect(10f, Screen.height - 30f, Screen.width - 20f, 24f), NetPlayText.Help, label);
            var result = NetPlayText.ResultBanner(client.Record.Result);
            if (result != null)
                DrawBanner(result, "The server froze the result. Close the window to finish.");
            else if (client.State == NetClientState.Disconnected || client.State == NetClientState.Refused)
                DrawBanner(client.State == NetClientState.Refused ? "REFUSED (" + client.Refusal + ")" : "DISCONNECTED", "The server is gone; nothing more can be ordered.");
        }

        string StatusLine()
        {
            var text = new StringBuilder("State ").Append(client.State).Append("   round trip ").Append(client.RoundTripMs).Append(" ms");
            var map = NetPlayText.MapStatus(client.Record);
            if (map != null)
                text.Append("   ").Append(map);
            if (controls != null && controls.LastFeedback != null)
                text.Append("   ").Append(controls.LastFeedback);
            else if (client.LastReject != NetRejectReason.None)
                text.Append("   last order refused: ").Append(client.LastReject);
            if (controls != null && controls.Following)
                text.Append("   FOLLOWING (WASD steers, Esc frees the camera)");
            return text.ToString();
        }

        void DrawObjectives()
        {
            GUI.Box(NetPlayText.ObjectivesPanel, "Objectives");
            var y = NetPlayText.ObjectivesPanel.y + 24f;
            foreach (var objective in client.Record.Objectives)
            {
                GUI.Label(new Rect(NetPlayText.ObjectivesPanel.x + 10f, y, NetPlayText.ObjectivesPanel.width - 20f, 22f), NetPlayText.ObjectiveLine(objective), label);
                y += 22f;
            }
        }

        void DrawSquad()
        {
            var panel = NetPlayText.SquadPanel(client.OwnedCount, Screen.height);
            GUI.Box(panel, "Squad");
            var y = panel.y + 26f;
            for (var i = 0; i < client.OwnedCount; i++)
            {
                var id = client.FirstOwnedId + i;
                var state = Find(id);
                var line = "Operative " + (i + 1) + (controls != null && controls.IsSelected(id) ? "  [selected]" : string.Empty);
                if (state != null)
                    line += state.Alive ? "   " + state.Health + "/" + state.MaxHealth + "   " + state.Order : "   DOWN";
                GUI.Label(new Rect(panel.x + 10f, y, 260f, 22f), line, label);
                if (state != null && state.Alive)
                    Bar(new Rect(panel.x + 270f, y + 6f, 130f, 10f), state.Health, state.MaxHealth, new Color(0.2f, 0.8f, 0.3f));
                y += NetPlayText.SquadRowHeight;
            }
        }

        void DrawWorldLabels()
        {
            if (viewCamera == null)
                return;
            foreach (var state in NetClient.VisibleStates())
            {
                if (!state.Alive || !ToGui(state.transform.position + Vector3.up * 2.3f, out var p))
                    continue;
                Bar(new Rect(p.x - 25f, p.y, 50f, 6f), state.Health, state.MaxHealth, state.Hostile ? new Color(0.9f, 0.25f, 0.2f) : new Color(0.2f, 0.8f, 0.3f));
            }
            if (avatars != null)
                foreach (var pair in avatars.LastKnown)
                    if (ToGui(pair.Value + Vector3.up * 0.5f, out var p))
                        GUI.Label(new Rect(p.x - 40f, p.y - 10f, 80f, 20f), "? last seen", label);
            if (mapView != null && mapView.Extraction != null && ToGui(mapView.Extraction.position + Vector3.up * 3.2f, out var zone))
                GUI.Label(new Rect(zone.x - 50f, zone.y - 10f, 100f, 20f), "EXTRACTION", label);
            floaters.RemoveAll(f => Time.time - f.Born > FloatSeconds);
            foreach (var (id, text, born) in floaters)
            {
                var state = Find(id);
                if (state != null && ToGui(state.transform.position + Vector3.up * (2.6f + (Time.time - born)), out var p))
                    GUI.Label(new Rect(p.x - 30f, p.y - 10f, 60f, 20f), text, label);
            }
        }

        void DrawBanner(string title, string detail)
        {
            if (banner == null)
                banner = new GUIStyle(GUI.skin.box) { fontSize = 40, alignment = TextAnchor.MiddleCenter };
            var rect = new Rect(Screen.width * 0.5f - 300f, Screen.height * 0.5f - 70f, 600f, 100f);
            GUI.Box(rect, title, banner);
            GUI.Label(new Rect(rect.x, rect.yMax + 6f, rect.width, 24f), detail, label);
        }

        static void Bar(Rect rect, int value, int max, Color colour)
        {
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0f, new Color(0f, 0f, 0f, 0.6f), 0f, 0f);
            var fill = rect;
            fill.width = rect.width * (max > 0 ? Mathf.Clamp01((float)value / max) : 0f);
            GUI.DrawTexture(fill, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0f, colour, 0f, 0f);
        }

        // World point to IMGUI coordinates (origin top left); false behind the camera.
        bool ToGui(Vector3 world, out Vector2 gui)
        {
            var p = viewCamera.WorldToScreenPoint(world);
            gui = new Vector2(p.x, Screen.height - p.y);
            return p.z > 0f;
        }

        static NetUnitState Find(int unitId)
        {
            foreach (var state in NetClient.VisibleStates())
                if (state.UnitId == unitId)
                    return state;
            return null;
        }
    }
}
```

- [ ] **Step 5: Write `Assets/_Project/Scripts/Net/Play/NetPlayBootstrap.cs`.**

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>The play client's start: reads -server, -port and -token (NetClientOptions) and connects. It runs no harness.</summary>
    public sealed class NetPlayBootstrap : MonoBehaviour
    {
        public const string Prefix = "[NetPlay] ";

        [SerializeField] NetClient client;

        internal void Initialize(NetClient netClient) => client = netClient;

        void Start()
        {
            var options = NetClientOptions.Parse(Environment.GetCommandLineArgs(), out var warnings);
            foreach (var warning in warnings)
                Debug.LogWarning(Prefix + "option ignored: " + warning);
            if (options.Harness != null)
                Debug.LogWarning(Prefix + "-netharness belongs to the net-test client; the play client ignores it");
            if (string.IsNullOrEmpty(options.Token))
            {
                Debug.LogWarning(Prefix + "no -token was given; not connecting");
                return;
            }
            Debug.Log(Prefix + FormattableString.Invariant($"connecting to {options.Server}:{options.Port}"));
            if (!client.Connect(options.Server, (ushort)options.Port, options.Token))
                Debug.LogWarning(Prefix + "the connection could not be started");
        }
    }
}
```

- [ ] **Step 6: Write `Assets/_Project/Editor/Net/NetPlayClientSceneBuilder.cs`.**

```csharp
#if UNITY_EDITOR
using System;
using System.Linq;
using FishNet.Managing.Object;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the play client's scene (decision 052): a camera rig with the offline TacticalCameraController, a light, and one
    /// object holding the client, map view, avatars, controls, HUD and bootstrap. No ground plane: the projected map is the floor.
    /// Rebuilt from scratch every run; no other scene is opened or changed.
    /// </summary>
    public static class NetPlayClientSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/NetPlayClient.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        [MenuItem("Blackglass/Net/Build Net Play Client Scene")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var light = new GameObject("Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(55f, 35f, 0f);

            var rig = new GameObject("CameraRig");
            var cameraObject = new GameObject("Camera") { tag = "MainCamera" };
            cameraObject.transform.SetParent(rig.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
            cameraObject.AddComponent<AudioListener>();
            var cameraRig = rig.AddComponent<TacticalCameraController>();
            cameraRig.Initialize(camera, Action("Camera", "Pan"), Action("Camera", "Rotate"), Action("Camera", "RotateDrag"),
                Action("Camera", "PointerPosition"), Action("Camera", "Zoom"));

            var holder = new GameObject("NetPlayClient");
            var client = holder.AddComponent<NetClient>();
            client.Initialize(AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset"));
            var map = holder.AddComponent<NetMapView>();
            map.Initialize(client, Load<EnvironmentTheme>("Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset"),
                Load<Material>("Assets/_Project/Materials/Ground.mat"), Load<Material>("Assets/_Project/Materials/Obstacle.mat"));
            var controls = holder.AddComponent<NetPlayControls>();
            controls.Initialize(client, camera, cameraRig, map, Action("Commands", "Command"), Action("Commands", "PointerPosition"),
                Action("Commands", "QueueModifier"), Action("Commands", "Stop"), Action("Commands", "Cancel"), Action("Camera", "Pan"));
            var avatars = holder.AddComponent<NetPlayAvatars>();
            avatars.Initialize(client, null, Load<GameObject>("Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Visual.prefab"),
                Load<WeaponVisualAsset>("Assets/_Project/Data/Items/Visuals/RifleVisual.asset"),
                Load<GameObject>("Assets/_Project/Presentation/Prefabs/TracerRifle.prefab"),
                Load<Material>("Assets/_Project/Materials/SelectionRing.mat"), Load<Material>("Assets/_Project/Materials/IntelMarker.mat"));
            holder.AddComponent<NetPlayHud>().Initialize(client, controls, avatars, map, camera);
            holder.AddComponent<NetPlayBootstrap>().Initialize(client);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Could not save " + ScenePath + ".");
            Debug.Log("NetPlayClientSceneBuilder: saved " + ScenePath + ".");
        }

        static InputActionReference Action(string map, string action)
        {
            var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.name == action && r.action.actionMap != null && r.action.actionMap.name == map);
            if (reference == null)
                throw new InvalidOperationException($"No InputActionReference for '{map}/{action}' in {ControlsPath}.");
            return reference;
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path);
            return asset;
        }
    }
}
#endif
```

The avatars' selection callback is a delegate, which a scene cannot serialize, so it is wired at runtime. Add these two
lines at the end of `NetPlayHud.OnEnable` (Step 4):

```csharp
            if (avatars != null && controls != null)
                avatars.SetSelection(controls.IsSelected);
```

- [ ] **Step 7: Write `Assets/_Project/Editor/Net/NetPlayClientBuild.cs`.**

```csharp
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the Windows play client from NetPlayClient.unity into Builds/NetPlayClient/ (git-ignored). Local, development-only,
    /// never deployed; not a Development build (no player connection). The Editor's build target must already be Windows Standalone.
    /// </summary>
    public static class NetPlayClientBuild
    {
        public const string OutputPath = "Builds/NetPlayClient/NetPlayClient.exe";

        [MenuItem("Blackglass/Net/Build Net Play Client")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry: exits 0 on success, 1 on any failure.</summary>
        public static void BuildFromCommandLine() => EditorApplication.Exit(Build() ? 0 : 1);

        static bool Build()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                Debug.LogError("[NetPlayClientBuild] the active build target is " + EditorUserBuildSettings.activeBuildTarget + ", not StandaloneWindows64.");
                return false;
            }
            var folder = Path.GetDirectoryName(OutputPath);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { NetPlayClientSceneBuilder.ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            });
            Debug.Log("[NetPlayClientBuild] " + report.summary.result + " " + (report.summary.totalSize / 1048576) + " MB " + OutputPath);
            return report.summary.result == BuildResult.Succeeded;
        }
    }
}
#endif
```

- [ ] **Step 8: Write `Tools/build-netplay-client.sh`.**

```bash
#!/usr/bin/env bash
# Builds the Windows play client (decision 052): local, development-only, never deployed. The Unity Editor must be closed.
# Usage: Tools/build-netplay-client.sh      Output: Builds/NetPlayClient/NetPlayClient.exe
set -u
root_unix="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$root_unix" && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
log="$root/Logs/NetPlayClientBuild.log"
mkdir -p "$root_unix/Logs" "$root_unix/Builds"
rm -rf "$root_unix/Builds/NetPlayClient"
echo "source: revision=$(git -C "$root_unix" rev-parse HEAD)"

"$unity" -batchmode -quit -projectPath "$root" \
  -executeMethod Blackglass.EditorTools.NetPlayClientBuild.BuildFromCommandLine -logFile "$log"
code=$?

if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
grep -h "\[NetPlayClientBuild\]" "$log" 2>/dev/null
if [ "$code" -ne 0 ] || [ ! -f "$root_unix/Builds/NetPlayClient/NetPlayClient.exe" ]; then
  echo "BUILD FAILED (Unity exit $code). See $log"
  echo "EXIT=1"
  exit 1
fi
echo "client=$root_unix/Builds/NetPlayClient/NetPlayClient.exe"
echo "EXIT=0"
```

- [ ] **Step 9: Write `Tools/run-local-netplay.sh`.**

```bash
#!/usr/bin/env bash
# The local two-process demonstration (decision 052): the local fixture server (-attemptFixture fixture 03, -net on
# 127.0.0.1:7770, WebSockets) and the play client, both on this PC. A fresh development token file is made under
# Builds/LocalNetPlay/ (git-ignored); the token is passed to the client on its command line and never printed.
# Needs Builds/LocalFixtureServer/BlackglassServer.exe (Tools/build-local-fixture-server.sh) and
# Builds/NetPlayClient/NetPlayClient.exe (Tools/build-netplay-client.sh). Closing the client window stops the server.
# Last lines: the server's ATTEMPT FROZEN line (if the mission ended) and where the logs are.
set -u
root="$(cd "$(dirname "$0")/.." && pwd)"
port="${NET_PORT:-7770}"
server="$root/Builds/LocalFixtureServer/BlackglassServer.exe"
client="$root/Builds/NetPlayClient/NetPlayClient.exe"
work="$root/Builds/LocalNetPlay"
[ -x "$server" ] || { echo "build the server first: Tools/build-local-fixture-server.sh"; exit 2; }
[ -x "$client" ] || { echo "build the client first: Tools/build-netplay-client.sh"; exit 2; }
mkdir -p "$work"
tokens="$work/dev-tokens.txt"
BLACKGLASS_TOKENS="$tokens" "$root/Tools/LinuxServer/make-dev-tokens.sh" --force >/dev/null || { echo "could not make the token file"; exit 2; }
token="$(awk '$1=="account-a"{print $2}' "$tokens")"
[ -n "$token" ] || { echo "the token file has no account-a"; exit 2; }
fixture="$(cd "$root" && pwd -W)/Backend/contracts/attempt-v1/03-seeded-collect-and-use.json"
log="$work/server.log"
rm -f "$log"

"$server" -batchmode -nographics -logFile "$(cygpath -w "$log")" -attemptFixture "$fixture" \
  -net -netPort "$port" -netTokens "$(cygpath -w "$tokens")" -heartbeat 10 &
server_pid=$!
cleanup() { kill "$server_pid" 2>/dev/null; wait "$server_pid" 2>/dev/null; }
trap cleanup EXIT

for _ in $(seq 1 120); do
  grep -q "\[Net\] listening 127.0.0.1:$port" "$log" 2>/dev/null && break
  kill -0 "$server_pid" 2>/dev/null || { echo "the server stopped during startup; see $log"; exit 1; }
  sleep 1
done
grep -q "\[Net\] listening 127.0.0.1:$port" "$log" || { echo "the server did not listen within 120 s; see $log"; exit 1; }
grep -h "map projection" "$log"
echo "server listening; starting the play client (close its window to finish)"

"$client" -server 127.0.0.1 -port "$port" -token "$token" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 \
  -logFile "$(cygpath -w "$work/client.log")"
grep -h "ATTEMPT FROZEN" "$log" || echo "no ATTEMPT FROZEN line: the mission did not end"
echo "logs: $log $work/client.log"
```

The `FIXTURE OPERATOR AID` line is still written to `server.log` by the fixture build. The script never prints it, and the
demonstration must not use it.

- [ ] **Step 10: Generate the scene, then write and run the scene test.** Unity must be closed.
Run: `Tools/run-method.sh Blackglass.EditorTools.NetPlayClientSceneBuilder.BuildFromCommandLine`
Expected: the log says `NetPlayClientSceneBuilder: saved Assets/_Project/Scenes/NetPlayClient.unity.`

Then write `Assets/_Project/Tests/PlayMode/NetPlayClientScenePlayTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using FishNet.Managing;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The generated play-client scene holds the play client, wired to real assets, and nothing of the harness or the game.</summary>
    public class NetPlayClientScenePlayTests
    {
        const string ScenePath = "Assets/_Project/Scenes/NetPlayClient.unity";
        Scene scene;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(scene.isLoaded, Is.True);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            NetManagerFactory.DestroyAll();
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        [Test]
        public void TheScene_HoldsThePlayClient_AndNoHarnessNorGame()
        {
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).ToArray();
            foreach (var type in new[] { typeof(NetClient), typeof(NetMapView), typeof(NetPlayAvatars), typeof(NetPlayControls), typeof(NetPlayHud), typeof(NetPlayBootstrap), typeof(TacticalCameraController) })
                Assert.That(all.Count(c => c != null && c.GetType() == type), Is.EqualTo(1), type.Name);
            foreach (var type in new[] { typeof(NetHarness), typeof(NetTestControls), typeof(NetClientView), typeof(MissionDirector), typeof(NetServer), typeof(CommandableUnit) })
                Assert.That(all.Any(c => c != null && c.GetType() == type), Is.False, type.Name + " must not be in the play client");
        }

        [Test]
        public void TheSceneBuilder_WiredEveryAsset()
        {
            var map = Object.FindObjectsByType<NetMapView>(FindObjectsSortMode.None).Single();
            var serialized = new UnityEditor.SerializedObject(map);
            Assert.That(serialized.FindProperty("theme").objectReferenceValue, Is.Not.Null);
            var avatars = new UnityEditor.SerializedObject(Object.FindObjectsByType<NetPlayAvatars>(FindObjectsSortMode.None).Single());
            foreach (var field in new[] { "visualPrefab", "weapon", "tracerPrefab", "ringMaterial", "markerMaterial" })
                Assert.That(avatars.FindProperty(field).objectReferenceValue, Is.Not.Null, field);
            var controls = new UnityEditor.SerializedObject(Object.FindObjectsByType<NetPlayControls>(FindObjectsSortMode.None).Single());
            foreach (var field in new[] { "commandAction", "pointerAction", "queueAction", "stopAction", "cancelAction", "panAction" })
                Assert.That(controls.FindProperty(field).objectReferenceValue, Is.Not.Null, field);
        }
    }
}
#endif
```

- [ ] **Step 11: Run the tests.**
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.NetPlayTextTests"`
Expected: PASS.
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetPlayClientScenePlayTests|Blackglass.Tests.NetPlayControlsPlayTests|Blackglass.Tests.NetClientScenePlayModeTests"`
Expected: PASS. The net-test client's scene test is unchanged and still passes.

- [ ] **Step 12: Commit.**

```bash
chmod +x Tools/build-netplay-client.sh Tools/run-local-netplay.sh
git add Assets/_Project/Scripts/Net/Play/NetPlayText.cs* Assets/_Project/Scripts/Net/Play/NetPlayHud.cs* \
  Assets/_Project/Scripts/Net/Play/NetPlayBootstrap.cs* Assets/_Project/Editor/Net/NetPlayClientSceneBuilder.cs* \
  Assets/_Project/Editor/Net/NetPlayClientBuild.cs* Assets/_Project/Scenes/NetPlayClient.unity* \
  Tools/build-netplay-client.sh Tools/run-local-netplay.sh Assets/_Project/Tests/EditMode/NetPlayTextTests.cs* \
  Assets/_Project/Tests/PlayMode/NetPlayClientScenePlayTests.cs* Assets/_Project/Tests/PlayMode/NetPlayControlsPlayTests.cs
git commit -m "Play client scene: HUD, bootstrap, scene builder, Windows build and the local two-process launcher" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: The unassisted mission and the offline guard

**Files:**
- Test: `Assets/_Project/Tests/PlayMode/NetPlayUnassistedPlayTests.cs`
- Test: `Assets/_Project/Tests/EditMode/OfflineSceneGuardTests.cs`

**Interfaces:**
- Consumes: `NetPlayControls.SelectAllLiving/MoveSelected/AttackWithSelected`, `NetMapView.Layout`, `NetClient.Record`, `NetClient.ShotReceived`, `NetClient.VisibleStates()`.

- [ ] **Step 1: Write the offline guard** `Assets/_Project/Tests/EditMode/OfflineSceneGuardTests.cs`. Its hashes were measured at
  `c061ff9` with `tr -d '\r' < scene | sha256sum`.

```csharp
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// The offline scenes are byte for byte the accepted ones (owner, 2026-10-22: the offline prototype stays unchanged). Line
    /// endings are ignored. A deliberate offline change needs the owner's authorization and a new hash here.
    /// </summary>
    public class OfflineSceneGuardTests
    {
        [TestCase("Assets/_Project/Scenes/ProceduralMission.unity", "89ba4eea16fe964aae80fac83ca2a927e1a2b4edcc5cfbefb636c2d6320df9c8")]
        [TestCase("Assets/_Project/Scenes/Prototype.unity", "b5a15d0ac7aa5ab27c53bfde9f5502af2a7201f27bc5628bd42b4c355a27ae1b")]
        public void TheOfflineScene_IsTheAcceptedOne(string path, string sha256)
        {
            var bytes = File.ReadAllBytes(path).Where(b => b != (byte)'\r').ToArray();
            using (var sha = SHA256.Create())
            {
                var hash = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
                Assert.That(hash, Is.EqualTo(sha256), path + " changed");
            }
        }
    }
}
```

- [ ] **Step 2: Prove it can fail, then run it.** The guard pins the base state, so it passes at once. To watch it fail, add a
  temporary third `TestCase` that points at a scratch copy of `Prototype.unity` with one byte changed, under
  `Application.temporaryCachePath`, and that expects the original hash. Run it and see that case FAIL. Delete the case and the
  copy, and ledger the result.
Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.OfflineSceneGuardTests"`
Expected: PASS, 2 tests.

- [ ] **Step 3: Write the unassisted mission test** `Assets/_Project/Tests/PlayMode/NetPlayUnassistedPlayTests.cs`.

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Fixture 03 played to extraction through the play client's own command layer, with no operator aid. The "player" below
    /// decides only from what the client was sent:
    /// - the projected map: it explores room-sized floor rectangles nearest first;
    /// - the replicated avatars: it attacks a hostile it observes;
    /// - the last-known records: it walks to where the hostile was seen;
    /// - the objectives: it walks to the extraction point once extraction opens;
    /// - the end message.
    /// The server runs -attemptFixture and -net without -netQuiet: the hostile's AI, the squad's autonomy and the hit rolls are
    /// the normal ones. Nothing is warped, damaged directly or completed by a call. The server's state is read only for the final
    /// assertions.
    /// </summary>
    public class NetPlayUnassistedPlayTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.Director.Clear();
            yield return null;
            var fixture = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Backend", "contracts", "attempt-v1", "03-seeded-collect-and-use.json");
            rig.Bootstrap.Begin(new[] { "-attemptFixture", fixture });
            yield return TestWorld.WaitUntil(() => rig.Bootstrap.Session != null || rig.Bootstrap.HasExited, 60f);
            Assert.That(rig.Bootstrap.Session, Is.Not.Null, rig.Bootstrap.ExitReason);
            rig.BeginNet(quiet: false);
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [UnityTest, Timeout(480000)]
        public IEnumerator A_player_with_only_the_play_client_finds_engages_and_extracts()
        {
            var acceptedBefore = rig.Server.Host.Accepted;
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var client = rig.Client;
            Assert.That(client.State, Is.EqualTo(NetClientState.Ready));
            var view = NetPlayKit.AddMapView(client.gameObject, client, false);
            var controls = client.gameObject.AddComponent<NetPlayControls>();
            controls.Initialize(client, null, null, view, null, null, null, null, null, null);
            var shots = 0;
            var shotsNamingUnseenUnits = 0;
            client.ShotReceived += shot =>
            {
                shots++;
                var visible = NetClient.VisibleStates().Select(s => s.UnitId).ToList();
                if (!visible.Contains(shot.AttackerId) || !visible.Contains(shot.TargetId))
                    shotsNamingUnseenUnits++;
            };
            yield return TestWorld.WaitUntil(() => view.Built && client.Record.Objectives.Count == 2 && Own(client).Count == client.OwnedCount, 15f);
            controls.SelectAllLiving();

            // Explore: room-sized floor rectangles, nearest first from the squad, until a hostile is observed.
            var squadStart = Centre(Own(client));
            var waypoints = view.Layout.FloorRects.Where(r => r.width * r.height >= 16).Select(r => view.Layout.RectCenter(r))
                .Where(p => Vector3.Distance(p, squadStart) > 4f).ToList();
            var deadline = Time.realtimeSinceStartup + 360f;
            int hostileId = 0;
            var nextOrder = 0f;
            while (Time.realtimeSinceStartup < deadline && client.Record.Result == NetMissionResult.None)
            {
                var objectives = client.Record.Objectives;
                var extraction = objectives.FirstOrDefault(o => o.Kind == NetObjectiveKind.Extraction);
                var hostile = NetClient.VisibleStates().FirstOrDefault(s => s.Hostile && s.Alive);
                if (Time.realtimeSinceStartup >= nextOrder)
                {
                    nextOrder = Time.realtimeSinceStartup + 1f;
                    controls.SelectAllLiving();
                    if (extraction.State == ObjectiveState.Active)
                        controls.MoveSelected(extraction.Point, false);
                    else if (hostile != null)
                    {
                        hostileId = hostile.UnitId;
                        if (Own(client).Any(u => u.Order != NetOrderKind.Attack))
                            controls.AttackWithSelected(hostile.UnitId);
                    }
                    else if (hostileId != 0 && client.Record.LastKnown.TryGetValue(hostileId, out var seen))
                        controls.MoveSelected(seen, false);
                    else if (waypoints.Count > 0)
                    {
                        var here = Centre(Own(client));
                        var next = waypoints.OrderBy(p => Vector3.Distance(p, here)).First();
                        if (Vector3.Distance(here, next) < 2.5f)
                            waypoints.Remove(next);
                        else
                            controls.MoveSelected(next, false);
                    }
                }
                yield return null;
            }

            var session = rig.Bootstrap.Session;
            Assert.That(client.Record.Result, Is.EqualTo(NetMissionResult.Success), "the client was told the mission succeeded");
            Assert.That(session.IsFrozen, Is.True);
            Assert.That(session.Result.Outcome.ToString(), Is.EqualTo("Success"));
            Assert.That(session.Outcome.Manifest, Is.Not.Null, "a manifest was built at the freeze");
            Assert.That(rig.Server.Host.Accepted - acceptedBefore, Is.GreaterThan(0));
            Assert.That(shots, Is.GreaterThan(0), "the client was shown at least one shot");
            Assert.That(shotsNamingUnseenUnits, Is.Zero, "every shot named units this client was sent");
            Assert.That(client.Record.MapMessages, Is.EqualTo(1));
            Assert.That(client.Record.EndMessages, Is.EqualTo(1));
        }

        static List<NetUnitState> Own(NetClient client) => NetClient.VisibleStates()
            .Where(s => !s.Hostile && s.UnitId >= client.FirstOwnedId && s.UnitId < client.FirstOwnedId + client.OwnedCount && s.Alive).ToList();

        static Vector3 Centre(List<NetUnitState> units) =>
            units.Count == 0 ? Vector3.zero : units.Aggregate(Vector3.zero, (sum, u) => sum + u.transform.position) / units.Count;
    }
}
#endif
```

The squad's attacks may also come from its own autonomy (`CompanionAI`). The test asserts that the mission succeeds through
the play client's orders and the server's rules. The checkpoint test already pins "at least one attack request accepted".

- [ ] **Step 4: Run it.**
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetPlayUnassistedPlayTests"`
Expected: PASS within 8 minutes. Hit rolls are real. If the squad is wiped (Failure), record the run and re-run once. Two
failures in a row is a finding to report, not something to tune away: do not change balance.

- [ ] **Step 5: Mutation check (not committed).** Temporarily make `NetServer.SendBriefing` skip the map broadcast.
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NetPlayUnassistedPlayTests"`
Expected: FAIL. With no map the player has no waypoints and the hostile is never found, so the run times out without Success.
Revert the change, confirm `git diff` is empty for `NetServer.cs`, and ledger the result.

- [ ] **Step 6: Commit.**

```bash
git add Assets/_Project/Tests/PlayMode/NetPlayUnassistedPlayTests.cs* Assets/_Project/Tests/EditMode/OfflineSceneGuardTests.cs*
git commit -m "Unassisted mission through the play client's command layer, and the offline-scene guard" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Documentation, final validation and the two-process demonstration

**Files:**
- Modify: `Docs/DedicatedServer.md` (a "Play client (local)" section)
- Modify: this plan (an "As built" section with the evidence)
- Modify: `CLAUDE.md` (one progress sub-bullet under Stage 12, after the loopback checkpoint bullet)

- [ ] **Step 1: Document.**
  - In `Docs/DedicatedServer.md`, add a short section: what the play client is; that the projection is sent only for
    Full/All without a terminal or cameras; build and run commands (`Tools/build-local-fixture-server.sh`,
    `Tools/build-netplay-client.sh`, `Tools/run-local-netplay.sh`); the controls table (D7); that it is local and
    development-only; and a pointer to decision 052, including the seed-fingerprint blocker.
  - In `CLAUDE.md`, add one sub-bullet. Its wording is the owner's to approve at merge: "Visual network client (2026-10-2x):
    local, human-playable play client over the public map projection (decision 052); human playtest result: <owner's words>."

- [ ] **Step 2: Run both full suites, one after the other, on the final tree** (a clean worktree, as in the checkpoint, so the
  owner's two settings files are not involved).
Run: `Tools/run-tests.sh EditMode` then `Tools/run-tests.sh PlayMode`
Expected: all pass. Record the exact counts against `main`'s 2006 and 1459 plus the new tests.

- [ ] **Step 3: Build both players from the same revision**, in the clean worktree, one after the other.
Run: `Tools/build-local-fixture-server.sh` then `Tools/build-netplay-client.sh`
Expected: `EXIT=0` for each. Record the revision and the `Blackglass.dll` sha256 of each.

- [ ] **Step 4: Smoke the two processes.** Run `Tools/run-local-netplay.sh`. The client window opens. Confirm the server log
  shows `map projection on floors=` and `client 1 welcomed account=account-a owns=2`, and close the client. Do not play the
  mission yourself. Grep both logs for the token, which must appear 0 times.

- [ ] **Step 5: Hand over for the owner's playtest.** Report how to run `Tools/run-local-netplay.sh` and what to try:
  1. find the hostile using the map;
  2. engage it;
  3. walk around obstacles to the green extraction zone after the objective opens;
  4. see "MISSION SUCCESS", and the server's `ATTEMPT FROZEN outcome=Success` line printed by the script.

  The owner's result is the acceptance of requirement 9. Record it verbatim in "As built".

- [ ] **Step 6: Final review and commit.** Run one independent whole-branch review: the four new messages against decision 052;
  that `git diff main --stat` touches no offline folder; that the gate, visibility and attempt code are unchanged; the Review
  Focus list. Fix Critical and Important findings test-first. Then commit the docs:

```bash
git add Docs/DedicatedServer.md Docs/superpowers/plans/2026-10-22-visual-network-mission.md CLAUDE.md
git commit -m "Visual network mission: docs, final suites, builds and the local two-process demonstration" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

No merge or push until the owner accepts the playtest.

---

## 5. Out of scope

- Map knowledge Partial or None, terminals, cameras, layout streaming and geometry secrecy (D3).
- Item, ability and interaction requests (Stage 14), reconnect, co-op.
- The backend HTTP path, login, loadout, tickets, receipts (Stage 13 and the online entry scene).
- Any Linux VM work, deployment, Stage 10, B24.
- Client-side prediction, tactical pause online.
- Final art, audio and animation polish; operative names on the wire.

## 6. Risks

- **Unassisted test flakiness.** Hit rolls and autonomy are real. Mitigation: a single re-run rule; never tune balance.
- **FishNet message size.** The largest map is about 2.4k boxes × 13 bytes plus floors, roughly 40 KB in the worst case. The
  fixture is a few KB. FishNet splits large reliable broadcasts. If a large map is ever refused by the transport, the client
  shows "No map authorized", which is fail-closed. A PlayMode case for the largest settings is a cheap later addition.
- **IMGUI HUD.** Placeholder quality. The offline HUD is coupled to offline units and is deliberately not reused.

## Self-review

- **Spec coverage.** Every requirement row in the traceability table names a task, and every Review Focus line names a test in
  a task.
- **Placeholders.** None. The scene and the offline hashes are measured values. The `CLAUDE.md` wording waits for the owner's
  playtest words by design.
- **Type consistency.** The names were checked across tasks:
  - `NetMap`, `NetMapBox`, `NetObjective`, `NetObjectiveKind`, `NetMissionResult`, `NetShot`;
  - `NetMapValidation.Problem`, `NetMapProjection.Map` and `Objectives`;
  - `NetMapGeometry.ToLayout`, `FloorBounds`, `BoxBounds`, `ContainerBounds`;
  - `NetMapWire.ToMessage` and `FromMessage`;
  - `NetClientRecord.ReceiveMap`, `ReceiveObjectives`, `ReceiveEnd`, `Map`, `Objectives`, `Result`;
  - `NetClient.ShotReceived`, `NetShotRelay.Flush`;
  - `NetMapView.Build`, `Layout`, `Obstacles`, `Cubes`, `Extraction`;
  - `NetPlayAvatars.ViewOf`, `Floater`, `SetSelection`;
  - `NetPlayControls.CommandAtScreen`, `MoveSelected`, `AttackWithSelected`, `StopSelected`, `SelectAllLiving`, `Send`, `Blocks`;
  - `NetPlayText.Covers`, `ObjectivesPanel`, `SquadPanel`.

## Execution recommendation

**Native** (superpowers:executing-plans), with one fresh whole-branch reviewer on the most capable model at the end. Every
task needs a Unity test run, Unity runs one instance at a time on this low-memory PC, and the tasks build tightly on each
other's interfaces. Fresh per-task subagents would wait on the same single Unity process.
