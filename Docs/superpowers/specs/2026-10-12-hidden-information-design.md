# Networking experiment, hidden information and per-connection visibility (design)

Date: 2026-10-12. Branch: `networking-hidden-info` (off `main` @ `3dce9ac`). The owner set the brief and approved Option A (real intelligence by default, deterministic fixture for testing) with six constraints, then approved design decisions D3 to D9 in conversation on 2026-10-12. Two rulings were made while writing this spec and are marked **(ruling)** for the owner's review: §3.3 (friendly avatars go to their owners only) and §3.9 (the fixture uses grants only, not real sight).

## 1. Goal and scope

The server owns the full truth. Each connection receives only the live hostile state its intelligence view allows. A hidden hostile sends nothing live. The only thing allowed while hidden is a frozen last-known record that the server authorized. Hidden movement never updates it, and one connection's observation never widens another's view.

Acceptance is judged on **decoded client state and replication traffic**. Hidden renderers, disabled objects or `forceRenderingOff` do not count.

**In scope:**
- a library-free visibility projection in `Scripts/Net/Core`, and an observer condition in the adapter;
- the real `IntelligenceService` on the server, with fog on, feeding normal `-net` mode;
- a fail-closed policy for missing or disabled intelligence;
- the gate answering `UnknownUnit` for hostiles a connection cannot see;
- a minimal attack-chase mitigation for gate-accepted attacks;
- a development-only deterministic fixture (`-netFixture`) with observation grants, scripted hostile moves and one fixture container;
- explicit server authority on the avatar;
- tests, an extended VM harness, and narrow doc updates.

**Acceptance checks covered (numbering from audit §7):**

| Check | Covered |
|---|---|
| A3 nine bad requests | **seven** of nine: the six from part 1 plus the attack on an unobserved hostile |
| A5 hidden information | yes, through the fixture's observation grant instead of Recon Scan (§3.9) |
| A6 container | searching only: contents reach the searcher only after the search; taking items is deferred |
| A9 non-owner client | yes, and B now receives no unit at all (§3.3) |
| A10 suites stay green | yes |

**Out of scope:** the account backend, PostgreSQL, settlement, reconnect, matchmaking, PvP, Kubernetes or Agones, layout streaming, Asset Studio, client-side prediction, Phase 13. Abilities, the real loot and inventory system, and the client scenes are unchanged.

## 2. Starting point (observed 2026-10-12)

- **Every avatar, hostile included, reaches every connected client**, even one that has not said hello. The avatar prefab has no `NetworkObserver`, so FishNet's defaults apply. A client can read a hostile's position, rotation, health, alive state, order kind, queue length and steering flag.
- **The gate is an existence oracle.** A move for a hostile id answers `NotOwner`; a made-up id answers `UnknownUnit`.
- **No seed, spawn plan or mission plan** appears in any of the five broadcast messages or the avatar's SyncVars.
- **The avatar transform is client-authoritative by default** (`NetworkTransform._clientAuthoritative: 1` in the prefab). It is safe today only because no avatar has an owner.
- **The server has no `IntelligenceService`.** `ServerSceneBuilder.FixtureSettings()` uses `new IntelligenceSettings()`, which leaves fog off, so the model would be open anyway.
- **The fixture has no loot.** The server scene has no inventory, catalogue or loot table.
- **An accepted attack keeps chasing** its target after the squad loses sight of it. This is a 037 limitation for player orders; abilities already stop with "no target".
- **The PlayMode loopback rig is host mode**: server and client share one process, so it proves server observer sets, not client-side absence.
- **Baselines (main @ 3dce9ac):** EditMode 1366/1366, PlayMode 1403/1403. Part 2 VM run: harness median latency 261.8 ms through the tunnel (client round trip 93 ms); 10-minute idle with `-net`: 59.8 to 60.0 fps, CPU 1.9 to 2.2 %, peak RSS 116.3 MB.

## 3. Design

### 3.1 The visibility projection (`Scripts/Net/Core`, library-free)

`NetVisibility` keeps, per connection and per hostile id, a `NetKnowledge` of `Unknown`, `Observed` or `LastKnown`, plus a frozen last-known position.

Each server frame it asks a source whether the connection observes each hostile, and applies these rules:

| From | Observed now | Not observed now |
|---|---|---|
| `Unknown` | `Observed` | no change |
| `Observed` | no change | `LastKnown` at the last position recorded while observed |
| `LastKnown` | `Observed`, record cleared | no change, position frozen |

