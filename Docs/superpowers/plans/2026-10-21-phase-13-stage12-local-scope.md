# Stage 12 — Revised Scope: Local Unity Work First

**Status (2026-10-21): Track L authorized by the owner and implemented on `phase13-stage12` (L1–L7 plus the review fixes; see §5 "As built"); stopped for the owner. Not merged, not pushed. Track N blocked on B24.** It reorders `2026-10-20-phase-13-stage12-server-mission.md` (revision 2.2). It changes no task's content, and none of its acceptance criteria. Where the two differ on order, this document wins; on content, the plan wins.

**B24:** mandatory and unsatisfied. The custom listen supervisor is paused by the owner's ruling of 2026-10-21, and the prototype branch stays empty. The architecture comparison the owner asked for is not part of this document (see "Not covered").

**B25:** unchanged. It is the approved one-entry-per-roll online container fill with its nine acceptance tests, from the Stage 11 plan's "Final owner rulings".

## 1. Two tracks

### Track L: local Unity and backend-domain work (no VM, no real mission)

Each task is tested entirely in Unity EditMode or PlayMode on the workstation; Task 7 also runs the backend domain tests (no database, no network). None of them starts a Linux mission, needs B24, or touches either VM.

| Order | Plan task | Tests | Depends on |
|---|---|---|---|
| L1 | Task 2: `AttemptStart`, the validated attempt (core) | `AttemptStartTests` (EditMode) | the gameplay assembly → core reference (S12-D7; see §3) |
| L2 | Task 3: the non-merging attempt fill and pool registration (**B25**) | `AttemptLootTests`, `OnlineContainerFillTests` (EditMode): the nine B25 tests, forced duplicate-roll fixtures included | L1 |
| L3 | Task 4: `GenerateForAttempt` and the ready report | `ReadyReportBuilderTests` (EditMode), `AttemptMissionPlayTests` part 1 (PlayMode) | L2 |
| L4 | Task 5: roster and inventory from the snapshot | `SnapshotRosterTests`, `SnapshotInventoryTests` (EditMode) | L1 |
| L5 | Task 6: `AttemptSession`: ledger wiring, freeze and the manifest | `AttemptSessionTests` (EditMode), `AttemptMissionPlayTests` part 2 (PlayMode) | L3, L4 |
| L6 | Task 7: cross-runtime fixtures, the backend judging generated attempts | `AttemptFixtureExportTests` (EditMode; committed files equal a fresh export), `AttemptFixtureTests` (backend domain) | L5 |
| L7 | Task 8: abilities and combat on the server, `-attemptFixture` in Editor and fixture builds only (S12-D4) | `AttemptAbilitiesPlayTests` (PlayMode) | L5 |

L4 can run beside L2–L3 (it needs only L1), but with one Unity instance at a time the order above is the practical one.

**B25's evidence** comes entirely from Track L: L2's nine tests, plus L6, where the real backend `LootPoolValidator` accepts every generated fixture. B25 can therefore be completed and reviewed before any B24 work resumes.

**What Track L delivers:** the authoritative attempt runs end to end in the Editor:
- a fixture snapshot;
- the validated start;
- the B25 fill;
- generation and the ready report;
- the snapshot roster and inventory;
- authoritative abilities and items;
- the frozen ledger and the truthful manifest, with the backend domain accepting the generated ready reports and manifests.

This is the server half of the first persistent online mission, playable locally through `-attemptFixture` with the existing network client view on loopback. It still lacks Stage 13's HTTP backend source and the online client scene, which are later, separately authorized work.

**Gates per task:** each task ends with full EditMode and PlayMode green (one Unity process at a time), no change to the offline client scenes, prefabs, HUD or input, and the Checkpoint A diagnostic still passing at Track L's end.

### Track N: network-security-dependent deployment work (blocked on B24)

