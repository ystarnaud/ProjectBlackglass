# Networking experiment, first slice: server gate and two clients (design)

Date: 2026-10-11. Branch: `networking-gate-slice` (off `main` @ `0f4ce4d`). The owner chose this scope, the library approach (FishNet, spike first), the client shape (a minimal net-test client) and the reachability method (SSH tunnel) in conversation on 2026-10-11. The written spec still needs the owner's review.

## 1. Goal and scope

Show that an authoritative Linux dedicated server can take orders from remote clients through a server-side gate in front of the existing command path, and replicate the result back, without changing any client scene, HUD, input or gameplay code.

This is the first slice of the audit's §7 proof ("Smallest client/server proof"). It is **not** Checkpoint A (which had no networking) and **not** Phase 13 (authenticated between-mission flow and trusted settlement).

**In scope:**
- a FishNet spike with explicit go/no-go criteria, and a library decision record;
- plain-C# request messages, net ids and a `ServerCommandGate` in front of `CommandableUnit.Issue` and `SetMoveIntent`;
- a thin FishNet server adapter wired into the existing server bootstrap, behind a `-net` option;
- a minimal net-test client (capsule proxies, Input System controls, an on-screen log) and a headless `-netharness` mode;
- a reproducible Windows client build and a tunnel-based run on `bg-dev-mission01`;
- EditMode and PlayMode tests, and a recorded acceptance run;
- narrow updates to `CLAUDE.md`, the audit (§9) and the run guide, plus a decision record.

**Acceptance checks covered (numbering from audit §7):**

| Check | Covered |
|---|---|
| A2 direct control and queued moves | yes, with latency measured through the tunnel |
| A3 nine bad requests | **six** of nine (see below) |
| A4 server-resolved attack, replicated health | yes |
| A9 non-owner client | yes |
| A10 suites stay green | yes |
| A1 (container, 10-minute run) | the 10-minute idle run is repeated with FishNet present; the container is not |

**A3, the six covered:** a move for a hostile; an attack on a friendly; a duplicate command id; an older sequence number; a wrong attempt id; a malformed request.

**A3, the three deferred:** an attack on an unobserved hostile, an ability on cooldown, a collect from an unsearched container. The fixture mission has no intelligence model, abilities or loot (decision 045), so they belong to the hidden-information slice.