- **Death.** A hostile that dies while `Observed` stays `Observed`, so its death is witnessed and its alive state reaches the client. A hostile that dies while hidden leaves that connection's state as it was. A last-known record carries no alive state.
- **Changes only.** The projection returns a change record only on a transition. Re-evaluating an unchanged world returns nothing (idempotent).
- **Connections.** A connection enters the projection when its hello is accepted and leaves it on disconnect.
- **Sources** are an interface with two implementations: squad intelligence (§3.2) and fixture grants (§3.9). Exactly one is active per run.

### 3.2 Whose knowledge a connection gets (constraints 1, 2 and 4)

In normal `-net` mode the source is the real squad model (`MissionIntelligence`, fed by `IntelligenceService`):
- **A connection gets squad knowledge only if it owns every living friendly.** Today admission gives a `squad` token all friendlies and a `none` token nothing, so account A gets the squad view and account B gets nothing.
- **A hostile counts as observed** only when the model reports `Observed` for it. The source does not use `MissionIntelligence.IsObserved`, which answers true for untracked units and for an open model.
- **Split ownership fails closed.** If ownership were ever split, no connection would get squad knowledge until per-owner models exist. Admission cannot produce that today. How co-op squads share knowledge is a later decision.
- **Exposure by firing (037) is kept.** A hidden hostile that fires becomes `Observed` in the squad model for 3 s, so the owning connection receives it.

**Fail closed.**
- At startup, `-net` refuses to start (exit code 2, one error line) if the scene has no `IntelligenceService`, its model is missing after `Begin`, or fog is off.
- While running, if the service is disabled or destroyed or its model becomes null, the source reports nothing observed for every connection. Every hostile goes to `LastKnown` or stays `Unknown`, and one error line is logged.
- The server fixture turns fog on: map Full, objectives All, no enemy markers, no cameras. It keeps the existing 14 m sight and 3 s exposure.

### 3.3 Replication through FishNet observers (`Scripts/Net/Adapter`)

- **The condition.** One `NetVisibilityCondition` (a Normal `ObserverCondition` asset) is set on the avatar prefab's `NetworkObserver`, overriding the manager's defaults. It asks the server's projection through the avatar's `NetUnitState`, with no static state, and answers "not visible" whenever that reference is missing.
  - A hostile avatar is visible to a connection only while its knowledge is `Observed`.
  - **(ruling)** A friendly avatar is visible only to connections that own it. B therefore receives no unit at all. The squad's live state would otherwise leak hostile information to B: an `Attack` order, a turn toward the target, or health falling. Seeing an ally's squad is a later co-op decision.
  - Nothing is visible to a connection whose hello has not been accepted.
- **Rebuilds.** The adapter calls `RebuildObservers` only for the avatar and connection whose state changed, plus a full rebuild for a connection when its hello is accepted. FishNet then despawns or spawns that avatar for that one connection. A respawn carries the current server state, so re-observation never shows a stale position.
- **Last-known record.** On `Observed` to `LastKnown`, the server sends `NetKnowledgeMessage { UnitId, Knowledge = LastKnown, Position }` to that connection only. On re-observation it sends `{ UnitId, Knowledge = Observed }`, which clears the client's record. Age is computed by the client from the time of receipt.
- **Hostile avatars never carry order, queue or steering.** `NetAvatars.Sync` writes `None`, 0 and false for hostiles, even while observed.
- **Rejected alternative:** per-connection snapshot messages of our own. They would be easier to inspect, but they duplicate transform replication and give friendlies and hostiles two different paths.

### 3.4 Gate answers for hidden hostiles: `UnknownUnit`

The gate takes a visibility query (`CanSee(connection, unitId)`). A move or an attack that names a hostile this connection cannot see gets `UnknownUnit`, the same answer as an id that does not exist, in either the unit or the target slot. Friendly ids keep their current answers (`NotOwner`), because friendlies are not hidden information.

The check order stays as it is; the hidden case joins the existing `UnknownUnit` checks. The four test sites that build a gate pass a query that sees everything.

### 3.5 Attack-chase mitigation (constraint 5)

- When the gate accepts an attack, the server records the unit, the `AttackCommand` instance and the connection.
- Each frame, after the projection runs, the server checks each record:
  - if that command is no longer the unit's current order, the record is dropped;
  - if the target is no longer `Observed` for that connection, the server calls `CommandableUnit.DropCurrent(command)` and drops the record.
- **`DropCurrent(UnitCommand)` is new and narrow.** It ends the given command the way an unreachable attack ends (the existing private `Finish()`), but only if that command is still the current one. Orders queued behind it then start. No existing caller changes, and tests pin that.
- Disconnecting drops that connection's records; the order then runs as before.
- **Untouched:** enemy AI, companion assist, AutoRetaliate, abilities and single-player play. This matches 037's existing "no target" rule for abilities.

