# Project Blackglass online-readiness audit (before Phase 13)

Read-only audit, 2026-10-11. No project files were changed while it was carried out. Labels used below:

- **[O]** observed in the code, the data or a test run.
- **[I]** inferred from what was observed.
- **[R]** a risk that nothing has exercised yet.

§1–§8 are the original audit record and are unchanged. Later checkpoints append their results in §9.

The audited stack (Unity Dedicated Server for Linux, FishNet, ASP.NET Core on .NET 10, PostgreSQL, Docker on isolated Linux VMs, HAProxy) is a proposal, not a decision.

---

## 1. Revision, working tree, environment and coverage

| Item | Value |
|---|---|
| Branch / HEAD | `main` @ `48502b3db99b5759475225f64fa57d8c131c3f04` ("Phase 12.5: combat presentation and VFX foundation"), equal to `origin/main` at the time of writing. |
| Uncommitted changes | `Assets/Settings/Mobile_RPAsset.asset` (`k_AssetPreviousVersion` 12→13) and `ProjectSettings/URPProjectSettings.asset` (adds `m_ProjectSettingFolderPath`). Both are URP upgrade bookkeeping and don't change gameplay. `.claude/` is untracked tool workspace. The untracked `Assets/InitTestScene*.unity` is a git-ignored leftover from test runs. |
| Unity | 6000.3.25f1 (e1dba0a9aba4) |
| Packages | ai.navigation 2.0.14, inputsystem 1.20.0, URP 17.3.0, test-framework 1.6.0, ugui 2.0.0. There is **no networking package**. |
| Main playable scene | `Assets/_Project/Scenes/ProceduralMission.unity` (build index 1). `Prototype.unity` (index 0) is the hand-built regression arena. |
| Assemblies | There is one runtime assembly, `Blackglass` (`Scripts/Blackglass.asmdef`). It references InputSystem, UnityEngine.UI, AI.Navigation and URP Runtime, and holds simulation, input, HUD, debug and presentation together. The test assemblies are EditMode and PlayMode, plus an editor AssetPipeline assembly. |
| Build support installed | `Editor/Data/PlaybackEngines` contains **only `windowsstandalonesupport`**. There is no Linux or Linux Dedicated Server module. |
| Host OS | Windows 10 build 19041, which is below Unity 6's minimum (already known; decision 001). |

**What was read:**
- **Docs:** `CLAUDE.md`, and `Docs/Decisions.md` 001–044 in full, focused on 006, 007, 013, 014, 025, 026, 033, 036, 037, 042, 043 and 044.
- **Code, end to end:**
  - `Commands/`, `Units/` (CommandableUnit, UnitMover, UnitAttacker, AutoRetaliate) and `Combat/`.
  - `Abilities/UnitAbilities`, `AbilityRules` and the Issue path in `AbilityTargeting`.
  - `Controls/DirectControlInput`, `PlayerCommandInput`, `CommandResolver` and `Input/PointerTargetResolver`.
  - `Intelligence/IntelligenceService`, `MissionIntelligence` and `Knowledge`.
  - `Mission/MissionDirector`, `MissionSpawner`, `MissionRuntime` and `MissionInteractable`.
  - `Items/` (session, state, inventory, transfer, UnitItems, LootContainer).
  - `Operatives/` (roster, state, identity), `AI/EnemyAI`, and parts of `CompanionAI` and `ActiveCharacter`.
- **Serialized data:**
  - The `ProceduralMission.unity` component map.
  - `FriendlyUnit.prefab` components.
  - The Darius and EnemyUnit visual prefabs (root motion overrides) and the Darius clip metas (animation events).
  - The input asset's `Developer` map, `EditorBuildSettings`, the Time and Dynamics settings, and the operative and item data ids.

**Not read in full:** HUD panels, `InventoryModal` (882 lines), the internals of `MissionGenerator`, `CoverGenerator` and the presentation layer, and most test bodies (the rigs and test names were read). Asset Studio is out of scope.

**Tests run for this audit** (2026-10-11, Editor PlayMode, `-batchmode -nographics`; the log confirms `Forcing GfxDevice: Null`):

| Run | Result |
|---|---|
| `MissionDirectorPlayModeTests`, `MissionRuntimePlayModeTests`, `InventoryLifecyclePlayModeTests` | **43/43 passed** |
| `ProceduralMissionSceneTests`, `IntelligenceServiceMissionTests`, `LootContainerPlayModeTests`, `PresentationMissionTests`, `MissionObjectivesSceneTests` | **46/46 passed** |

The logged errors in both runs were deliberate test fixtures: the "spawning throws" test and an empty loot-table rig. Results and logs were written to `.claude/scratchpad/online-audit/` on the audit machine (local only, not committed), outside `Logs/`, so the existing reports were untouched. `git status` was identical before and after both runs.

**Existing reports, not run for this audit:**
- `Logs/TestResults-PlayMode.xml` shows 1354/1354 passed on 2026-10-11, 14:38–14:50Z, in batch mode with a GPU device.
- `Logs/TestResults-EditMode.xml` is a filtered run of one test.
- Project notes record a full EditMode run at 1249/1249; not re-checked.

**Not exercised at all:**
- Any Linux or dedicated-server player build, or the `UNITY_SERVER` define.
- Any networking, multiple processes, or disconnect/reconnect.
- Long-running server loops and audio-free server runtime.

All headless player behaviour and all networking behaviour below is therefore **unverified**.

---

## 2. System map and the five traces

**Source map (layer → main files):**
- **Input (client):**
  - `Controls/PlayerCommandInput`, `DirectControlInput`
  - `Abilities/AbilityTargeting`
  - `Input/TacticalCursor`, `PointerTargetResolver`
  - `Hud/HudRequests`, `InventoryRequests`, `InventoryModal`
  - the `*DeveloperInput` components (F5–F12)
- **Command boundary:**
  - `Commands/UnitCommands.cs`: 9 command types (plus the `UnitCommand` base) that hold **live object references**
  - `CommandQueue`, `GroupOrders`
  - `CommandableUnit.Issue` / `SetMoveIntent`
