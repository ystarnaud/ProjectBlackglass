# Loopback fixture mission checkpoint (local, after Stage 12 Track L)

Status: **Assisted automated loopback mission complete. Human-controlled playtest and player-facing online environment remain outstanding.** Accepted by the owner (final rulings 2026-10-22, below) and merged into `main`. Stage 12 overall is not complete: B24 and Track N remain open. A small local checkpoint authorized by the owner after accepting Track L. It is **not** Stage 13,
Stage 14, Track N or B24 work. Branch `phase13-loopback-checkpoint` from `main` `139a9a0`.

## Goal

One complete fixture mission over local loopback, driven through the existing network client and gate:
the attempt snapshot loads, the snapshot squad spawns with its equipment, the client authenticates with a development token,
moves and attacks through the gate, the hostile fights through its normal AI, its elimination opens extraction, a living
operative walks into the zone, the server detects extraction on its own, `AttemptSession` freezes once and builds a canonical
manifest, and nothing is committed anywhere.

## What exists, and the limits found

- **Server.** `ServerMission` with `-attemptFixture <file>` (Editor and fixture builds only), then `-net` exactly as before. The
  fixture `03-seeded-collect-and-use.json` is the server fixture: seed 1, the 2x2 grid, two operatives (C Kestrel, D Darius), one
  hostile, eliminate-hostiles, no terminal, extraction needing one living operative (`ServerFixture.Settings()`).
  Without `-netQuiet` the hostile's `EnemyAI` and `AutoRetaliate` run, and so do the friendlies' `CompanionAI` and `AutoRetaliate`
  (normal autonomy), with real hit rolls. `-netFixture` and `-netQuiet` are not used.
- **Client.** `NetTestClient` sends only the four existing requests (`NetMoveIntent`, `NetMoveTo`, `NetAttack`, `NetStop`).
  It shows a capsule per replicated avatar (its own units, and the hostile only while the squad observes it), a grey disc
  where a hostile was last seen, and a text overlay; the camera frames the visible avatars.
- **Limits.**
  - The client receives no geometry, objectives, extraction location or mission end. Nothing on the wire says where the
    hostile or the extraction zone is.
  - So a run is necessarily an **assisted fixture test**: an operator reads the hostile's spawn and the extraction zone from
    the server's own log and steers toward them.
  - This never shows that a normal client can discover or navigate the level.
  - No message is added to close that gap; that belongs to the online client stages.

## Changes (no new network message, no change to what is replicated)

1. **Operator aid, server log only.** After `ATTEMPT READY`, a fixture attempt in an Editor or fixture build logs
   `FIXTURE OPERATOR AID (server log only; never sent to a client) extraction=x,z radius=2.0 hostile1=x,z`.
   - The code is compiled only under `UNITY_EDITOR || BLACKGLASS_NET_FIXTURE`, and a source audit pins it.
   - The seed is still never logged.
   - The `ATTEMPT FROZEN` line gains `outcomeSequence=` (every build; only Editor and fixture builds can run an attempt), and in Editor and fixture builds the canonical manifest is logged once.
2. **Client overlay.** Each own unit's position, each observed hostile's health, and the cursor's ground point `x,z`; the cursor line (with the selected unit) is drawn by `NetTestControls`, the only place allowed to read the mouse.
   All of this is information the client already holds, shown so an operator can click a known coordinate.
3. **Scripted assisted client role.** `-netharness attempt -assistHostile x,z -assistExtraction x,z`.
   - It sends only the existing requests, in this order:
     1. move both units toward the hostile hint;
     2. attack the hostile once it is replicated (observed); the role fails unless at least one of its attack requests is accepted by the gate;
     3. after it dies (its avatar reports dead), move both units to the extraction hint;
     4. pass when an own unit stands inside the zone radius.
   - It reads only what the client decoded, plus the two operator coordinates.
4. **Local Windows fixture server build.** `Tools/build-local-fixture-server.sh` builds `ServerMission` as a Windows 64 player
   with the `BLACKGLASS_NET_FIXTURE` define (non-development, like the Linux fixture build) to `Builds/LocalFixtureServer/`.
   - The demonstration runs two light processes instead of the Editor; the PC has about 2.8 GB free.
   - `ServerBuild.cs` (the Linux release and fixture builds) is unchanged; a Linux fixture build run with `-attemptFixture` would also log the operator aid and the manifest.