### 3.6 Combat-event leakage

No message names a target, carries a shot record or a hit event, or exposes a hostile's order or queue:
- hostile avatars push no order data (§3.3);
- friendlies go only to their owners (§3.3);
- `NetResultMessage` carries only the command id and a reason, and hidden targets answer `UnknownUnit` (§3.4);
- a friendly's own health falling is shown only to its owner, who is entitled to it. In normal mode the shooter is exposed to that owner by 037.

### 3.7 Avatar authority

- `NetAssetBuilder` sets the prefab's `NetworkTransform._clientAuthoritative` to 0. Ownership therefore never grants transform authority.
- An EditMode test reads the serialized field.
- A PlayMode check confirms every spawned avatar has no owner.
- A source audit confirms the adapter never calls `GiveOwnership` and never passes an owner to `Spawn`.

### 3.8 Seed, spawn data and plans

- **Reflection test (EditMode).** It walks every `IBroadcast` type and `NetUnitState`'s SyncVars. The allowed field types are:
  - primitives, strings, enums, `Vector3` and `Quaternion`;
  - arrays of structs declared in `Scripts/Net`.

  The test fails on any field whose name contains `seed`, `plan`, `spawn` or `layout`, and on any field typed from the mission, procedural or intelligence namespaces.
- **Harness check (decoded state).** The client records every decoded message and every avatar spawn. Role `visibility-a` checks three things:
  - no avatar for the hostile existed before the first reveal;
  - no decoded message before the first reveal held a `Vector3` within 5 cm of the hostile's revealed position;
  - every decoded message type is on the allowed list.

### 3.9 The deterministic fixture (constraints 3 and 4)

**Isolation.**
- **The fixture is compiled only into the Editor and development builds** (`UNITY_EDITOR || DEVELOPMENT_BUILD`). The release server build contains no fixture code.
- `Tools/build-server.sh --fixture` makes a development build. The default build stays release.
- **Superseded 2026-10-13 (owner decision, decision 047):** the fixture build is not a Development build. The fixture is compiled under `UNITY_EDITOR || BLACKGLASS_NET_FIXTURE`, set through `BuildPlayerOptions.extraScriptingDefines` in a non-Development build, because the Development build opened Unity's player connection on `0.0.0.0:55000` and `0.0.0.0:46686` on the VM. The two bullets above, and the other mentions of a development build in this document, read accordingly.
- `-netFixture` also requires `-net` and `-netQuiet` (hostile AI frozen, so the hostile neither moves nor fires on its own) and a loopback bind, which is checked again before the fixture is enabled. In a release build it is refused with exit code 2.
- **Test identity.** The token file's ownership field takes a `+fixture` suffix (`squad+fixture`). Only connections admitted with that token may drive the fixture. `make-dev-tokens.sh` marks account A.

**Behaviour.**
- **(ruling)** In fixture mode the source is the fixture's grants only; real sight and exposure are not consulted. Visibility is then independent of geometry and of how close the scripted moves bring the hostile. `IntelligenceService` must still be present (§3.2), and real intelligence is tested separately (§4).
- **Messages.** `NetFixtureMessage { Verb }` has no ids or account fields. The verbs are Observe, Unobserve, MoveHostile and Search.
  - Grants are keyed by the authenticated server-side connection object.
  - Each message passes the existing request budget.
  - From a connection without the fixture identity, or when the fixture is off, the message changes nothing and counts toward the abuse limit. The server always registers a handler for it, because FishNet would otherwise skip the message silently and nothing would be counted.
- **Hostile moves.** MoveHostile warps the fixture's hostile (`NavMeshAgent.Warp`) to the next of a few NavMesh-sampled points near its spawn, at least 3 m apart. The points are computed on the server and never sent.
- **Container.** A fixture container in Core holds fixed contents: definition ids, quantities and instance ids. Search requires the fixture identity and a living owned operative. It marks the container searched for that connection only, and only that connection then receives `NetContainerMessage { ContainerId, Entries[] }`. Nothing about the container is sent before the search. The real `LootContainer` and inventory stay off the server; replicating them is future work.
- **Step counter.** After each verb the server sends `NetFixtureStepMessage { Step }`, which carries no other data, to every welcomed connection. B can then check its view at each of A's steps.

### 3.10 Rate limits and logs

