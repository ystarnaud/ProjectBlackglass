# Phase 13 Stage 13: Server ↔ Backend Lifecycle in Unity — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status (2026-10-23): revision 1.1, planning only, for the owner's review. Not authorized for implementation.** Revision 1.1 resolves the independent architecture review (0 Critical, 7 Important, 13 Minor; every finding applied, see "Architecture review"). On the local branch `phase13-stage13-plan`, cut from `main` at `08d7d1a` (the visual network mission, accepted). Nothing here is implemented, no number below is a measurement, and nothing in this plan has run against a backend or on either VM. The independent architecture review and its resolution are recorded at the end ("Architecture review").

**Goal:** the Unity mission server, given an attempt id and its attempt-scoped process credential, runs the attempt's whole process-side lifecycle against the existing backend API: register, validate, generate, ready, the per-participant start hold with idempotent start retries, heartbeats, the outcome freeze, the durable outbox write, the bounded `recorded` notice, publication of the ready result whether or not the backend answers, and exit, without polling settlement.

**Architecture:** a small new assembly, `Blackglass.Backend.Adapter`, holds the only HTTP code: a loopback-only endpoint, the credential reader, a `UnityWebRequest` transport behind an `IHttpTransport` seam, and a pure retrying call over it. The server side adds a polled HTTP registration (`BackendAttemptSource`; the synchronous `IAttemptSource` stays unchanged), a `ParticipantStartHold`, a `ProcessHeartbeat`, a `ResultPublisher` over Stage 11's `OutboxWorker`, and one orchestrator, `BackendAttemptRunner`, owned by `ServerBootstrap` in a new `-attempt` mode. Every protocol rule is tested in EditMode against a scripted fake transport and a portable outbox directory; one explicit, separately approved Editor end-to-end run proves it against the real API.

