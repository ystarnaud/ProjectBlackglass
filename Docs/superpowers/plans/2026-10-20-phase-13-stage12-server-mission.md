# Phase 13 Stage 12: Server Mission from a Snapshot, with Abilities — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status (2026-10-20):** **plan revision 2.2, for the owner's review. Not authorized for implementation.** Revision 2.2 applies re-reviews 3 and 4; revision 2.1 applied security review 2 (0 Critical, 5 Important, 15 Minor). The final independent re-check found no unresolved Critical or Important finding ("Re-review 3" and "Re-check 4" at the end). On `phase13-stage12-plan`, after Stage 11's merge (`075fcd4`). Revision 2 applies the owner's Stage 12 rulings of 2026-10-20: a C launcher versioned with the agent's trusted components, UDP denied, SCTP, MPTCP and io_uring closed, the unbound `listen()` mediated or B24 kept blocked, every prohibited operation against a positive control, and an off-host reachability procedure (`2026-10-20-phase-13-stage12-reachability-procedure.md`, prepared, not executed). Revision 1.1 applied the first independent review. Nothing here has run on either VM. Each VM phase needs the owner's approval of its own host manifest first; the outbox self-test's is `2026-10-20-phase-13-stage12-outbox-selftest-manifest.md`.

**Goal:** the Unity Linux dedicated server can run one authoritative solo attempt from a backend admission snapshot: the backend's operatives, inventory and picks; a deterministic mission whose loot containers keep one entry per rolled item (B25); a ready report the real backend accepts; abilities and combat resolved on the server; item events feeding Stage 11's ledger; the outcome frozen at the first terminal event, with a format-v1 manifest built from it. On the mission VM, a mission unit can bind and listen only on its own slot's TCP port (B24), demonstrated there before any real Unity mission workload runs.

**Architecture:** a pure, library-free attempt model in `Blackglass.Backend.Core` (`AttemptStart` from the session response, `AttemptLoot` for the non-merging fill, `ReadyReportBuilder`), consumed by a thin Unity layer: `MissionDirector.GenerateForAttempt`, `LootContainer.InitializeForAttempt`, `SquadRoster.InitializeFromSnapshot`, `SquadInventory.InitializeFromSnapshot`, and one server component, `AttemptSession`, which owns the `MissionItemLedger` and turns `UnitItems` detail events into ledger events. The attempt arrives through an `IAttemptSource` seam: Stage 12 has only a fixture source (Editor and fixture builds); Stage 13 adds the HTTP source. B24 is a small root-owned C launcher versioned with the agent (Landlock ABI 4 bind and connect rules, a seccomp socket policy, a mediated `listen()`), plus systemd's IP address filter with a root pre-check that it is attached; proven test-first in a container and then on `bg-dev-mission01` against positive controls ("B24 security design").

**Tech stack:** Unity 6000.3.25f1 (Mono, Linux Dedicated Server subtarget), NUnit EditMode/PlayMode, .NET 10 / xUnit for the backend cross-checks, C (static, built in a digest-pinned GCC container on the existing test runner) for the launcher, Linux Landlock ABI 4, seccomp with user notification and `pidfd_getfd` (kernel 6.8 on the mission VM), systemd 255 (`-BPF_FRAMEWORK`).

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
5. **Landlock's listen autobind:** `listen()` on an unbound TCP socket picks an ephemeral port without a `bind()`, which ABI 4 doesn't mediate. It is a must-refuse line under the listen supervisor (S12-D9); without the supervisor, B24 stays blocked unless the owner accepts the residual in writing. Tasks 9 and 10.

---

## Decisions for the owner