- The Part 2 budgets are unchanged: 60 requests/s with a burst of 60, and an abuse limit of 200 in 10 s. Fixture messages count against them.
- **Visibility messages are bounded.** They are sent only on transitions, at most one per connection and hostile per frame. Real intelligence changes at most 5 times a second (one pass every 0.2 s), and the fixture changes only on a budgeted verb.
- **No per-transition log lines** outside fixture mode. Fail-closed errors log once.
- **Ids are validated before they are resolved.** Fixture messages carry no ids, and the gate's checks are unchanged.
- The pre-hello and never-hello items from the Part 2 ledger stay deferred.

### 3.11 Client and harness

- **Client.** `NetClient` registers the knowledge, container and step messages and keeps:
  - the last-known records, one per unit id, replaced or cleared by later messages;
  - the searched contents;
  - the current fixture step;
  - a decode log (message type, time and any `Vector3` fields), plus a log of avatar spawns and despawns through `NetUnitState`.

  `NetClientView` draws last-known records as hollow markers. No client scene, HUD or gameplay file changes.
- **Role `visibility-a`** (account A, fixture build). Steps 1 to 15 below match the brief's transitions:
  1. Connect, wait 2 s, and check: no hostile avatar, no knowledge record and no container message (1, 2).
  2. Send a guessed attack: own unit against the first hostile id (`FirstOwnedId + OwnedCount`), and against 999. Both must answer `UnknownUnit`. The own unit's order and position must not change.
  3. Observe, then check the hostile avatar arrives with health and alive state (4, 5).
  4. MoveHostile, then check the avatar moves to the new point (7, 8).
  5. Order the melee operative to attack the observed hostile and check it is accepted. The melee operative is still walking toward the hostile when observation ends, so the hostile takes no damage and cannot die while observed, which would freeze it as `Observed` (§3.1).
  6. Unobserve, then check (9, 10):
     - the avatar is despawned;
     - exactly one last-known record exists, within 0.25 m of the final observed position;
     - the own unit's attack order ends (§3.5).
  7. MoveHostile while hidden. After 1 s, check the record is unchanged and no avatar or knowledge message arrived (11, 12).
  8. Reuse the known id in an attack. It must answer `UnknownUnit`.
  9. Observe again. The avatar must arrive at the current point, at least 2 m from the stale record, and the record must clear (14, 15).
  10. Before Search, no container message. Search, then check the contents arrive.
  11. Run the seed and spawn checks of §3.8.
- **Role `visibility-b`** (account B, concurrent). It connects first and prints a ready line. Then, every frame and at each fixture step, it checks:
  - no unit avatar of any kind;
  - no knowledge record;
  - no container contents (3, 6, 13).

  It also sends its own Observe once and checks it still sees nothing. It passes on the final step and fails if that step never arrives.
- **Role `fixture-denied`** (account A, against the release build with `-net`). It sends Observe and checks that within 3 s it has no hostile avatar and no knowledge record.
- **Part 2 roles.**
  - Role `a` sends Observe before it looks for the hostile.
  - Role `b` no longer receives friendlies, so it addresses friendly ids 1 and 2 directly. It still expects `NotOwner` for the four orders, with a friendly as the attack target, and expects `UnknownUnit` for an attack on the hidden hostile.
  - Roles `ticket` and `badtoken` are unchanged.
  - Role `b`'s no-change check can no longer be observed from B. Server-side no-change is covered by the gate tests and by role `a`'s six bad cases.
- **The runner.**
  - It starts B in the background, waits for its ready line, runs A, then waits for B.
  - It runs the Part 2 roles in sequence.
  - It kills B and the tunnel on every exit path (EXIT trap and PID checks), and verifies after cleanup that no client or tunnel process remains.

### 3.12 Geometry

Layout secrecy is not claimed. The net-test client receives no geometry, but the client build contains the generator and the fixture's seed is fixed. Streaming or hiding geometry is a later decision.

## 4. Verification

**EditMode (new):**
- the projection's transitions, including idempotency, frozen last-known, death while observed and death while hidden;
- per-connection independence: A's grant never changes B;
- the squad source: knowledge follows ownership, split ownership gives nothing, and a missing or disabled model reports nothing;
- gate answers: a hidden hostile and a nonexistent id are identical in both slots;
- `DropCurrent`: it ends only the matching current command, queued orders continue, and existing callers are unchanged;
- the token `+fixture` suffix;
- the fixture: refused without the identity, keyed by connection, never by account string;
- the container: no contents before search, and contents only for the searcher;
- the reflection test (§3.8);
- the avatar authority field and source audit (§3.7);
- the core source audit, extended with `Unity.Transport` and `Unity.Collections`;
- bounded change queues and one-shot error logs.