**Tech stack:** Unity 6000.3.25f1 (Mono; Linux Dedicated Server subtarget for production), `UnityWebRequest` (module `com.unity.modules.unitywebrequest`, already in `Packages/manifest.json`), `JsonUtility` over Stage 11's DTOs, NUnit EditMode/PlayMode; .NET 10 / xUnit for the backend-side contract checks (no database).

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` (revision 2.14), read with the later rulings that supersede it: Stage 8 (`2026-10-17-phase-13-stage8-hosts-start.md`, B7–B11, R1, R4, R8), Stage 9 (`2026-10-17-phase-13-stage9-settlement.md`, S9-B5, S9-B6, S9-B23, §C and **§H, the publish contract**), Stage 10 (`2026-10-17-phase-13-stage10-mission-agent.md`, A10-D1, C1, I3), Stage 11 (`2026-10-19-phase-13-stage11-unity-backend-core.md`, "Final owner rulings", the Stage 12/13 handoff), Stage 12 (`2026-10-20-phase-13-stage12-server-mission.md`, `2026-10-21-phase-13-stage12-local-scope.md` "Final rulings"), and the visual network mission (`2026-10-22-visual-network-mission.md`, decision 052). Where a document's example differs from the code, **the code wins** (see "Corrections to stale documents").

---

## 1. What exists today (read from the code at `08d7d1a`)

**Backend process routes** (`Backend/src/Accounts.Api/Hosts/ProcessEndpoints.cs`; parsers in `Accounts.Domain/Attempts/MissionRequests.cs` and `ReadyReport.cs`). All take `Authorization: Bearer pc1_…` (the `Process` scheme, one database lookup per request, no cache), are rate-limited per attempt, refuse bodies over `MissionBodies.MaxBodyBytes`, are strict about members (400 `InvalidBody`), and answer `Cache-Control: no-store`. A database outage answers 503, never 401.

| Route (all under `/server/v1/attempts/{attemptId}`) | Body → answer | Definitive refusals | Idempotency (from the stores) |
|---|---|---|---|
| `POST /sessions` | `{buildRevision, catalogueVersion}` → `{processSessionId, seed, missionTemplate:{id,version}, catalogueVersion, participants:[{accountId, loadout}], deadlines:{launch}}` | 409 `BuildNotAllowed`, 409 `InvalidState`, 401 | A repeat while `launching` returns the same session and configuration; accepted while `assigned` (moves it to `launching`) or `launching` |
| `POST /ready` | `{processSessionId, layoutHash, lootTableVersion, lootHash, objectives, lootPool}` → `{joinDeadline}` | 422 `InvalidLootPool` (the attempt is cancelled), 422 `InvalidObjectives` (nothing written), 409 `WrongSession`, 409 `CatalogueMismatch`, 409 `InvalidState` (not launching, past the launch deadline, or ready with a different report), 401 | An identical repeat while `ready` answers the stored join deadline |
| `POST /tickets/redeem` | `{processSessionId, ticket}` → `{accountId, operativeIds}` | 401 (unknown, expired, revoked, redeemed by another session), 409 `WrongAttempt`, `WrongSession`, `InvalidState` | A repeat by the same session answers the same |
| `POST /participants/{acc}/start` | `{processSessionId}` → `{started:true, startedAt}` | 404 `ParticipantNotFound`, 409 `NotRedeemed`, `Cancelled`, `DeadlinePassed`, `WrongSession`, `InvalidState`, 401 | A repeat answers the stored start, **even after the join deadline** (B8); never a second charge |
| `POST /heartbeat` | `{processSessionId, phase, participants:[{accountId, connected}]}` → `{}` | 400 `UnknownParticipant`, 409 `WrongSession`, 401 | Moves started participants `joined ⇄ left` by `connected`; `unresponsive → running` |
| `POST /participants/{acc}/recorded` | `{manifestHash}` → `{state}` | 409 `ConflictingReport` (anomaly; an unsettled participant is quarantined), 409 `InvalidState`, 404, 401 | An identical repeat answers 200; **a settled attempt's current credential still authenticates here only** (T0-1) |
| `POST /failed` | `{processSessionId, reason}` (reason: `^[a-z0-9]+([._-][a-z0-9]+)*$`, ≤ 64) → `{applied}` | 409 `WrongSession`, 409 `ConflictingReport`, 401 after the attempt ended | Before any start: cancels and releases; after a start: stored as evidence only (B11 revised) |

There is **no** process status or settlement route the process should call (S9-B6), and the heartbeat carries **no directive**: stop instructions travel only through the host agent (R4).

**The credential:** the Stage 10 root broker writes exactly the ASCII credential, `pc1_` plus 43 canonical base64url characters, no newline, to `/run/blackglass/credentials/<attempt>/process`; the unit's `LoadCredential=process:…` makes it `$CREDENTIALS_DIRECTORY/process`, readable only by the slot's dynamic user.

**The outbox (Stage 11, `Assets/_Project/Scripts/Backend/Core/Outbox/`):** `OutboxDirectories.OpenForServer($STATE_DIRECTORY, "/var/lib/blackglass-outbox", attemptId)` (linux-x64 only; fails closed elsewhere) and `OpenForEditor(path)`; `OutboxWorker.Submit(account, bytes, hash)` → `PublicationHandle` with stages `Queued → Writing → Written → (AllowPublication) → Publishing → Published`, or `Conflict`, `Failed`, `Cancelled`. The worker never talks to the backend; `AllowPublication()` is Stage 13's call once the `recorded` attempt has ended.

**The server (Stage 12, `Assets/_Project/Scripts/Server/`):** `ServerBootstrap` runs `-attemptFixture <path>` (Editor and fixture builds only) through `IAttemptSource.TryLoad` (synchronous), `AttemptStart.TryCreate`, the snapshot squad and `GenerateForAttempt`; on `Ready` it begins `AttemptSession`, logs the ready report and, on `MissionFinished`, logs the frozen manifest. Nothing is sent or written. The mission runs as soon as it is ready: there is no hold. `NetServer` admits only through the development token file (`NetAdmission`), and `ServerCommandGate` has no `NotStarted` refusal.

**Stage 11's DTOs** (`Core/Dto/ProcessDtos.cs`) match the backend's bodies field for field, but their comments name stale routes (`/server/v1/session/register`, …).

**The mission unit** (`Backend/deploy/mission/blackglass-mission-slot.service.in`, installed on `bg-dev-mission01` for the trusted stub): `ExecStart=/opt/blackglass/mission/current/run-mission %i` (the attempt id only), `SystemCallFilter=~listen` (no stream listener at all), `SocketBindAllow=udp:@PORT@` (not enforced on Ubuntu 24.04), `LoadCredential=process:…`, `StateDirectory=blackglass-outbox/%i`. **A Unity server, which must listen on TCP, cannot run under it as installed.** That is B24's territory (Part C).

## 2. Corrections to stale documents (the code wins)

| Document says | The code and later rulings say | Consequence here |
|---|---|---|
| Spec §16.3: heartbeat → `{directive}` | `{}`; stop goes through the agent (R4): after a cancellation or settlement the agent's stop list sends the unit SIGTERM (`MissionHost.Agent/Supervisor.cs`) | No directive handling; the process sees two stop signals: a 401 on a session call (§3.5; never on redeem or `failed`) and SIGTERM |
| Spec §16.3: `GET …/participants/{acc}/status`; spec §9.2.6 and the old Stage 13 line: poll settlement for 120 s | No such route; S9-B6: the process never polls | The process exits after publication; the transport is tested never to see a status or receipt path |
| Spec §16.3: start errors only `409 Cancelled` | Also 404, `NotRedeemed`, `DeadlinePassed`, `WrongSession`, `InvalidState`, 401 | All handled (Task 7) |
| Spec §7.5: the process redeems tickets from `NetHelloMessage` | Correct, but that wiring is Stage 14's (`NetTicketAdmission`) | Decision S13-D1 |
| `ProcessDtos.cs` comments: `/server/v1/session/register`, `/session/ready`, `/session/start`, `/tickets/redeem`, `/session/heartbeat`, `/session/failed` | `/server/v1/attempts/{aid}/sessions`, `/ready`, `/participants/{acc}/start`, `/tickets/redeem`, `/heartbeat`, `/failed` | Comment-only correction in Task 1; no byte of any DTO changes |
| Spec §9.2 step 1: `<accountId>.json` | `<accountBase58>.manifest.tmp|written|ready` (§H) | Stage 11 already implements §H |
| Spec §0.11/§7.4: "a mission can't listen at all" was the Stage 10 posture | B24 (§0.12) requires listening on the slot's TCP port only | Part C |

---

## 3. Architecture

### 3.1 The process lifecycle (backend mode, solo; one participant per attempt as Stage 12 enforces)

```
start ─► parse options ─► read credential ─► open outbox ─► register ─► validate (AttemptStart) ─► generate
          (-attempt, -backend)  ($CREDENTIALS_DIRECTORY)  ($STATE_DIRECTORY)   (idempotent retries)        (GenerateForAttempt)
                                                                                                                │
   ┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   ▼
 Ready ─► PAUSE (TacticalPause) ─► AttemptSession.Begin ─► POST ready (same bytes on every retry) ─► HOLD
                                                                                                     │  AdmitTicket(ticket)  (Stage 14's network admission calls this;
                                                                                                     │                         Stage 13's E2E test calls it with a real ticket)
                                                                                                     ▼
                                                     redeem (idempotent) ─► account and operatives must equal the snapshot
                                                                                                     ▼
                                                     start (idempotent retries to a bound) ── 200 ──► RESUME, clock restarted ─► PLAY
                                                                    └─ definitive refusal / bound ──► "could not start" ─► failed{} ─► exit 5
 PLAY ─► first terminal event ─► AttemptSession freeze (manifest bytes + hash, Stage 12) ─► OutboxWorker.Submit
        ─► handle Written (durable .written) ─► POST recorded (≤ 30 s total, any answer ends it) ─► AllowPublication
        ─► handle Published (.ready) ─► ResultSaved ─► stop heartbeats ─► worker.Stop(drain) ─► exit 0
 Throughout, from registration: heartbeat every 10 s (one in flight); a 401 on ready, start, heartbeat or recorded ─► STOP (keep and publish any durable result; never fabricate an outcome) ─► exit 6
   (a 401 on redeem only refuses that ticket; a 401 on failed changes nothing; a heartbeat 401 after Published keeps exit 0)
```

**The order §H requires is enforced by construction:** `recorded` is sent only when `handle.Status.Stage == Written` (the hash names a durable result); `AllowPublication()` is called only after the `recorded` call ended (any definitive answer, a 401, or its 30 s bound); the process exits only after the handle is `Published`, or after a terminal `Conflict`/`Failed` that it reports. Nothing waits for the backend to settle, and nothing in the process computes or claims a reward.

### 3.2 Components and boundaries

| Component | Assembly / folder | Responsibility | Never |
|---|---|---|---|
| `BackendEndpoint` | `Blackglass.Backend.Adapter` | Parses `-backend`: only `http://127.0.0.1:<port>` or `http://[::1]:<port>`, no path, query, fragment or user info | Accepts a non-loopback host or HTTPS (the tunnel is the transport security) |
| `ProcessCredential` | Adapter | Reads `$CREDENTIALS_DIRECTORY/process`: exactly 47 bytes, `pc1_` + 43 canonical base64url; regular file; no link | Exposes the text (no public property; `ToString()` is `"pc1_<redacted>"`); appears in a log, an exception message or a command line |
| `IHttpTransport`, `HttpExchange` | Adapter | One request in flight: method, path under `/server/v1/attempts/{aid}/`, JSON body, per-attempt timeout → `Completed(status, body)` / `Timeout` / `ConnectionFailed` / `Aborted` | Knows retry policy or routes |
| `UnityWebRequestTransport` | Adapter | The only `UnityWebRequest` use: attaches `Authorization`, `Content-Type: application/json`, `Accept`; caps response bodies at 256 KiB by aborting once `downloadedBytes` passes the cap (`DownloadHandlerBuffer` can't truncate); aborts at its own deadline; polled on the main thread | Logs headers or bodies; follows redirects (`redirectLimit = 0`); runs off the main thread |
| `RetryPolicy`, `BackendCall` | Adapter | One logical call: the same body bytes on every attempt; backoff with jitter from a private `System.Random` (never `UnityEngine.Random`, which would shift the mission's hit rolls); classification into `Succeeded`, `Refused(status, code)`, `Unauthorized`, `GaveUp` | Retries a definitive answer; changes the body between attempts |
| `ProcessApi` | Adapter | Route paths and typed calls: `Register`, `Ready`, `Redeem`, `Start`, `Heartbeat`, `Recorded`, `Failed`; `JsonUtility` over Stage 11's DTOs | Calls any other route |
| `BackendAttemptSource` | `Blackglass` (`Scripts/Server/`) | Registration with retries under its own bound, polled by the bootstrap directly (it does not implement the synchronous `IAttemptSource`, which stays unchanged, S13-D8); on success it hands `(attemptId, response)` to the same validation path the fixture uses | Logs the seed |
| `ParticipantStartHold` | Server | Per account: `Waiting → Redeeming → Starting → Started`, or `CouldNotStart(reason)`, `Stopped`; the public entry `AdmitTicket(string ticket)` returns a `TicketAdmission` handle (§6) | Starts anyone without a backend `Redeemed` answer for that account; resumes the simulation before a `200 Started` |
| `ProcessHeartbeat` | Server | Every `HeartbeatSeconds`, one in flight; `phase` and `connected`; tracks `BackendReachable` (Stage 15 uses it) | Stops on anything but a 401 |
| `ResultPublisher` | Server | Freeze → `Submit` → `Written` → bounded `recorded` → `AllowPublication` → `Published`; bounded resubmission of a `Failed` write | Publishes before the `recorded` attempt ended; overwrites a conflicting result |
| `BackendAttemptRunner` | Server | Orchestrates phases, owns the exit decision and its code, the pause during the hold, the join-hold bound, `failed` reports | Holds game rules; touches FishNet |
| `ServerBootstrap` (modified) | Server | `-attempt` mode: option checks, credential and outbox opening, the runner's lifecycle; its startup-failure paths (the startup deadline, `MissionState.Failed`, a `BeginSession` refusal) routed to the runner in backend mode; the fixture path unchanged | Accepts `-attempt` with `-attemptFixture`, `-outboxSelftest`, `-diagnostic`, `-seed`, `-net`, `-netTokens`, `-netQuiet` or `-netFixture` (Stage 13) |
| `ServerCommandGate` (modified) | `Scripts/Net/Core/` | An optional start predicate: a request from a connection whose account has not started is refused with the new `NetRejectReason.NotStarted` | Changes anything when no predicate is given (every existing path) |

**Why a separate assembly (S13-D5):** it keeps `UnityWebRequest` in one auditable place (a source audit fails if it appears anywhere else), keeps networking-library types out of it (no FishNet reference), and leaves `Blackglass.Backend.Core` byte-for-byte unchanged apart from Task 1's comments, so Core's Linux CoreCLR tests and golden vectors are untouched. It references only `Blackglass.Backend.Core`; `Blackglass` references it.

### 3.3 Retry and classification (one table for every call)

| Answer | Class | Effect |
|---|---|---|
| 2xx | `Succeeded` | Parsed by the caller |
| 401 | `Unauthorized` | Definitive. After registration: the runner stops (§3.5). Before a session exists: exit 4 |
| 400, 404, 409, 413, 415, 422, and **every other status** not listed here (3xx, 403, 405, …); a redirect that `UnityWebRequest` reports as a system error under `redirectLimit = 0`; an oversize body | `Refused(status, code)` | Definitive, never retried; `code` from the ProblemDetails body (`{"code": …}`, a bounded word, or `none`; `oversize`, `redirect`) |
| 408, 429 (honouring `Retry-After` up to the backoff cap), 5xx, `Timeout`, `ConnectionFailed` | retry | Backoff from 0.5 s doubling to 8 s, ±20 % jitter (provisional), until the call's deadline, then `GaveUp` |

**Redeem is the exception to the 401 rule:** the backend answers the uniform 401 for any bad ticket (unknown, expired, revoked, redeemed by another session), so a redeem 401 refuses that ticket only; a revoked credential is learned from the next heartbeat. A 401 on `failed` (expected after a lost answer: the cancellation ended the credential) never changes the exit code.

Every call keeps its body bytes from the first attempt, so a retry is the same request: the backend's idempotency (§1) does the rest. **A lost response is just a timeout followed by an identical retry**, answered with the stored result (register, ready, redeem, start, recorded), so nothing is applied twice. No call is ever made "differently" after a failure.

### 3.4 Timers (all provisional configuration, defaults below, each a `ServerOptions` flag with a range)

| Value | Default | Flag | Bound |
|---|---|---|---|
| Request attempt timeout | 10 s | `-backendRequestTimeout` | 2..60 |
| Registration bound | 60 s, its own bound (generation keeps `-startupTimeout`, which starts only when registration succeeds) | `-backendRegisterBound` | 5..300 |
| Ready bound | min(30 s after generation, the registered `deadlines.launch` converted once to the local monotonic clock) | `-backendReadyBound` | 5..300 |
| Start bound | join deadline (as the backend stated it, converted once to the local monotonic clock) + 30 s grace | `-backendStartGrace` | 0..300 |
| Join hold (no ticket admitted) | the same as the start bound | — | — |
| Heartbeat interval | 10 s (the backend's lease is 30 s) | `-backendHeartbeat` | 2..30 |
| `recorded` bound | 30 s total (§H step 3) | `-recordedBound` | 1..120 |
| Outbox resubmissions after `Failed` | 3, 2 s apart | — | — |
| Exit drain (`OutboxWorker.Stop`) | 10 s | — | — |

**Backend timestamps** (`deadlines.launch`, `joinDeadline`, `startedAt`) are System.Text.Json `DateTime` values from PostgreSQL microseconds: UTC with a `Z` and 0 to 7 fractional digits (trailing zeros trimmed). They are parsed round-trip (`DateTimeStyles.RoundtripKind`) accepting only that shape; an unparsable or non-UTC value is a definitive refusal of that answer, never a hold with a wrong bound.

**Why the start bound may pass the join deadline:** a committed start answers its stored result even after the deadline (B8), and an uncommitted one is refused with `DeadlinePassed`, so retrying a little past it can only learn the truth; it never starts anyone late. The grace absorbs clock skew between the VMs.

### 3.5 Failure handling (pre-start versus post-start)

| Situation | Process action | Backend effect (existing) |
|---|---|---|
| Credential missing or malformed; outbox refused | Exit 4 before any backend call; the reason code only | The launch deadline cancels and releases |
| Register `409 BuildNotAllowed`/`InvalidState`, 401, or gave up | Exit 4 (no session, so no `failed`) | Launch deadline |
| `AttemptStart` refusal, generation failure (`MissionState.Failed`), the generation deadline, an `AttemptSession.Begin` refusal | `failed {reason}` (bounded, best effort; a 401 answer ignored), exit 4, never today's direct exit 2 | Cancelled, holds released (pre-start) |
| Ready `422 InvalidLootPool` | Exit 4 (the backend already cancelled) | Cancelled |
| Ready other refusal, or gave up | `failed {reason}`, exit 4 | Cancelled |
| No ticket admitted by the join hold | `failed {"join_deadline"}`, exit 5. **In practice** the backend's 15 s sweep cancels at the join deadline, so the next heartbeat's 401 usually ends the process first with exit 6; both are correct | Cancelled or released (the worker also does it) |
| A redeem refused (401, `400 InvalidBody`, 409 `WrongAttempt`/`WrongSession`/`InvalidState`, or a malformed ticket refused locally) or given up (`redeem_gave_up`) | That ticket is refused; the hold returns to `Waiting`; no stop | Nothing |
| Start: definitive refusal (`Cancelled`, `DeadlinePassed`, `NotRedeemed`, `WrongSession`, `InvalidState`, 404), or the start bound passed | "could not start" for that account; solo: `failed {"start_refused"}`, exit 5; the simulation never resumes | Released, or (lost committed start) the agent's `exited present:false` → the positive-evidence void refunds (S9-B7) |
| 401 on ready, start, heartbeat or recorded (not redeem, not `failed`; a heartbeat 401 after the handle is `Published` is the expected settlement and keeps exit 0) | **Stop:** no new simulation; publish any durable result per §H (its `recorded` step is already over: a 401 ends it); exit 6 | Revoked or cancelled: nothing settles automatically (S9-B1) |
| Backend unreachable during play | Play continues (D-11); heartbeats keep failing quietly; `BackendReachable` false | Possibly `unresponsive`; never a void |
| Backend unreachable at the freeze | `recorded` gives up after 30 s → publish → exit 0 | The agent relays `.ready` when the backend returns; settles once |
| Manifest build `Failed` (no bytes) | `failed {"manifest_build_failed"}` (post-start: evidence only), exit 7 | Agent `exited present:false` → protective void, refund |
| Outbox write `Failed` after 3 resubmissions | `failed {"outbox_write_failed"}`, exit 7 | As above, unless a durable `.written` exists (the agent relays it, A10-D1) |
| Outbox `Conflict` (a different result already exists) | Never overwrite; exit 7 | The agent relays what exists; the backend judges it |
| SIGTERM (the agent's stop after a cancellation or settlement, the operator's emergency stop) | Abort in-flight HTTP; `AllowPublication()` on any `Written` handle (shutdown ends the notice attempt); `Stop(drain)` | The agent relays `.ready` or `.written` |

---

## 4. Decisions for the owner

| # | Question | Options | Recommendation |
|---|---|---|---|
| **S13-D1** | **The ticket dependency.** The backend's start commit requires a ticket this process session redeemed (`409 NotRedeemed` otherwise), and the client-facing ticket admission (`NetTicketAdmission`, the hello token) is Stage 14's. | **(a)** Stage 13 owns the process-side redeem call and one public entry, `BackendAttemptRunner.AdmitTicket(string ticket) : TicketAdmission`, which redeems and then starts. The handle reports `Pending`, `Redeemed(account, operativeIds)`, `Refused(AdmissionRefusal)`, `Started` or `CouldNotStart(reason)`, already mapped onto Stage 11's `AdmissionRefusal`, so Stage 14's `NetTicketAdmission.Begin/Poll` is a thin per-client wrapper over it. Until Stage 14, backend mode refuses `-net` and `-netTokens`, so no network client can reach a backend attempt, and the only caller is the Part B test with a real ticket minted by the real player route. **(b)** Pull `NetTicketAdmission` forward into Stage 13. **(c)** A fixture redeem path. | **(a).** No fixture authentication exists in any production path: the ticket is real, the redemption is the backend's, and the type that starts a participant can only be built from a parsed `200` redeem answer. (b) moves Stage 14's ownership and message work forward; (c) is what the authorization forbids. |
| **S13-D2** | **The "result saved, pending" client message** (`NetResultRecordedMessage`). | (a) Stage 13 raises a `ResultSaved(account)` event and logs it; the wire message is added with Stage 14's ticketed client. (b) Add the message now. | **(a):** in Stage 13 no client can be connected to a backend attempt (S13-D1), so a message now would be untestable end to end and would touch `NetMessages` before Stage 14 owns them. |
| **S13-D3** | **The Part B environment** (needs the owner's separate approval; nothing is created by this plan). | **(a)** The real API run locally on the workstation (`dotnet run … serve`, loopback, its own port), against a disposable `bg_t_e2e_*` database on the existing `blackglass-test` cluster through the existing `tools/testdb.sh tunnel` (the documented fallback runner; the cluster is never stopped or reconfigured; the database is dropped after). **(b)** A local PostgreSQL 17 on the workstation (Docker Desktop or native), no VM at all. **(c)** Defer Part B to Stage 17. | **(a)**, because it reuses approved infrastructure and its safeguards (`cluster_name` check, `bg_t_*` only) and adds nothing to the workstation. (b) avoids the services VM but installs a database next to the Unity Editor on a memory-constrained machine. |
| **S13-D4** | **The build identity in the Editor.** The Editor reports build revision `editor`. | (a) Part B allowlists `editor` with the current catalogue for `procedural-standard:1` in the disposable database only; a player whose stamped revision is missing or malformed refuses backend mode before registering (today it would report `unknown`). (b) Stamp the Editor with the git revision. | **(a).** |
| **S13-D5** | **A separate `Blackglass.Backend.Adapter` assembly** for the HTTP code. | (a) New asmdef (references Core only); `Blackglass` and the test assemblies reference it. (b) Put it in the `Blackglass` assembly. | **(a)** (§3.2). |
| **S13-D6** | **The timing values** (§3.4). | Accept the defaults as provisional configuration, each a ranged flag. | Accept. |
| **S13-D7** | **The gate half of the hold** (`NetRejectReason.NotStarted`). | (a) Add the enum value (at the end, so existing bytes keep their meaning) and an optional start predicate on `ServerCommandGate` now, unused by every existing path. (b) Leave it to Stage 14. | **(a):** small, keeps §7.5's two halves together, and gives Stage 14 a tested seam. |
| **S13-D8** | **How registration, which spans frames, enters the bootstrap.** | (a) Make `IAttemptSource` polled (breaks `AttemptFixtureSourceTests` and the `OtherTemplateSource` wrapper in `AttemptAbilitiesPlayTests`). (b) Keep `IAttemptSource.TryLoad` unchanged; the bootstrap polls `BackendAttemptSource` directly and both paths share one `ContinueAttempt(attemptId, response)`. | **(b)** (review I6): no existing test changes. |
| **S13-D9** | **Post-start failures without a valid result** (manifest build failed, outbox write failed). | (a) Report `failed` as evidence, exit 7, and let the agent's positive evidence void and refund (S9-B7). (b) Keep the process alive for an operator. | **(a)**: it is the spec's protective outcome, and a durable `.written`, if any, is still relayed. |
| **S13-D11** | **Plain HTTP to loopback under the project's `insecureHttpOption: 0`** (`ProjectSettings.asset`, "not allowed"). Unity may or may not exempt `127.0.0.1`/`[::1]`. | Task 2's first step proves it in the Editor (Part C repeats it on the Linux player). If loopback is refused, implementation **stops** for an owner decision (allow HTTP for development builds only, or the server-only player setting); the project setting is never changed silently. | As stated. |
| **S13-D10** | **New exit codes** (evidence for the agent's `exited` report; the agent treats every code alike). | 0 published; 4 `BackendRefused` (pre-start refusal or configuration); 5 `NotStarted`; 6 `Stopped` (401 after registration); 7 `ResultNotPublished`. Existing 1–3 unchanged. | Accept. |

---

## 5. The Stage 12 dependency: three parts

### Part A — local implementation and tests (no B24, no backend, no VM): Tasks 0–11

Everything that can be proved on the workstation: the adapter, the protocol state machines, the bootstrap's `-attempt` mode, the hold, heartbeats, publication, every crash boundary and outage path against a scripted fake transport and a real portable outbox directory, the secrecy and source audits, and the **contract vectors** (`Backend/contracts/process-v1/`): the exact request bytes Unity sends, parsed by the backend's own strict parsers, and the exact response bytes the backend's own records serialize, parsed by Unity. The backend side of the vectors runs in the `UNIT` filter (`Requires!=Postgres`): no database, no VM. Part A ends with a local Linux dedicated-server compile check (built, never deployed or run on a VM).

### Part B — integration against a running backend (needs S13-D3 and the owner's approval): Tasks 12–13

The real API with a disposable PostgreSQL database; one Unity Editor PlayMode end-to-end run in an explicit category that the normal suites skip: the test plays the operator (a script prepares the database), the player (dev login, deploy, ticket, receipt) and the agent (heartbeat, claim, launching, relay of the `.ready` file, `exited`); the server under test runs in `-attempt` mode in the same Editor with the claimed credential handed to it through the bootstrap's internal credential seam (never a command line). Variants: a dropping proxy loses the first start response (charged once), and the API is stopped during play and restarted after publication (settled once, no void).

### Part C — Linux mission-host operations, **blocked by B24**: not tasks of this plan

These items belong to **Stage 12 Track N** (its Tasks 9–11: B24, the template, Unity on Linux), which stays open; Part A does not accept Stage 12. None of this runs, and no real Unity mission runs on `bg-dev-mission01`, until B24 is demonstrated there and the owner approves each VM step under its own host manifest. The Stage 10 trusted-stub installation stays exactly as it is; this plan changes no file under `Backend/deploy/mission/`. **The paused syscall-supervisor research stays paused.** What Part C will need, recorded so nothing is forgotten:
1. **B24 itself** (§18.2): an enforced per-slot TCP bind, demonstrated on the VM.
2. **The template can't host Unity today:** `SystemCallFilter=~listen` refuses every listener, and `SocketBindAllow=udp:` names the wrong protocol. Whatever B24 adopts replaces those lines; a template change is a VM change under the owner's manifest.
3. **The launcher's arguments:** `run-mission` receives only `%i`. The Unity release launcher must pass `-attempt %i -backend http://127.0.0.1:5081 -net -netPort <slot port>` (`-net` only once Stage 14 lifts S13-D1's refusal); the slot's port is not passed to `run-mission` today (a template `Environment=` line or a derivation from the unit name; decided with B24).
4. **Linux Mono runtime checks:** `UnityWebRequest` to the tunnel's loopback port, SIGTERM handling (does the player run `Application.quitting`?), the Stage 11 outbox path inside the real unit, and the credential read under `LoadCredential`.
5. **Stage 17's end-to-end evidence** (B7, B9, B13–B15) with the real agent.

---

## 6. Start commit and tickets (the Stage 14 boundary)

- **No play without a committed start, by construction.** The simulation is paused (`TacticalPause.Pause()`, the only writer of `Time.timeScale`) in the same `Ready` handler that begins the session, before any frame advances, and resumes only in `ParticipantStartHold`'s `Started` transition, which is reached only from a parsed `200 {started:true}`. `ServerClock` restarts there, so `serverElapsedMs` excludes the hold. Tested: 300 held frames change no unit position, no health and no `CommittedEvents`, and every refusal path leaves the mission paused.
- **No start without a redeemed ticket, by construction.** `ParticipantStartHold` begins a start only from a `Redeemed` value (account and operative ids) built by `ProcessApi` from a parsed `200` redeem answer; the account must be the attempt's participant and the operative ids exactly the snapshot's, in order (anything else: "could not start", reason `redeem_mismatch`).
- **The production entry.** `BackendAttemptRunner.AdmitTicket(string ticket)` is public API returning a `TicketAdmission` handle: `Pending` → `Redeemed(account, operativeIds)` → `Started`, or `Refused(AdmissionRefusal)` (`UnknownToken` for a 401, a 400 or a locally malformed ticket; `WrongAttempt`; `Unavailable` for `WrongSession`/`InvalidState` or a redeem that gave up; `AlreadyConnected` while another admission is in progress or after a start), or `CouldNotStart(reason)`. Stage 14's `NetTicketAdmission.Begin(clientId, token)`/`Poll(clientId)` keep one handle per client and map `Redeemed` onto `AdmissionDecision.Admit` (Stage 11's `INetAdmission`); the mapping is tested in Stage 13. The ticket's shape (`jt1_` + 43 canonical base64url characters) is checked locally before any request. Stage 14 also wires `ServerCommandGate`'s start predicate to the hold and replaces the heartbeat's `connected` source (Stage 13: admitted through `AdmitTicket` and not released) with the real connection state.
- **What Stage 13 cannot test end to end without Stage 14:** a network client joining with its ticket in `NetHelloMessage`, the `NotStarted` refusal on the wire, and `NetResultRecordedMessage`. The smallest safe resolution is S13-D1 (a): the Part B test hands a real, freshly minted ticket to `AdmitTicket`. **Nothing in a production path accepts a ticket that the backend did not redeem**, and backend mode refuses `-net`/`-netTokens` until Stage 14 lifts it with its own tests.
- **True dependencies on Stage 14** (none blocks Stage 13's acceptance; each is listed in the handoff): `NetTicketAdmission` over `AdmitTicket`; lifting the `-net`/`-netTokens` refusal; the gate's start predicate wired; `NetResultRecordedMessage`; the heartbeat's `connected` from real connections. On Stage 15: `BackendReachable` drives the reconnect window and resume tokens.

---

## 7. Security and secrecy

- **Credential:** read once from `$CREDENTIALS_DIRECTORY/process` (production) — never an argument, never an environment variable's value, never a fixture file in the repository. `ProcessCredential` has no public accessor; only `UnityWebRequestTransport` (same assembly) builds the header. A log scan over a whole fake lifecycle (Task 10) and over the Part B run asserts the credential, the ticket and the seed never appear, and no `ServerOptions` flag accepts a credential (an option-table test).
- **Tickets:** passed only to `AdmitTicket` and the redeem body; never logged, never stored after the call.
- **Seed:** used only by `AttemptStart`; no DTO or adapter type has a `ToString` that prints it; error logs carry the HTTP status and the problem `code` only, never a response body.
- **Loopback only:** `BackendEndpoint` refuses any non-loopback address; redirects are disabled.
- **Credential scope:** the attempt id comes from `-attempt`, must be canonical (`CanonicalIds.IsAttemptId`), and is the route's `{attemptId}`; the backend's own check (`process credential presented on another attempt's route` → 401) stays the authority.
- **Decision 052:** backend mode refuses `-net`, so the development map messages (Editor and fixture builds only, behind their compile guards, and recoverable to the seed) can't reach a backend attempt. **Stage 14 obligation:** when it lifts the `-net` refusal, backend mode in a fixture build must still never emit those messages. Decision 052's seed-recovery blocker still stands before any public or untrusted client.
- **Part B secrets:** the claimed credential's temporary directory and the account key and host secret live outside the repository (the session's temp directory), mode 600, deleted by `teardown.sh`; nothing is written under `.claude/` (untracked but not ignored).
- **B24:** unresolved; no Part C step happens in this stage.

---

## Global Constraints

- **Planning branch only until the owner authorizes implementation.** Implementation happens on its own branch from `main` after approval. No merge, push or deployment without the owner's approval.
- **No VM, tunnel, agent or backend deployment change.** Part A touches neither VM. Part B runs only after the owner approves S13-D3 and its preparation script. No real Unity mission runs on `bg-dev-mission01` before B24. The Stage 10 installation and every file under `Backend/deploy/mission/` stay unchanged.
- **The existing backend contract is the contract.** No backend route, store, migration, parser or response changes; no replacement API; no second settlement mechanism; the process never settles, never polls settlement and never computes a reward.
- **§H order:** frozen manifest → durable `.written` → bounded `recorded` (≤ 30 s, any answer ends it) → `.ready` → exit. A backend outage never discards a durable result and never causes a void by itself.
- **Secrets:** Part B's secrets only outside the repository (§7); no credential, ticket or seed in a log, an exception message, a command line, a Unity diagnostic or a committed file; no VM IP address or key name in the repository.
- **Unchanged behaviour:** the offline scenes (`ProceduralMission`, `Prototype`), their HUD, input and gameplay; the fixture attempt (`-attemptFixture`), the loopback and visual network clients, `NetServer`'s token-file admission and every existing network message; `Blackglass.Backend.Core` except Task 1's comment correction; `Backend/contracts/manifest-v1`, `ledger-v1`, `attempt-v1` bytes.
- **Repository rules:** stage explicit paths, never `git commit -a`; never stage `Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset` or `.claude/`; commit trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Unity runs are sequential** (workstation memory): one Unity process at a time, never alongside a backend test run; a run killed by the memory reaper is reported, never restarted automatically.
- **Baselines:** measured fresh in Task 0 (the last recorded: EditMode 2144, PlayMode 1494 + 1 skipped at `c72d06f`). Every existing test stays green; counts only grow.

## Review Focus

1. **A start answer lost after the backend committed it** (the retry gets `200` replayed): the participant starts once, the simulation resumes once, the clock restarts once. If instead the bound passes with no answer at all, the process must exit without playing, and the backend's evidence void must be possible (no result file, no `recorded`). Task 7 pins both with the fake transport; Task 13 pins the first against the real API through the dropping proxy.
2. **The freeze while the backend is unreachable, then a 401 or 409 when it returns mid-notice**: the result is published within the bound in every case, and exit waits for `Published`. Task 9.
3. **A 401 arriving on the heartbeat while a publication is in flight**: the stop must not abandon the outbox; the handle still reaches `Published` (or `.written` stays) before exit. Task 10.
4. **A response body with extra, missing or mistyped members** (a future backend, a proxy page on 502): `JsonUtility` silently fills defaults, so every parsed answer is validated after parsing (non-empty session id, canonical ids, operative ids equal to the snapshot's, `started == true`), and an invalid one is treated as a definitive refusal, never as success. Tasks 5 and 7.
5. **SIGTERM between `Written` and the end of the `recorded` attempt**: the durable result is published or left `.written`, never lost; nothing is half-written. Task 10.

---

## File structure

**Created (`Assets/_Project/Scripts/Backend/Adapter/`, new asmdef `Blackglass.Backend.Adapter`: references `Blackglass.Backend.Core`; `noEngineReferences: false`; no FishNet):**
- `Blackglass.Backend.Adapter.asmdef`, `AssemblyInfo.cs` (internals visible to the two test assemblies and the shared test support).
- `BackendEndpoint.cs` — loopback URL parsing.
- `ProcessCredential.cs` — the credential reader and redacted holder.
- `HttpTransport.cs` — `IHttpTransport`, `HttpExchange`, `HttpOutcome`, `HttpMethodKind`.
- `UnityWebRequestTransport.cs` — the production transport.
- `BackendCall.cs` — `RetryPolicy`, `CallState`, `BackendCall`.
- `ProcessApi.cs` — routes, typed calls, `Redeemed`, response validation, `ProblemCode`.

**Created (`Assets/_Project/Scripts/Server/`):**
- `BackendAttemptSource.cs`, `ParticipantStartHold.cs`, `ProcessHeartbeat.cs`, `ResultPublisher.cs`, `BackendAttemptRunner.cs`, `BackendTimings.cs`.

**Modified:**
- `Assets/_Project/Scripts/Blackglass.asmdef` (reference the adapter).
- (Unchanged by S13-D8 (b): `IAttemptSource.cs`, `FixtureAttemptSource.cs` and their tests.)
- `Assets/_Project/Scripts/Server/ServerOptions.cs` (`-attempt`, `-backend`, timing flags), `ServerBootstrap.cs` (backend mode; exit codes 4–7).
- `Assets/_Project/Scripts/Net/Core/NetResult.cs` (`NotStarted` appended), `ServerCommandGate.cs` (optional start predicate).
- `Assets/_Project/Scripts/Backend/Core/Dto/ProcessDtos.cs` (comments only).
- `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef`, `Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef` (references).

**Created (tests):**
- `Assets/_Project/Tests/BackendSupport/` (new Editor-only asmdef `Blackglass.Tests.BackendSupport`, `UNITY_INCLUDE_TESTS`): `FakeHttpTransport.cs` (scripted answers per path, drops, delays, a record of every request), `TempOutbox.cs` (a portable outbox in a temp directory).
- EditMode `Backend/`: `BackendEndpointTests`, `ProcessCredentialTests`, `BackendCallTests`, `ProcessApiTests`, `ProcessContractVectorTests`, `AdapterSourceAuditTests`.
- EditMode: `BackendAttemptSourceTests`, `ParticipantStartHoldTests`, `ProcessHeartbeatTests`, `ResultPublisherTests`, `BackendAttemptRunnerTests`, `ServerBootstrapBackendTests` (the bootstrap's backend-mode refusals, over the existing bootstrap test rig), and additions to `ServerOptionsTests` and `NetGateRejectionTests`.
- PlayMode: `BackendAttemptPlayTests` (the ServerMission scene in `-attempt` mode over the fake transport), `BackendE2EPlayTests` (Part B; `[Category("BackendE2E")]`, `[Explicit]`, skipped without its local configuration).
- `Backend/contracts/process-v1/` (request and response vectors, README).
- `Backend/tests/Accounts.Domain.Tests/ProcessRequestVectorTests.cs` (the backend's parsers over Unity's request bytes); `Backend/tests/Accounts.Integration.Tests/ProcessResponseVectorTests.cs` (no `Requires=Postgres` trait: the API's response records serialized with the API's own options must equal the response vectors).
- `Tools/backend-e2e/prepare.sh`, `teardown.sh`, `README.md` (written in Part A, **run only in Part B after approval**).

**Docs:** this plan; at the end, the staged plan's Stage 13 status line and `CLAUDE.md`'s status lines.

---

## Task 0: Baseline and the branch

**Files:** none committed except, at the end, a ledger line.

- [ ] **Step 1:** After the owner's authorization, create the implementation branch `phase13-stage13` from `main`; confirm `git status --short` shows only the owner's two settings files and `.claude/`.
- [ ] **Step 2:** Measure baselines, sequentially: `Tools/run-tests.sh EditMode` then `Tools/run-tests.sh PlayMode`; from `Backend/`, `dotnet test Backend.sln --filter "Requires!=Postgres"` (domain, agent and Linux-core tests that need no database). Record counts in the ledger.
- [ ] **Step 3:** Expected: every suite green with today's counts; nothing to commit.

## Task 1: The adapter assembly, the endpoint and the credential

**Files:** create the asmdef, `AssemblyInfo.cs`, `BackendEndpoint.cs`, `ProcessCredential.cs`, `Assets/_Project/Tests/BackendSupport/*`; modify `Blackglass.asmdef`, both test asmdefs, `ProcessDtos.cs` (comments only); tests `BackendEndpointTests.cs`, `ProcessCredentialTests.cs`.

**Interfaces (produces):**
```csharp
namespace Blackglass.Backend.Adapter
{
    public sealed class BackendEndpoint
    {
        public static bool TryParse(string text, out BackendEndpoint endpoint, out string refusal); // refusal: missing, not_http, not_loopback, has_path, has_query, has_userinfo, bad_port
        public string BaseUrl { get; }   // "http://127.0.0.1:5081" (no trailing slash)
    }
    public sealed class ProcessCredential
    {
        public const string FileName = "process";
        public static bool TryRead(string credentialsDirectory, out ProcessCredential credential, out string refusal); // refusal: no_directory, missing, not_regular, too_large, malformed
        public override string ToString();          // "pc1_<redacted>"
        internal string AuthorizationHeaderValue(); // "Bearer pc1_…": the transport only
        internal static ProcessCredential FromTextForTests(string text); // tests only (InternalsVisibleTo), never a production path
    }
}
```

- [ ] **Step 1: Failing tests.** `BackendEndpointTests`: accepts `http://127.0.0.1:5081` and `http://[::1]:5090`; refuses `https://127.0.0.1:5081`, `http://localhost:5081` (name resolution is not loopback proof), `http://10.0.0.1:5081`, `http://127.0.0.1:5081/x`, `?q`, `#f`, `http://u@127.0.0.1:1`, port 0 and 65536, empty and null, each with its refusal code. `ProcessCredentialTests` (temp directories): reads a valid 47-byte file; refuses a missing directory, a missing file, a directory named `process`, a symbolic link (where the platform allows creating one; otherwise `Assert.Ignore` with the reason), 48 bytes, a trailing newline, a wrong prefix (`hs1_`), a non-canonical base64url tail; `ToString()` never contains the tail; the refusal strings never contain any byte of the file.
- [ ] **Step 2:** `Tools/run-tests.sh EditMode "Blackglass.Tests.BackendEndpointTests|Blackglass.Tests.ProcessCredentialTests"` → FAIL (types missing).
- [ ] **Step 3: Implement** the two types and the asmdef wiring; correct `ProcessDtos.cs`'s route comments to the real routes (§2). Base64url check: 43 characters of `[A-Za-z0-9_-]`, and the last character in the set that encodes 32 bytes canonically (`[AEIMQUYcgkosw048]`), as the backend's `OpaqueSecret` requires.
- [ ] **Step 4:** Same command → PASS. Then `Tools/run-tests.sh EditMode "Blackglass.Tests.CoreSourceAuditTests|Blackglass.Tests.DtoTests"` → PASS (Core unchanged in behaviour).
- [ ] **Step 5: Commit** `git add` the listed paths; message `Stage 13: the backend adapter assembly, the loopback endpoint and the process credential reader`.

## Task 2: The transport seam and the `UnityWebRequest` transport

**Files:** create `HttpTransport.cs`, `UnityWebRequestTransport.cs`, `Tests/BackendSupport/FakeHttpTransport.cs`; tests `AdapterSourceAuditTests.cs`, and a transport test in `BackendCallTests.cs`'s file set (`UnityWebRequestTransportTests.cs`, EditMode, against a local `HttpListener` on 127.0.0.1 inside the test only).

**Interfaces (produces):**
```csharp
public enum HttpMethodKind { Post }
public enum HttpOutcome { Pending, Completed, Timeout, ConnectionFailed, Aborted }
public sealed class HttpExchange
{
    public HttpOutcome Outcome { get; }   // Pending until finished
    public int Status { get; }            // Completed only
    public byte[] Body { get; }           // Completed only; null otherwise; at most MaxResponseBytes
    public string RetryAfter { get; }     // the raw header or null
}
public interface IHttpTransport
{
    HttpExchange Send(HttpMethodKind method, string path, byte[] jsonBody, double timeoutSeconds, double now);
    void Tick(double now);
    void AbortAll();
}
public sealed class UnityWebRequestTransport : IHttpTransport
{
    public const int MaxResponseBytes = 262144;
    public UnityWebRequestTransport(BackendEndpoint endpoint, ProcessCredential credential);
}
```

- [ ] **Step 0: The insecure-HTTP check (S13-D11).** A one-off EditMode probe: a `UnityWebRequest` POST to an `HttpListener` on `http://127.0.0.1:<free port>/` under the project's current settings. If Unity refuses it ("Insecure connection not allowed"), **stop** and report to the owner; change nothing. If it succeeds, keep the probe as the first `UnityWebRequestTransportTests` case.
- [ ] **Step 1: Failing tests.**
  - `AdapterSourceAuditTests`: `UnityWebRequest` and `UnityEngine.Networking` appear in no `.cs` file under `Assets/_Project/Scripts` outside `Backend/Adapter/`; the adapter has no `FishNet`, `System.Net.Sockets` or `HttpClient`; no adapter file calls `Debug.Log` with a variable named `body`, `credential`, `ticket` or `seed`; the asmdef references exactly `Blackglass.Backend.Core`; `CoreSourceAuditTests` still pass.
  - `UnityWebRequestTransportTests` (a `System.Net.HttpListener` on `127.0.0.1:<free port>` in the test; `[UnityTest]`-free EditMode loop that ticks until done, bounded 10 s): the request carries `Authorization: Bearer <credential>`, `Content-Type: application/json` and the exact body bytes; a 409 with a body completes with status 409 and the body; a listener that never answers ends in `Timeout` at the given timeout; a closed port ends in `ConnectionFailed`; a 302 is not followed (reported as `Completed` 302, or as the redirect system error, both mapped to the definitive `redirect`); a 300 KiB body is aborted once `downloadedBytes` passes the cap and reported as `Completed` with its `Status`, a `null` body and an `Oversize` flag the caller treats as a definitive refusal; `AbortAll()` ends a pending exchange in `Aborted`.
- [ ] **Step 2:** `Tools/run-tests.sh EditMode "Blackglass.Tests.AdapterSourceAuditTests|Blackglass.Tests.UnityWebRequestTransportTests"` → FAIL.
- [ ] **Step 3: Implement.** `UnityWebRequest` with `UploadHandlerRaw`, `DownloadHandlerBuffer`, `redirectLimit = 0`, `timeout` left at 0 (the transport enforces its own deadline from `now` and calls `Abort()`), `disposeUploadHandlerOnDispose`; one exchange per request, disposed when finished. `FakeHttpTransport`: per-path queues of scripted results (`Answer(status, body)`, `Drop()` = the server applied it but the answer is lost → `Timeout`, `Refuse()` = `ConnectionFailed`, `Delay(seconds)`), an applied-request record (path, body bytes), and a check that every path starts with `/server/v1/attempts/<attempt>/`.
- [ ] **Step 4:** Same command → PASS.
- [ ] **Step 5: Commit** `Stage 13: the HTTP transport seam and the loopback UnityWebRequest transport`.

## Task 3: `BackendCall`, the retrying call

**Files:** create `BackendCall.cs`; test `BackendCallTests.cs`.

**Interfaces (produces):**
```csharp
public sealed class RetryPolicy
{
    public double AttemptTimeoutSeconds = 10, InitialBackoffSeconds = 0.5, MaxBackoffSeconds = 8, JitterFraction = 0.2;
}
public enum CallState { Running, Succeeded, Refused, Unauthorized, GaveUp }
public sealed class BackendCall
{
    public BackendCall(IHttpTransport transport, string path, byte[] body, RetryPolicy policy, double deadline, Func<double> random01, double now);
    public void Tick(double now);
    public CallState State { get; }
    public int Status { get; }          // last completed status (0 if none)
    public string ProblemCode { get; }  // "none" when absent or malformed
    public byte[] Body { get; }         // Succeeded only
    public int Attempts { get; }
    public void Abort();                // → GaveUp, the exchange aborted
}
```

- [ ] **Step 1: Failing tests** (fake transport, a fake clock, `random01 = () => 0.5`; the production default is a private `System.Random`, and a test asserts `UnityEngine.Random.state` is unchanged across a run of calls): 200 first time → `Succeeded`, 1 attempt; 503, 503, 200 → `Succeeded`, 3 attempts, the body bytes identical on all three (compare the fake's record); backoff waits 0.5, 1, 2, 4, 8, 8 s; `Retry-After: 3` on 429 waits 3 s, `Retry-After: 600` waits only the cap; `Drop()` then 200 → `Succeeded` (the lost-response path); 401 → `Unauthorized` after 1 attempt; each of 400, 404, 409, 413, 415, 422 → `Refused` with its `code` parsed from `{"type":…,"code":"Cancelled"}`; a body without `code`, or with a code over 64 characters or outside `[A-Za-z]+` → `none`; the deadline passing during a backoff → `GaveUp` without another send; `Abort()` while in flight → `GaveUp` and the fake sees the abort; an oversize answer → `Refused` with code `oversize`; 302, 403 and 405 → `Refused` after 1 attempt (anything unlisted is definitive).
- [ ] **Step 2:** `Tools/run-tests.sh EditMode "Blackglass.Tests.BackendCallTests"` → FAIL.
- [ ] **Step 3: Implement** the state machine (§3.3).
- [ ] **Step 4:** Same command → PASS.
- [ ] **Step 5: Commit** `Stage 13: one retrying backend call with definitive and transient answers`.

## Task 4: The process contract vectors (both runtimes, no database)

**Files:** create `Backend/contracts/process-v1/` with `register.request.json`, `register.response.json`, `ready.request.json` (from the committed attempt-v1 fixture 03's ready report), `ready.response.json`, `redeem.request.json` (a syntactically valid `jt1_` ticket of zeros, never a real one), `redeem.response.json`, `start.request.json`, `start.response.json`, `heartbeat.request.json`, `heartbeat.response.json` (`{}`), `recorded.request.json`, `recorded.response.json`, `failed.request.json`, `failed.response.json` (`{"applied":"cancelled"}`), `register.response.shuffled.json` (the same values with the loadout's keys reordered, as PostgreSQL `jsonb` returns them), `timestamps.json` (`joinDeadline`/`startedAt` examples with 0, 3, 5, 6 and 7 fractional digits), `problem.409.json` (hand-written in ASP.NET Core's ProblemDetails shape with the `code` extension; captured from the real API in Part B and compared there), `README.md`; tests `ProcessContractVectorTests.cs` (Unity), `ProcessRequestVectorTests.cs` (backend domain), `ProcessResponseVectorTests.cs` (backend integration project, no Postgres trait).

- [ ] **Step 1: Failing tests.**
  - Unity: `JsonUtility.ToJson` of each Stage 11 request DTO, filled with the vector's values, equals the request vector byte for byte (UTF-8, no BOM, no trailing newline); `JsonUtility.FromJson` of each response vector into its DTO gives the expected values (the register response's participant loadout equal to fixture 03's snapshot, from both the ordered and the key-shuffled vector: key order is not pinned; every `timestamps.json` value parses round-trip to the same UTC instant, and a value without `Z`, with an offset, or with 8 fractional digits is refused).
  - Backend domain: `RegisterRequest.Parse`, `ReadyReport.Parse`, `RedeemRequest.Parse`, `StartRequest.Parse`, `ProcessHeartbeatRequest.Parse`, `RecordedRequest.Parse`, `FailedRequest.Parse` accept each request vector and give its values; `ObjectiveList` and `LootHash` accept the ready vector.
  - Backend integration project (no database): `JsonSerializer.Serialize(new ProcessEndpoints.RegisterResponse(…), JsonSerializerDefaults.Web options)` and the other response records equal the response vectors byte for byte (the API's own records and options, so a backend change that alters the bytes fails here first). The register vector's loadout is built through `JsonNode.Parse` as the endpoint does; its key order is the vector's, and Unity's test proves order doesn't matter.
- [ ] **Step 2:** Run each side: `Tools/run-tests.sh EditMode "Blackglass.Tests.ProcessContractVectorTests"` → FAIL; then (Unity closed) `dotnet test Backend.sln --filter "FullyQualifiedName~ProcessRequestVectorTests|FullyQualifiedName~ProcessResponseVectorTests"` → FAIL.
- [ ] **Step 3: Write the vectors** (generated once from each side's real serializer, then reviewed by hand against §1), and the README stating that they are golden and changed only with both runtimes' tests.
- [ ] **Step 4:** Both commands → PASS; then `dotnet test Backend.sln --filter "Requires!=Postgres"` → the baseline plus the new tests.
- [ ] **Step 5: Commit** `Stage 13: process-v1 contract vectors checked by Unity's serializer and the backend's parsers and records`.

## Task 5: `ProcessApi`, typed calls and validated answers

**Files:** create `ProcessApi.cs`; test `ProcessApiTests.cs`.

**Interfaces (produces):**
```csharp
public sealed class Redeemed { public string AccountId { get; } public IReadOnlyList<string> OperativeIds { get; } } // built only by ProcessApi.TryRedeemed
public sealed class ProcessApi
{
    public ProcessApi(IHttpTransport transport, string attemptId, RetryPolicy policy, Func<double> random01);
    public BackendCall Register(SessionRegisterRequest body, double deadline, double now);
    public BackendCall Ready(byte[] readyRequestJson, double deadline, double now);       // the exact bytes logged at ready
    public BackendCall Redeem(string processSessionId, string ticket, double deadline, double now);
    public BackendCall Start(string processSessionId, string accountId, double deadline, double now);
    public BackendCall Heartbeat(ProcessHeartbeatRequest body, double deadline, double now);
    public BackendCall Recorded(string accountId, string manifestHash, double deadline, double now);
    public BackendCall Failed(string processSessionId, string reason, double deadline, double now);
    public static bool TryRegistered(BackendCall call, out SessionRegisterResponse response, out string problem);
    public static bool TryReady(BackendCall call, out ReadyResponse response, out string problem);
    public static bool TryRedeemed(BackendCall call, out Redeemed redeemed, out string problem);
    public static bool TryStarted(BackendCall call, out StartResponse response, out string problem);
    public static string Route(string attemptId, string tail); // "/server/v1/attempts/<aid>/<tail>"
}
```

- [ ] **Step 1: Failing tests:** each call posts to exactly its route (`sessions`, `ready`, `tickets/redeem`, `participants/<Base58>/start`, `heartbeat`, `participants/<Base58>/recorded`, `failed`) with the vector's bytes; the constructor refuses a non-canonical attempt id; `Start`/`Recorded` refuse a non-canonical Base58 account; `Failed` refuses a reason outside the backend's word pattern; `TryRegistered` refuses (with a problem code, never the body) a missing or non-canonical `processSessionId`, an absent `missionTemplate`, an empty `participants`, a body that is not JSON (a 200 proxy page); `TryRedeemed` refuses a non-Base58 account or an empty operative list; `TryStarted` refuses `started:false`; no `ProcessApi` method exists for a status, receipt or results route (reflection test over its public methods).
- [ ] **Step 2:** `Tools/run-tests.sh EditMode "Blackglass.Tests.ProcessApiTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4:** Same command → PASS.
- [ ] **Step 5: Commit** `Stage 13: typed process routes with validated answers`.

### Checkpoint 1: independent review of the adapter (Tasks 1–5)

Fresh reviewer, most capable model, high effort: credential handling and redaction, loopback enforcement, the classification table against the backend's real answers, idempotency of retried bodies, the vectors' coverage. Critical and Important findings are fixed test-first before Task 6; Minor ones are ledgered.

## Task 6: Options, the polled attempt source and the bootstrap's backend mode

**Files:** modify `ServerOptions.cs`, `ServerBootstrap.cs` (including its exit-2 paths: the startup deadline at `Tick`, `OnStateChanged(Failed)` and `BeginSession`'s refusal, which in backend mode go to the runner); create `BackendAttemptSource.cs`, `BackendTimings.cs`; tests: additions to `ServerOptionsTests.cs`; new `BackendAttemptSourceTests.cs`, `ServerBootstrapBackendTests.cs`. `IAttemptSource.cs`, `FixtureAttemptSource.cs` and their tests are **unchanged** (S13-D8 (b)).

**Interfaces (produces):**
```csharp
public enum RegistrationStatus { Pending, Loaded, Failed }
public sealed class BackendAttemptSource   // not an IAttemptSource
{
    public BackendAttemptSource(ProcessApi api, string attemptId, string buildRevision, string catalogueVersion, double bound, double now);
    public RegistrationStatus Poll(double now, out SessionRegisterResponse response, out string problem);
}
public sealed class BackendTimings { /* §3.4 defaults; FromOptions(ServerOptions) */ }
// ServerOptions: public string Attempt { get; }  public string Backend { get; }  plus the §3.4 flags
// ServerExitCodes: BackendRefused = 4, NotStarted = 5, Stopped = 6, ResultNotPublished = 7
// ServerBootstrap (internal seams, tests replace): Func<string> CredentialsDirectory  (default: Environment "CREDENTIALS_DIRECTORY")
//   Func<string, IOutboxDirectory> OpenAttemptOutbox (default: OutboxDirectories.OpenForServer($STATE_DIRECTORY, "/var/lib/blackglass-outbox", attemptId))
//   Func<BackendEndpoint, ProcessCredential, IHttpTransport> TransportFactory (default: new UnityWebRequestTransport(...))
```

- [ ] **Step 1: Failing tests.**
  - `ServerOptionsTests`: `-attempt <canonical uuid>` and `-backend <url>` parse; a missing value still selects backend mode, which then fails (never the plain mission by accident); every timing flag's range keeps its default with a warning; `Describe` prints `attempt=backend` and never a seed or credential; no option's value is ever treated as a credential (an option-table test: the parser has no case whose name contains `credential`, `token` other than the existing `-netTokens` path, `secret` or `ticket`).
  - Bootstrap refusals (EditMode with the existing bootstrap test rig): `-attempt` with `-attemptFixture`, `-outboxSelftest`, `-diagnostic`, `-seed`, `-net`, `-netTokens`, `-netQuiet` or `-netFixture` → exit 4 with the named conflict (checked before the self-test's early return); a non-canonical `-attempt`; a bad `-backend`; a missing or malformed credential (via `CredentialsDirectory` → a temp directory) → exit 4 and **the fake transport records no request**; an outbox refusal → exit 4, no request; in a player whose stamped build revision is missing or malformed (seam) → exit 4, no request (S13-D4).
  - Startup failures in backend mode (fake transport): a generation failure, the generation deadline and a `BeginSession` refusal each send one `failed {reason}` and exit 4, never exit 2; in fixture mode each still exits 2 exactly as today; the registration bound is separate (a register that takes 50 s of retries leaves the full generation deadline).
  - `BackendAttemptSourceTests` (fake transport): register 503, 503, 200 → `Loaded` with the response; `409 BuildNotAllowed` → `Failed` (`register_refused BuildNotAllowed`); 401 → `Failed` (`register_unauthorized`); the startup deadline → `Failed` (`register_gave_up`); the request body names the build revision and the build's catalogue version; the logged problem never contains the seed (the scripted response carries a recognizable seed, the log capture is scanned).
- [ ] **Step 2:** `Tools/run-tests.sh EditMode "Blackglass.Tests.ServerOptionsTests|Blackglass.Tests.BackendAttemptSourceTests|Blackglass.Tests.ServerBootstrapBackendTests"` → FAIL.
- [ ] **Step 3: Implement.** Move `BeginAttempt`'s validation and generation, unchanged, into `ContinueAttempt(attemptId, response)`; the fixture path calls `TryLoad` then `ContinueAttempt` synchronously, exactly as today; backend mode polls `BackendAttemptSource` from `Tick` under the registration bound and sets the generation deadline only when it calls `ContinueAttempt`. Backend mode order: options → build revision → credential → outbox → transport → `BackendAttemptSource`.
- [ ] **Step 4:** Same command → PASS; then `Tools/run-tests.sh EditMode "Blackglass.Tests.AttemptFixtureSourceTests"` and `Tools/run-tests.sh PlayMode "Blackglass.Tests.AttemptFixturePlayTests|Blackglass.Tests.AttemptMissionPlayTests|Blackglass.Tests.AttemptAbilitiesPlayTests"` → PASS, unedited (fixture path unchanged).
- [ ] **Step 5: Commit** `Stage 13: the -attempt backend mode's options, checks and polled registration`.

## Task 7: `ParticipantStartHold`, the ticket admission handle and the gate's `NotStarted`

**Files:** create `ParticipantStartHold.cs`; modify `NetResult.cs`, `ServerCommandGate.cs`; tests `ParticipantStartHoldTests.cs`, additions to `NetGateRejectionTests.cs`.

**Interfaces (produces):**
```csharp
public enum HoldState { Waiting, Redeeming, Starting, Started, CouldNotStart, Stopped }
public enum TicketAdmissionState { Pending, Redeemed, Refused, Started, CouldNotStart }
public sealed class TicketAdmission
{
    public TicketAdmissionState State { get; }
    public string AccountId { get; }                      // Redeemed and Started only
    public IReadOnlyList<string> OperativeIds { get; }    // Redeemed and Started only
    public AdmissionRefusal Refusal { get; }              // Refused only (Stage 11's enum)
    public string Reason { get; }                         // CouldNotStart only
}
public sealed class ParticipantStartHold
{
    public ParticipantStartHold(ProcessApi api, string processSessionId, AttemptStart attempt, BackendTimings timings, double startDeadline);
    public TicketAdmission AdmitTicket(string ticket, double now); // Refused(AlreadyConnected) unless Waiting; Refused(UnknownToken) for a malformed ticket, nothing sent
    public void Tick(double now);
    public HoldState State { get; }
    public string Reason { get; }                         // CouldNotStart/Stopped: a bounded code
    public bool Connected { get; }                        // admitted and not released (Stage 14 replaces the source)
    public void Release();                                // the client left (Stage 14); before start: back to Waiting
    public event Action Started;                          // raised once, from a parsed 200 only
}
// NetRejectReason.NotStarted appended after OrderRefused.
// ServerCommandGate: optional Func<NetConnection, bool> isStarted (null = always started): a not-started connection's request → Rejected(NotStarted), before any other check except Malformed.
```

- [ ] **Step 1: Failing tests** (fake transport and clock):
  - redeem 200 (account and operatives = the snapshot's) → start 200 → `Started` raised once; start `Drop()`, `Drop()`, 200 → `Started` once and the fake shows 3 identical start bodies;
  - redeem 200 for another account, or operative ids in another order or count → `CouldNotStart` (`redeem_mismatch`), **no start request sent**;
  - redeem 401, `400 InvalidBody`, 409 `WrongAttempt` → the admission `Refused` (`UnknownToken`, `UnknownToken`, `WrongAttempt`), the hold back to `Waiting`, **no stop**, no start sent; 409 `WrongSession`/`InvalidState` and redeem 503 until its bound (`redeem_gave_up`) → `Refused(Unavailable)`, back to `Waiting`; a ticket that isn't `jt1_` + 43 canonical characters (65 characters, wrong prefix, empty) → `Refused(UnknownToken)` with **no request sent**;
  - the handle passes `Pending → Redeemed(account, operatives) → Started` in that order and is the same object throughout;
  - start each of `409 Cancelled`, `DeadlinePassed`, `NotRedeemed`, `WrongSession`, `InvalidState`, `404` → `CouldNotStart` with the code;
  - start 503 until the start deadline → `CouldNotStart` (`start_gave_up`);
  - start 401 → `Stopped` (the admission `CouldNotStart` with `unauthorized`);
  - a second `AdmitTicket` while `Redeeming` or `Starting` → `Refused(AlreadyConnected)`, nothing sent;
  - after `Started`, `AdmitTicket` → `Refused(AlreadyConnected)` (reconnect is Stage 15);
  - the ticket text appears in no log line (log capture) and in no request but the redeem body;
  - gate: with no predicate every existing gate test passes unchanged; with a predicate returning false, a valid move request is `NotStarted`, a malformed one still `Malformed`; `NotStarted`'s byte value is one more than `OrderRefused`'s.
- [ ] **Step 2:** `Tools/run-tests.sh EditMode "Blackglass.Tests.ParticipantStartHoldTests|Blackglass.Tests.NetGateRejectionTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4:** Same command → PASS; then the net suites: `Tools/run-tests.sh EditMode "Blackglass.Tests.Net"` → PASS.
- [ ] **Step 5: Commit** `Stage 13: the participant start hold over redeem and idempotent start, and the gate's NotStarted refusal`.

## Task 8: `ProcessHeartbeat`

**Files:** create `ProcessHeartbeat.cs`; test `ProcessHeartbeatTests.cs`.

**Interfaces (produces):**
```csharp
public sealed class ProcessHeartbeat
{
    public ProcessHeartbeat(ProcessApi api, string processSessionId, string accountId, Func<string> phase, Func<bool> connected, BackendTimings timings, double now);
    public void Tick(double now);
    public bool BackendReachable { get; }   // false after 30 s (the lease) without a successful call; true again on the first success
    public bool Unauthorized { get; }       // a 401 was received
    public void Stop();
}
```

- [ ] **Step 1: Failing tests:** the first heartbeat goes at registration plus the interval, then every interval; never two in flight (a slow answer delays the next, it never stacks); the body names the session, the phase word (`holding`, `running`, `publishing`) and `[{accountId, connected}]`; 503 for 40 s → `BackendReachable` false after 30 s, true at the next 200; 401 → `Unauthorized` and no further heartbeat; `400 UnknownParticipant` or `409 WrongSession` → logged with the code once, heartbeats continue (only a 401 stops, §3.5); `Stop()` aborts an in-flight call; each heartbeat call's deadline is the interval (no retries pile past the next tick).
- [ ] **Step 2:** `Tools/run-tests.sh EditMode "Blackglass.Tests.ProcessHeartbeatTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4:** Same command → PASS.
- [ ] **Step 5: Commit** `Stage 13: process heartbeats with backend reachability and the 401 stop`.

## Task 9: `ResultPublisher`, the §H steps 3–5

**Files:** create `ResultPublisher.cs`; test `ResultPublisherTests.cs` (fake transport; a real `OutboxWorker` over `TempOutbox`, i.e. `OutboxDirectories.OpenForEditor`).

**Interfaces (produces):**
```csharp
public enum PublisherState { Idle, Writing, Notifying, Publishing, Published, NotPublished }
public sealed class ResultPublisher
{
    public ResultPublisher(OutboxWorker worker, ProcessApi api, BackendTimings timings);
    public void Begin(string accountId, ManifestBuildOutcome outcome, double now); // a Failed outcome → NotPublished (manifest_build_failed), nothing written
    public void Tick(double now);
    public void Shutdown();                         // SIGTERM path: abort the notice, AllowPublication on a Written handle
    public PublisherState State { get; }
    public string Reason { get; }                   // NotPublished: outbox_failed, outbox_conflict, manifest_build_failed
    public string RecordedAnswer { get; }           // ok, conflicting_report, invalid_state, not_found, unauthorized, gave_up, aborted
    public event Action<string> ResultSaved;        // raised once, when Published
}
```

- [ ] **Step 1: Failing tests:**
  - happy path: `.written` exists before the first `recorded` request (the fake checks the directory at send time), `recorded` carries the handle's hash, `.ready` exists only after the `recorded` answer, `ResultSaved` once, the files' bytes equal the manifest's canonical bytes;
  - `recorded` 503 for 31 s → `gave_up`, published at 30 s; `ConnectionFailed` throughout → the same (**an outage never blocks publication**);
  - `recorded` 401, 409 `ConflictingReport`, 409 `InvalidState`, 404 → published at once with the answer recorded;
  - `recorded` `Drop()` then 200 → two identical `recorded` bodies, one publication (repeated notification);
  - a pre-existing different `.written` for the account → `NotPublished` (`outbox_conflict`), **no `recorded` sent**, the existing file's bytes unchanged;
  - a pre-existing identical `.ready` (a relaunch over the same directory) → `Published` without a `recorded` call;
  - an outbox fault on write (Stage 11's `FaultingOutboxDirectory`) → resubmitted 3 times 2 s apart, then `NotPublished` (`outbox_failed`), no `recorded`;
  - a fault on publish after a durable write → `NotPublished` with the `.written` still in place;
  - `Shutdown()` while `Notifying` → the notice aborted, the handle allowed, `.ready` after the worker drains;
  - a `Failed` manifest outcome → `NotPublished` (`manifest_build_failed`), no file and no request;
  - no request ever goes to a path other than `…/recorded` (the fake's record).
- [ ] **Step 2:** `Tools/run-tests.sh EditMode "Blackglass.Tests.ResultPublisherTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4:** Same command → PASS; then `Tools/run-tests.sh EditMode "Blackglass.Tests.OutboxWorkerTests|Blackglass.Tests.OutboxWriterTests"` → PASS (Stage 11 unchanged).
- [ ] **Step 5: Commit** `Stage 13: result publication through the outbox with the bounded recorded notice`.

## Task 10: `BackendAttemptRunner` and the bootstrap's lifecycle

**Files:** create `BackendAttemptRunner.cs`; modify `ServerBootstrap.cs`; tests `BackendAttemptRunnerTests.cs` (EditMode, the runner over fakes and a stub mission interface), `BackendAttemptPlayTests.cs` (PlayMode, the real ServerMission scene in `-attempt` mode, fixture 03's snapshot as the scripted register answer, the fake transport and a temp outbox).

**Interfaces (produces):**
```csharp
public enum RunnerPhase { Registering, Generating, ReportingReady, Holding, Running, Publishing, Exiting }
public sealed class BackendAttemptRunner
{
    public RunnerPhase Phase { get; }
    public TicketAdmission AdmitTicket(string ticket); // public: Stage 14's NetTicketAdmission calls it; Part B's test calls it with a real ticket
    public bool BackendReachable { get; }
    public event Action<string> ResultSaved;
    // internal: constructed and ticked by ServerBootstrap; Exit(code, reason) through the bootstrap's Finish
}
```

- [ ] **Step 1: Failing tests.**
  - EditMode (runner), **the ready report**: the ready body is the exact `JsonUtility` bytes `BeginSession` logs, identical on every retry; `Drop()` then 200 → one hold, the join deadline from the replayed answer; `422 InvalidLootPool` → exit 4, **no `failed`, no retry**; `422 InvalidObjectives`, `409 CatalogueMismatch`, `409 InvalidState`, `409 WrongSession` → `failed {"ready_refused"}` then exit 4; the ready bound → `failed {"ready_gave_up"}`, exit 4; the bound is min(30 s, the registered launch deadline): a launch deadline 10 s away bounds it at 10 s; an unparsable `joinDeadline` → `failed {"ready_bad_answer"}`, exit 4, never a hold; a ready 401 → exit 6.
  - EditMode (runner): the phases advance only on their triggers; `failed` is sent with the §3.5 reason for each pre-start refusal and at most once; the join hold expiring with no ticket → `failed {"join_deadline"}`, exit 5; `CouldNotStart` → `failed {"start_refused"}`, exit 5; a 401 on any call after registration → exit 6 after any publication in flight finishes; `NotPublished` → `failed {reason}` then exit 7; `Published` → exit 0 only after the worker's drain, and a heartbeat 401 arriving after `Published` keeps exit 0; a 401 answer to `failed` never changes the exit code; **the full request record of a successful run is exactly** `sessions, ready, tickets/redeem, participants/<acc>/start, heartbeat×n, participants/<acc>/recorded` and nothing else (no status, receipt or results route ever).
  - PlayMode (`BackendAttemptPlayTests`):
    1. **Hold:** after `Ready` the mission is paused before the first frame; 300 frames later no unit moved, no health changed, `CommittedEvents == 0`; after `AdmitTicket` and a scripted 200 start, it resumes and `ServerClock` restarted then.
    2. **Full run:** the test finishes the mission with the same helpers `AttemptMissionPlayTests` uses (extraction); the manifest is frozen, `.written` then `recorded` then `.ready`; exit 0; the `.ready` bytes equal the logged manifest's.
    3. **Refused start:** a scripted `409 Cancelled` → the mission never resumes, exit 5, no outbox file.
    4. **Stop:** a scripted heartbeat 401 during play → exit 6, no outcome fabricated, no outbox file; a heartbeat 401 while `Notifying` → `.ready` still produced, then exit 6.
    5. **Outage:** every request after the start fails with `ConnectionFailed`; the mission is played to the end anyway; published within the `recorded` bound; exit 0.
    6. **Secrecy:** across runs 1–5, the captured log (`Application.logMessageReceived`) contains neither the scripted credential, nor the ticket, nor the scripted seed's decimal text in any line (the seed is chosen to be an unmistakable 9-digit value).
    7. **The hold can't be lifted by someone else:** a `TacticalPause.Resume()` from another component while `Holding` (as `MissionDirector.ResetSystems` or `TacticalPause.OnDisable` would) is detected on the next tick, re-paused and logged; no frame of simulation runs.
    8. **SIGTERM** (the bootstrap's quit path, called directly): while `Notifying` → the notice aborted, `.ready` produced by the drain; while the handle is `Writing` → the durable write completes and `.written` stays (no `.ready` without the notice ending, which shutdown does), never a partial file; while `Holding` → exit with no outbox file.
    9. **Fixture unchanged:** a `-attemptFixture` run in the same scene still generates, runs without a hold and logs its manifest exactly as before.
- [ ] **Step 2:** `Tools/run-tests.sh EditMode "Blackglass.Tests.BackendAttemptRunnerTests"` and `Tools/run-tests.sh PlayMode "Blackglass.Tests.BackendAttemptPlayTests"` → FAIL.
- [ ] **Step 3: Implement.** The runner owns: the session id; the `ParticipantStartHold`; the `ProcessHeartbeat` (from registration); the ready call with the exact `JsonUtility` bytes `BeginSession` logs; `TacticalPause.Pause()` in backend mode inside `OnStateChanged(Ready)` before `BeginSession`; `Resume()` and `clock.Restart()` on `Started`; the `ResultPublisher` on `MissionFinished`; `OnApplicationQuit` → `ResultPublisher.Shutdown()`, transport `AbortAll()`, `OutboxWorker.Stop(drain)`; while `Holding`, each tick re-checks `TacticalPause.IsPaused` and re-pauses (with a log line) if anything resumed it.
- [ ] **Step 4:** Both commands → PASS.
- [ ] **Step 5: Commit** `Stage 13: the backend attempt runner: hold, play, freeze, publish and exit without polling settlement`.

### Checkpoint 2: independent review of the lifecycle (Tasks 6–10)

Fresh reviewer, most capable model, high effort: the §H order, every crash boundary in §3.5, the hold's guarantee (no play without a committed start), the fixture path's equivalence, secrecy, and Review Focus 1–5. Critical and Important findings are fixed test-first before Task 11.

## Task 11: Part A final validation, documentation and the stop

**Files:** this plan ("As built"), `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` (Stage 13 status line), `CLAUDE.md` (status lines), `Tools/backend-e2e/{prepare.sh,teardown.sh,README.md}` (written, **not run**).

- [ ] **Step 1:** Full suites, sequentially: `Tools/run-tests.sh EditMode`, `Tools/run-tests.sh PlayMode`, then `dotnet test Backend.sln --filter "Requires!=Postgres"`. Expected: baselines plus the new tests, all green.
- [ ] **Step 2:** A local Linux dedicated-server compile check with `Tools/build-server.sh`, whose output is fixed (`Builds/LinuxServer/` and a tarball under `Builds/`, both git-ignored; it replaces the previous local build there, which is not deployment evidence of anything). It proves the adapter and `UnityWebRequest` compile under `UNITY_SERVER` for linux-x64. Never copied to a VM, never run.
- [ ] **Step 3:** Write `Tools/backend-e2e/` for Part B (S13-D3 (a)): `prepare.sh` refuses unless the tunnel's cluster reports `cluster_name = blackglass-test`, creates one `bg_t_e2e_<random>` database, runs `migrate` into it, starts `serve` on `127.0.0.1:5090` with that database, then runs the admin verbs, written from the real usage text in `Backend/src/Accounts.Api/Admin/AdminArguments.cs` (each with `--operator`, `--reason` and `--yes`): `energy policy publish`, `account create`, `energy open` and `energy grant`, `mission template publish --template-id procedural-standard …`, `host register --host-id e2e-host --capacity 1 --port-base 7800`, `build allow --revision editor --catalogue-version <catalogue> --templates procedural-standard:1`; it writes the account key and host secret to `e2e.json` in the session's temp directory (outside the repository, mode 600); `teardown.sh` stops `serve`, drops exactly that database and deletes `e2e.json` and the temporary credentials directory. Both print no secret.
- [ ] **Step 4:** Evidence: `git status --short`, `git diff --stat main...HEAD` shows only the listed paths; the owner's two settings files untouched; no `.claude/` staged.
- [ ] **Step 5:** Final whole-branch review (most capable model); fix Critical/Important test-first; ledger Minors. Write "As built" with counts, commits and rulings. **STOP for the owner:** Part A acceptance, and the Part B decision.

## Task 12 (Part B, after approval): the local backend environment

- [ ] **Step 1:** With the owner's approval of S13-D3 and of `prepare.sh` as written: open the existing test tunnel (`tools/testdb.sh tunnel`), run `Tools/backend-e2e/prepare.sh`; record its output (no secrets) under `.claude/scratchpad/phase13/stage-13/`.
- [ ] **Step 2:** Expected: `serve` healthy on `127.0.0.1:5090` (`GET /health`), schema current, one account, one host, one build allowlisted; the cluster's other databases untouched.

## Task 13 (Part B): the Editor end-to-end run

**Files:** `Assets/_Project/Tests/PlayMode/BackendE2EPlayTests.cs` (written in Part A behind `[Explicit]` and `[Category("BackendE2E")]`, reading `e2e.json` from the path in `BLACKGLASS_E2E_CONFIG` (outside the repository); `Assert.Ignore` when it is absent, so the normal suites never need a backend).

- [ ] **Step 1: The run** (one Unity process; nothing else running): the test, as the agent, heartbeats (`complete: true`, no units) so admission sees capacity; as the player, logs in with the dev key and deploys `procedural-standard:1` with the starter operatives; as the agent, claims (with an `Idempotency-Key`), posts `launching` with the generation, and writes the claimed credential to a temporary credentials directory outside the repository (mode 600, deleted at teardown); starts the server in the ServerMission scene with `-attempt <id> -backend http://127.0.0.1:5090` and the bootstrap's `CredentialsDirectory` seam pointing there, and a temp outbox through `OpenAttemptOutbox`; as the player, mints a ticket (`POST /v1/attempts/{id}/tickets`) and passes it to `AdmitTicket`; plays to extraction with the shared helpers; when `.ready` appears, as the agent, relays it to `POST /agent/v1/results` and posts `exited {exitCode:0, results:[{present:true}], outbox:{complete:true}}`; as the player, polls `GET /v1/attempts/{id}/receipt` until 200.
- [ ] **Step 2: Variants:** (a) a test-only TCP proxy between the server and the API drops the first `start` response: the participant starts once, the wallet ledger shows one `charge:<attempt>` entry; (b) the test stops `serve` after the start, plays to the end, the process publishes within the `recorded` bound and exits 0, the test restarts `serve`, relays, and the receipt settles once with the real outcome (no void).
- [ ] **Step 3: Evidence:** the transcript (no secrets: the log scan of Task 10 runs over it too), the receipt JSON, the attempt's state rows, counts.
- [ ] **Step 4:** `Tools/backend-e2e/teardown.sh`; confirm the database is gone. **STOP for the owner.**

---

## Checkpoints, stopping points and recommended models

| Checkpoint | Scope | Runs where | Approval needed | Model and effort |
|---|---|---|---|---|
| **CP0** | Task 0: branch and baselines | Workstation | Owner's implementation authorization | Sonnet 5.5, medium |
| **CP1** | Tasks 1–5: adapter, transport, call, vectors, typed API; **review 1** | Workstation (Unity, then `dotnet test` UNIT, sequentially) | None beyond CP0 | Implementation: Opus 5.5, medium (native; Unity serializes the runs). Review: Opus 5.5, high |
| **CP2** | Tasks 6–10: backend mode, hold, heartbeat, publisher, runner; **review 2** | Workstation | None | Implementation: Opus 5.5, high (the lifecycle and crash boundaries carry the risk). Review: Opus 5.5, high |
| **CP3 — STOP** | Task 11: full suites, Linux compile check, Part B scripts written, final review, "As built" | Workstation | **Owner: accept Part A; decide S13-D3 and approve `prepare.sh`** | Opus 5.5, high (final review: Opus 5.5, high or xhigh); docs: Sonnet 5.5, medium |
| **CP4 — STOP** | Tasks 12–13: local backend environment and the Editor end-to-end run | Workstation + the services VM's test cluster through the tunnel (S13-D3 (a)) | **Owner's explicit approval first** | Opus 5.5, high |
| Part C | B24, template and launcher, Linux Mono checks, Stage 17 | `bg-dev-mission01` | Blocked by B24; each step under its own owner-approved host manifest | Not planned here |

**Execution method:** native, sequential (Unity's one-instance limit serializes every test run, as for the last two milestones), with the two checkpoint reviews and the final review as fresh reviewers.

## Risks and remaining decisions

- **`JsonUtility`'s silent defaults** (Review Focus 4): mitigated by post-parse validation and the vectors; residual risk if the backend adds a member Unity should honour (the vectors' backend test fails first).
- **`UnityWebRequest` on the Linux Mono server player** is unproven until Part C; the local compile check (Task 11) proves only that it builds.
- **SIGTERM on the Linux player:** whether Unity runs `Application.quitting` on SIGTERM is a Part C check; without it, the durable `.written` is still relayed by the agent after exit (A10-D1), so no result is lost, only the publication step is skipped.
- **Clock skew** between the VMs bounds the start retries' accuracy; the grace (S13-D6) covers it, and late retries are harmless (B8).
- **Part B on the shared test cluster** (S13-D3 (a)): an isolated database, but the API's dev settings must never point at anything else; `prepare.sh` refuses any other cluster.
- **Open decisions:** S13-D1 to S13-D10 above; Part C's launcher arguments and template replacement are decided with B24, not here.

## Acceptance criteria (Part A; Part B and C separately)

1. Every test named in Tasks 1–10 exists, failed first and passes; full EditMode, PlayMode and backend UNIT suites green against Task 0's baselines.
2. The contract vectors are reproduced byte for byte by Unity's serializer and accepted by the backend's own parsers and records.
3. A successful fake run sends exactly the allowed routes in §H order, and no run ever calls a status, receipt or results route.
4. No path plays before a `200` start, and no path starts without a parsed `200` redeem.
5. Every §3.5 row has a test; an outage never prevents publication; a 401 never abandons a durable result.
6. The log scans find no credential, ticket or seed; the source audits pass.
7. The fixture attempt, the loopback and visual network clients, the offline scenes and Core behave exactly as before (their suites unchanged and green; the diff stat shows no offline scene, prefab, HUD or input file).
8. The Linux dedicated server compiles with the adapter (not deployed).

## Out of scope

Stage 14 (`NetTicketAdmission`, ownership lists, new gate requests, `NetResultRecordedMessage`, lifting the `-net` refusal), Stage 15 (reconnect, resume tokens, reachability windows), Stage 16 (the online client scene, receipts in the client), Stage 17 (VM deployment), B24 and anything on `bg-dev-mission01`, backend changes of any kind, co-op.

## Obligations handed on

- **Stage 14:** implement `NetTicketAdmission.Begin/Poll` over `BackendAttemptRunner.AdmitTicket`'s `TicketAdmission` handle (one per client); keep the development map messages out of backend mode even in a fixture build (decision 052); wire `ServerCommandGate`'s start predicate to `ParticipantStartHold`; send `NetResultRecordedMessage` on `ResultSaved`; feed the heartbeat's `connected` from real connections (`Release()` on disconnect); lift the `-net`/`-netTokens` refusal in backend mode with its own tests.
- **Stage 15:** use `BackendReachable` for the reconnect window and the hard cap; resume tokens after `Started`; give the start hold its own hold reason, distinct from the solo disconnect pause, so one never lifts the other.
- **Part C / Stage 17:** the release launcher passes `-attempt %i -backend http://127.0.0.1:5081` (and `-net -netPort <slot port>` after Stage 14); the template's listener and bind lines are replaced under B24; verify `UnityWebRequest`, SIGTERM and the outbox on Linux Mono inside the real unit.

---

## Architecture review

Independent reviewer (Opus 5.5, high effort, read-only, 2026-10-23) over revision 1 (`40a0fdd`), checked against the backend stores and endpoints, the Stage 9/10/11/12 documents and the Unity server code. **Verdict: 0 Critical, 7 Important, 13 Minor; the core invariants hold** (no play without a committed start, no start without a backend-redeemed ticket, no double-applied or lost settlement, no fixture authentication in a production path, no VM touched). Every finding is applied in revision 1.1:

| Finding | Resolution |
|---|---|
| I1 redeem's uniform 401 would trigger the stop rule (a garbage hello token could stop the mission once Stage 14 wires it); redeem refusals underspecified; a 401 on `failed` | Redeem is exempt from the 401 stop: every refusal refuses that ticket and returns to `Waiting`; local ticket-shape check; `redeem_gave_up`; a `failed` 401 never changes the exit code (§3.3, §3.5, Task 7) |
| I2 `AdmitTicket : bool` isn't the seam `INetAdmission` needs | `TicketAdmission` handle with `Pending`/`Redeemed`/`Refused(AdmissionRefusal)`/`Started`/`CouldNotStart`, the mapping tested now (S13-D1, §6, Task 7) |
| I3 the bootstrap's exit-2 paths (startup deadline, `MissionState.Failed`, `BeginSession`) would bypass `failed`; one deadline bounded registration and generation | Those paths route to the runner in backend mode (`failed`, exit 4); registration has its own bound (§3.4, §3.5, Task 6) |
| I4 no ready-report tests | Ready tests added to Task 10 (bytes, lost answer, each refusal, the bound from the launch deadline, an unparsable `joinDeadline`) |
| I5 timestamps with 0–7 fractional digits; `jsonb` key order; ProblemDetails isn't a record; `failed.response` missing | Round-trip UTC parse and a timestamps vector; a key-shuffled register vector; the problem vector hand-written and checked in Part B; `failed.response.json` (§3.4, Task 4) |
| I6 a polled `IAttemptSource` breaks Stage 12 tests | S13-D8 now (b): `IAttemptSource` unchanged; backend registration polled directly; one shared `ContinueAttempt` |
| I7 `insecureHttpOption: 0` may refuse plain HTTP to loopback | S13-D11 and Task 2 Step 0: proven first; if refused, stop for the owner; never changed silently |
| M1 `prepare.sh` verbs wrong-shaped | Rewritten from `AdminArguments.cs`, with `--operator`, `--reason`, `--yes` and `energy policy publish` (Task 11) |
| M2 `build-server.sh` has a fixed output | Accepted: `Builds/` (git-ignored), stated (Task 11) |
| M3 join-hold expiry usually ends in exit 6 | Documented (§3.5) |
| M4 a heartbeat 401 after publication is the settlement | Keeps exit 0 (§3.5, Task 10) |
| M5 SIGTERM is a second stop signal | §2 and §3.5 corrected |
| M6 unlisted statuses, redirects, the body cap | Anything unlisted is definitive; the redirect system error is `redirect`; the cap by abort on `downloadedBytes` (§3.2, §3.3, Tasks 2–3) |
| M7 jitter must not use `UnityEngine.Random` | Private `System.Random`, tested (§3.2, Task 3) |
| M8 others can lift `TacticalPause` | The runner re-pauses and logs while `Holding`, tested; Stage 15 gets a separate hold reason (Task 10, obligations) |
| M9 more option conflicts | `-outboxSelftest`, `-seed`, `-netQuiet`, `-netFixture` refused with `-attempt`, before the self-test's early return (§3.2, Task 6) |
| M10 decision 052 handoff | Stage 14 obligation (§7, obligations) |
| M11 no SIGTERM test | PlayMode cases while `Notifying`, `Writing` and `Holding` (Task 10) |
| M12 Part C overlaps Stage 12 Track N | Track N named as the owner; Part A doesn't accept Stage 12 (§5) |
| M13 Part B secrets under `.claude/` | Outside the repository, mode 600, deleted at teardown (§7, Tasks 11 and 13) |

The reviewer also verified: the route table against the stores; 503 (never 401) during a database outage; the per-attempt rate limit and the safety of retrying 429/408/5xx/timeouts; the S9-B7 interplay of every exit path; the §H order against `OutboxWorker`/`PublicationHandle`; that nothing changes during the hold (`SimulationTime.IsRunning` checks); that a late `ServerClock` restart is safe under V13; the redeemed operative order; `editor` as a valid build revision; the canonical base64url tail set; the assembly layout; the template's inability to host Unity today; and that `JsonUtility`'s request bytes parse strictly.