5. **PlayMode regression test** `AttemptLoopbackPlayTests`: the server scene in the Editor with the real gate (host mode, as
   `NetLoopbackRig`), `-attemptFixture` 03 and `-net` without quiet, the `attempt` role driving the client.
   - It asserts the freeze, a Success outcome, the canonical manifest, `outcomeSequence = committed + 1`, both operatives' equipment
     matching the snapshot, and that no XP or inventory was settled.
   - No warp, no direct damage and no terminal call.

## Manual test procedure (owner, player-driven, assisted)

The Unity Editor must be closed for the builds. Run from the repository root in Git Bash.

1. Build both players, one after the other:
   - `Tools/build-local-fixture-server.sh` gives `Builds/LocalFixtureServer/BlackglassServer.exe`.
   - `Tools/build-netclient.sh` gives `Builds/NetTestClient/NetTestClient.exe`.
2. Make a local development token file (release format, no `+fixture`):

   ```bash
   mkdir -p "$HOME/blackglass-local"
   BLACKGLASS_TOKENS="$HOME/blackglass-local/dev-tokens.txt" Tools/LinuxServer/make-dev-tokens.sh --force
   TOKEN="$(awk '$1=="account-a"{print $2}' "$HOME/blackglass-local/dev-tokens.txt")"
   ```
3. Start the server (leave this terminal open):

   ```bash
   Builds/LocalFixtureServer/BlackglassServer.exe -batchmode -nographics -logFile "$HOME/blackglass-local/server.log" \
     -attemptFixture "$(pwd -W)/Backend/contracts/attempt-v1/03-seeded-collect-and-use.json" \
     -net -netPort 7770 -netTokens "$(cygpath -w "$HOME/blackglass-local/dev-tokens.txt")" -heartbeat 10 &
   tail -f "$HOME/blackglass-local/server.log" | grep --line-buffered -E "\[Server\]|\[Net\]"
   ```
4. Wait for `ATTEMPT READY`, `[Net] listening 127.0.0.1:7770` and the `FIXTURE OPERATOR AID` line, and note `hostile1=x,z` and
   `extraction=x,z` (for seed 1: hostile `10.5,4.5`, extraction `-9.5,4.5`; the squad starts near `-8.5,-7.5` and `-5.5,-8.5`).
5. In a second terminal, start the interactive client:

   ```bash
   Builds/NetTestClient/NetTestClient.exe -server 127.0.0.1 -port 7770 -token "$TOKEN" -screen-fullscreen 0 -screen-width 1280 -screen-height 800
   ```
6. The overlay shows `state Ready`, units `#1` and `#2` with hp, order and position (`at x,z`), and below them `selected #1   cursor x,z`.
   The server logs `client 1 welcomed account=account-a owns=2`.
7. **Move.**
   - Press 1 and click the ground toward the hostile hint (the camera follows your units, so walk in steps).
   - Press 2 and do the same.
   - WASD steers the selected unit.
8. **Attack.** When a red capsule appears (the squad observes the hostile), press 1 and click it, then press 2 and click it.
   - The overlay shows the hostile's hp.
   - The hostile fires back by itself, and your units may also fire on their own (normal autonomy).
9. **Elimination.** The red capsule disappears with no grey disc: it died.
   - A grey disc means it was only lost from sight; walk back toward the disc.
   - The server log shows nothing yet; extraction opens silently (the client has no objective display).
10. **Extract.**
    - Move the cursor until it reads the extraction coordinates, and click with one unit selected (walking in steps as needed).
    - When it enters the 2 m radius, the server logs `ATTEMPT FROZEN outcome=Success build=Normal integrity=Ok`, then `outcomeSequence` and `manifestHash`.
    - The client shows nothing about the end; the pending result and the receipt are later stages.
11. Close the client window, then stop the server (`kill %1`, or close its terminal). Keep `server.log` as the evidence.

Expected: no reward, XP, inventory or energy change anywhere. There is no backend and no outbox, the roster and the inventory are
in snapshot mode, and the server never settles.

## Validation

- Run the affected EditMode classes (`NetClientOptionsTests`, `NetSourceAuditTests`, `AttemptFixtureSourceTests`, the overlay
  formatting test), the PlayMode `AttemptLoopbackPlayTests` plus the existing `AttemptNetPlayTests` and `ServerMissionPlayModeTests`,
  then both full suites once.
- Unity processes run one at a time; the two players of the demonstration are the only concurrent pair.
- One independent review at the end.

## Recorded runs and results (2026-10-21)

