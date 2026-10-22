# Loopback fixture mission checkpoint (local, after Stage 12 Track L)

Status: in progress (2026-10-21). A small local checkpoint authorized by the owner after accepting Track L. It is **not** Stage 13,
Stage 14, Track N or B24 work. Branch `phase13-loopback-checkpoint` from `main` `139a9a0`. No merge or push without approval.

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
   - In fixture builds, the `ATTEMPT FROZEN` line gains `outcomeSequence=`, and the canonical manifest is logged once.
2. **Client overlay.** Each own unit's position, each observed hostile's health, and the cursor's ground point `x,z`; the cursor line (with the selected unit) is drawn by `NetTestControls`, the only place allowed to read the mouse.
   All of this is information the client already holds, shown so an operator can click a known coordinate.
3. **Scripted assisted client role.** `-netharness attempt -assistHostile x,z -assistExtraction x,z`.
   - It sends only the existing requests, in this order:
     1. move both units toward the hostile hint;
     2. attack the hostile once it is replicated (observed);
     3. after it dies (its avatar reports dead), move both units to the extraction hint;
     4. pass when an own unit stands inside the zone radius.
   - It reads only what the client decoded, plus the two operator coordinates.
4. **Local Windows fixture server build.** `Tools/build-local-fixture-server.sh` builds `ServerMission` as a Windows 64 player
   with the `BLACKGLASS_NET_FIXTURE` define (non-development, like the Linux fixture build) to `Builds/LocalFixtureServer/`.
   - The demonstration runs two light processes instead of the Editor; the PC has about 2.8 GB free.
   - The Linux release and fixture builds are unchanged.
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
