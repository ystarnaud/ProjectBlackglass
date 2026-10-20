# Phase 13 Stage 12: Server Mission from a Snapshot, with Abilities — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status (2026-10-20):** **plan revision 1.1, for the owner's review. Not authorized for implementation.** Written on the plan branch `phase13-stage12-plan` after Stage 11's merge (`075fcd4`). Revision 1.1 applies an independent review of revision 1, the self-test and its manifest (1 Critical, 7 Important, 23 Minor; "Plan review" at the end). Nothing here has run on either VM. Each VM phase (the outbox self-test, the B24 demonstration, Unity under B24) needs the owner's approval of its own host manifest first; the outbox self-test's is `2026-10-20-phase-13-stage12-outbox-selftest-manifest.md`. "Decisions for the owner" lists what this plan can't settle alone.

**Goal:** the Unity Linux dedicated server can run one authoritative solo attempt from a backend admission snapshot: the backend's operatives, inventory and picks; a deterministic mission whose loot containers keep one entry per rolled item (B25); a ready report the real backend accepts; abilities and combat resolved on the server; item events feeding Stage 11's ledger; the outcome frozen at the first terminal event, with a format-v1 manifest built from it. On the mission VM, a mission unit can bind and listen only on its own slot's TCP port (B24), demonstrated there before any real Unity mission workload runs.

**Architecture:** a pure, library-free attempt model in `Blackglass.Backend.Core` (`AttemptStart` from the session response, `AttemptLoot` for the non-merging fill, `ReadyReportBuilder`), consumed by a thin Unity layer: `MissionDirector.GenerateForAttempt`, `LootContainer.InitializeForAttempt`, `SquadRoster.InitializeFromSnapshot`, `SquadInventory.InitializeFromSnapshot`, and one server component, `AttemptSession`, which owns the `MissionItemLedger` and turns `UnitItems` detail events into ledger events. The attempt arrives through an `IAttemptSource` seam: Stage 12 has only a fixture source (Editor and fixture builds); Stage 13 adds the HTTP source. B24 is a root-owned Landlock launcher in the mission release plus systemd's IP address filter, proven test-first in a container and then on `bg-dev-mission01`.

**Tech stack:** Unity 6000.3.25f1 (Mono, Linux Dedicated Server subtarget), NUnit EditMode/PlayMode, .NET 10 / xUnit for the backend cross-checks, Linux Landlock ABI 4 (kernel 6.8 on the mission VM), systemd 255 (`-BPF_FRAMEWORK`), Python 3 (the VM's system interpreter, already used by the Stage 10 stub) for the launcher if the owner picks option L1.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` (revision 2.14: §14.2–§14.5, §16.3, §18.2 B24 and B25, §0.14). Staged outline: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md`, Stage 12. Stage 11's plan (`2026-10-19-phase-13-stage11-unity-backend-core.md`, "Final owner rulings") holds B25's invariants and its nine required tests verbatim.

## Global Constraints

- **B24 before any real Unity mission workload on `bg-dev-mission01`:** a mission process binds and listens only on its slot's TCP port `portBase + NN` (7800–7807); no other TCP port on any address (not 127.0.0.1:5081, not another slot's, not port 0); the port unreachable from off the host; explicit UDP binds denied or a residual the owner accepts in writing. nftables alone is never proof. A systemd setting counts only once a probe shows it enforced on the VM.
- **B25:** one successful `LootItemPlan` roll = exactly one `ItemEntry` = exactly one registered `PoolLine`; lines consecutive from 0 per container; mapping fixed before gameplay; definition and quantity are the roll's; pool and SHA-256 hash from those entries; the real `LootPoolValidator` accepts them; a roll that can't be one entry (stackable: 1..`maxStack`; non-stackable: exactly 1) fails generation with an explicit error. Never merge, split, truncate or drop to make a valid ready report.
- **Unchanged:** `LootContainer.Initialize` and every offline path (`ProceduralMission`, `Prototype`, their HUD, input and gameplay); offline loot generation, presentation and deterministic hashes (`LootPlan.Hash`, layout hash); the backend's `LootPoolValidator`, `ManifestValidator` and stored catalogues; the Stage 11 ledger architecture; `Backend/contracts/manifest-v1` and `ledger-v1` bytes; client scenes and prefabs; FishNet messages (Stage 14 owns them).
- **Out of Stage 12:** HTTP to the backend, process credentials, the start hold, heartbeats, the `recorded` notice and outbox publication of real results (Stage 13); tickets, new gate requests and ownership lists (Stage 14); reconnect (Stage 15); co-op (one participant only; a second is refused).
- **VM rules:** no change to the installed Stage 10 templates, agent, tunnel, SSH or systemd services except under an owner-approved host manifest; never modify `/opt/blackglass/server`; the SSH tunnel stays up; no VM IP address or key name in the repository; remote output through the address-redaction `sed`.
- **Repository rules:** stage by explicit paths, never `git commit -a`; never commit the owner's `Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset` or `.claude/`; no generated run evidence or credentials; commit trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Baselines (main after the Stage 11 merge):** EditMode 1835, PlayMode 1434, backend domain 790, agent 254, integration 1773, Linux outbox 8. Every existing test stays green; the counts only grow.

## Review Focus