The local Windows fixture server was built at `47190d8` (stamped `dirty` because of the owner's two settings files and an uncommitted script edit). The client was built at `d3b7f48`. Tokens were a release-format file made by `make-dev-tokens.sh` in the session scratchpad; no token appears in either log.

| Run | What | Result |
|---|---|---|
| A | Two processes, windowless scripted client (`-batchmode -nographics -netharness attempt -assistHostile 10.5,4.5 -assistExtraction -9.5,4.5`) | Harness PASS, 9 steps. The server accepted 6 requests and rejected 0 (2 moves toward the hostile, 2 attacks, 2 moves to extraction), and logged `ATTEMPT FROZEN outcome=Success build=Normal integrity=Ok outcomeSequence=8 manifestHash=sha256:24caa6daf0c54bada6d9eab8edd4ad9da1d53b34ad63cbe4253aea3a099ee57b`. Listener: only TCP `127.0.0.1:7770`. |
| B | The same, client windowed (captured with PrintWindow, client window only) | Harness PASS, `outcomeSequence=8`. The frames show the blue capsules walking, `hostile #3 hp 2/60` in red while both units attack, then the walk to `-9.5,4.5`, then `state Disconnected`. |
| D | Interactive client, no harness (connect only) | Welcomed `owns=2`. The overlay shows `#1 hp 80/80 None queue 0 at -8.5,-7.5`, `#2 hp 130/130 ... at -5.5,-8.5`, the controls line and `selected #1 cursor x,z`, over a plain grey field: no geometry, no objectives, no extraction marker. |

Run A's evidence:
- **Manifest hash.** The logged manifest bytes (1113) hash to the logged `manifestHash`.
- **Ready report.** The logged ready report is byte-identical to the committed `03-seeded-collect-and-use.ready.json`.
- **Backend judgement.** A throwaway console program in the session scratchpad (not in the repo) ran the backend's real `ReadyReport.Parse`, `ObjectiveList`, `LootPoolValidator`, `LootHash`, `ResultManifest.Parse` and `ManifestValidator` over both files:
  - no errors;
  - the bytes are canonical;
  - the hash is the same.
- **Manifest content.**
  - Both operatives are alive, with exactly their snapshot items: C holds `2k` (equipped weapon), `2n` and medkit ×2; D holds `2m` (equipped weapon) and medkit ×1.
  - Nothing was collected or consumed.
  - `eliminate` and `extract` are both completed.
  - `outcomeSequence` is 8 (7 committed events, then the terminal one).

What the hostile did: in every run its `EnemyAI` was enabled and acquired a target (`hostileEngaged=True` in the PlayMode run). The squad, ordered to attack once it was observed, killed it with 3 shots (76 damage, with overkill, against 60 hp) before it fired. So the hostile's normal AI was live and engaging, but no hostile shot was observed in these runs.

Tests on the final code:
- EditMode 2005/2005 (full, at `27940eb`).
- PlayMode 1458/1458 (full, at `27940eb`).
- After the review fixes: the affected PlayMode classes, 19/19 (`AttemptLoopbackPlayTests`, `AttemptNetPlayTests`, `NetHarnessPlayModeTests`, `ServerMissionPlayModeTests`).

Independent review: 0 Critical and 1 Important (the test had no `[Timeout]`; fixed). Minors fixed: a lost-sight race on the first attack and a dead unit on the extraction move, plus doc wording. Minors left open:
- a hostile that dies while hidden fails the scripted role (rare);
- hints without `-netharness attempt` are ignored silently;
- pre-existing: the `options seed=1(fixture)` line prints the scene's default seed, which equals fixture 03's attempt seed (fixed by final ruling 1).

## Final rulings and final validation (2026-10-22)

The owner accepted the scripted, assisted, two-process loopback integration test. It proves the authenticated WebSocket gameplay path, authoritative movement and combat, extraction through ordinary network commands and backend-valid manifest generation. It does not prove a human-controlled playtest, a complete gameplay client, or backend HTTP integration.

1. **No numeric seed in attempt mode.** The `options` line of an attempt reads `seed=attempt`; neither the scene's default seed nor an ignored `-seed` is printed. Non-attempt runs are unchanged. Tests: `ServerOptionsTests.Describe_InAnAttempt_NamesNoNumericSeed` (EditMode) and a log check in `AttemptNetPlayTests` (no `seed=<digit>` from the attempt's options line on); both failed first.
2. **Final suites,** run one after the other in a clean worktree at the final code revision `e43cb45`: EditMode 2006/2006, PlayMode 1459/1459 (949 s). The strict verify afterwards found no tracked change; its only finding was the untracked report files `PresentationPerformanceReportTests` writes into that worktree's own `.claude/scratchpad/phase125/`. The commits after `e43cb45` change documentation only.
3. **Final binaries and run.** Built from a clean detached worktree at `e43cb45` (no uncommitted files), each build bracketed by `Tools/build-provenance.sh capture` and `verify` (both passed, `dirty=false`):
   - local fixture server: `build-info.txt` `revision=e43cb45b83cf6f6e2416b636bb9b11fe51a1264c dirty=false variant=local-fixture`; `Blackglass.dll` sha256 `f5e9130321186883b9d4544e834c8b1bab46bb54f5485c5bef2216daa497261e`;
   - NetTestClient (no embedded stamp; the bracketing capture names `e43cb45`): `Blackglass.dll` sha256 `0b76d16f7bb27d1e3bf9dabd95b49a4840016a0db9d2dd1b2b2bc3554c944c81`. (Both `.exe` files are Unity's identical player stub.)
   - The separate-process run: server listening only on TCP `127.0.0.1:7770`; client welcomed `owns=2`; harness PASS, 10 steps; all 6 requests accepted (2 moves toward the hostile, 2 attacks, 2 moves to extraction), none refused; `ATTEMPT FROZEN outcome=Success build=Normal integrity=Ok outcomeSequence=8 manifestHash=sha256:0fa8b5583abf910e0009ee777d6a242b32fe052bbfa3ba1ce2107f99dce8b83f`. The 1113 manifest bytes hash to it; the logged ready report is byte-identical to the committed `03-seeded-collect-and-use.ready.json`; the backend's real `ReadyReport`, `ObjectiveList`, `LootPoolValidator`, `LootHash`, `ResultManifest.Parse` and `ManifestValidator` report no errors and the bytes are canonical. The manifest differs from the accepted run's only in `buildRevision` and `serverElapsedMs`. No token in either log.
4. **Fixture-only logs and the protocol.** A Linux release server built with `Tools/build-server.sh` in the clean worktree at `e43cb45` (`provenance=verified`, `dirty=false`) contains none of `FIXTURE OPERATOR AID`, `attempt manifest `, `LogFixtureOperatorAid`, `LogFixtureManifest` or `FixtureAttemptSource` in any managed assembly, while `ATTEMPT FROZEN`, `seed=attempt` and the release refusal of `-attemptFixture` are present (the fixture build contains the operator aid, as expected). The release package was only built locally, never deployed. Since `main`, no network message, replicated field, gate, session host, admission, token, scene, prefab, settings, package or backend file changed; the server change is log lines only.
5. **The hostile fires.** `AttemptLoopbackPlayTests.The_hostile_fires_on_its_own_at_a_squad_that_only_walks_up`: the client only moves the squad toward the hostile through the gate (no attack request; the gate's accepted count equals the moves), the AI runs unfrozen and combat is unchanged. The hostile fired on its own about 5 s after the moves and its attack resolved (1 shot, a hit); no squad shot came before it engaged. A mutation check (hostile AI disabled) fails the test with 0 shots in 180 s.
6. **Review.** A fresh review of everything since the previous review, including the refusal-handling changes, found 0 Critical, 2 Important, 6 Minor. Both Importants are fixed (`e43cb45`):
   - the attempt role could pass with no attack request accepted (the squad's own autonomous attack also replicates as Attack): the role now re-orders until one of its attacks is accepted and has a step `attack accepted`; the loopback test requires it (failed first);
   - the hostile-fire test's "squad held fire until the hostile's first shot" was stronger than the game promises (a companion may assist once the hostile has engaged): it now asserts no squad shot before the hostile engaged and only logs shots before its first shot (a loosened assertion; no failing run was possible).
   The refusal handling itself (non-fatal first attack, `any` on the extraction move) has no race and stays far under the request budget (at most 2 requests a second).
   Minors left open: the hostile-fire test's 240 s timeout can pre-empt its diagnostic line; its "no attack order" check ignores refused and dropped counts; the loopback test's timeout comment understates setup; the no-seed log check covers only the `seed=` form and lines before the test body; `-seed` with `-attemptFixture` is ignored silently; refusals inside the re-order loop do not reach the harness's failure text.

**Provenance.** `Tools/build-provenance.sh` refuses the main working tree because `.claude/scratchpad/poc` and `.claude/scratchpad/sb` are git worktrees of this repository: `git ls-files --others` lists a nested repository as a bare directory entry, and `sha256sum` refuses a directory, so the capture fails ("content hash failed"). That is the script refusing content it cannot hash, which is correct; it was not changed, and the owner's scratchpad was not touched. A clean `git worktree add --detach` outside the repository captures and verifies normally.