| # | Question | Options | Recommendation |
|---|---|---|---|
| S12-D1 | **The launcher's form.** | **Ruled (2026-10-20):** a small C launcher, root-owned and versioned with the agent's trusted security components. | Resolved. |
| S12-D2 | **Outbound TCP.** | **Ruled:** 5081 is the only expected future backend destination; the launcher's Landlock connect rule allows only it, the IP filter only on loopback. | Resolved; Task 11 records any other connection Unity attempts, which would then fail. |
| S12-D3 | **What Landlock ABI 4 can't close.** | **Ruled:** UDP denied unless separately authorized; SCTP, MPTCP and io_uring closed by the launcher's seccomp policy; the unbound `listen()` is S12-D9. | Resolved, except S12-D9. |
| S12-D4 | **The attempt's fixture source** in Stage 12 (no backend yet). | Editor and fixture builds only (`BLACKGLASS_NET_FIXTURE`), never a release build, so a release server can't start a fake attempt. | As stated. |
| S12-D5 | **Order of the VM phases.** | Outbox self-test (manifest attached) → B24 probe demonstration (Task 10, its own manifest) → Unity under B24 (Task 11: the transport check and the fixture attempt, its own manifest). Each runs only after the owner approves its manifest. | As stated. |
| S12-D6 | **Where the launcher lives.** | **Ruled:** with the agent's trusted components, `/opt/blackglass/agent/releases/<rev>/bg-mission-launch`, beside `slot-guard`. | Resolved. |
| S12-D7 | **The self-test kept separate** on `phase13-outbox-selftest`. | **Ruled (2026-10-20):** separate from Stage 12 for now; the gameplay assembly's reference to the core is approved (it's needed to compile the diagnostic and changes no gameplay); the root check's uid reader is local to the diagnostic. Not merged or pushed. | Resolved. |
| S12-D8 | **B24's off-host positive control** (Task 10): the reachability procedure's allow-listed runs A1 and A2 (TCP 7899 and 7898, the filter allowing only localhost and the services VM) before and after the filtered run B, reached from `bg-dev-services01`. They expose two probe ports for at most 120 s each: to the services VM only if the filter is enforced, to the whole development network if it isn't. | Approve it with A1 (allow-listed) or A0 (fully unfiltered), or accept the weaker evidence (the VM's own-address test only). | Approve it with A1. |
| S12-D9 | **The unbound `listen()`** (an autobound ephemeral listener; a classic seccomp filter can't tell from `listen()`'s arguments whether the socket was bound). | **(a)** the launcher's listen supervisor (seccomp user notification, `pidfd_getfd`, `getsockname` on the very socket, which it then listens on itself; never `CONTINUE`): about 400 lines of C, enforcing B24's rule fully. **(b)** the owner's written acceptance of one residual: an ephemeral, loopback-only listener that can never be 5081 or a slot port. **(c)** neither: B24 stays blocked. | **(a).** |
| S12-D10 | **The launcher's container tests** on the runner may need `--cap-add SYS_PTRACE` on that one test container (for `pidfd_getfd`), a flag, not a Docker daemon change. | Authorize the flag for the launcher mode only; or prove the supervisor on the VM only. | Authorize the flag. |
| S12-D11 | **The IP filter's fail-closed pre-check:** `ExecStartPre=+bg-mission-launch --verify-ip-filter` (root, `BPF_PROG_QUERY` on the unit's cgroup), because systemd starts a unit even when it can't attach the filter. | Add it, or rely on the VM proof alone. | Add it. |

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
- `Backend/deploy/mission/launcher/` (`bg-mission-launch.c`, `probe.c`, tests) and the `vm-test.sh --launcher` mode; `blackglass-mission-slot.service.in`, `verify.sh` and `publish-agent.sh` changes (rendered, never installed by this plan's code tasks).
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
- `OutboxSelftestTests` (20, EditMode, over the portable directory): the vector pin, publish (7 checks, the `.ready` never replaced), hold then resume, resume without a held result, root refused before any file operation, a native-library exception and a failing uid read never passing, a long frame, a refused location, unknown and missing phases, the options, a self-test option without a phase never running a mission, the production-outbox refusal, the uid reader local to the diagnostic and linux-only, and the bootstrap running the self-test with no director and `-net` ignored (exit 0) or failing it (exit 3). EditMode 1863/1863 and PlayMode 1434/1434 at `88c0e39` (in the owner's working tree, with its two modified settings files). At the final self-test revision `5551a00`, in the clean worktree (2026-10-20): EditMode 1864/1864 and PlayMode 1434/1434, then the verified clean build whose hashes the manifest (revision 5) records.
- **The root check's uid reader** is `OutboxSelftest.EffectiveUserId()` (its own `geteuid` import, linux-x64 only); the core assembly is byte-identical to `main`.
- **Build provenance** (`88c0e39`, `7d7fbf7`, extended after security review 2 in `91d5185` (ignored files under the source folders, refused index flags and sparse checkouts, a content hash) and after re-review 3 in `5551a00` (fsmonitor refused, diffs without textconv or external drivers, no replace objects)): `Tools/build-server.sh` captures the commit, source tree and cleanliness (porcelain status with untracked files) before Unity starts (`Tools/build-provenance.sh`). Unity only stamps them (`ServerBuildInfo.SourceLines`: anything invalid stamps `unknown`, never clean). After Unity exits, the source and HEAD must be exactly as captured, or the build fails and nothing is packaged. The embedded stamp must equal the capture. `binarySha256`, `provenance=verified` and `files.sha256` (every shipped file: the executable is Unity's generic player, the game code is in `*_Data/Managed`) are then recorded. Tests: `Tools/tests/build-server-provenance-test.sh` (clean source with Unity's temporary settings change; tracked and untracked changes before the build; a change that survives the build; HEAD moving) 10/10, and `ServerBuildInfoTests`. Not covered: Unity's `Library/PackageCache` (the two git packages are resolved from `Packages/packages-lock.json`, whose content is covered, not the cached checkout).
- **`Tools/LinuxServer/outbox-selftest/selftest-vm.sh`:** the VM side the manifest runs. Each run is token-tagged in the journal. The independent uid evidence is the owner of the files the process wrote (`owner_matches_euid`) and `/proc` for the held run; the stdout stream's journal credentials only corroborate the unit's user.
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

## B24 security design (revision 2.1, owner rulings of 2026-10-20 §4; security review 2 applied)

**Status: a design to be implemented and proven; nothing here is enforced or demonstrated yet.** B24 stays blocking until every line of "Proof" below holds on `bg-dev-mission01`. The installed Stage 10 templates aren't changed by this plan. The Stage 12 template is a repository file, installed only under a later owner-approved manifest.

### Components

- **`bg-mission-launch`, a small C launcher**, root-owned 0755. It is versioned with the agent's trusted security components at `/opt/blackglass/agent/releases/<rev>/bg-mission-launch`, beside `slot-guard` and `record-exit`, and reached through `/opt/blackglass/agent/current`. It is never part of the swappable mission release it constrains.
  - It is built statically in a digest-pinned GCC container on the existing `bg-dev-services01` test runner: a new `vm-test.sh --launcher` mode, with no Docker daemon, network or firewall change.
  - Its SHA-256 is recorded in the agent release's manifest.
- **The mission still runs as its slot's dynamic user** (`DynamicUser=yes`, `User=blackglass-mNN`), unchanged. The launcher is that unit's `ExecStart`: it runs as that non-root user under `NoNewPrivileges=yes`.
- **systemd's IP address filter** (`IPAddressDeny=any`, `IPAddressAllow=localhost`) covers the address half, with a root pre-start check that the filter is attached (below).

### The launcher's sequence: fail closed

Every step that fails prints a bounded reason to stderr and exits 78 without `execve`.

1. **Arguments:** `--tcp-bind <port>` (1024–65535, rendered per slot), `--tcp-connect 5081`, then `--` and the program. Anything else is refused.
2. **Preconditions:**
   - `prctl(PR_GET_NO_NEW_PRIVS)` must already be 1 (the unit's `NoNewPrivileges=yes`);
   - `/proc/sys/kernel/yama/ptrace_scope` must be exactly 1 (0 would weaken the supervisor's protection; 2 or 3 would make every `listen()` fail);
   - the Landlock ABI query must return at least 4.
3. **Fork first.** The parent becomes the listen supervisor. It is never Landlock-restricted and never filtered by the launcher's seccomp policy (the unit's own systemd filters still apply to it), so the child's Landlock domain can't `ptrace` or `pidfd_getfd` it (Landlock's own ptrace scoping, independent of Yama). It calls `prctl(PR_SET_DUMPABLE, 0)`, so even a same-uid process outside any domain would need `CAP_SYS_PTRACE`.
4. **In the child only, in this order:**
   1. **No inherited descriptors:** `close_range(3, ~0U, 0)` (keeping only the socketpair end to the supervisor). Then a scan of `/proc/self/fd`, which must show only 0, 1, 2 and that end. Each of 0–2 is checked with `fstat` and, if it is a socket, `getsockopt(SO_DOMAIN)` must be `AF_UNIX` and `SO_ACCEPTCONN` 0. Stdin is `/dev/null`; stdout and stderr are systemd's journal stream (a connected `AF_UNIX` socket).
   2. **Landlock:** a ruleset handling `LANDLOCK_ACCESS_NET_BIND_TCP` and `LANDLOCK_ACCESS_NET_CONNECT_TCP`, with exactly two rules: bind on the slot port, connect on 5081. Then `landlock_restrict_self`. The restriction applies to IPv4 and IPv6 TCP by port, and is inherited across `execve` and by every descendant. The address half is the IP filter's.
   3. **The seccomp filter** (classic BPF; every scalar argument is judged on its low 32 bits, since the kernel truncates `int` arguments):
      - **Architecture:** must be `AUDIT_ARCH_X86_64`; x32 syscall numbers (bit 30) are killed.
      - **`socket`:** for `AF_INET`/`AF_INET6`, allowed only when `type & ~(SOCK_NONBLOCK|SOCK_CLOEXEC) == SOCK_STREAM` and `protocol ∈ {0, IPPROTO_TCP}`. Every other combination returns `EACCES`: UDP, SCTP over `SOCK_STREAM` or `SOCK_SEQPACKET` (`IPPROTO_SCTP` 132), MPTCP (`IPPROTO_MPTCP` 262), raw. `AF_UNIX` is allowed; every other family returns `EAFNOSUPPORT`.
      - **`socketpair`:** `AF_UNIX` only.
      - **TCP Fast Open:** `sendto` (flags, argument 3), `sendmsg` (argument 2) and `sendmmsg` (argument 3) with `MSG_FASTOPEN` (0x20000000) return `EACCES`. On kernel 6.8, `sendmsg` with `MSG_FASTOPEN` on an unconnected TCP socket connects through `tcp_sendmsg_fastopen`, which never reaches Landlock's connect hook; the connect rule alone would be bypassed. (`TCP_FASTOPEN_CONNECT` followed by `connect()` does go through the hook.)
      - **`io_uring_setup`, `io_uring_enter`, `io_uring_register`:** `EPERM`. `IORING_OP_SOCKET` and io_uring's own send operations bypass the rules above. The unit also gets `SystemCallFilter=~io_uring_setup io_uring_enter io_uring_register`.
      - **`listen`:** `SECCOMP_RET_USER_NOTIF`, installed with `SECCOMP_FILTER_FLAG_NEW_LISTENER`. The notification descriptor goes to the supervisor over the socketpair (`SCM_RIGHTS`), and the child closes its copy.
   4. **A last descriptor scan** immediately before `execve` (only 0–2 may remain), then **`execve`** of the program. A failure exits 78.
5. **The supervisor** (owner decision S12-D9).
   - **Why it exists:** Landlock ABI 4 mediates `bind()`, but `listen()` on an unbound socket autobinds an ephemeral port without a `bind()`. **A classic seccomp filter can't tell, from `listen()`'s arguments (a descriptor number and a backlog), whether or where the socket was bound;** a filter alone can only allow or refuse every `listen()`.
   - **What it does on each `listen` notification:**
     1. maps the notifying thread's id to its thread-group id (`/proc/<tid>/status`, `Tgid`; on 6.8 `pidfd_open` needs a thread-group leader);
     2. calls `pidfd_open`, then `SECCOMP_IOCTL_NOTIF_ID_VALID`, then `pidfd_getfd` for `args[0]`, then `SECCOMP_IOCTL_NOTIF_ID_VALID` again;
     3. inspects the socket it now holds: `SO_TYPE` must be `SOCK_STREAM`, `SO_PROTOCOL` must be `IPPROTO_TCP`, and `getsockname()` must be `AF_INET`/`AF_INET6` with the slot port. An unbound socket reports port 0 and is refused.
   - **The reply:** if the socket passes, the supervisor calls `listen(dup, args[1])` itself, on the same socket, and replies success; otherwise it replies `EACCES`. It never uses `SECCOMP_USER_NOTIF_FLAG_CONTINUE`, which the kernel documents as unsafe for security decisions. The action is taken on the socket that was inspected, so a racing `dup2` in another thread changes nothing.
   - **Exit:** it waits for the child and exits with its status. For a signal, it re-raises the same signal on itself, so systemd and `record-exit` see the mission's own result.
   - **If the supervisor dies:** its notification descriptor closes and every later `listen()` fails: fail closed. The child can kill it (same uid), which only fails closed.

**IP filter pre-check.** systemd starts a unit even when the kernel can't attach its IP filter, so the unit can't fail closed on it alone. A root `ExecStartPre=+/opt/blackglass/agent/current/bg-mission-launch --verify-ip-filter` therefore queries the unit's cgroup (`BPF_PROG_QUERY` for `BPF_CGROUP_INET_INGRESS` and `_EGRESS`). It refuses to continue unless both carry exactly the expected programs. IP accounting uses the same attach types, so the expected count and type are pinned per systemd version from the VM run, and a program being attached isn't taken as proof that it denies. The deciding evidence is behavioral (the reachability procedure).

### The Stage 12 slot template (repository only)

- **Removed:** `SystemCallFilter=~listen` (replaced by the mediated `listen`), and the inert `SocketBindAllow=udp:@PORT@` and `SocketBindDeny=any`.
- **Added:**
  - `ExecStartPre=+…/bg-mission-launch --verify-ip-filter`;
  - `ExecStart=/opt/blackglass/agent/current/bg-mission-launch --tcp-bind @PORT@ --tcp-connect 5081 -- /opt/blackglass/mission/current/run-mission %i`;
  - `IPAddressDeny=any` and `IPAddressAllow=localhost`;
  - `SystemCallFilter=~io_uring_setup io_uring_enter io_uring_register`.
- **Unchanged:** everything else (identity, filesystem, credentials, resources).

### Mechanisms evaluated and not chosen

- **systemd's `SocketBindAllow=`:** unenforced on this systemd (`-BPF_FRAMEWORK`, Stage 10's on-VM note). It would need a non-stock systemd.
- **Socket activation:** a root `.socket` with `ListenStream=127.0.0.1:<port>`, the unit keeping `SystemCallFilter=~bind listen`. It would close the unbound-listen and UDP cases too.
  - It fails on Unity Transport 2.7.4: its WebSocket interface creates its own Baselib socket, and it can't adopt an inherited descriptor without a custom `INetworkInterface`.
  - Rejected for Stage 12; revisit if the transport gains that.
- **nftables:** filters packets; it never shows that a process can't bind or listen. Not proof by the spec's rule.

### Each case, and how it is handled

| Case | Handled by | Residual |
|---|---|---|
| Bind any TCP port but the slot's (5081, another slot's, IPv4 or IPv6, any address) | Landlock bind rule (`EACCES`) | none expected |
| Bind port 0 | Landlock (port 0 has no rule: `EACCES`) | none expected |
| `listen()` without an explicit bind (autobind) | the supervisor: `getsockname` shows port 0, refused (`EACCES`) | none if S12-D9 is approved, else see below |
| A preopened or inherited socket | `close_range`, the `/proc/self/fd` scans and the 0–2 socket checks; the template has no `Sockets=` | none expected |
| Outbound TCP to any port but 5081 (`connect`) | Landlock connect rule (`EACCES`) | none expected |
| Outbound TCP through TCP Fast Open (`sendto`/`sendmsg`/`sendmmsg` with `MSG_FASTOPEN`) | seccomp flag rule (`EACCES`) | none expected |
| Outbound TCP to 5081 on a non-local address | the IP filter | depends on the filter's VM proof |
| UDP (any port, IPv4 or IPv6) | seccomp `socket` rule (`EACCES`) | none (refused unless separately authorized) |
| SCTP and MPTCP | seccomp `socket` allow-list (`protocol ∈ {0, 6}`) | none expected |
| Sockets or sends through io_uring | seccomp and `SystemCallFilter` | none expected |
| The mission reaching the supervisor (`ptrace`, `pidfd_getfd`, `/proc/<ppid>/mem`) | Landlock's ptrace scoping (the child is in a domain, the supervisor isn't), the non-dumpable supervisor, Yama checked at start | none expected |
| Off-host reachability of the slot port | the IP filter | depends on the filter's VM proof |
| Abstract `AF_UNIX` sockets (outside B24's TCP scope) | not restricted on 6.8 (Landlock scoping arrives with ABI 6) | recorded for the owner |

**If S12-D9 isn't approved** (no supervisor), B24's "listen only on its slot's port" can't be fully enforced on kernel 6.8. The smallest credible alternative is then the owner's written acceptance of one residual:
- an autobound listener on an ephemeral port (32768–60999), never 5081 or a slot port;
- reachable only from the host itself under the IP filter;
- receiving nothing from the agent or the tunnel, which connect only to fixed ports.

Without either, B24 stays blocked.

### Proof: every prohibited operation against a positive control

The same probe binary runs twice in the same unit shape:
- **once under the launcher:** each prohibited operation must fail with its expected error (`EACCES`, `EAFNOSUPPORT`, `EPERM`), and anything else fails the line;
- **once without it,** as a control unit whose `ExecStart` runs the probe directly: the same operation must succeed, showing the probe could otherwise do it.

**No control may change the host's kernel state.** No SCTP socket is created anywhere: an unprivileged SCTP `socket()` makes the kernel autoload `sctp.ko` through its root modprobe helper, and `ProtectKernelModules=yes` doesn't prevent that. The allow-list is proven with protocols that need no module. Every VM manifest records `/proc/modules` and `kernel.modules_disabled` before and after.

| Operation | Control (no launcher) | Under the launcher |
|---|---|---|
| bind+listen 127.0.0.1, 0.0.0.0, [::1], [::] on the slot port | succeeds | succeeds |
| bind 127.0.0.1:7804 and [::]:7804 (another slot) | succeeds | `EACCES` |
| bind 127.0.0.1:5081 | `EADDRINUSE` while the tunnel listens, so the control uses a free stand-in (7899) that succeeds | `EACCES` (not `EADDRINUSE`) |
| bind port 0 (IPv4 and IPv6) | succeeds | `EACCES` |
| `listen()` on an unbound socket | succeeds (an ephemeral port) | `EACCES` |
| connect to the stand-in listener 127.0.0.1:7897 (a trusted probe in its own transient unit, so neither sshd nor anything else on the host is touched) | succeeds | `EACCES` |
| TFO `sendto(MSG_FASTOPEN)` to the stand-in listener 127.0.0.1:7897 (`net.ipv4.tcp_fastopen` read first; the client bit must be set) | connects | `EACCES` |
| connect 127.0.0.1:5081 (the tunnel) | succeeds | succeeds |
| UDP socket and bind (IPv4, IPv6) | succeeds | `EACCES` at `socket()` |
| MPTCP socket (`IPPROTO_MPTCP`; `net.mptcp.enabled=1` read first) | succeeds | `EACCES` |
| `socket(AF_INET, SOCK_STREAM, IPPROTO_UDP)` (the allow-list on a protocol with no module) | `EPROTONOSUPPORT` | `EACCES` (the filter answers first) |
| io_uring `IORING_OP_SOCKET` (`kernel.io_uring_disabled` read first) | succeeds | `EPERM` at `io_uring_setup` |
| a listening socket inherited as descriptor 3 (a test wrapper opens it) | visible to the probe | absent (`/proc/self/fd` = 0, 1, 2) |
| from the child: `ptrace(PTRACE_ATTACH, parent)` and `pidfd_getfd(parent, n)` | the same calls in the permitted direction (parent to child, as the supervisor uses them) succeed. The reverse can't have a true positive control on this host: Yama scope 1 refuses it by itself, and lowering the sysctl would be a host change. The line proves the layered refusal, not each layer alone | fail (`EPERM`) |
| the supervisor killed, then `listen()` | (n/a) | fails |
| the IP filter: a listener on 0.0.0.0/[::] reached from `bg-dev-services01` and through the VM's own addresses | reachable (the reachability procedure) | times out |

The container on the test runner proves the logic first, but its kernel (the services VM's Debian kernel) isn't the mission VM's. `pidfd_getfd` and the seccomp notification may need `--cap-add SYS_PTRACE` on that one test container: a flag, not a daemon change, but a runner change that needs the owner's authorization (S12-D10). Without it the supervisor is proven on the VM only. No container control creates an SCTP socket either; the services VM runs PostgreSQL on the same kernel.

### What still needs VM proof (assumptions, not facts)

- **The IP filter attaches** on systemd 255.4 (`-BPF_FRAMEWORK`): its in-tree firewall (`BPF_CGROUP_INET_INGRESS`/`EGRESS` programs) on a mission unit's cgroup. To my knowledge it uses systemd's own BPF assembler, not libbpf, so `BPF_FRAMEWORK` shouldn't matter. But nothing counts until:
  - the pre-check sees the expected programs;
  - `journalctl -b -g firewalling` has no "BPF firewalling not supported" line since boot (systemd prints it once per boot, for the first unit that uses the filter);
  - the reachability comparison shows the filtered listener (run B) unreachable where the allow-listed one (runs A1 and A2) was reachable.
- **The filter's scope:** `IPAddressAllow=localhost` admits IPv4-mapped loopback (`::ffff:127.0.0.1`) and nothing broader, and applies to both families of the unit's sockets.
- **Landlock on 6.8:** ABI 4 refuses port 0 and both families as described (the VM's ABI query returned 4 on 2026-10-18).
- **TCP Fast Open:** the bypass exists on 6.8 (the control proves it can connect), and the seccomp flag rule stops it.
- **Yama:** `ptrace_scope` must be exactly 1 on the VM (`yama` was in the LSM list; the value is read at execution). Below 1 the launcher refuses to start; at 2 or 3 the supervisor's `pidfd_getfd` needs `CAP_SYS_PTRACE`, so every `listen()` would fail (fail closed, but the mission can't serve), and the launcher refuses those too.
- **Fail-closed preconditions of the supervisor:** the mission process must stay dumpable, or `pidfd_getfd` fails and every `listen()` is refused. Every `AF_UNIX` `listen()` is refused too, because the check accepts only a TCP socket on the slot port. If Unity needs an `AF_UNIX` listener, that is an explicit policy decision, not a silent allowance.
- **Unity Transport:** its WebSocket server binds its port explicitly before `listen()` (so the supervisor allows it) and needs no UDP or TFO (Task 11 records every socket the player opens).
- **The VM's firewall state:** `ufw` inactive and nftables empty on 2026-10-18 (Stage 10 records), re-read at execution. The VM refused SSH on port 22 when this plan was written (2026-10-20); nothing about the host was read since.

## Task 9: B24, the repository side (test-first, nothing installed)

**Files:** create `Backend/deploy/mission/launcher/bg-mission-launch.c`, its `Makefile` and a static build through a new `vm-test.sh --launcher` mode (digest-pinned GCC image; build networked without secrets, tests with `--network none`, as `--unity-core` does); `Backend/deploy/mission/launcher/probe.c` (the probe binary of the table above); modify `blackglass-mission-slot.service.in`, `render-slot-units.sh`, `verify.sh` (TCP probes, the IP filter's boot-time check, `ip_local_port_range`, `kernel.yama.ptrace_scope`, `kernel.io_uring_disabled`), `publish-agent.sh` (ships the launcher with the agent release and records its SHA-256), `README.md`; tests `Backend/tests/MissionHost.Agent.Tests/Deploy/SlotTemplateTests.cs` (rendering) and `Backend/deploy/mission/launcher/tests/run.sh` (in the container).

**Tests (RED first, each prohibited operation beside its positive control):**
- the template renders the pre-check, the launcher line with the slot's port for all 8 slots, the IP lines and the io_uring filter, and no `~listen`;
- the full probe table above under the launcher and in its control, in the container;
- every fail-closed path: ABI below 4 (simulated), `landlock_restrict_self` failing, the seccomp install failing, an extra descriptor that can't be closed, a bad argument, `execve` failing. Each exits 78 and never runs the program;
- the supervisor: a bound slot-port listen allowed with the notified backlog; an unbound one refused; a racing `dup2` that can't change the outcome; the TID-to-TGID mapping for a listen from a non-main thread; the supervisor killed, after which `listen()` fails; the child's exit status and signal propagated; the child unable to `ptrace` or `pidfd_getfd` the supervisor;
- the TCP Fast Open rule (`sendto`, `sendmsg` and `sendmmsg` with `MSG_FASTOPEN`) against its control; no SCTP socket in any test (the allow-list is proven with MPTCP and `IPPROTO_UDP` over `SOCK_STREAM`); `/proc/modules` equal before and after the container run.

- [ ] Steps: the tests → RED → implement → GREEN (`vm-test.sh --launcher` and `--agent`) → commit. Nothing is copied to `bg-dev-mission01`.

## Task 10: B24 demonstration on `bg-dev-mission01` (owner-approved manifest first)

Not executed without the owner's approval of its host manifest (written at the start of this task, in the format of the outbox self-test's manifest). Only trusted probe code runs; no Unity binary.

- **No installed file changes:** the launcher and the probe come as a disposable probe release `/opt/blackglass-b24probe/<rev>/` (root-owned; removed at cleanup). Every probe runs in a **transient** unit (`systemd-run`) carrying the Stage 12 slot template's properties verbatim (rendered from the repository template for slot 03, port 7803, with a dynamic user of its own), never through the installed `blackglass-mission-NN@` templates, `/opt/blackglass/mission/current` or the agent release. The agent stays disabled and the tunnel up. Before and after: the same whole-tree integrity baseline and tunnel continuity checks as the self-test manifest.
- **Template prerequisites in transient form:** `LoadCredential` and `slot-guard` are left out (no credential exists; the probe needs none). Every other property is rendered as installed, and a test pins that the transient unit's properties equal the rendered template's apart from those two lines, so the demonstration matches the real unit's sandbox.
- **Probes from inside a mission unit** (slot 03, port 7803): the whole proof table of "B24 security design", every row a must-refuse (or must-allow) line, each against its control. `EADDRINUSE` (the tunnel holds 5081) or a timeout proves nothing and fails the line. Nothing is merely recorded. A row that can't be refused (the unbound `listen()` without S12-D9) keeps B24 blocked.
- **Kernel state:** `/proc/modules` and `kernel.modules_disabled` recorded before and after; no probe creates an SCTP socket.
- **Control run without the launcher** (same transient unit shape, the probe executed directly): every prohibited operation of the table succeeds, or gives its documented native error, which shows the refusals above are the launcher's.
- **Address half:** by the reachability procedure (`2026-10-20-phase-13-stage12-reachability-procedure.md`): the same listener allow-listed (runs A1 and A2: reachable from `bg-dev-services01` over IPv4 and IPv6) and filtered (unreachable there, loopback still open), plus the pre-check's attached programs and the journal's BPF lines.
- **Evidence:** the probe transcript, `systemctl show` of the unit's effective properties, `verify.sh`'s TCP section, the residuals for the owner. B24 is satisfied only if every "refused" line holds.

## Task 11: Unity on Linux under B24 (owner-approved manifest first; after Task 10)

Two runs in transient units carrying the Stage 12 slot template's properties (the launcher, the IP filter; as in Task 10, never the installed templates), each bounded by `RuntimeMaxSec`, with no backend, no credential and no client:

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
| I3 manifest: a dirty build | resolved in revision 2: the provenance correction (`88c0e39`, `7d7fbf7`), a clean worktree build at `310e9aa` stamped `dirty=false` and `provenance=verified`, every shipped file hashed; the install step verifies all of it |
| I4 Task 10: refusals not attributable; no positive controls | `EACCES` only; a control run without the launcher; an off-host positive control (S12-D8); explicit timeouts |
| I5 MPTCP, SCTP, IPv6 unprobed | probed in Tasks 9 and 10; S12-D3 (c) closes them with seccomp if Landlock doesn't |
| I6 test 9 skipped `ReadyReport.Parse` and `ObjectiveList.Validate` | Task 7 runs the backend's whole ready path over the exact bytes, preferably through an extracted `ReadyValidation` |
| I7 the attempt path never ran on Linux Mono | Task 11 run 2: the fixture attempt on the VM, compared byte for byte with the Editor |
| Minors | M1 a `main_thread` bound on time inside `Step`; M2 a self-test option without a phase fails, never a mission; M3 a native `RENAME_NOREPLACE` check (`noreplace`, errno 17); M4 a comment; M5–M9 the manifest (invocation-scoped journal, exact expected lines, a whole-tree baseline with the tunnel's PID and restart count, cleanup, `bash -s`); M10 socket activation evaluated; M11 wording; M12 the filter's journal and boot-time checks; M13 test 4 by sequence; M14 a static table test; M15 Review Focus 2 restated; M16 `-netBindCheck` is a code task and holds for capture; M17 S12-D6 and the seccomp details; M18 the template prerequisites; M19 combat coverage; M20 an injectable gate; M21 the stale ledger comment; M22 the runner's seccomp profile; M23 the `NativeLibc` comment |

## Owner rulings (2026-10-20, Stage 12 planning) and revision 2

- **Build provenance:** fixed on the self-test branch (`88c0e39`). The source state is captured before Unity starts, Unity only stamps it, and a change that survives the build fails it. The binary's SHA-256 is recorded.
- **The self-test stays separate:** the core reference is kept (it is needed to compile the diagnostic); the root check's uid reader is local to the diagnostic, and the core is byte-identical to `main`.
- **B24:**
  - a C launcher versioned with the agent's trusted components;
  - the mission keeps its non-root dynamic user;
  - every step fails closed;
  - the evaluation is in "B24 security design", with each case's handling and each prohibited operation against a positive control;
  - the unbound `listen()` is S12-D9: B24 stays blocked unless the supervisor is approved and proven, or the owner accepts the residual;
  - the installed Stage 10 templates are not modified.
- **The reachability experiment:** prepared in its own procedure; nothing is exposed until the owner approves it.

## Security review 2 (2026-10-20, independent reviewer, revision 2, the self-test at `310e9aa` and the manifest revision 3)

0 Critical. The earlier C1 and I1–I7 were all confirmed resolved. Five new Important findings, all applied:

| Finding | Resolution |
|---|---|
| I-N1 provenance: ignored files Unity imports and index flags were invisible to `git status` | `build-provenance.sh` counts ignored files under `Assets`, `Packages` and `ProjectSettings` as changes, refuses assume-unchanged, skip-worktree and sparse checkouts, refreshes the index, and compares a content hash of every change (also M1); tests 8/8 (`91d5185`) |
| I-N2 manifest: the journal identity would include PID 1's `_UID=0` lines and overstated its independence | `_TRANSPORT=stdout` only, described as systemd's connect-time credentials; the independent evidence is an automatic owner check (files owned by the reported euid) and `/proc` for the held run; manifest revision 4 |
| I-N3 TCP Fast Open bypasses Landlock's connect hook on 6.8 | a seccomp flag rule on `sendto`, `sendmsg` and `sendmmsg`; a proof row with its control; listed for VM proof |
| I-N4 the supervisor's protection rested on an unchecked Yama setting | fork first, Landlock and seccomp in the child only, a non-dumpable supervisor, the launcher refuses `ptrace_scope` below 1, proof rows |
| I-N5 an SCTP control would autoload `sctp.ko` on the host | no SCTP socket anywhere; the allow-list is proven with protocols that need no module; `/proc/modules` before and after every VM manifest |
| Minors | M2 pipefail and checked tar; M4 internal uid reader; M5 the option-seen rule; M6 key=value output; M7 no link-following in the state listing; M8 a wider baseline; M9 tests rerun in the clean worktree; M10 IPv6 redaction; M11 Task 10's must-refuse wording; M12 socket activation restored; M13 the supervisor's details; M14 the filter's evidence; M15 the reachability procedure |

## Re-review 3 (2026-10-20, independent reviewer: the review 2 fixes)

0 Critical. I-N1 to I-N5 were confirmed resolved, with the reviewer's own fake-Unity probes of the provenance script. One new Important finding, and minors:

| Finding | Resolution |
|---|---|
| I-R1 the reachability driver waited on `read` while its stdin was the script, so run B was skipped | the driver is step-based (`before`, `start <run>`, `finish <run>`, `after`, `cleanup`), one non-interactive SSH call per step, with a per-run journal token. A local run with stubbed system tools shows each step returning on its own, three distinct tokens, and the correct allow-lists for A1, B and A2 |
| N-m1 textconv, external diff drivers, replace objects, fsmonitor | refused or neutralized in `build-provenance.sh`; tests 10/10 (`5551a00`) |
| N-m3 resume's owner check isn't independent | stated in the manifest (§5.4) |
| N-m4 sshd drop-ins | hashed in the baseline |
| N-m5 the driver's run isolation, trap and empty addresses | tokens; no trap needed (no interactive session); an empty address is reported as skipped |
| N-m6 the sshd-based controls and the supervisor's preconditions | a stand-in listener (7897) as the connect and TFO target; Yama exactly 1, a dumpable mission and the `AF_UNIX` listen policy listed |
| N-m7 stale statements | corrected (Review Focus 5, S12-D8, Task 1, launcher step 3) |
| M3 PackageCache | noted as not covered |

## Re-check 4 (2026-10-20, independent reviewer: the I-R1 fix and the last deltas)

**No unresolved Critical or Important finding.** I-R1 was confirmed resolved with the driver fed through a pipe, as `ssh … bash -s` feeds it: no step reads stdin, three distinct run tokens, the allow-lists match, and `start` refuses overlapping runs. N-m1, N-m4, N-m5 and N-m6 are resolved; provenance tests 10/10 from an archive of `5551a00`. Six new minors, applied in the procedure's revision 2.1:
- m1: the run name is validated in the step itself;
- m2: the probe sources must each be one address;
- m3: stale statements here are corrected;
- m4: the module list is taken first and the addresses recorded;
- m5: the journal matches require the unit and its stdout stream;
- m6: a skipped own-address probe leaves its cell unproven.