1. **A roll whose quantity exceeds the item's `maxStack`** (a loot-table entry with `maxQuantity > maxStack`, or a later catalogue edit): generation must fail with `loot_roll_exceeds_stack` before ready, never truncate. Task 3 pins it with a forced fixture.
2. **A deposit into a loot container:** no gameplay path deposits into one today (`UnitItems.TryCollect` is the only container mutation), but `LootContainer.Contents.Add` stays public. Attempt-mode containers record their registration, and any later addition is caught by the ledger's `VerifyContainer` at the freeze (a `ledgerMismatch`, never `ok`). Task 3 pins it with a forced bypass.
3. **A snapshot operative whose `definitionId` is unknown to this build, or a participant count other than one:** the attempt is refused with an explicit code (`unknown_operative_definition`, `unsupported_participants`), the mission never generates. Task 2.
4. **Events after the freeze** (an ability kill or a collect in the same frame as extraction): they change no facts; the manifest is the freeze's. Task 6.
5. **Landlock's listen autobind:** `listen()` on an unbound TCP socket picks an ephemeral port without a `bind()`, which ABI 4 doesn't mediate. The VM probe measures it; if it succeeds, the residual goes to the owner (Decision S12-D3). Task 10.

---

## Decisions for the owner

| # | Question | Options | Recommendation |
|---|---|---|---|
| S12-D1 | **The B24 launcher's form.** Landlock is applied by the process itself or by a launcher it execs from. | **L1:** a root-owned Python 3 launcher (`landlock-exec`, about 120 lines, `ctypes` syscalls 444–446, then `os.execv`) using the VM's existing system interpreter: no new package, no compiler. **L2:** a small static C launcher built in a digest-pinned GCC container on the services VM's test runner: a new toolchain image. **L3:** Unity applies Landlock to itself at startup through `syscall()`: no launcher, but the engine's own initialization runs unrestricted first, and the control lives inside the code it constrains. | **L1.** It needs nothing installed, it's reviewable in one file, and the restriction exists before Unity's first instruction. L2 if the owner prefers not to depend on the system Python. |
| S12-D2 | **Also restrict TCP connects** (Landlock `CONNECT_TCP`, one rule: 5081, the backend tunnel). | Yes: a compromised mission can connect to nothing else on TCP. No: only binds are restricted (B24's literal scope). | **Yes, if the transport check (Task 11) shows Unity opens no other TCP connection.** It costs one rule. |
| S12-D3 | **What Landlock ABI 4 can't close** (each measured in Task 10, never assumed): (a) `listen()` on an unbound socket autobinds an ephemeral port (32768–60999 per `ip_local_port_range`; never 5081 or a slot port; unreachable off-host under the IP filter); (b) UDP binds; (c) other stream protocols: MPTCP (`IPPROTO_MPTCP` 262, enabled by default on Ubuntu 24.04, and an MPTCP listener accepts plain TCP clients) and SCTP, if 6.8's Landlock TCP hooks don't mediate them. | (a): accept, recorded (no upstream kernel offers a Landlock listen right as far as is known; verify). (b): **U1:** the launcher's seccomp filter refuses `socket(AF_INET/AF_INET6, SOCK_DGRAM)` (masking `SOCK_NONBLOCK`/`SOCK_CLOEXEC`, checking the arch and the x32 bit), if Unity needs no UDP; **U2:** accept, recorded. (c): if not refused with `EACCES`, the same seccomp filter refuses `IPPROTO_MPTCP` and `IPPROTO_SCTP` sockets, or `net.mptcp.enabled=0` (a host setting: owner decision). | (a) accept, recorded; (b) U1 if Unity needs no UDP; (c) the seccomp rule. With U1 or (c), the launcher is L2 (C) rather than Python: a hand-assembled BPF filter is too much for `ctypes`. |
| S12-D4 | **The attempt's fixture source** in Stage 12 (no backend yet). | Editor and fixture builds only (`BLACKGLASS_NET_FIXTURE`), never a release build, so a release server can't start a fake attempt. | As stated. |
| S12-D5 | **Order of the VM phases.** | Outbox self-test (manifest attached) → B24 probe demonstration (Task 10, its own manifest) → Unity under B24 (Task 11: the transport check and the fixture attempt, its own manifest). Each runs only after the owner approves its manifest. | As stated. |
| S12-D6 | **Where the launcher lives.** | In the swappable mission release (`/opt/blackglass/mission/current`), or at a fixed root-owned path beside `slot-guard` (`/opt/blackglass/agent/current/landlock-exec`), so the control is never swapped with the workload it constrains. | Beside `slot-guard`. |
| S12-D7 | **The self-test prepared ahead** (Task 1) on `phase13-outbox-selftest`: it brings forward the gameplay assembly's first reference to `Blackglass.Backend.Core` (Stage 11 reserved it for Stage 12) and adds `OutboxDirectories.EffectiveUserId()` to the core. | Keep it separate until the VM run, then merge it into Stage 12's branch; or treat it as Stage 12's first task. | Keep it separate. |
| S12-D8 | **B24's off-host positive control** (Task 10): a probe listener on 0.0.0.0:7803 in a unit **without** the IP filter, reached from `bg-dev-services01`, to show the network path exists before the filtered run shows it closed. It briefly exposes one probe port on the development network. | Approve it in Task 10's manifest, or accept the weaker evidence (the VM's own-address test only). | Approve it, bounded to the probe's lifetime. |

---

## File structure

**Created (Unity core, `Assets/_Project/Scripts/Backend/Core/`, library-free):**
- `Attempt/AttemptStart.cs`: the validated attempt (session id, seed, template, catalogue version, one participant's snapshot) built from `SessionRegisterResponse`; refusal codes.
- `Attempt/AttemptLoot.cs`: the roll-fit rule and the pool registration from per-container roll lists (`LootRoll`, `AttemptLootError`).
- `Attempt/ReadyReportBuilder.cs`: `ReadyRequest` from the layout hash, the table version, objectives and the registered pool.

**Created (Unity runtime):**
- `Scripts/Server/AttemptSession.cs`: owns the ledger for the attempt, binds `UnitItems` events, freezes at the first terminal event, builds the manifest.
- `Scripts/Server/IAttemptSource.cs`, `Scripts/Server/FixtureAttemptSource.cs` (fixture builds and Editor only).
- `Scripts/Server/ServerClock.cs`: elapsed wall time for `serverElapsedMs`.
- `Scripts/Server/OutboxSelftest.cs`: the `-outboxSelftest` diagnostic (no mission, no network, no credential): prepared ahead on `phase13-outbox-selftest` (Task 1).

**Modified (Unity runtime), each additive:**
- `Items/LootContainer.cs`: new `InitializeForAttempt`; `Initialize` untouched.
- `Items/ItemInventory.cs`: no change (`TryAddEntry` is already internal to the assembly).
- `Mission/MissionContent.cs`: an attempt-mode branch of `AddLootContainers` calling `InitializeForAttempt`.
- `Mission/MissionDirector.cs`: `GenerateForAttempt(AttemptStart)`, the post-fill container read.
- `Operatives/PersistentOperativeState.cs`: an internal `Restore(id, experience, choices, track)` factory.
- `Operatives/SquadRoster.cs`: `InitializeFromSnapshot`.
- `Items/SquadInventory.cs`, `Items/InventorySession.cs`: snapshot initializers.
- `Items/UnitItems.cs`: additive detail events (`CollectedDetail`, `UsedDetail`, `EquippedDetail`).
- `Server/ServerOptions.cs`, `Server/ServerBootstrap.cs`: `-attemptFixture <path>` (the self-test options are on the self-test branch).
- `Editor/Server/ServerSceneBuilder.cs` and the regenerated `ServerMission.unity`.

**Created (backend):**
- `Backend/contracts/attempt-v1/`: generated attempt fixtures (snapshot, pool, objectives, the expected manifest), written by a Unity export and pinned by a staleness test, like the catalogue export.
- `Backend/tests/Accounts.Domain.Tests/Settlement/AttemptFixtureTests.cs`: the real `LootPoolValidator` and `ManifestValidator` over every fixture.
- `Backend/deploy/mission/landlock-exec` (L1) and its tests; `blackglass-mission-slot.service.in` and `verify.sh` changes (rendered, never installed by this plan's code tasks).
- `Backend/tests/MissionHost.Agent.Tests/Deploy/LandlockLauncherTests.cs` (template rendering) and `Backend/tests/UnityCore.Linux.Tests/LandlockProbeTests.cs` (the launcher in a container, `vm-test.sh --unity-core`).

**Tests (Unity):** `AttemptStartTests`, `AttemptLootTests`, `OnlineContainerFillTests`, `ReadyReportBuilderTests`, `SnapshotRosterTests`, `SnapshotInventoryTests`, `AttemptSessionTests` (EditMode); `AttemptMissionPlayTests`, `AttemptAbilitiesPlayTests` (PlayMode); `AttemptFixtureExportTests` (staleness). `OutboxSelftestTests` is already on the self-test branch.

---

## Task 1: The Linux Unity outbox self-test (prepared ahead, on its own branch)

**Prepared, not part of Stage 12's implementation authorization.** The owner asked for a separate, minimal Linux Unity runtime self-test of Stage 11's native outbox worker, with an exact host manifest naming the binary and its revision. That needs the binary, so the self-test is implemented test-first on its own branch, `phase13-outbox-selftest` (from `main` `075fcd4`, not merged, not run on any VM). Its host manifest is `2026-10-20-phase-13-stage12-outbox-selftest-manifest.md`. If the owner prefers it as an ordinary Stage 12 task, the branch is discarded or merged into Stage 12's branch; nothing else depends on it.

**As built on that branch:**
- `Scripts/Server/OutboxSelftest.cs`: every phase first refuses euid 0 (`uid`). Phases `publish` (A written then published; a new worker finds A already published; B a conflict; then, with no worker alive, a fresh `.tmp` renamed onto the `.ready` must fail with `rename_failed`, errno 17: the native `RENAME_NOREPLACE`), `hold` (A durable as `.written`, never published, waiting to be killed) and `resume` (B a conflict against the held A; A published once). One fresh `OutboxWorker` per check, so every check after the first meets the files on disk. Two bounds: time inside `Step` (`main_thread`, at most 50 ms, excluding the steps that open the directory by design) and the longest frame interval while a job is in flight (`frame_loop`, at most 250 ms). Any exception fails the phase; a phase passes only with exactly its checks (publish 7, resume 5). The bytes are manifest-v1's `01-minimal` (a test pins them to the vector file) and a variant with `outcomeSequence` 8813.
- `ServerOptions`: `-outboxSelftest <publish|hold|resume>`, `-selftestParent` (default `/var/lib/blackglass-selftest`), `-selftestId`.
- `ServerBootstrap`: `Start` calls `Begin(args)`; with `-outboxSelftest`, `Begin` creates the self-test and returns before the director, the generation and `-net`; `Update` calls `Tick(now)`, which steps the self-test and exits 0 or `SelftestFailed = 3`. The directory opener refuses any parent naming `blackglass-outbox` (`production_outbox`), then uses `OutboxDirectories.OpenForServer($STATE_DIRECTORY, parent, id)`.
- `Blackglass.asmdef` now references `Blackglass.Backend.Core` (the first runtime reference; Stage 12 needs it anyway).
- `OutboxSelftestTests` (19, EditMode; EditMode 1854/1854 at `cc053b5`, PlayMode 1434/1434 at `d3ab09d`, not re-run at `cc053b5`; over the portable directory): the vector pin, publish (7 checks, the `.ready` never replaced), hold then resume, resume without a held result, root refused before any file operation, a native-library exception and a failing uid read never passing, a long frame, a refused location, unknown and missing phases, the options, a self-test option without a phase never running a mission, the production-outbox refusal, and the bootstrap running the self-test with no director and `-net` ignored (exit 0) or failing it (exit 3).
- `OutboxDirectories.EffectiveUserId()` (core, public, linux-x64 only): `geteuid` for the root refusal. **Drafted, not yet applied or tested** (the session's Unity runs were stopped for low system memory): `ServerBuildInfo.ProvenanceLines` and `ServerBuild`, so `build-info.txt` records `dirty` from the source before the build, with `dirtyPaths` and `changedByBuild`. A clean worktree build of `d3ab09d` still stamped `dirty=true` because `dirty` is read after `BuildPlayer`, which rewrites settings it switches and restores (the worktree was clean before and after). Until this lands, the manifest's binary can't satisfy `dirty=false`. `Tools/LinuxServer/outbox-selftest/selftest-vm.sh`: the VM side the manifest runs.
- `CoreSourceAuditTests`: Stage 11's "the gameplay assembly does not reference core yet" becomes "only the gameplay assembly references core, never the reverse" (S12-D7).

## Task 2: `AttemptStart`, the validated attempt (core)

**Files:** create `Backend/Core/Attempt/AttemptStart.cs`; test `Tests/EditMode/Backend/AttemptStartTests.cs`.

**Interfaces:**
- Consumes: `SessionRegisterResponse`, `LoadoutSnapshotDto`, `OperativeSnapshot.FromDto`, `CanonicalIds`, `LedgerCatalogue`.
- Produces: `AttemptStart.TryCreate(string attemptId, SessionRegisterResponse response, string buildCatalogueVersion, Func<string, bool> knownOperativeDefinition, out AttemptStart start, out string refusal)`; properties `AttemptId`, `ProcessSessionId`, `Seed`, `TemplateId`, `TemplateVersion`, `CatalogueVersion`, `AccountId`, `Snapshot` (`IReadOnlyList<OperativeSnapshot>`), `OperativeDefinitionIds` (`IReadOnlyList<string>`, slot order), `Experience`, `Choices`.

**Refusal codes** (first failure wins, in this order): `bad_attempt_id` (not canonical UUIDv7), `bad_session_id`, `catalogue_mismatch` (response or snapshot catalogue version ≠ the build's), `unsupported_participants` (count ≠ 1), `bad_snapshot` (`FromDto` throws, or the snapshot's account ≠ the participant's), `unknown_operative_definition`, `bad_slots` (slots not 0..n-1 distinct, n 1..8).

- [ ] Step 1: failing tests, one per code, plus `A_valid_response_gives_the_snapshot_in_slot_order` built from `ledger-v1/01-owned-swaps-and-use.json`'s snapshot.
- [ ] Step 2: run; Expected: EXIT=1 (missing type).
- [ ] Step 3: implement.
- [ ] Step 4: run; Expected: all pass.
- [ ] Step 5: commit.

## Task 3: The non-merging attempt fill and pool registration (B25)

**Files:** create `Backend/Core/Attempt/AttemptLoot.cs`; modify `Items/LootContainer.cs` (add `InitializeForAttempt`), `Mission/MissionContent.cs` (attempt branch); tests `Tests/EditMode/Backend/AttemptLootTests.cs`, `Tests/EditMode/Items/OnlineContainerFillTests.cs`.

**Interfaces:**
- Produces (core):
  - `LootRoll(string definitionId, int quantity)`;
  - `AttemptLoot.CheckRoll(LootRoll roll, LedgerItemDefinition definition, int maxStack) → string` (null when it fits; else `loot_roll_unknown_item`, `loot_roll_quantity_invalid` (≤ 0), `loot_roll_exceeds_stack`, `loot_roll_not_single` (non-stackable ≠ 1));
  - `AttemptLoot.Register(MissionItemLedger ledger, int containerIndex, IReadOnlyList<ContainerEntry> entriesInRollOrder)`: calls `ledger.RegisterContainer`; lines are the list's indices 0..n-1.
- Produces (Unity): `LootContainer.InitializeForAttempt(IReadOnlyList<LootItemPlan> rolls, ItemCatalogue catalogue, out IReadOnlyList<ItemEntry> entries, out string error) → bool`: for each roll in order, `CheckRoll`, then `contents.TryAddEntry(new ItemEntry(definitionId, quantity))`; any failure leaves the container empty, returns false with the code and the roll's index. Marks the container `AttemptMode`: `Deposit`-style adds (`ItemInventory.Add` from gameplay into a container) are refused in attempt mode.
- `MissionContent.AddLootContainers(..., bool attemptMode, out string fillError)`: in attempt mode calls `InitializeForAttempt`; the director turns a fill error into generation failure `attempt: loot: <code> container <i> roll <j>`.

**The source-identity mapping:** registration happens once, after the fill and before gameplay, from `entries` in roll order: `ContainerEntry(entry.InstanceId, entry.DefinitionId, entry.Quantity)`. The ledger keeps `entry id → (container, line)` for the attempt; a later collect or removal of another entry never renumbers (lines are stored, not recomputed from the inventory).

**The owner's nine tests**, mapped (forced duplicate-roll fixtures: a hand-built `LootContainerPlan` with `item.medkit ×1` and `item.medkit ×2` in one container, plus the 200 seeded random missions):
1. `Two_medkit_rolls_make_two_entries_and_two_valid_lines` (EditMode; the pool's lines `(0,0,medkit,1)`, `(0,1,medkit,2)`; the backend half in Task 7's fixtures).
2. `Partial_and_complete_collects_keep_separate_provenance` (collect 1 of line 1, all of line 0; the ledger's collected lots are per line).
3. `Destination_merging_does_not_change_source_accounting` (both collects into one bag stack; `VerifyContainer` and the facts still name both lines).
4. `Loot_first_oldest_first_consumption_across_the_lines`: oldest is by collection sequence, not line index: collect line 1 first, then line 0; the first use consumes line 1's lot, the next line 0's, and owned stock only after both.
5. `Invalid_quantities_are_refused_before_ready` (`maxQuantity > maxStack`, 0, a non-stackable ×2, an unknown id: generation fails with the code; no ready report exists).
6. `Pool_lines_are_stable_after_source_mutations` (remove entry 0, collect from entry 2: `PoolLines()` unchanged, line indices unchanged).
7. `The_loot_hash_matches_the_backend` (`LootPoolHash.Compute` over the pool equals the hash in the Task 7 fixture, which the backend recomputes with its own `LootHash.Compute`).
8. `Offline_generation_and_hashes_are_unchanged` (the existing deterministic hash tests stay as they are; a new test runs `Generate(seed)` for the fixture seeds and checks `LootPlan.Hash`, the layout hash and every offline container's merged contents against values pinned before this task).
9. `The_backend_accepts_generated_cases`: the backend half, Task 7.

Plus Review Focus 1 and 2: `A_roll_over_max_stack_fails_generation_with_its_code`, `A_bypassing_deposit_is_a_ledger_mismatch_at_the_freeze`. And `Every_shipped_loot_entry_fits_one_entry` (static, over the exported catalogue's table: a non-stackable's `maxQuantity` is 1, a stackable's at most its `maxStack`), so a table edit can't make the online path fail at runtime unnoticed. Test 6's three-entry container is a core test only; attempt-v1 fixtures respect the golden table's limits (3 containers, 1–2 items each). `MissionItemLedger.RegisterContainer`'s doc comment ("after ItemInventory.Add merged…") is corrected to the B25 meaning (docs only; the ledger's behavior is unchanged).

- [ ] Steps: failing tests → RED (missing members) → implement → GREEN → the full offline loot suites (`Blackglass.Tests.Loot*`, `MissionDirector*`) unchanged → commit.

## Task 4: `GenerateForAttempt` and the ready report

**Files:** modify `Mission/MissionDirector.cs`; create `Backend/Core/Attempt/ReadyReportBuilder.cs`; tests `ReadyReportBuilderTests` (EditMode), `AttemptMissionPlayTests` (PlayMode, part 1).

**Interfaces:**
- `MissionDirector.GenerateForAttempt(AttemptStart attempt) → bool`: sets `InstanceId = attempt.AttemptId`, the seed from the attempt, attempt mode for the fill; the generation's report keeps a failure reason as today. `AttemptContainers` (read): `IReadOnlyList<(int Index, IReadOnlyList<ItemEntry> EntriesInRollOrder)>`, available once `Ready`.
- `ReadyReportBuilder.Build(string processSessionId, ulong layoutHash, int lootTableVersion, IReadOnlyList<ObjectiveResult> objectives, IReadOnlyList<PoolLine> pool) → ReadyRequest` (layout hash as 16 lowercase hex; `lootHash` from `LootPoolHash.Compute`).
- Container indices: the plan's order, 0-based (the same order `LootPlan.Containers` gives).

- [ ] Steps: failing tests (the ready report of a fixed fixture attempt equals a pinned `ReadyRequest`; generation with a forced bad roll fails with its code and produces no report) → RED → implement → GREEN → commit.

## Task 5: Roster and inventory from the snapshot

**Files:** modify `Operatives/PersistentOperativeState.cs`, `Operatives/SquadRoster.cs`, `Items/InventorySession.cs`, `Items/SquadInventory.cs`; tests `SnapshotRosterTests`, `SnapshotInventoryTests`.

**Interfaces:**
- `PersistentOperativeState.Restore(string id, int experience, IReadOnlyList<string> choices, ProgressionTrack track, out string problem)`: refuses a choice the track doesn't offer at that rank or more picks than the rank allows (`bad_progression`).
- `SquadRoster.InitializeFromSnapshot(IReadOnlyList<(OperativeDefinition Definition, PersistentOperativeState State)> members, ProgressionTrack track)`: never subscribes to `MissionFinished` (the backend awards XP, dead operatives included); slot order.
- `InventorySession.FromSnapshot(ItemCatalogue catalogue, int bagCapacity, IReadOnlyList<OperativeSnapshot> snapshot, out string problem)`: non-stackables keep the backend item id as `InstanceId`; stacks split by `maxStack` with local ids; equipment set by item id; bag capacity checked (`bag_overflow`).
- `SquadInventory.InitializeFromSnapshot(...)`: no `StarterLoadout` seeding, no `MissionFinished` settlement; `BeginMission(attemptId)` as today.

- [ ] Steps: failing tests (the snapshot's ids, XP, picks and equipment appear exactly; a mission finish awards nothing and settles nothing; a split stack's entries sum to the snapshot's quantity; bad progression refused) → RED → implement → GREEN → the offline roster and inventory suites unchanged → commit.

## Task 6: `AttemptSession`: ledger wiring, freeze and the manifest

**Files:** modify `Items/UnitItems.cs` (detail events); create `Server/AttemptSession.cs`, `Server/ServerClock.cs`; tests `AttemptSessionTests` (EditMode), `AttemptMissionPlayTests` (part 2).

**Interfaces:**
- `UnitItems.CollectedDetail: event Action<string operativeId, LootContainer container, string entryId, int taken>`; `UsedDetail: event Action<string operativeId, string definitionId>`; `EquippedDetail: event Action<string operativeId, string entryId>`. Raised after the existing events, only on success; no rule changes.
- `AttemptSession.Begin(AttemptStart attempt, MissionDirector director, SquadInventory inventory, LedgerCatalogue catalogue, ServerClock clock) → string refusal`: `AddParticipant`, `AttemptLoot.Register` for every container, subscribes to every friendly unit's detail events; a `LedgerEventResult` other than `Applied`/`Frozen` marks the participant's integrity failure (Stage 11 rule).
- `AttemptSession.ReadyReport → ReadyRequest` (Task 4's builder over the registered pool).
- On the first `MissionFinished` (or `Abandon`, later stages): `Freeze` with the runtime state read from `SquadInventory` and the containers; `ManifestBuilder.BuildForPublication(header, result, facts, ...)` inside a `try` that turns an `ArgumentException` into `Failed` with its message (Stage 11 M2); keeps `Outcome` (`ManifestBuildOutcome`). No outbox write in Stage 12 (Stage 13 owns publication).
- `ServerClock.ElapsedMs`: `Stopwatch` from the start commit (Stage 12: from `Begin`).

**Tests:** `Collect_and_use_feed_the_ledger_and_stay_consistent`, `Play_after_the_end_changes_nothing` (a collect and an ability kill after `MissionFinished`: facts and bytes unchanged), `A_ledger_mismatch_builds_the_truthful_fallback` (a forced bypass: `integrity` is never `ok`), `An_ArgumentException_from_the_builder_is_a_failed_outcome`, the 200 seeded runs of Stage 11's `LedgerSimulation` re-run through `AttemptSession` with attempt-mode containers (B25 lines).

- [ ] Steps: failing tests → RED → implement → GREEN → commit.

## Task 7: Cross-runtime fixtures: the backend judges generated attempts

**Files:** create `Assets/_Project/Editor/Backend/AttemptFixtureExport.cs` (menu `Blackglass > Backend > Export Attempt Fixtures` and a batch method), `Backend/contracts/attempt-v1/*.json` (generated), `Backend/contracts/attempt-v1/README.md`; tests `AttemptFixtureExportTests` (EditMode: the committed files equal a fresh export, like the catalogue's staleness test), `Backend/tests/Accounts.Domain.Tests/Settlement/AttemptFixtureTests.cs`; the domain test csproj copies the folder.

**Fixtures** (deterministic, from the golden catalogue's table): `01-forced-duplicate-medkits` (one container, `medkit ×1`, `medkit ×2`), `02-forced-mixed` (stackable and non-stackable rolls in three containers), `03..06` seeded attempts with collects and uses scripted by `LedgerSimulation`'s fixed scripts, `07-forced-partial-and-complete` (owner test 2's script). Each holds the snapshot, objectives, the pool, the ready report and the expected manifest.

**Backend test (owner test 9: the real ready and settlement validators):** the export writes each ready report as the exact bytes Stage 13 will send (`JsonUtility` over `ReadyRequest`). Over those bytes the backend runs its whole ready path: `ReadyReport.Parse` (exact members, the 16-hex layout hash, the `sha256:` loot hash, ids, limits), `ObjectiveList.Validate` (1–32 objectives, at least one required), `LootPoolValidator.Validate` against the admission catalogue's table, and `LootHash.Compute` equal to the report's hash. Preferably `ProcessStore.ReadyAsync`'s checks are first extracted into one pure `ReadyValidation.Validate(report, table)` used by both the endpoint and the test (a backend refactor, test-first, behavior unchanged), so the test can't drift from the endpoint. Then `ResultManifest.Parse` accepts the manifest; `ManifestValidator.Validate` returns no error against the reconstructed context; the planned collections equal the manifest's per line. The merged representation of fixture 01 (`medkit ×3` on one line) is still refused (I1's test, unchanged).

- [ ] Steps: the backend test first, over a hand-written `01` (RED: the folder is missing) → the export → GREEN on both sides → commit.

## Task 8: Abilities and combat on the server

**Files:** modify `Editor/Server/ServerSceneBuilder.cs` (catalogue, loot table, roster and inventory in snapshot mode, `AttemptSession`), regenerate `ServerMission.unity`; modify `Server/ServerBootstrap.cs` (`-attemptFixture <path>` in Editor and fixture builds: `FixtureAttemptSource` → `AttemptStart` → `GenerateForAttempt`; refused in release builds with exit 2); tests `AttemptAbilitiesPlayTests`.

**Tests (PlayMode):**
- the snapshot operatives spawn with their gear and abilities, scaled by their picks (`UnitIdentity`);
- Recon Scan and Aimed Shot resolve on the server through `AbilityRules` and `UnitAbilities`; cooldowns enforced (a second use inside the cooldown fails with its `AbilityFailure`);
- server-side basic attacks and kills resolve with no client; hostile AI acts on its own; a friendly death shows in the manifest (`alive:false`, its held items as frozen);
- `-attemptFixture` is refused unless the fixture gate is on, the gate injectable as `-netFixture`'s is (an Editor compile can't observe a release define);
- the Checkpoint A diagnostic (`-diagnostic`) still passes, and the offline-scene suites are unchanged.

- [ ] Steps: failing tests → RED → implement → regenerate the scene through the builder → GREEN → full EditMode and PlayMode → commit (the diff stat shows no client-scene file).

## Task 9: B24, the repository side (test-first, nothing installed)

**Files:** create `Backend/deploy/mission/landlock-exec` (L1; root-owned 0755 when installed); modify `blackglass-mission-slot.service.in`, `render-slot-units.sh`, `verify.sh`, `README.md`; tests `Backend/tests/MissionHost.Agent.Tests/Deploy/SlotTemplateTests.cs` (rendering), `Backend/tests/UnityCore.Linux.Tests/LandlockProbeTests.cs`.

**The launcher (L1, or L2 under S12-D3's seccomp rules; at the S12-D6 path):** `landlock-exec --tcp-bind <port> [--tcp-connect <port>] -- <program> <args...>`:
1. refuses to run unless `prctl(PR_GET_NO_NEW_PRIVS)` is 1 (the unit's `NoNewPrivileges=yes`);
2. `landlock_create_ruleset(NULL, 0, LANDLOCK_CREATE_RULESET_VERSION)` must return ≥ 4, else exit 78 (`landlock_unavailable`): fail closed, the mission never starts unrestricted;
3. a ruleset handling `LANDLOCK_ACCESS_NET_BIND_TCP` (and `CONNECT_TCP` under S12-D2), one `LANDLOCK_RULE_NET_PORT` rule per allowed port;
4. `landlock_restrict_self`, then `os.execv`. Every descendant inherits the domain; nothing can lift it.

**The template at Stage 12:**
- `ExecStart=/opt/blackglass/mission/current/landlock-exec --tcp-bind @PORT@ [--tcp-connect 5081] -- /opt/blackglass/mission/current/run-mission %i`;
- `SystemCallFilter=~listen` removed (a TCP server must listen); `SocketBindAllow=`/`SocketBindDeny=` removed (inert here, and misleading);
- `IPAddressDeny=any` and `IPAddressAllow=localhost` (the address half; counted only once Task 10 shows it enforced);
- under S12-D3 U1: the launcher also installs the UDP seccomp filter.

**Tests:** template rendering (the launcher line with the slot's port for all 8 slots; no `~listen`; the IP lines). In a container on the test runner (shared kernel, not the mission VM's): own port bind+listen allowed; 5081, another slot's port and port 0 refused with exactly `EACCES` (anything else is a failure to prove); the same over IPv6 (`[::1]`, `[::]`); MPTCP (protocol 262) and SCTP binds recorded; a restricted process's child inherits; an ABI below 4 (simulated by an option) refuses to start. **First** check that the runner's default Docker seccomp profile allows the `landlock_*` syscalls without `--privileged` or `seccomp=unconfined`; if it doesn't, the container test is a runner change that needs the owner's authorization, and the logic is proven on the VM only. The container proves the logic; only Task 10 proves the VM.

**Mechanisms not chosen** (recorded for B24's evaluation order): systemd's `SocketBindAllow` is unenforced on this systemd (`-BPF_FRAMEWORK`, Stage 10's on-VM note) and would need a non-stock systemd. Socket activation (a root `.socket` with `ListenStream=127.0.0.1:<port>`, the unit keeping `SystemCallFilter=~bind listen`) would close residuals (a) and (b) too, but Unity Transport 2.7.4's WebSocket interface creates its own Baselib socket and can't adopt an inherited descriptor without a custom `INetworkInterface`: rejected for Stage 12, revisit if the transport gains that.

- [ ] Steps: failing tests → RED → implement → GREEN (`vm-test.sh --unity-core` and `--agent`) → commit. Nothing is copied to `bg-dev-mission01`.

## Task 10: B24 demonstration on `bg-dev-mission01` (owner-approved manifest first)

Not executed without the owner's approval of its host manifest (written at the start of this task, in the format of the outbox self-test's manifest). Only trusted probe code runs; no Unity binary.

- A probe release `/opt/blackglass/mission/releases/<rev>-b24probe/` (`landlock-exec` plus a probe `run-mission` that runs the probes below and exits); `/opt/blackglass/mission/current` relinked to it for the run and back afterwards; one slot template re-rendered to the Stage 12 shape **only under the approved manifest**, restored afterwards; the agent stays disabled, the tunnel up.
- **The slot template's prerequisites:** a dummy, non-secret credential file at `/run/blackglass/credentials/<id>/process` (`LoadCredential` fails without one); a canonical UUIDv7 attempt id (`slot-guard` refuses others); cleanup removes `/var/lib/private/blackglass-outbox/<id>`, `/var/lib/blackglass/exits/<id>.exit` and the credential directory, which the template's asserts and `record-exit` leave behind.
- **Probes from inside a mission unit** (slot 03, port 7803). Every refusal must show `EACCES`; `EADDRINUSE` (the tunnel holds 5081) or a timeout proves nothing and fails the line. Allowed: bind+listen 127.0.0.1:7803, 0.0.0.0:7803 and [::1]:7803. Refused: 127.0.0.1:5081, 0.0.0.0:5081, [::1]:5081, 127.0.0.1:7804, [::]:7804, port 0. Recorded for S12-D3: `listen()` on an unbound socket (and the port it got), MPTCP and SCTP binds on 5081, 7804 and 0, UDP binds 127.0.0.1:7803 and :0; TCP connect to 5081 allowed and to 22 refused under S12-D2.
- **Control run without the launcher** (same unit shape, the launcher replaced by a direct exec): port 0, 7804 and a free test port (7899) succeed, and 5081 fails only with `EADDRINUSE`. That shows the refusals above are Landlock's.
- **Address half** (every connect with an explicit timeout: dropped packets time out, they aren't reset): with the probe listening on 0.0.0.0:7803 under `IPAddressDeny=any`/`IPAddressAllow=localhost`, a root shell on the VM connects to the VM's own non-loopback address and port 7803 (must time out) and to 127.0.0.1:7803 (must succeed); from `bg-dev-services01`, a plain TCP connect to the mission VM's port 7803 must time out. **Positive control (S12-D8):** the same listener in a unit without the IP filter is reachable from both places, so the filtered failures are the filter's, not the hypervisor's or `ufw`'s (whose state is recorded too). The journal is checked for systemd's "BPF firewalling not supported" message (systemd starts the unit anyway if the kernel can't attach the filter, so the unit can't fail closed on it), and `verify.sh` gains a boot-time probe of the filter and a check of `ip_local_port_range` (residual (a) depends on it).
- **Evidence:** the probe transcript, `systemctl show` of the unit's effective properties, `verify.sh`'s TCP section, the residuals for the owner. B24 is satisfied only if every "refused" line holds.

## Task 11: Unity on Linux under B24 (owner-approved manifest first; after Task 10)

Two runs in a Stage 12-shaped mission unit (the launcher, the IP filter), each bounded by `RuntimeMaxSec`, with no backend, no real credential (a dummy file) and no client:

1. **The transport:** the release build with a new diagnostic, `-netBindCheck` (a code task here: `ServerOptions`, `ServerBootstrap`, a test; it starts FishNet's WebSocket transport on the slot port, logs the bind, then **holds** until the operator has captured `ss -tanup`/`ss -uanp` and `/proc/<pid>/net` from root, then exits). Shows the transport works under Landlock and the filter, and lists every socket the player opened (settles S12-D2 and U1).
2. **The attempt path on Linux Mono (review I7):** a fixture build runs `-attemptFixture` with an attempt-v1 fixture for a bounded time: generation from the snapshot, the roster and inventory, the B25 fill, `AttemptSession`, a scripted end (the fixture's own terminal event), the freeze and `ManifestBuilder`. It logs the ready report and the manifest bytes (hashes and the bytes to the journal). They must equal the Editor-generated fixture's, byte for byte: a difference (JSON, culture, floating-point layout generation) fails Stage 12. Sockets are captured here too.

Neither run is a backend-connected mission; both happen only after B24 is demonstrated (Task 10) and under their own approved manifest.

## Task 12: Final validation, review and the stop

Full EditMode, PlayMode, backend domain, agent, integration, `--unity-core`; the attempt-v1 fixtures on both sides; manifest-v1 and ledger-v1 byte-identical; an independent final review; the plan's "As built"; stop for the owner. Stage 12 is accepted only with B24 (Task 10) and B25 (Tasks 3, 7) evidenced.

---

## Acceptance criteria

- B25's invariants hold, with the nine tests, forced fixtures included, and the backend accepting every generated fixture.
- B24 demonstrated on `bg-dev-mission01` from a mission unit, with each residual either closed or accepted by the owner in writing.
- The outbox self-test passed on the VM under its approved manifest (Mono player).
- Task 11: the transport binds under B24, and the fixture attempt's ready report and manifest on Linux Mono equal the Editor's byte for byte.
- Snapshot roster and inventory exact; abilities and cooldowns authoritative; play after the end changes nothing; the manifest builds truthfully.
- Every existing suite green; no client scene, prefab or offline gameplay change; the Checkpoint A diagnostic passes.

## Compatibility with Stages 13 and 14

- Stage 13 adds the HTTP `IAttemptSource`, sends `AttemptSession.ReadyReport`, holds start, and writes `AttemptSession.Outcome`'s bytes through the `OutboxWorker` (`recorded` between `Written` and `AllowPublication`).
- Stage 14 adds the gate requests that call `UnitItems`/`UnitAbilities` for remote players; the detail events already feed the ledger.

## Plan review (2026-10-20, independent reviewer, revision 1, the self-test and its manifest)

0 Critical in the plan itself; the Critical was in the manifest. All findings are applied in this revision, the self-test branch and the manifest:

| Finding | Resolution |
|---|---|
| C1 manifest: nested quotes left `$PROPS` unset, so every phase would have run the player as root without the sandbox | the VM side is one script (`Tools/LinuxServer/outbox-selftest/selftest-vm.sh`) sent over `ssh … bash -s`, with `set -u` and a bash array for the properties; the self-test refuses euid 0 (`uid FAIL running_as_root`) and logs its uid; the hold step prints the unit's user and the process's uid |
| I1 self-test: an exception from the native layer gave `result=pass` | `Step` catches every exception (`exception FAIL <type>`); a phase passes only when it ran to its end with exactly its checks (publish 7, resume 5) |
| I2 manifest: `set -e` stopped at `is-enabled` | no `set -e`; every probe prints its value |
| I3 manifest: a dirty build | the build comes from a clean worktree, and the install step requires `dirty=false`. **Open:** a clean worktree build still stamps `dirty=true` (read after `BuildPlayer`); the fix (read the status before the build, record `dirtyPaths` and `changedByBuild`) is drafted, not yet applied or tested |
| I4 Task 10: refusals not attributable; no positive controls | `EACCES` only; a control run without the launcher; an off-host positive control (S12-D8); explicit timeouts |
| I5 MPTCP, SCTP, IPv6 unprobed | probed in Tasks 9 and 10; S12-D3 (c) closes them with seccomp if Landlock doesn't |
| I6 test 9 skipped `ReadyReport.Parse` and `ObjectiveList.Validate` | Task 7 runs the backend's whole ready path over the exact bytes, preferably through an extracted `ReadyValidation` |
| I7 the attempt path never ran on Linux Mono | Task 11 run 2: the fixture attempt on the VM, compared byte for byte with the Editor |
| Minors | M1 a `main_thread` bound on time inside `Step`; M2 a self-test option without a phase fails, never a mission; M3 a native `RENAME_NOREPLACE` check (`noreplace`, errno 17); M4 a comment; M5–M9 the manifest (invocation-scoped journal, exact expected lines, a whole-tree baseline with the tunnel's PID and restart count, cleanup, `bash -s`); M10 socket activation evaluated; M11 wording; M12 the filter's journal and boot-time checks; M13 test 4 by sequence; M14 a static table test; M15 Review Focus 2 restated; M16 `-netBindCheck` is a code task and holds for capture; M17 S12-D6 and the seccomp details; M18 the template prerequisites; M19 combat coverage; M20 an injectable gate; M21 the stale ledger comment; M22 the runner's seccomp profile; M23 the `NativeLibc` comment |