- **Simulation:**
  - `CommandableUnit` (runs orders)
  - `UnitMover` (the only NavMeshAgent writer)
  - `UnitAttacker`, `UnitAbilities` + `AbilityRules`
  - `Health`, `Encounter`, `LineOfSight`
  - `UnitCover` / `CoverRegistry`
  - `EnemyAI`, `CompanionAI`, `AutoRetaliate`
  - `UnitItems`, `UnitInteractor` + `MissionInteractable`, `LootContainer`
- **Mission lifecycle:** `MissionDirector` coroutine → pure seeded placers (`MissionGenerator`, `ObjectivePlacer`, `SecurityPlacer`, `LootPlacer`) → `MissionBuilder` (geometry, NavMesh, themed visuals) → `MissionNavigation` → `CoverDiscovery` → `MissionSpawner` → `MissionRuntime` (plain C#, ticked from `MissionDirector.Update`).
- **Knowledge:** `IntelligenceService` (MonoBehaviour) + `MissionIntelligence` (pure model) → presenters (`FogPresenter`, `HostilePresenter`, `DevicePresenter`).
- **"Persistence" (in memory only):** `SquadRoster` + `PersistentOperativeState`; `SquadInventory` + `InventorySession` + `InventoryState`.
- **Time:** `TacticalPause` (the only `Time.timeScale` writer) and `SimulationTime.IsRunning`.
- **Presentation:** `Presentation/*` (records raised after resolution), `UnitAnimationDriver`, and the HUD snapshot builder.

Paths below are relative to `Assets/_Project/Scripts/` unless stated otherwise.

### T1a. Direct-control movement

1. `DirectControlInput.Update` (`Controls/DirectControlInput.cs:95-118`, execution order −100) reads Move, applies the release gate, and turns the input into a world direction using the camera yaw (`:62`).
2. That calls `CommandableUnit.SetMoveIntent` (`Units/CommandableUnit.cs:266-270`), which only flattens and clamps it.
3. Each simulation frame, `CommandableUnit.Update` (`:287-301`) checks: if the intent is non-zero, it runs `StopAll()` and then `UnitMover.Steer`.
4. `Steer` (`Units/UnitMover.cs:179-195`) calls `Agent.Move(dir * speed * Time.deltaTime)`. The position changes once per frame.

- **Events:** none. `UnitAnimationDriver` reads the root's movement, and `CompanionAI.YieldToControl` reads `MoveIntent`.
- **[O]** There is no ownership check. The intent stays set until something changes it; it has no expiry.

### T1b. Queued tactical move

1. Mouse release → `PointerTargetResolver.Resolve` (`Input/PointerTargetResolver.cs:22-59`) → `PlayerCommandInput.Act` (`Controls/PlayerCommandInput.cs:362-386`) → `CommandResolver.Resolve` (`Controls/CommandResolver.cs:11`) → `GroupOrders.Issue` (`Commands/GroupOrders.cs:17`, Append when Shift is held).
2. `Issue` (`CommandableUnit.cs:179-256`): queuing only checks `CanStart` (`:331-371`), which for a move means a NavMesh point within 2 m.
3. When the current order finishes, `StartNext` (`:704-711`) calls `TryStart` → `Mover.MoveTo` (`UnitMover.cs:157`). An order that can no longer start is **silently skipped**.
4. Orders are accepted while paused and run once `SimulationTime.IsRunning` is true again.

### T2a. Basic attack

1. A click resolves **any living Health without a `SelectableUnit`** as a hostile (`PointerTargetResolver.cs:46-48`). With fog on, the ray passes through hostiles that haven't been observed (`:63-88`).
2. `AttackCommand(Health)` → `Issue` checks only `HasWeapon` (`:187`) and `IsAttackable` (alive and active, `:911`). **It checks neither side nor knowledge.**
3. `UpdateAttack` (`:775-814`) runs approach, reposition and attack.
4. `UnitAttacker.TryAttack` (`Units/UnitAttacker.cs:193-217`) checks range and line of sight, applies the cooldown from `Time.time` (`:197`), and rolls cover against the **global `UnityEngine.Random`** (`:38`).
5. `Health.TakeDamage` (`Combat/Health.cs:58`) then:
   - raises `Damaged` and `AttackedBy`; the latter triggers `AutoRetaliate.OnAttackedBy`, which calls `Issue(AttackCommand)` (`Units/AutoRetaliate.cs:32-42`);
   - on death, raises `Died` → `CommandableUnit.OnDied` (StopAll) and `DeathMarker`, which creates a root-level disc (`Combat/DeathMarker.cs:31`), then `SetActive(false)` (`Health.cs:80`).
6. `Attacked`/`Missed` → `IntelligenceService.Expose` (3 s). Then `Fired(ShotRecord)` → `UnitPresentation`/`CombatPresenter`, gated by knowledge.

- **Settlement:** none. There is no XP for kills.

### T2b. Ability (Aimed Shot, Recon Scan)

1. `AbilityTargeting` arms the ability; `Act` → `Confirm` → `AbilityCommand.OnUnit` or `AtGround` → `caster.Issue` (`Abilities/AbilityTargeting.cs:233`).
2. `UnitAbilities.Check` (`Abilities/UnitAbilities.cs:147-176`) runs `Knowledge.CanTarget` (`:152`), then `AbilityRules.CheckBasics` (alive, known slot, target, **side** via `:341-347`, cooldown, range), then line of sight.
3. On the first running frame, `RunAbilities` (`CommandableUnit.cs:491-516`) calls `TryUse` (`:182-202`). The cooldown is set as `readyAt = Time.time + …` (`:197`).
4. `Apply` (`:281-311`):
   - Damage → `Hit`, with a cover roll on the same global RNG → `TakeDamage` (`:325`).
   - Reveal → `IntelligenceService.Scan` (`:291`).
5. Events: `Used`, `ShotResolved`/`AreaResolved` → presentation.

- **[O]** Hostiles have no `UnitAbilities` (pinned by `MissionDirectorPlayModeTests`).

### T3a. Equipment change

- **Between missions:** `InventoryRequests.Equip` (`Hud/InventoryRequests.cs:33-39`) → `InventorySession.Equip` (`Items/InventorySession.cs:~140`). This writes the **persistent session directly**, then `EquipmentChanged` → `UnitIdentity.Reapply`.
- **During a mission:**
  1. `SwapIn` (`:42-51`) issues an `EquipItemCommand`.
  2. `UpdateEquip` (`CommandableUnit.cs:569-577`) waits 1.5 s of scaled time, then calls `UnitItems.TryEquip` (`Items/UnitItems.cs:176-188`).
  3. That calls `InventorySession.EquipInMission`, which changes only the working copy.
  4. `UnitIdentity.Apply` then updates `Health.SetMax`, `UnitMover.SetSpeed`, `UnitAttacker.ApplyEffective`, `UnitAbilities.SetModifiers` and `UnitWeaponVisual.Show`.
- **A writer hidden in a read path:** `OperativeLoadout.Resolve` drops dangling equipment references (`Items/OperativeLoadout.cs:31-37`) when the panel builds its view. This is already noted in decision 043.

### T3b. Loot pickup

1. **Generation:** `LootPlacer` uses `SeededRandom.ForLoot` / `ForLootContents` (`Mission/SeededRandom.cs:38-45`) → `LootContainer.Initialize` (`Items/LootContainer.cs:47`). The contents are created with **client-generated GUIDs** (`Items/ItemInventory.cs:96`, `ItemEntry.cs:18`).
2. **Search:** the interact path → `MissionInteractable.Advance` (`Mission/MissionInteractable.cs:90-110`) → `Completed` → `LootContainer.OnCompleted` (`:93`) marks it searched and raises `OpenRequested`, which opens the panel.
3. **Take:**
   1. `InventoryRequests.Take` (`:84-96`) issues a `CollectCommand`.
   2. `UpdateCollect` (`CommandableUnit.cs:606-634`) re-validates every frame.
   3. `UnitItems.TryCollect` (`:235-282`) calls `ItemTransfer.Move` from the container into the working bag. Stacks get new instance ids when they merge.
4. **Settle:**
   1. `MissionFinished` → `SquadInventory.HandleMissionFinished` (`Items/SquadInventory.cs:140-144`) → `InventorySession.Settle` (`:96-105`).
   2. On success it does `Session = Working`, a **replacement of the whole state**; otherwise it discards the working copy.
   3. Duplicates are prevented only by `ActiveMissionId`, which lives in memory.

### T4. Recon reveal, loss of visibility, last-known position

- **Reveal:**
  1. `IntelligenceService.Scan` (`Intelligence/IntelligenceService.cs:263-275`) reveals the area and its devices and adds a pulse until `Time.time + seconds`, then runs a pass.
  2. Passes run every 0.2 s of scaled time (`:174-180`). `RunPass` (`:242-256`) does `BeginPass` → `ObserveFromFriendlies` (range plus a raycast to each hostile, `:277-309`) → cameras → pulses and exposures → `EndPass`.
- **Loss of visibility:**
  1. `MissionIntelligence.EndPass` (`:129-166`) demotes an enemy that wasn't seen this pass from Observed to Discovered and keeps its `LastPosition`.
  2. `CanTarget` (`:186`) then turns false, and `TryLastKnown` (`:192`, model `:201-208`) supplies the marker position.
- **Consumers:** `PointerTargetResolver`, `UnitAbilities.Check`, `CompanionAI` assist, the HUD builder and `HostilePresenter` (`forceRenderingOff`, `:51,82`).
- **Lifetime:** knowledge is keyed by `Health` references (`MissionIntelligence.cs:31`) and thrown away by `Clear()`.

### T5. Mission initialization, extraction, failure, exit

- **Initialization:**
  1. Deploy → `InventoryRequests.DeployNew` (`:100`) → `MissionDirector.GenerateNew`, with the seed from `Environment.TickCount` (`Mission/MissionDirector.cs:154`).
  2. `Run` (`:176-217`) tears down, yields, sets `InstanceId = Guid` (`:187`) and calls `SquadInventory.BeginMission` (`:189`).
  3. Each attempt (`:244-376`) does layout, placement, build, bake, validation, cover, spawn, runtime, intelligence, then the camera.
- **Advancing:** `MissionDirector.Update` → `MissionRuntime.Tick` (`Mission/MissionRuntime.cs:90`).
- **Extraction:** at least `extractionUnits` living squad members within 2 m of the zone → `Finish(Success)` → `MissionFinished`, which goes to:
  - `SquadRoster`, which gives XP to **every member, dead ones included** (`Operatives/SquadRoster.cs:162-169`, `190-194`);
  - `SquadInventory.Settle(true)`.
- **Failure:** the whole squad is dead (`:94`) or a required objective failed → `Settle(false)`, which discards.
- **Exit and cleanup:** `Teardown` (`:400-415`) calls `AbortMission`, then `ResetSystems` (`:418-441`, including `pause.Resume()` at `:440`), then destroys the root, the NavMeshData and the death markers. `TacticalPause.OnDisable` resumes time.
- **Missing behaviour:**
  - There is no abandon or retreat order.
  - There is no handling for disconnect, timeout or interruption, and nothing is persisted: quitting loses the roster and inventory.
  - Play continues after Success or Failure (decision 033).
  - "Deploy (same seed)" rebuilds an identical mission, including identical loot.

---

## 3. Authority table

| State | Current writers [O] | Proposed owner | Required boundary |
|---|---|---|---|
| Unit position/rotation | `UnitMover` (MoveTo/Steer/Stop), `CommandableUnit.FaceTowards`, `UnitAbilities.FaceTowards`, `MissionSpawner` placement/Warp | Mission server | Clients send orders and intents only; transforms are replicated and interpolated; client NavMeshAgents are disabled |
| Move intent | `DirectControlInput` (local) | Mission server, per connection | Intent message with sequence number and expiry; server clears it when stale |
| Health and death | `UnitAttacker.TryAttack`, `UnitAbilities.Hit`/Heal, `UnitItems.TryUse`, `UnitIdentity` (SetMax), the preview panel | Mission server | No client mutation path; health replicated read-only |
| Attack and ability cooldowns | `UnitAttacker.nextAttackTime`, `UnitAbilities.readyAt` (`Time.time`) | Mission server | Remaining time replicated for display |
| Orders and queues | `Issue`, called by `PlayerCommandInput`, `GroupOrders`, `AbilityTargeting`, `InventoryRequests`, `HudRequests`, `EnemyAI`, `CompanionAI`, `AutoRetaliate` | Mission server | Request envelope (player, unit net id, attempt id, sequence) → server gate → existing `Issue` |
| Selection | `UnitSelection` (input, HUD, spawner) | Client only | Never sent as authority; orders name units explicitly |
| Controlled character and follow flag | `ActiveCharacter` (input, HUD, spawner); read by `CompanionAI` | Mission server, per player | Explicit "take control" and "toggle follow" requests (they change simulation) |
| Effective operative stats | `UnitIdentity` from roster and equipment | Mission server, from the backend's attempt snapshot | Computed on the server only |
| XP and advancement picks | `SquadRoster` (on `MissionFinished`), F10/F11 (`OperativeDeveloperInput.cs:61,67`), `OperativePanelView.cs:86` | Backend | XP computed from a verified result; picks through an authenticated API; developer writers disabled in client builds |
| Persistent inventory (stash, loadouts) | `InventorySession.Equip/Unequip/ToStash/FromStash/EquipFromStash` via the UI, `SeedStarter`, `Settle` (replace), `OperativeLoadout.Resolve` | Backend (PostgreSQL) | API with version checks; loadout locked or snapshotted at deploy |
| Mission working inventory | `UnitItems.TryUse/TryCollect/TryEquip` → `EquipInMission` | Mission server (provisional) | Never committed by a client; becomes a result manifest |
| Loot contents | `LootContainer.Initialize` (seeded), `ItemTransfer` in `TryCollect` | Mission server | Contents never sent before the squad has searched the container |
| Objectives, interaction progress, phase | `MissionRuntime.Tick`/`Evaluate`, `MissionInteractable.Advance`/`Release`, `LootContainer` searched flag | Mission server | Phase and objective view replicated |
| Knowledge / intelligence | `IntelligenceService` passes, `Scan`, `Expose`, briefing, camera terminal; F12 truth view, F5 preset | Mission server, per squad or player | Server-side model drives which objects each client receives; no truth view on clients |
| Seed, layout, plans | `MissionDirector` (clock seed, Inspector, F5/F6/F7) | Backend assigns; mission server generates | The seed never reaches a client |
| Mission instance id | `MissionDirector.InstanceId` (GUID per generation) | Backend mints the mission-attempt id | Assignment record binds account, attempt and server |
| Rewards (XP, loot commit) | `SquadRoster`, `SquadInventory` on a local event | Backend settlement | Result signed by the mission server; idempotent per attempt id |
| Simulation time and pause | `TacticalPause`, called by `PlayerCommandInput`, `HudRequests`, `ResetSystems`, `OnDisable` | Mission server (pause policy) | Pause requests checked against policy; wall-clock timers kept separate |
| Cover claims | `UnitCover` (the only writer) | Mission server | Replicated for display |
| Randomness | `UnityEngine.Random` for hit rolls; `SeededRandom` for layout; `CosmeticRandom` for effects | Server-side random stream per mission attempt for combat; cosmetic stays on the client | Never sent to clients |

---

## 4. Findings

**F1. The command boundary has no caller identity, ownership or knowledge check.**
- [O] `Issue` (`CommandableUnit.cs:179`) accepts any order for any unit, hostiles included.
- [O] An `AttackCommand` is checked only for alive, active and weapon (`:187`, `:911`). There is no side check, so attacking a friendly is accepted, and no knowledge check.
- [O] Side and fog filtering live in the input layer (`PointerTargetResolver.cs:46-48, 79-88`). Abilities, by contrast, do check side and knowledge (`UnitAbilities.cs:152, 341-347`).
- [I] A network layer that forwards requests straight to `Issue` could attack friendlies or unseen hostiles, or order hostile units. Having commands and events is **not** network readiness. A server-side gate in front of `Issue` is required. It is small, and leaves AI paths unchanged.

**F2. Commands hold object references and carry no id or sequence number; most entities have no stable network id.**
- [O] Commands reference `Health`, `CoverLocation`, `MissionInteractable`, `LootContainer` and `AbilityDefinition` directly (`UnitCommands.cs:13-172`).
- [O] Units are named `FriendlyUnit_n` / `HostileUnit_n`, and cover names are labels, not keys (decision 023). `AbilityDefinition` and `CombatArchetype` have no id.
- [O] Item definitions, operative definitions and advancement choices do have stable string ids.
- [I] This needs network ids, transfer objects for commands, and a resolver on the server.

**F3. The mission simulation runs without a camera, HUD, input, presenters or a GPU, in the Editor.**
- [O] `MissionRig` (`Tests/PlayMode/TestSupport/MissionRig.cs:28-60`) has no camera by default, no HUD, no input and no presenter. It still generates, spawns, ticks the runtime and settles inventory.
- [O] The camera, ability targeting, intelligence and presentation entries in `MissionSystems` are optional (`MissionSlots.cs:28`).
- [O] Under the Null graphics device, 43/43 and 46/46 tests passed, including the full scene.
- [R] A Linux dedicated-server player build is unverified (the module is missing), as are `UNITY_SERVER` stripping, audio-free runtime and frame pacing. There is no `targetFrameRate`, so a server would run uncapped.
- [O] Coupling to clean up for a server:
  - The single assembly references InputSystem, UI and URP.
  - The scene's `Systems` object mixes simulation components with input and debug views.
  - The director calls `FrameCamera` and `DevicePresenter.AttachTo`.
  - The spawner adds `HostilePresenter` whenever intelligence is wired.
  - The unit prefabs carry `AttackLineView`, `CommandQueueView` and `HitFlash`.
  - `FogPresenter` uses `Camera.main` and URP camera data (`FogPresenter.cs:228-231`).

  All of these check for null, so it is cleanup, not a rewrite.

**F4. Animation and presentation do not affect gameplay.**
- [O] Root motion is overridden off on `Darius_Visual` and `EnemyUnit_Visual` (`m_ApplyRootMotion: 0`), and the Darius clip metas have `events: []`.
- [O] `UnitAnimationDriver` only reads gameplay state.
- [O] Presentation records are raised after damage is resolved, and cosmetic randomness is isolated (decision 044, `PresentationSourceAuditTests`).
- [I] All of this can be kept.

**F5. Process-wide state.**
- [O] `Time.timeScale` is global. `SimulationTime.IsRunning` reads it (`SimulationTime.cs:12`), and NavMeshAgents, Animators and physics all scale with it.
- [O] Hit rolls use the global `UnityEngine.Random`.
- [O] Line of sight uses the default physics scene, and the NavMesh is global.
- [O] There are no singletons, no `DontDestroyOnLoad` and no static mutable registries in gameplay code. `FindObjectsByType` appears only in the no-argument `CoverDiscovery.Discover()` (Prototype path) and in the HUD's EventSystem lookup.
- [I] Conclusion: **one mission per server process**. Several missions in one process would need separate physics scenes, NavMesh handling and clocks, which is not a small change.

**F6. Shared assets are not mutated at runtime.**
- [O] Definitions are immutable by design (decisions 025, 036, 043). `ApplyArchetype` copies values onto the unit.
- [O] A search of `Scripts/` found no runtime writes to ScriptableObjects.
- [R] Not proven exhaustively.

**F7. Persistent state has several local writers, some of them UI or developer paths.**
- [O] XP is written on a local event. The F10/F11 developer keys write XP and reset progression.
- [O] Advancement picks come straight from the IMGUI `OperativePanelView`.
- [O] Inventory edits call `InventorySession` on the persistent session directly.
- [O] `Resolve` changes equipment while the panel is only reading it.
- [I] Every one of these must become a backend request or be removed from client builds.

**F8. Settlement is triggered by a client event and replaces the whole state.**
- [O] `Settle` replaces the session with the working copy (`InventorySession.cs:96-105`). Duplicate protection is the in-memory `ActiveMissionId`.
- [O] The XP award has no id-based protection; it relies on `MissionFinished` firing once.
- [O] On success, dead operatives' bags are committed. On failure, consumables that were used are given back (prototype policy, decision 043).
- [O] Items have no record of where they came from; their ids are client GUIDs.
- [I] The backend needs a **result manifest**: consumed instance ids and quantities, gained items with server-minted ids, per-operative outcome and XP inputs. It should be applied in one transaction keyed by the attempt id, and accepted only from the assigned mission server.

**F9. Identity is not account-scoped.**
- [O] The operative id *is* the authored definition id (`Data/Operatives/Definitions/*.asset`, e.g. Darius `6f1d2a40-…`; `OperativeDefinition.cs:19`), so every account would share it.
- [O] Item instance ids and the mission instance id are client GUIDs.
- [O] There is no account, mission-attempt or settlement id.
- [I] The backend must mint account-scoped operative ids that reference the definition id. Keep the definition, item and choice ids as catalogue keys.

**F10. All hidden information can be derived from the seed, and the client holds the full truth.**
- [O] The layout, hostile spawns, guards, terminal, cameras, crate positions **and crate contents** all come from `SeededRandom` streams of (seed, attempt) (`SeededRandom.cs:20-45`; `MissionDirector.cs:244-280`).
- [O] Hostile objects exist on the client with rendering forced off (`HostilePresenter.cs:51,82`).
- [O] Container contents are fields on each client's container (`LootContainer.cs:22`).
- [O] Death-marker discs are not hidden (a known decision 037 limitation), and the F12 truth view exists.
- [I] Clients must never receive the seed, the spawn data or the objective, security and loot plans. Unobserved hostiles must not be sent at all, and contents only after a search. The knowledge model must run on the server and decide what each client receives. Whether the floor plan itself is secret is a product decision (§6).

**F11. Pause and time.**
- [O] `TacticalPause` is the single writer of time scale, which is a good seam.
- [O] Toggles come from input, the HUD pause chip, `ResetSystems` (Resume) and `OnDisable`. Opening the inventory never pauses.
- [O] Scaled time drives cooldowns, interaction progress, gear swaps, intelligence passes, exposures, scan pulses, AI thinking and walk-stall timers.
- [O] **There is no wall-clock concept at all**: nothing for connection, expiry or timeouts.
- [I] For a solo mission, the server can pause its own process. Reconnect windows, attempt expiry and pause budgets must use real (UTC) time, never `Time.time`.

**F12. Commands are mostly re-checked when they run; stale and duplicate requests are not handled.**
- [O] Re-checks: a move is re-snapped at start; an attack is checked every frame; an ability gets the static check when queued and the full check when it runs (`:491-516`); interact and collect are checked every frame; a use is fully checked when it runs; a swap is checked again when its time is up.
- [O] Gaps:
  - An attack keeps chasing a target that is no longer observed (decision 037).
  - Only same-target attack and interact orders are coalesced (`:220-232`).
  - There is no sequence number, so a delayed old `Replace` could wipe newer orders.
  - Malformed commands **throw** from constructors and from `Issue` (`:208-209`).
- [I] The server gate must reject bad requests without throwing. It must order requests by a per-connection sequence, de-duplicate by command id, and recheck knowledge for player-issued attacks.

**F13. The "controlled character" changes simulation.**
- [O] `CompanionAI` steps aside for the controlled unit, parks units on a switch and follows the leader (`CompanionAI.cs:191-205`; decisions 018 and 022). `ActiveCharacter.RefreshEligibility` runs every frame.
- [I] The controlled unit and the follow flag must be server state per player. The squad logic assumes one leader.

**F14. There are two sides and no ownership.**
- [O] `Encounter` holds two lists, and `UnitSelection.Roster` holds every friendly for the single local player.
- [I] The proof needs per-connection sets of owned units on the server.

**F15. The simulation is frame-driven.**
- [O] All simulation runs in `Update` with a variable `deltaTime`; there is no `FixedUpdate`. Physics auto-simulates with `AutoSyncTransforms` off.
- [I] Server authority doesn't need determinism, but the server needs a frame cap. Without client prediction, direct control will feel delayed by the network round trip.

**F16. Toolchain and scope gates.**
- [O] There is no Linux or dedicated-server module, no build script and no CI.
- [O] `CLAUDE.md` says "Do not spend time on … multiplayer, networking". It must be amended before any implementation.
- [R] FishNet is untested here on every point that matters:
  - compatibility with Unity 6000.3;
  - server builds on the Null graphics device;
  - per-connection visibility (observer conditions) driven by fog;
  - client NavMeshAgents fighting replicated transforms;
  - tick timing while `Time.timeScale = 0`;
  - licensing.

**F17. Retainable core.**
- [O] These are pure C# and covered by tests: `MissionIntelligence`, `MissionRuntime`, `AbilityRules`, `CommandQueue`, `ItemTransfer`, `InventorySession`, `ProgressionTrack` and the seeded placers.
- [O] The HUD and presentation layers already read gameplay without changing it and are knowledge-gated (decisions 042 and 044, including the fog sweep tests).
- [I] They can be kept on the server, and the HUD can be re-pointed at replicated proxies.
- [I] .NET code can't share these classes directly because they use ScriptableObjects and UnityEngine types, so the backend should re-implement inventory validation.

---

## 5. Prioritized work

**Blockers for the small proof**
1. Install the Linux Build Support (Mono) and Linux Dedicated Server modules. The owner must approve this; it is an environment change.
2. Amend `CLAUDE.md` to allow a networking spike, and record a decision for FishNet versus an alternative. Keep the gate and transfer-object design independent of the library.
3. Add a server bootstrap scene with simulation components only (director, encounter, cover, interactables, intelligence, pause, roster/inventory fed by fixtures) and a frame cap.
4. Add network ids for units and interactables, plus transfer objects for commands and a resolver.
5. Add a **server gate** in front of `Issue` and `SetMoveIntent`, covering:
   - ownership;
   - attempt id;
   - per-connection sequence and command id;
   - side and knowledge for attack, ability, interact and collect;
   - intent expiry;
   - rejection without throwing.
6. Generate missions on the server only. Clients get geometry, never the seed, spawns or plans.
7. Use the server-side intelligence model to decide which hostiles each client receives. Clients show last-known positions as data.
8. Keep container contents on the server; send them only after the squad searches.
9. Make the spawner role-aware: no presenters or debug views on the server, and on clients the units are proxies with AI, agents and simulation turned off.
10. Have the mission server send a result manifest to a disposable settlement endpoint, de-duplicated by a durable unique key.

**Phase 13 requirements:** see the revision in §8.
- Authentication, an account-scoped roster and inventory in PostgreSQL, and an API for loadout edits.
- Mission assignment and attempt records, mission-server credentials, an attempt-bound join ticket, transactional settlement and result retrieval.
- An expiry job that applies the decided default outcome.

**Phase 14 requirements:** recovery after a server crash or expiry, hardened reconnection, an inventory and XP ledger, audit trail, migrations and backups, observability and rate limits, and a decision on whether checkpoints are needed.

**Deferred:** orchestration and fleet management, matchmaking, several missions per process, client-side prediction, PvP, extracting shared code for netstandard, a cross-region edge, and anti-cheat beyond server authority.

---

## 6. Product decisions needed

| Topic | Current behaviour [O] | Options | Recommendation |
|---|---|---|---|
| Solo pause | Unlimited global pause, toggled any time | (a) unlimited server pause; (b) pause budget; (c) slow motion only | (a) for solo. The attempt expiry keeps running on wall-clock time. |
| Shared pause (co-op) | Not applicable | Any player pauses with a budget; a vote; leader only; no pause (slow motion) | Leave shared missions out of the proof. Decide before co-op work. |
| Reconnect | Not applicable | Mission auto-pauses for a window W; continues with AI; units hold position | Solo: auto-pause for W minutes, then apply the interruption policy. |
| Interrupted mission (crash, never returns, expiry) | Nothing persists; a regeneration discards | Treat as Failure; a separate "Abandoned" outcome; restore the loadout | Add an **Abandoned** outcome with its own loss rules, applied idempotently by the backend. |
| Inventory consumption and retention | Failure gives back used consumables; success commits dead operatives' bags; pickups are lost on failure | Consumables always spent; gear lost on death; equipped versus bag rules | Decide explicitly. Don't carry the prototype rules forward by default. |
| Hidden layouts | The client generates everything; map presets exist (Full, Partial, None) | Floor plan known, contents hidden; floor plan streamed region by region | Proof: geometry is known to clients, while hostiles, terminal, cameras, crate positions and contents are hidden. Decide later whether room outlines are secret. |
| Retry | Deploy (same seed) gives the same mission and loot | Same seed allowed; a new seed for every attempt | A new server-chosen seed for every attempt; clients never choose seeds. |
| XP for dead operatives; play after the mission ends | Both allowed | — | Ratify or change these before Phase 13 settlement. |

---

## 7. Smallest client/server proof

**Topology:**
- One Linux dedicated-server process in Docker or a VM, hosting **one** mission attempt.
- Two separate Windows client processes. Client A owns the squad. Client B is a second authenticated account used **only** to test ownership and information boundaries.
- A disposable development backend: a minimal ASP.NET Core API with PostgreSQL in Docker. It has two tables: `attempts(attempt_id PK, account_id, server_id, state)` and `settlements(attempt_id PK, result_hash, payload, applied_at)`.
- Accounts and server keys use static development tokens and an HMAC key, labelled as fixtures. HAProxy is optional for the proof.

**Encounter:** use the existing generator with small private settings: 2 rooms, 1 hostile, no guards, eliminate-hostiles on, terminal off, 1 loot container and fog preset Blind. The squad is Darius plus Kestrel (who carries Recon Scan). Everything runs through existing paths: `Issue`, `SetMoveIntent`, `TryAttack`, `TryUse`, `Scan`, `TryCollect` and `MissionRuntime`.

**Touch points in existing code (targeted):**
- `MissionDirector`: takes its seed and settings from the server bootstrap; skips the camera and device presenters when running as server.
- `MissionSpawner`: attaches presenters and simulation components according to role.
- A new gate component on the server calls the existing `Issue` and `SetMoveIntent`.
- `IntelligenceService` runs on the server and decides what each connection receives.
- `SquadInventory` and `SquadRoster` handlers on the server are replaced by a manifest builder.
- `InventoryRequests` and `PlayerCommandInput` send network requests on the client.

| # | Acceptance check (measurable) |
|---|---|
| A1 | The Linux server build starts in its container and logs "mission ready" with the attempt id in ≤ 30 s. It then runs for 10 minutes with zero `Camera`, `AudioListener` and HUD components (self-check count), with the frame rate capped and memory stable. |
| A2 | Direct control: client A's held input moves Darius on the server, and A sees it with ≤ 150 ms added on the LAN. Shift-queued moves run in order on the server, and the replicated queue matches. |
| A3 | A test harness sends nine bad requests. **All nine are rejected** with a typed reason, the server makes no state change, and nothing throws. The requests are:<br>- a move for a hostile;<br>- an attack on a friendly;<br>- an attack on an unobserved hostile;<br>- an ability on cooldown;<br>- a collect from an unsearched container;<br>- a duplicate command id;<br>- an older sequence number;<br>- a wrong attempt id;<br>- a malformed request. |
| A4 | A basic attack and Aimed Shot resolve on the server and health replicates. No client message type can change health. |
| A5 | Before observation, a scan of what A's client received (spawned objects plus a packet log searched for the true coordinates) finds no hostile, no seed, no spawn coordinates and no container contents. Recon Scan makes the hostile visible to A for 6 s. Afterwards A holds only a last-known marker at the last observed position, and the hostile object is despawned or hidden. |
| A6 | Searching the container sends its contents only when the search completes. Taking items changes only the server's working inventory, and no client message can add an item. |
| A7 | Killing client A mid-mission makes the server apply the reconnect policy (auto-pause). Reconnecting as the same account within the window resyncs units, queues, knowledge and any searched contents, and play continues. After the window, the interruption outcome is applied. |
| A8 | On extraction, the server POSTs a signed manifest (attempt id, server id, outcome, consumed, gained, XP inputs) three times, plus one conflicting payload. **Exactly one row is applied**, and the conflicting payload is rejected and logged. After restarting the API and DB containers, a resend still applies nothing new. Client A gets the same result by attempt id after a reconnect. |
| A9 | Client B, with no ownership: every order for A's units is rejected. A ticket for another attempt is refused. B receives nothing beyond its permitted view (same scan as A5). |
| A10 | The existing EditMode and PlayMode suites stay green against a freshly measured baseline, and new EditMode tests cover the gate's rejections. |

**Gameplay authority versus settlement authority:** the mission server is authoritative for play. The backend is authoritative for what persists. It only accepts results signed by the server **assigned** to that attempt, and a client never submits rewards. The proof's unique key gives durable de-duplication. Anything held in memory is labelled as not surviving a restart.

---

## 8. Proposed revision of Phases 13 and 14

**Phase 13: authenticated between-mission flow and trusted settlement.** The proof's authority model is extended to the whole squad, and the durable minimum is added.

- **Backend:** ASP.NET Core on .NET 10 with PostgreSQL, behind HAProxy TLS, running under Docker Compose.
- **Tables:** `accounts`, `operatives` (server-minted id, `definition_id`, XP, picks), `item_instances` (server-minted id, definition, quantity, owner, location as stash, bag or slot), `loadout_versions`, `mission_attempts` (private seed, server id, loadout snapshot, state, expiry), `settlements` (attempt id unique, result hash, applied at) and `mission_servers` (credentials).
- **Flows:**
  1. Log in.
  2. Edit roster and loadout through the API, with version checks. This replaces local `InventorySession` edits.
  3. Deploy: create the attempt, snapshot or lock the loadout, and issue a join ticket.
  4. The mission server fetches the attempt configuration with its own credential.
  5. Settle one manifest per attempt in a single transaction.
  6. Clients fetch the result.
  7. An expiry job applies the decided interruption outcome.
- **Client:** the loadout panel reads from the API and keeps no local session. Developer writers (F5–F12, XP, regeneration) are compiled out of player builds or gated on the server.
- **Exit criteria:** A1–A10 hold for the full squad. Restarting the backend loses no committed state. Duplicate or conflicting settlements are provably rejected.
- **Decision records:** netcode library; the result manifest format; the outcomes and loss rules; the identity scheme. These supersede the "Phase 14 serializes locally" notes in decisions 036 and 043.

**Phase 14: server persistence and recovery.**
- Recovery after a mission-server crash: heartbeat, expiry, resolution to the decided outcome.
- Hardened reconnection: resync snapshots and edge cases.
- An item and XP ledger with an audit trail.
- Persisting advancement picks.
- Migrations, backups, observability, rate limiting and a static VM server pool.
- An explicit decision on whether mid-mission checkpoints are needed. They are not assumed.

**What can be kept as is:**
- Commands as data, with a single `Issue` entry (006).
- A single owner of time (007).
- The pure mission runtime (033).
- The definition / state / unit split (036).
- The knowledge layer and the reveal API (037).
- The HUD snapshot boundary (042).
- The session / working-copy split for inventory (043).
- Presentation records raised after resolution (044).
- Reproducible seeded generation (useful for server audits and replays).
- The source-audit tests, which can be extended into rules such as "simulation code must not reference Input or UI".

**Missing evidence and the smallest follow-up for each:**
- **Linux server build behaviour:** install the modules and run a one-scene server build spike.
- **FishNet fit:** a short spike covering per-connection visibility, Unity 6.3 and time-scale behaviour.
- **Server CPU and memory for NavMesh baking and passes:** measure in the spike's container.

---

## 9. Follow-up record

### 9.1 Steps after the audit

| Step | Scope | Status |
|---|---|---|
| **Checkpoint A: headless Linux server startup** | Server-only scene and bootstrap, fixture mission, frame cap, identity and readiness logs, bounded move and attack diagnostic, Linux Dedicated Server (Mono) build, VM smoke run. **No networking, no backend.** | Done 2026-10-11 (§9.2) |
| Networking experiment | The §7 proof: library choice (FishNet is a candidate only), server gate, per-connection visibility, two clients. | Not started; needs the owner's authorization and a library decision |
| Phase 13 | Authenticated between-mission flow and trusted settlement (§8). | Not started |

### 9.2 Checkpoint A results (2026-10-11)

Design: decision 045. Run guide: `Docs/DedicatedServer.md`.

**Revision and environment actually tested:**
- branch `checkpoint-a-linux-server`, build revision `7d42506`, packaged as `blackglass-server-linux-7d42506-dirty.tar.gz`;
- `dirty=true` comes from the owner's two uncommitted settings files and the documentation not yet committed at build time; no runtime code was uncommitted;
- SHA-256 `78bb78730777fdc427841f0ebc210956a9b1d473aa3cd76abb02059b7da3cfd8`;
- Editor host: Windows 10 build 19041, Unity 6000.3.25f1 with Linux Build Support (Mono) and Linux Dedicated Server Build Support, installed 2026-10-11 through Unity Hub at the owner's request;
- VM `bg-dev-mission01`: Ubuntu 24.04.5 LTS, kernel 6.8.0-146, Intel Xeon Platinum 8260 at 2.40 GHz, 4 vCPU, 7.9 GiB RAM, no swap.

**Editor validation** (Windows, batch mode, D3D12 device; Editor runs, not a Linux run):
- baseline before changes: EditMode 1249/1249, PlayMode 1354/1354;
- after: EditMode 1271/1271, PlayMode 1365/1365;
- new tests: `ServerOptionsTests` (18), `ServerBuildInfoTests` (4), `ServerDiagnosticPlayModeTests` (4), `ServerMissionPlayModeTests` (6) and `MissionDirectorPlayModeTests.Generate_RecordsTheNavMeshBakeTime`;
- fixture seed 1 generates on attempt 1 (layout hash `cf2e14dece9cc2a6`) and passes the diagnostic: move 1.5 s, attack 3.6 s;
- no client scene, prefab, data asset or project setting changed.

**Linux build** (Windows host):
- `Tools/build-server.sh` → StandaloneLinux64, Server subtarget, Mono;
- an 82.7 MB player and a 30 MB archive;
- 69 s after the first Linux-target import, 13 s on rebuild;
- the active target was restored to Windows Standalone, checked by a separate Unity launch;
- a refused build packaged nothing.

**Linux runtime** (`bg-dev-mission01`):
- **Diagnostic:** exit 0.
  - `UNITY_SERVER=yes`, `platform=LinuxServer`, `graphics=Null`.
  - Same layout hash as the Editor.
  - Self-check: 0 cameras, audio listeners, canvases and event systems; 10 unit views disabled.
  - Move PASS in 1.4 s, travelling 5.0 m; attack PASS in 3.6 s, health 60→45.
  - `navMeshBakeMs=16.6`, `generationMs=104`.
  - It also passed when started from `/`.
- **Smoke run** (600 s, normal mode, idle fixture mission; initial evidence only, not a stability proof):
  - launch to Ready ≤ 5 s by wall clock (5 s sampling); `navMeshBakeMs=15.4`, `generationMs=99`;
  - CPU after Ready: mean 1.4 %, max 13.0 % of one core;
  - heartbeat FPS 60.0 on all 10 lines, so the frame cap held;
  - RSS peak 102.5 MB, steady mean over the last 5 minutes 101.4 MB;
  - 0 exception or error lines;
  - stopped by SIGTERM, exit status 0;
  - the VM had 729 MB in use before the runs.
- **Open:** RSS rose steadily from 98.6 MB at 1 minute to 102.5 MB at 10 minutes, about 0.43 MB/min, while the managed heap stayed at 1.1–1.3 MB. This is native memory growth, and 10 minutes cannot tell allocator warm-up from a slow leak. A longer run is needed before any stability claim.
- **One warning line per run:** "Trying to access a shader but no shaders were included in the build because Dedicated Server Optimizations is enabled". The inferred source is the default material `GameObject.CreatePrimitive` assigns to the mission's cubes; this is not verified. It does not affect the simulation.

**What this resolves:**
- F3 [R] "A Linux dedicated-server player build is unverified": verified for startup, generation, NavMesh bake, movement and a basic attack. `UNITY_SERVER` builds, the Null graphics device and an audio-free runtime were exercised, and frame pacing was measured (above).
- F16 [O] "no Linux or dedicated-server module, no build script": modules installed, `Tools/build-server.sh` added. There is still no CI.

**Still unverified:**
- all networking;
- multiple processes;
- disconnect and reconnect;
- runs longer than 10 minutes, including the RSS trend above;
- a mission under load; the smoke run was idle;
- abilities, intelligence, inventory and loot in the server build (not in the fixture);
- IL2CPP;
- running as a non-root service user;
- Docker.
