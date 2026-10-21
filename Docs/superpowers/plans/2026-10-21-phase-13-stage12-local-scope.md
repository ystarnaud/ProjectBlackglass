# Stage 12 — Revised Scope: Local Unity Work First

**Status (2026-10-21): for the owner's review; not authorized for implementation.** It reorders `2026-10-20-phase-13-stage12-server-mission.md` (revision 2.2). It changes no task's content, and none of its acceptance criteria. Where the two differ on order, this document wins; on content, the plan wins.

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