| Plan task | Why it waits |
|---|---|
| Task 9: B24, the repository side | its design is what the owner is reassessing; the paused supervisor was part of it |
| Task 10: B24 demonstration on `bg-dev-mission01` | needs a chosen B24 architecture, its own manifest and the owner's approval; the reachability procedure (S12-D8) belongs here |
| Task 11: Unity on Linux under B24 (the transport check and the fixture attempt on Linux Mono) | needs Task 10; no real Unity mission workload runs on the mission VM before B24 is demonstrated there |
| Stage 12 slot template changes | follow the chosen B24 architecture; the installed Stage 10 templates are unchanged until a later owner-approved manifest |

Every host manifest in Track N runs all host checks before capturing the integrity baseline (the self-test's accepted lesson).

### Task 12: final validation

Unchanged: Stage 12 is accepted only with B24 (Track N) **and** B25 (Track L) evidenced. Track L finishing does not accept Stage 12.

## 2. Proposed order

1. Owner review of this scope.
2. **The gameplay assembly → core reference** (§3), on a new Stage 12 implementation branch from `main`.
3. **Track L, L1 to L7,** test-first, one task at a time. An independent review closes L2 and L6 (B25), and the whole track.
4. **Stop for the owner:** Track L's results, B25's evidence and the Editor-playable fixture attempt.
5. **Track N,** once the owner rules on the B24 architecture.

## 3. Decisions needed before Track L starts

| # | Question | Recommendation |
|---|---|---|
| L-D1 | **Where the gameplay assembly's reference to the core comes from.** S12-D7 approved it; today it exists only on `phase13-outbox-selftest`, together with the accepted self-test. | Merge `phase13-outbox-selftest` into the Stage 12 implementation branch first, so the accepted diagnostic and the reference arrive together. Alternatively, re-create only the reference. Either way, no merge into `main` without the owner's approval. |
| L-D2 | **The branch.** | A new `phase13-stage12` from `main` `075fcd4`, with the plan branch merged or rebased in so the plan travels with the code. |
| L-D3 | **Track L's stop point.** | After L7, with B25 reviewed, before any Track N work. |

## 4. Not covered

The B24 architecture reassessment (the comparison of options A–D, the recommendation, the residuals, the Stage 10 changes and the proofs needed on `bg-dev-mission01`) is not in this document. Its preparation in this session was stopped by a safety classifier and was not retried. It needs another route chosen by the owner.

## 5. As built (2026-10-21, branch `phase13-stage12`; stopped after L7 for the owner)

The owner authorized Track L on 2026-10-21. Not merged, not pushed. Track N (B24) untouched: no VM operation, no tunnel, agent,
slot template, firewall or systemd change, no real Unity workload on `bg-dev-mission01`, no live backend. The paused supervisor
branch is unused.

**Branch (L-D1, L-D2):** `phase13-stage12` from `main` `075fcd4`, with the plan branch merged (`2c0fb4b`) and the accepted outbox
self-test branch merged (`5f0e36e`, S12-D7's core reference). The two branches share no file. The self-test runs only on
`-outboxSelftest` or one of its options, and no asset, setting or package changed.

| Step | Commit | What |
|---|---|---|
| L1 (Task 2) | `aae5d87` | `AttemptStart`: the validated attempt; refusal codes in order |
| L2 (Task 3, B25) | `21ccf41` | `LootContainer.InitializeForAttempt` (one entry per roll, fail closed), `AttemptLoot`, `ItemLedgerMapping` |
| L3 (Task 4) | `17fa613` | `MissionDirector.GenerateForAttempt`, `ReadyReportBuilder`, `AttemptReady`, `ServerFixture` |
| L4 (Task 5) | `1841dfc` | snapshot roster and inventory (`Restore`, `AttemptSquad`, `FromSnapshot`, snapshot modes) |
| L5 (Task 6) | `3aa4e59` | `AttemptSession` (ledger wiring, freeze, manifest), `ServerClock`, `UnitItems` detail events |
| L6 (Task 7) | `e1cd9f1` | `Backend/contracts/attempt-v1` from Unity's generation; backend `AttemptFixtureTests` |
| L7 (Task 8) | `62b8608` | `-attemptFixture`, `IAttemptSource`, server scene with the snapshot squad; abilities, combat, loopback |
| Review fixes | (this commit) | the independent review's two Important findings and the minors listed below |

**Counts.**
- Baseline on the combined tree: EditMode 1864, PlayMode 1434.
- Final: see "Final validation" below.
- Backend domain: 803/803 (790 plus 13 `AttemptFixtureTests`).
- Not run: the agent and integration suites. No backend source changed, and the integration suite needs the services VM.
- Unchanged from `main`: manifest-v1, ledger-v1, the catalogue and `Backend/src`, byte for byte. `LedgerScenarioTests.A_merged_container_line_is_refused_by_the_ready_report_rule` is unchanged.

**B25 evidence (the owner's nine tests).**
- **Tests 1–8:** `OnlineContainerFillTests` and `AttemptLootTests`.
  - Forced duplicate medkits (×1 and ×2) in one container.
  - Partial and complete collects.
  - Destination merging.
  - Loot first, oldest first by collection sequence.
  - Invalid quantities refused before ready.
  - Lines stable after source mutations.
  - The loot hash against an independent SHA-256 of the backend's text.
  - Offline layout and loot hashes and merged contents, pinned at `aae5d87` before any loot change.
- **Test 9:** the backend's real ready path and `ManifestValidator` judge 7 generated fixtures and 4 deliberately invalid ones.
  - Every valid fixture passes.
  - Every invalid fixture gets exactly its expected refusal.
  - `AttemptFixturePlayTests` shows each seeded fixture's ready report is byte for byte what `GenerateForAttempt` produces.
- **The server fixture mission (seed 1)** holds the I1 case naturally: container 1 has medkit ×1 and medkit ×2, now two valid lines.
- **Generation fails closed:** a bad roll, a missing loot table, or an exception after the fill stops it. No other layout attempt rerolls.

**What plays in the Editor.** `-attemptFixture` (Editor and fixture builds only) runs the server half of an online attempt end to end:
- the fixture's register answer and the validated start;
- the snapshot roster and inventory;
- generation with the B25 fill and the ready report (logged);
- authoritative abilities (Recon Scan, Aimed Shot, cooldowns) and combat (hostile AI, basic attacks, deaths);
- items feeding the ledger;
- the freeze at the first end, and the manifest (logged hash; nothing written or sent).

The existing network client on loopback is welcomed with the snapshot squad and steers it. A full mission played from start to
finish through the network client was not tested: the tests end the mission by scripted elimination and extraction.

**Rulings** (the full list with costs is in the session ledger and the commit messages):
- `AttemptStart` adds `bad_response` (no answer or a negative seed) and `bad_template`, and runs the ledger's snapshot rules on a throwaway ledger.
- `ReadyReportBuilder` takes `ReadyObjective(id, required)`.
- The roster also refuses a stated rank that isn't the track's (`bad_progression`), and more operatives than a mission spawns (`unsupported_squad_size`). The session refuses an unspawned operative (`squad_not_spawned`).
- An admitted bag over capacity keeps its admitted entries as capacity (S5-D7).
- The fixtures use the shipped catalogue and table.
- `ReadyValidation` was not extracted from `ProcessStore.ReadyAsync`. That would change `Accounts.Persistence`, and only the services VM can verify it. The test runs the endpoint's three checks in its order instead.
- The loot table goes to the director only for an attempt, so the `-diagnostic` and `-net` fixture mission is unchanged.
- An attempt's seed is never logged.

**Ruled by the owner afterwards (see "Final rulings" below):** the `outcomeSequence` definition, the supported templates and removal-only online containers.

**Independent review (2026-10-21):** 0 Critical, 2 Important, 9 Minor.
- **Fixed, each with a test that failed first or a mutation check:**
  - I1, a bag admitted over capacity refused;
  - I2, operatives beyond the spawn limit;
  - Minor 1, regraded Important: a reroll after an exception;
  - Minor 4, regraded Important: the release guard pinned in source;
  - Minors 2, 5 and 6;
  - Minor 3 in part: the template id;
  - Minor 8: the gap tests.
- **Deferred:** a director-level test of every roll code (the container-level tests cover all four). The template refusal, the `outcomeSequence` definition and the deposit refusal were ruled by the owner and built (see "Final rulings").

**Track N and B24:** unchanged and blocked. B24 remains mandatory and unsatisfied, and Stage 12 is accepted only with B24 and B25.
Track L's code doesn't depend on any isolation mechanism.

**Final validation (2026-10-21, on the review-fix tree):**
- EditMode 1961/1961 and PlayMode 1454/1454, run one after the other.
- Backend domain 803/803.
- The Checkpoint A diagnostic passes (`ServerMissionPlayModeTests`).
- The changed paths were audited: no offline scene, prefab, HUD, input, package or project setting changed. The only scene change is the regenerated `ServerMission.unity`.

## 6. Final rulings (2026-10-21) and acceptance

The owner approved Track L at `cc5ac6c`, subject to three corrections (decision 051), final verification, an independent review
and then the merge.

1. **`outcomeSequence`** is the number of the first committed terminal gameplay event (`OutcomeSequence`, `AttemptSession`).
   - The counter starts at zero and counts each committed authoritative state change once:
     - health changes: damage, healing and death, for any unit;
     - a resolved basic-attack miss; a hit counts as its damage;
     - an accepted ability use;
     - an item event the ledger applied: a collect per container entry moved, a use, an equip;
     - an interaction's first completion: a container's search, or a terminal;
     - an objective transition.
   - Refused, malformed, duplicate, unauthorized and no-effect requests aren't counted. That includes a re-open of a searched container, a zero change and a ledger-refused item event.
   - One action can cause several transitions, and each is counted. A killing blow is damage plus a death; a medkit is the use plus the healing.
   - The terminal event takes the next number, and the freeze keeps it. Nothing afterwards changes the number, the result, the bytes or the hash.
   - An overflow fails closed: Failed, no bytes, `sequence_overflow`.
   - Anti-replay sequences are untouched, and manifest-v1 is unchanged.
2. **Templates:** exactly `procedural-standard:1`, 1 to 3 operatives (`MissionTemplates.Supported`).
   - `AttemptStart` refuses `unsupported_template` (no fallback) and `unsupported_squad_size` before any generation, ready report or gameplay.
   - The bootstrap exits 2 with the code.
   - No HTTP. The backend allowlist is unchanged.
3. **Online containers are removal-only after the fill** (`ItemInventory.LockAdditions`).
   - `Add`, `TryAddEntry` and `RoomFor` refuse before any change, so a deposit through `ItemTransfer` moves nothing.
   - Collection still removes.
   - An attempt container can't be re-initialized.
   - Offline inventories never lock, so `ItemInventory.Add` and offline stacking behave as before.
   - The ledger remains the backstop: an unreported change is still a `ledgerMismatch`.

**Commits:**
- `ca1005e`: the three corrections.
- `c813478`: the review follow-ups. The full committed-event set (I-A), a read-only `Sequence`, the locked re-initialization, and template spec validation.
- A third commit: a re-open isn't counted.

Each correction's tests failed first, or were mutation-checked: removing a correction failed its tests.

**Independent review of the corrections:** 0 Critical, 1 Important (I-A, the counted set narrower than the ruling), 5 Minor.
- I-A is fixed. Minors 1, 2, 4 and 5 are fixed, and Minor 3 is documented above.
- A scoped re-check of the fix found 1 Important (a re-open of a searched container was counted) and 2 Minor (documentation). All are fixed: the re-open test failed first.
- The earlier review's Importants remain closed: the over-capacity bag, the squad size, `squad_not_spawned`, no reroll after an exception, and the release guard.
- The new `AttemptSourceFactory` seam can't enable a fixture attempt in a release build: the gate is checked first, and the default factory returns null there.