**Out of scope (each needs the owner's separate authorization):**
- hidden information and per-connection visibility (A5, A6): in this slice clients see every unit, and the spec says so;
- streaming level geometry to clients: the client draws capsules on a flat plane;
- reconnect (A7), accounts, the backend, PostgreSQL, settlement (A8), Docker, HAProxy, orchestration;
- pause policy: clients cannot send pause or time messages, so the server never pauses;
- the real game scene in a remote mode, client-side prediction, PvP, several missions per process;
- Asset Studio, any change to client scenes, prefabs, data or gameplay behaviour.

## 2. Starting point (observed 2026-10-11)

- **Checkpoint A is on `main`.** `ServerBootstrap` (`Scripts/Server/`) parses `ServerOptions`, caps the frame rate, generates the fixture mission (seed 1), disables unit views on spawned instances and runs the optional diagnostic. `ServerMission.unity` holds only simulation systems, under three roots: `Server`, `Systems` and `Mission`. A 2-hour VM run held 60 fps, about 1.4 % of one core and a flat 106.4 MB after warm-up (audit §9.2).
- **The command boundary has no caller identity** (audit F1). `CommandableUnit.Issue(UnitCommand, IssueMode)` accepts any order for any unit and returns false when it cannot be carried out; `SetMoveIntent(Vector3)` holds the intent until set again (`CommandableUnit.cs:179, 266`). Commands hold object references (`Health`, `CoverLocation` and so on) and carry no id or sequence number (audit F2).
- **Units have no stable network id.** They are named `FriendlyUnit_n` / `HostileUnit_n`.
- **No networking package is in `Packages/manifest.json`.** Neither FishNet, Netcode for GameObjects nor Unity Transport is installed.
- **The VM.** `bg-dev-mission01`: Ubuntu 24.04, 4 vCPU, 7.9 GiB RAM. Its firewall (`ufw`) is inactive and only SSH listens. Its address looks publicly routable, which is why the server binds loopback only (§3.7). The repo never contains its address or key file names.
- **The Editor allows one instance per project.** Tests and builds run one at a time.
- **Baselines (main, 2026-10-11):** EditMode 1271/1271, PlayMode 1367/1367.

## 3. Design

### 3.1 Library decision and the spike

The audit lists FishNet as a candidate only and marks its fit as missing evidence. CLAUDE.md asks for a tradeoff before any major dependency. The owner chose **FishNet, decided by a spike**. The spike is the first task and gates every task that touches the library.

**Go/no-go: all five must hold.**
1. FishNet installs and compiles on Unity 6000.3.25f1 with a clean console, and the existing suites stay green. Preferred install is a package-manager reference so the vendor source is not committed. If it has to live under `Assets/`, the owner is asked first (CLAUDE.md: no large assets without discussion).
2. A Linux Dedicated Server (Mono) build containing FishNet starts on `bg-dev-mission01`, and a Windows client connects to it through the SSH tunnel on a **TCP-based** transport. The spike establishes which transport FishNet offers; none is assumed.
3. FishNet's timing does not depend on `Time.timeScale`, and its transform synchronisation works in the headless server build.
4. The per-connection visibility API exists and compiles. It is not exercised here.
5. A 10-minute idle server run with FishNet present shows no meaningful regression against the Checkpoint A baseline (60 fps, CPU about 1.4 %, memory about 106 MB once warm).

**On failure the work stops and the owner decides.** The evidence and a recommended fallback (Netcode for GameObjects or a thin layer on Unity Transport) are reported first. A different library is a new dependency, so it is not substituted silently.

**Outcome:** decision 046, recording what was chosen, why, the alternatives rejected, and what the spike measured.

### 3.2 Protocol: plain C#, no library types (`Scripts/Net/`)

The messages and the gate must not reference any networking library, so the design stays portable and testable in EditMode. A source-audit test enforces it (§4).

- **`NetIds`.** The server numbers every unit at spawn (an `int`, starting at 1, never reused within an attempt) and maps id to `CommandableUnit` and its `Health`. Unknown ids resolve to nothing.
- **Request envelope.** Every request carries: `attemptId` (string), `sequence` (a per-connection, strictly increasing `uint`), and `commandId` (a per-connection `uint`, unique).
- **Request payloads: a closed set of four.**
  - `MoveIntent`: unit id and a direction (flattened and length-clamped by the existing `SetMoveIntent`).
  - `MoveTo`: unit id, a destination, and an `append` flag (maps to `IssueMode.Append`).
  - `Attack`: unit id and a target unit id.
  - `Stop`: unit id.

  There is no message that sets health, position, cooldowns or any state directly. A test enumerates the request types and fails if the set changes (A4).
- **`RejectReason`:** `Malformed`, `WrongAttempt`, `StaleSequence`, `DuplicateCommand`, `UnknownUnit`, `NotOwner`, `WrongSide`, `OrderRefused` (the existing `Issue` returned false).
- **Result.** Each request gets an answer: accepted, or the reject reason, correlated by `commandId`.

### 3.3 `ServerCommandGate` (`Scripts/Net/`, plain C#)

Input: the connection's identity (the account and the set of net ids it owns), the envelope, and the payload. Output: accepted or a `RejectReason`. It **never throws** and a rejected request **changes nothing**.

Checks, in this order:
1. `Malformed`: a missing payload, an unknown payload type, NaN or infinite numbers, an `append` flag on a non-move request.
2. `WrongAttempt`: the envelope's attempt id is not the mission's `InstanceId`.
3. `StaleSequence`: the sequence is not greater than the connection's last accepted one.
4. `DuplicateCommand`: the command id is among the connection's most recent 256.
5. `UnknownUnit`: a net id that maps to nothing, or to a dead unit.
6. `NotOwner`: the unit is not in the connection's owned set. This covers a move ordered for a hostile.
7. `WrongSide`: for an attack, the target is not a hostile of the attacker (an attack on a friendly).
8. Then the existing `Issue` or `SetMoveIntent`; a false result from `Issue` is `OrderRefused`.

**Sequence consumption.** Checks 1 to 4 are freshness checks. A request that passes them records its sequence and command id, even if it later fails ownership, side or `Issue`. A replay of that request is then a `DuplicateCommand`. A request that fails checks 1 to 4 records nothing.

**Move-intent expiry.** The gate keeps the real time of each unit's last accepted `MoveIntent`. A server tick clears any intent not refreshed within 0.5 s (`SetMoveIntent(Vector3.zero)`), so a client that drops or stalls cannot leave a unit walking. Clients refresh a held intent at 10 Hz and on every change. A zero intent clears it at once.

### 3.4 Server adapter (library-dependent, small)

- **`NetServer`** (under `Scripts/Net/`, FishNet types only here). It starts the library's server **only when `-net` is passed**, listening on `127.0.0.1`, so the Checkpoint A diagnostic and smoke run behave as before without it.
- **Connection.** On connect the client sends a development token. The server maps it to an account using a **token file** on the VM, outside the repo (`/opt/blackglass/dev-tokens.txt`, path given by `-netTokens`). Format, one line per entry: `<account> <token> <squad|none> [attempt]`. An entry with `squad` owns `Friendlies[0]` and `Friendlies[1]`; an entry with `none` owns nothing. An entry with an `attempt` field is refused unless that value equals the mission's `InstanceId`, so a fixture entry with a made-up attempt id demonstrates "a ticket for another attempt is refused". The tokens are labelled development fixtures and are never committed.
- **After a successful token check** the server replies with the mission's `InstanceId` (the attempt id), the connection's owned net ids, and the list of units.
- **Receiving requests.** Every request goes to the gate; the result goes back to that connection only.
- **Replication.** Each unit gets a small state component carrying its net id, side, health, current order kind and queue length. Position and rotation replicate through the library's transform synchronisation if the spike shows it works headless; otherwise as compact snapshots at 20 Hz. The spike's outcome decides this and is recorded in decision 046.
- **Bootstrap wiring.** `ServerBootstrap` reads `-net`, `-netPort` (default 7770), `-netTokens`, and `-netQuiet`, starts the adapter after `Ready`, and logs `[Net] listening`, one line per accepted or rejected connection, and one per rejected request (reason, account, command id). `-netQuiet` applies the diagnostic's controls (hostile AI and the other friendlies' autonomy off, the owned units' hit roll forced) so the acceptance run is repeatable. Those controls are **extracted from `ServerDiagnostic` into one shared helper** used by both, with the diagnostic's existing tests still passing.
- **Scene.** The adapter and the library's manager object are added to `ServerMission.unity` by the existing editor builder. The scene-composition test's allowed-type list is extended accordingly.

### 3.5 Net-test client (`NetTestClient.unity`, built by an editor builder)

- **View.** A flat ground plane, a plain camera, and one capsule proxy per replicated unit, coloured by side. Proxies have no NavMeshAgent, AI, health logic or collider-driven behaviour. Positions are interpolated with a 100 ms buffer. Dead units are hidden.
- **Input** (Input System, per CLAUDE.md): WASD sends `MoveIntent`; a ground click sends `MoveTo`, with Shift for `append`; a click on a hostile sends `Attack`; X sends `Stop`. Input only requests; it holds no rules.
- **Overlay.** Connection state, round-trip time, the last reject reason, and each own unit's health, order kind and queue length.
- **Arguments:** `-server <host>`, `-port <n>`, `-token <t>`, `-netharness`.
- **No existing code changes.** The client scene is new; no existing client scene, HUD, input action asset or gameplay file is modified.

### 3.6 Headless harness (`-netharness`)

A windowless client mode that runs a fixed script and ends with a PASS or FAIL line and an exit code (0 pass, 1 check failed, 2 could not connect, 124 killed by `timeout`). All waits are real-time deadlines.

1. As account A: send a good `MoveIntent`, then a good `MoveTo` and an appended `MoveTo`; require the replicated position to move, the replicated queue to match, and record the round-trip latency (median of 10).
2. As account A: a good `Attack`; require the target's replicated health to fall.
3. As account A: the six bad requests; require the typed reason for each, and require that every replicated unit state (positions, health, order kind, queue length) is unchanged across the rejections.
4. As account B (a second harness run): an order for each of A's units, for each of the four payloads; require `NotOwner` every time and no state change.
5. As the account whose token names another attempt: require the connection to be refused.

### 3.7 Reachability, build and tooling

- **Tunnel.** The server listens on `127.0.0.1:<port>` on `bg-dev-mission01`. Clients connect through `ssh -L <port>:127.0.0.1:<port> bg-dev-mission01`, using the existing alias and the Blackglass key. No firewall change and no new listening address.
- **Builds.** `Tools/build-server.sh` is unchanged in shape and now includes FishNet. A new editor method and `Tools/build-netclient.sh` build the Windows client player into `Builds/NetTestClient/`, which stays ignored by git. Both clients are that one executable started with different tokens.
- **Run scripts.** `Tools/LinuxServer/run-net.sh` on the VM starts the server with `-net -netQuiet` and the token file; a local `Tools/run-net-harness.sh` opens the tunnel, runs the harness for each account, and records the log under the scratchpad.
- **Latency.** A2's 150 ms target is stated for a LAN. Through the tunnel the figure is measured and reported with the SSH round-trip time beside it, and the check is reported as met or missed on the measured number.

### 3.8 Files

New, under `Assets/_Project/`: `Scripts/Net/` (protocol, `NetIds`, `ServerCommandGate`, `NetServer`, client components, harness), `Editor/Net/` (client scene builder, client build), `Scenes/NetTestClient.unity`, tests (§4). Modified: `ServerBootstrap` (options and wiring), `ServerDiagnostic` (shared controls extracted), `ServerSceneBuilder` (adapter objects), `ServerMissionPlayModeTests` (allowed types), `Packages/manifest.json` (if FishNet installs by reference). Scripts and docs as listed in §6.

## 4. Verification and acceptance

**EditMode (new):**
- the gate accepts a good `MoveIntent`, `MoveTo`, appended `MoveTo`, `Attack` and `Stop` for an owned living unit;
- each of the six bad requests is rejected with its reason, throws nothing and changes no unit state;
- a rejected authorisation failure still consumes the sequence and command id; a freshness failure does not;
- the 256-entry command id window drops the oldest;
- an intent expires after 0.5 s without a refresh and is kept by a refresh;
- net ids are never reused and unknown ids resolve to nothing;
- the request types are exactly the four listed (A4);
- **source audit:** the protocol and gate files reference no networking library namespace;
- `ServerOptions` parses the new `-net*` arguments with the existing rules.

**PlayMode (new):** server and client in one process over the library's loopback, covering a move, an attack with replicated health, and B being refused. If the spike shows loopback is not practical, these become gate-level tests with real units and no transport, and the spec is amended before the plan.

**Existing tests:** EditMode and PlayMode suites re-run against a fresh baseline; the Checkpoint A diagnostic tests keep passing after the shared-controls extraction.

**Editor validation, Linux build and Linux runtime are reported separately**, as in Checkpoint A:
- Editor: the suites above.
- Linux build: the reproducible server build with FishNet, with the size and hash of the archive.
- Linux runtime: the Checkpoint A diagnostic still PASS; a 10-minute idle run with FishNet; the harness for A through the tunnel (A2, A3, A4); the harness for B (A9); the token-for-another-attempt refusal.

**Done when:** the spike's five criteria hold; EditMode and PlayMode are green; the harness passes on the VM for A and B; the latency figure is reported; the three deferred A3 requests and the other deferred items are listed as such in the completion report; nothing generated is committed.

## 5. Risks and open points

- **FishNet fit** (Unity 6.3, the Linux server build, headless transform sync, a TCP transport). Mitigated by the spike, which can stop the work.
- **Tunnel latency.** TCP over SSH adds delay and can stall behind loss. A2 is measured and reported honestly, not assumed.
- **Single-instance Editor.** Tasks that run tests, builds or the spike's Editor checks cannot overlap.
- **Dev tokens.** Static, outside the repo, and only reachable through the tunnel. They are a fixture, not an identity scheme; that decision belongs to Phase 13.
- **Information boundary.** Clients receive every unit's position and health in this slice. Nothing here is a claim of hidden-information safety.
- **Assumptions I made, for the owner to overrule:** intent expiry 0.5 s; client intent refresh 10 Hz; interpolation buffer 100 ms; snapshot rate 20 Hz if snapshots are needed; a 256-entry command id window; the token file format in §3.4; `-netQuiet` as a development option; port 7770 as the default.
- **Open until the spike:** the install method (package reference or `Assets/`), the transport, and whether position uses library sync or snapshots.

## 6. Source control

- Branch `networking-gate-slice`; commits grouped by task, each with the project's co-author trailer. The work is not pushed or merged without the owner.
- The owner's two unrelated uncommitted settings files and `.claude/` stay untouched and uncommitted.
- Not committed: `Builds/`, Windows or Linux build output, the token file, tunnel logs, any key or credential.
- Documents: decision 046 in `Docs/Decisions.md`; a new §9.3 in `Docs/OnlineReadinessAudit.md` that distinguishes this slice from Checkpoint A and from Phase 13 and appends results without altering earlier sections; a networking section in `Docs/DedicatedServer.md`; the `CLAUDE.md` replacement below.

## Appendix A: replacement text for `CLAUDE.md`

In "Current Development Stage", replace the paragraph beginning "The step currently authorized is" and the list that follows it with:

> The step currently authorized is **the networking experiment's first slice: a server command gate and two clients** (`Docs/superpowers/specs/2026-10-11-networking-gate-design.md`). A FishNet spike decides the library. The server listens on loopback only and clients reach it through an SSH tunnel.
>
> Each of these stays out of scope until the owner authorizes it:
> - hidden information and per-connection visibility;
> - accounts, backend APIs and databases;
> - settlement redesign and reconnect;
> - Docker packaging and orchestration;
> - changes to the client scenes' input, HUD or gameplay for remote play;
> - final production content (graphics, animation, audio, story).

In "Current Priority", replace the section with:

> The networking experiment's first slice: a FishNet spike with go/no-go criteria, then a plain-C# server gate in front of `CommandableUnit.Issue` and `SetMoveIntent`, a thin server adapter, and a minimal net-test client, proven by a headless harness against the Linux server on `bg-dev-mission01`.
>
> - Keep the gate and the messages free of networking-library types.
> - Keep client scenes and gameplay behaviour unchanged.
>
> Later steps, each authorized separately: hidden information and per-connection visibility, then Phase 13 (authenticated between-mission flow and trusted settlement).
>
> Do not begin later-stage systems until specifically directed.