**PlayMode (new, host mode: proves server observer sets and messages, not client absence):**
- **Real intelligence, separate from the fixture.** Fog on, server scene, two connections:
  - the hostile is hidden from both at start;
  - when it fires while out of every friendly's sight, it becomes `Observed` for A by exposure and stays hidden from B;
  - about 3 s later it goes to `LastKnown` for A at its position;
  - a warp while hidden leaves the record unchanged.
- **Fail closed:**
  - `-net` with no intelligence service, or with fog off, exits with code 2;
  - disabling the service at runtime hides every hostile.
- **Fixture:** the fifteen transitions through observer sets and the messages sent.
- **Attack-chase:** losing sight ends a gate-accepted attack, while an AI attack continues.
- **Avatars:** none has an owner.

**Existing suites:** the full EditMode and PlayMode suites. Flaky tests are reported separately.

**Linux build and runtime (reported separately):**
- release and development server builds, each with its size and hash;
- the Checkpoint A diagnostic without `-net`, on the release build: PASS;
- `-net` on the release build: only the TCP listener on 127.0.0.1;
- `-netFixture` on the release build: refused with exit code 2;
- role `fixture-denied` against the release build;
- on the fixture build: roles `visibility-b` and `visibility-a` concurrently, then the Part 2 roles; each must print PASS and exit 0;
- no client or tunnel process left afterwards, including after a forced failure;
- a 10-minute idle run with `-net` on the release build, compared with part 2. Material regression means below 59 fps, more than 1 percentage point of CPU above part 2, or a peak RSS above 136 MB. The new intelligence pass is expected to cost a little.
- Tunnel latency is reported as transport evidence only, beside the 150 ms LAN target. Client prediction stays deferred.

**The eighteen acceptance criteria from the brief** are reported one by one in the completion report.

## 5. Risks and open points

- **Fog on in the server fixture** could change the Checkpoint A diagnostic. Its attack is issued directly through `Mover.Issue`, not gated by knowledge, so no change is expected. The diagnostic must still pass.
- **`DevicePresenter.AttachTo`** runs when intelligence is present. It must not create presentation work on the headless server; the plan's first server task checks this.
- **FishNet observer rebuilds** on transitions are assumed cheap at this scale (two connections, one hostile). Larger missions are not measured here.
- **Development build for the harness.** Its performance differs from release, so the idle run uses release.
- **Two-account fixture.** Two squad tokens on one mission would both receive squad knowledge. That is a later co-op decision.
- **Rulings for review:** §3.3 (friendly avatars only to their owners) and §3.9 (the fixture uses grants only).

## 6. Source control and docs

- **Branch.** `networking-hidden-info`; commits grouped by task, each with the co-author trailer. Nothing is pushed or merged without the owner.
- **Never committed:** the owner's two settings files, `.claude/`, `Builds/`, tokens, logs and keys.
- **Docs:**
  - decision 047 (hidden-information replication): what is filtered and what remains visible, per-connection versus shared intelligence, the fixture, limitations and deferred geometry secrecy;
  - audit §9.4 with the results, appended without rewriting earlier findings, plus the status rows in §9.1;
  - `DedicatedServer.md` for the `--fixture` build and the `-netFixture` option;
  - the `CLAUDE.md` text in Appendix A.

## Appendix A: replacement text for `CLAUDE.md`

In "Current Development Stage", replace the paragraph beginning "The step currently authorized is" and the list that follows it with:

> The step currently authorized is **the networking experiment's hidden-information slice: server-authoritative per-connection visibility** (`Docs/superpowers/specs/2026-10-12-hidden-information-design.md`). The server runs the real intelligence model and fails closed without it. A development-only fixture (`-netFixture`) drives the harness. The server still listens on loopback only.
>
> Each of these stays out of scope until the owner authorizes it:
> - accounts, backend APIs and databases;
> - settlement redesign and reconnect;
> - layout streaming and geometry secrecy;
> - Docker packaging and orchestration;
> - changes to the client scenes' input, HUD or gameplay for remote play;
> - final production content (graphics, animation, audio, story).

In "Current Priority", replace the section with:

> The networking experiment's hidden-information slice: a library-free visibility projection, FishNet observer conditions, `UnknownUnit` for hidden hostiles, a frozen last-known record, and a deterministic development fixture, proven by decoded client state on `bg-dev-mission01`.
>
> - Keep visibility policy free of networking-library types.
> - Keep client scenes and gameplay behaviour unchanged, apart from `CommandableUnit.DropCurrent`.
>
> Next step, authorized separately: Phase 13 (authenticated between-mission flow and trusted settlement).
>
> Do not begin later-stage systems until specifically directed.
