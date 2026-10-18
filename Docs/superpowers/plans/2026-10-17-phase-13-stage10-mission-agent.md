# Phase 13 Stage 10: the Linux Mission Host Agent (detailed plan, revision 1)

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status (2026-10-17, after Stage 9's merge at `d72a325`):** plan only. Implementation is **not** authorized. Nothing here changes `bg-dev-mission01`, `bg-dev-services01`, SSH, a firewall, Docker networking or the live accounts stack. The owner decides A10-D1 to A10-D8 below before any code is written. Task 12 (the VM step) also needs the owner present for its root installs.

**Goal:** an unprivileged agent on `bg-dev-mission01` that:
- claims attempts and launches each one as a hardened systemd unit only after the backend has acknowledged `launching`;
- reports a complete unit inventory;
- relays every durable result from the per-attempt outbox;
- reports a process exit only after relaying that attempt's results.

It does all of this crash-safely across agent restarts, VM reboots and long backend outages, and no outage ever causes a void.

**Architecture:**
- A .NET 10 console worker, `Backend/src/MissionHost.Agent/`. It talks to the backend over HTTP only and references no backend assembly, so the backend core stays untouched.
- All side effects sit behind small seams, each with a real Linux implementation and a fake:
  - the backend client;
  - the durable state store;
  - the outbox reader;
  - systemd: the launcher, the unit inventory and the unit status;
  - the clock.
- The agent's own state lives in `/var/lib/blackglass/agent` (owned by `blackglass-agent`, 0700). Every write goes to a temporary file, is `fsync`ed, renamed, and then the directory is `fsync`ed.
- Systemd is driven by the `systemctl` CLI with fixed argument vectors (never a shell), under the Q-5 polkit rule.

**Tech stack:**
- .NET 10 (`Microsoft.Extensions.Hosting` already ships with the SDK), xUnit, systemd 255 (Ubuntu 24.04), polkit.
- No new NuGet package.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`:
- §4.3–4.4 (credentials and secrets);
- §7.4 (units and polkit);
- §9.2 (outbox isolation and relay);
- §11 (failure matrix);
- §16.2 (agent routes);
- §0.8 R1–R8 (Stage 8 rulings);
- §0.9 (Stage 9 rulings).

The Stage 8 and Stage 9 plans' "As built" sections and §H of the Stage 9 plan (the publish/visibility contract) hold the binding obligations listed below.

## Obligations this stage inherits

From Stage 8 (R1, R2 and R4, Checkpoint 1 M5, final M3, and R8):
- Persist the claim's `Idempotency-Key` before sending the claim. Persist the claim response durably (`fsync`) before acting on it.
- Get `200` from `launching` with that response's `credentialGeneration` **before** `systemctl start`. Never launch with a generation the backend hasn't accepted.
- On `CredentialSuperseded`, retry the claim with the same key. After a restart, resume from the persisted state:

  | Persisted state | Next step |
  |---|---|
  | key, no response | claim retry |
  | response, no `launching` acknowledgement | `launching` retry |
  | acknowledged, unit not started | start |
- Send `inventory.complete: true` only after a `systemctl list-units 'blackglass-mission@*' --all` that exited 0 and parsed entirely. Either list every unit whose `launching` was acknowledged but which hasn't been seen to exit, or serialize enumeration with launches.
- Stop the units named in the heartbeat's `stop` list. Report `exited` only once the unit is gone.
- Provide a trusted emergency stop for mission units that works even when the agent can no longer authenticate (R8).

From Stage 9 (§C, §H and S9-B7):
- Relay `.ready` files while the process is alive. Never touch `.written` files while it is alive. After a verified exit, publish and relay the `.written` files (how depends on A10-D1). Never relay `.tmp` files or count them as present.
- Relay every published file before reporting `exited`. Report `present: false` only when the scan found neither a `.written` nor a `.ready` file for that participant.
- Send `outbox.complete: true` only after a full, successful read of the attempt's directory. Otherwise send `false`, and report a later successful rescan as the supplementary observation.
- Handle each answer exactly as §C says (table in Task 7), recording relay state in the agent's own directory. Never delete a relay file before Phase 14's cleanup. Keep `AttemptRevoked` files for the operator.

## Owner decisions (A10-D1 to A10-D8)

| # | Decision | Recommendation | Alternatives and costs |
|---|---|---|---|
| **A10-D1** | **Publishing `.written` files after an exit.** §H says the agent renames `.written` to `.ready`, but §9.2 gives the agent group *read* access only (directories `2750`, files `0640`): it can't rename. | **The agent relays a `.written` file directly once the unit's exit is verified, without renaming it.** Its relay state records "published by exit". The outbox stays strictly read-only to the agent, and the process never sees the difference, because it has exited. §H is amended to say so. | (b) A root `ExecStopPost=+` step in the fixed template renames `.written` to `.ready` after the main process exits. That adds root code and doesn't run if systemd itself is stopped mid-unit. (c) Make the directory group-writable (`2770`). Rejected: the agent could then alter or delete results. |
| **A10-D2** | **Where the participant roster comes from.** A complete exit report must list every started participant (`IncompleteResults` otherwise), but the agent only learns account ids from outbox file names. | **The claim response gains `participants: [accountBase58]`** (the attempt's admitted participants, known at claim). The agent persists it with the response. This is a small additive backend change in Stage 10 (route, store and test). | (b) A new host route, `GET /agent/v1/attempts/{id}/participants`, adds a round trip and a failure mode. (c) Never send `complete: true`, which means no exit-evidence void would ever be possible. |
| **A10-D3** | **How the mission VM reaches the loopback-only API.** Phase 13 keeps the API loopback-only behind SSH tunnels. The agent and the mission processes on `bg-dev-mission01` need `127.0.0.1:5080`. | **A dedicated `blackglass-tunnel.service`** on mission01:<br>• runs as user `blackglass-tunnel`;<br>• runs `ssh -N -L 127.0.0.1:5080:127.0.0.1:5080` with `Restart=always`;<br>• its key on services01 is restricted to `restrict,permitopen="127.0.0.1:5080",command="/bin/false"`.<br>The agent and the processes use `http://127.0.0.1:5080`. This changes services01's `authorized_keys` and mission01 (owner present, Task 12). | (b) autossh adds a package and changes nothing material. (c) A private VLAN or WireGuard is out of Phase 13's scope. |
| **A10-D4** | **The systemd interface.** | **The `systemctl` CLI** with fixed argument vectors:<br>• `start`/`stop blackglass-mission@<uuid>.service`;<br>• `list-units 'blackglass-mission@*' --all` with JSON output where the VM's systemd 255 provides it (Task 5 records real output from the VM; otherwise `--plain --no-legend --no-pager`, parsed strictly);<br>• `show -p ActiveState,SubState,Result,ExecMainCode,ExecMainStatus,InvocationID`.<br>No new package. | A D-Bus library (Tmds.DBus) gives typed signals, but it is a new dependency. Polling every 1 s is enough at this scale. |
| **A10-D5** | **Emergency stop.** | A root-only local command, `blackglass-agent emergency-stop`, run with `sudo` by the operator on the VM. It stops every `blackglass-mission@*` unit through systemd as root, needs no backend and no host secret, and logs what it stopped. **The agent does not stop running units on its own after a 401.** Processes stop themselves on a revoked credential (Stage 12), and ended attempts are stopped through the heartbeat's `stop` list. | (b) The agent stops all units after N consecutive heartbeat 401s. A rotated-but-not-installed secret would then kill legitimate missions. |
| **A10-D6** | **Runtime on the VM.** | A **self-contained, single-file `linux-x64` publish**, root-owned under `/opt/blackglass/agent/releases/<revision>/`, with a `current` symlink. No .NET runtime is installed on the VM. | Installing the .NET runtime from Microsoft's apt feed adds a system package and an external source. |
| **A10-D7** | **What the Stage 10 VM tests launch.** The server can't register before Stage 12. | The VM step uses the **real template** with a **root-owned stub mission executable**. A test `current` symlink under a separate `/opt/blackglass/mission-stub/` is selected by a template variable file (`EnvironmentFile=` stays root-owned). The stub writes `.tmp`, `.written` and `.ready` files and exits with a chosen status, so every §H crash point is exercised. The owner's `/opt/blackglass/server/` is untouched. | Point `current` at the owner's server build and test only launch and exit. That leaves the outbox path unexercised until Stage 12. |
| **A10-D8** | **Host secret rotation.** | The agent re-reads `/etc/blackglass/agent/host-secret` (0600, `blackglass-agent`) on `SIGHUP` and after any 401. The operator installs the new secret as root and sends `SIGHUP` (or restarts the agent). | Restart-only rotation leaves the agent idle until the restart. |

Provisional configuration (spec §19), each with a default in the agent's config file:
- heartbeat 10 s;
- claim long poll ≤ 25 s;
- relay backoff 1–60 s with full jitter;
- outbox scan 1 s;
- outbox disk alert at 80% of the filesystem or 1 GiB used;
- the agent unit's `MemoryMax=256M` and `TasksMax=64`;
- each mission unit's `MemoryMax=2G`, `CPUQuota=200%` and `TasksMax=256`;
- the largest file the agent sends: 4 MiB (anything larger is a local anomaly, never sent).

## Global constraints

- Touch only:
  - `Backend/src/MissionHost.Agent/`, `Backend/tests/MissionHost.Agent.Tests/`, `Backend/deploy/mission/`;
  - the additive claim-response change (if A10-D2 is approved): `Backend/src/Accounts.Persistence/Hosts/HostStore.cs`, `Backend/src/Accounts.Api/Hosts/*`, their tests and the README;
  - `Docs/` and `CLAUDE.md`.
- No migration (schema stays 9), unless A10-D2's alternative (b) needs none either.
- Keep `0000`–`0009` byte-identical.
- The agent is never root. Only the template's fixed `ExecStartPre=+` lines run as root, plus the emergency-stop command run by the operator with `sudo`.
- The agent validates every attempt id as a canonical lowercase UUID (`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`, after parsing) before building a unit name or a path, and refuses anything else.
- Never log, persist outside the state directory, or print:
  - the host secret, a process credential or an `Idempotency-Key`'s secret part;
  - manifest contents.

  Logs carry at most an 8-hex prefix of a hash. A static check enforces this, as Stage 8's M8 did.
- No result file is ever modified or deleted by the agent.
- Every timer, size and threshold is provisional configuration.
- Tests run on the VM-local runner (Linux containers), so filesystem semantics (`fsync`, rename, modes) are real. The root install steps run only in Task 12, with the owner.
- Never commit secrets. Stage by explicit paths only, never `git commit -a`.
- Preserve the owner's two Unity settings files and `.claude/`.
- No merge, push or deployment without the owner.

## Review focus

1. **A crash between any two durable steps of claim → persist → `launching` → credential file → `start` → started.** The expected behaviour: never a second unit for one attempt, never a launch with an unacknowledged generation, and no orphaned credential file after start. Task 3 injects a crash at every step.
2. **A `.written` file next to a live process, a `.tmp` file left by a crash, and a file that grows while it is being read.** The expected behaviour: only `.ready` files are relayed while the process is alive; `.written` files are relayed only after a verified exit; `.tmp` files are never relayed; a file is read once, by size and then content, and retried if it changes. Task 7.
3. **Enumeration racing a launch.** The expected behaviour: a `complete: true` inventory never omits a unit whose launch was acknowledged, so the backend never frees a live slot. Tasks 5 and 6.
4. **A long backend outage during a mission's end.** The expected behaviour: relays and exit reports wait and retry forever, nothing reports absence without a complete scan, and the backend applies no void. Tasks 7–9 and the integration test with the API stopped and started.
5. **A rotated or revoked host secret.** The expected behaviour: claims stop, files are kept, no unit is stopped on the agent's own initiative, the secret is re-read on 401 and `SIGHUP`, and the emergency stop works without the backend. Tasks 6, 9 and 11.

---

## Task 0: record the owner's rulings

- [ ] Record A10-D1 to A10-D8 as the owner rules, amend §H if A10-D1 is approved, and update the spec (§0.10), the staged plan and `CLAUDE.md`. Commit docs only.

## Task 1: skeleton, configuration, identifiers and durable files

**Files:**
- Create: `Backend/src/MissionHost.Agent/` (`Program.cs`, `AgentConfig.cs`, `AttemptIds.cs`, `AtomicFile.cs`, `StateStore.cs`);
- Create: `Backend/tests/MissionHost.Agent.Tests/` and add both projects to `Backend.sln`.

**Interfaces (produced):**
- `AgentConfig.Load(path)` reads JSON with `hostId`, `apiBaseUrl` (loopback only), `stateDir`, `outboxDir`, `relayDir`, `launchDir`, `serverBuildInfo` (the path of the server's `build-info.txt`, which supplies `revision`; `catalogueVersion` is read from the same file, or from the config until Stage 12's build writes it) and the provisional values. An invalid config is refused with exit 4.
- `AttemptIds.TryCanonical(string, out Guid, out string canonical)` and `UnitNames.For(string canonical)`, giving `blackglass-mission@<uuid>.service`.
- `AtomicFile.WriteAsync(path, bytes)` writes the temporary file, `fsync`s it, renames it and `fsync`s the directory. `AtomicFile.ReadAsync` ignores `*.tmp`.
- `StateStore`: claims, launches and relay records, one file each, keyed by canonical attempt id and Base58 account id.

**Steps (test-first):**
- [ ] Tests for `AttemptIds`. Upper-case, braces, no hyphens, spaces, `..`, a trailing newline, a 37-character string, the nil UUID, and a UUIDv4 rendered upper-case are all refused or canonicalized exactly once. The unit name is built only from the canonical form.
- [ ] Tests for `AtomicFile`:
  - after a write, only the final file exists;
  - a leftover `x.tmp` from a simulated crash is ignored and overwritten by the next write;
  - a concurrent reader sees the old bytes or the new ones, never partial ones;
  - files are created 0600 under a 0700 directory.
- [ ] Tests for `AgentConfig`: missing or invalid values are refused, a non-loopback `apiBaseUrl` is refused, and no secret path's contents are logged.
- [ ] Implement until green. Run on the VM runner: `vm-test.sh --filter MissionHost`.

## Task 2: the backend client and answer classification

**Files:**
- Create: `BackendClient.cs`, `Answers.cs`, `HostSecret.cs`.

**Interfaces (produced):**
- `BackendClient` with:
  - `ClaimAsync(key)` returning `Claimed`, `NoWork`, `Unavailable` or `Retry`;
  - `LaunchingAsync(attempt, generation)` returning `Acknowledged`, `Superseded`, `InvalidState`, `NotFound` or `Retry`;
  - `HeartbeatAsync(...)` returning `{drained, stop}` or `Retry`;
  - `ExitedAsync(attempt, report)` returning `Applied`, `Conflicting`, `IncompleteResults`, `UnknownParticipant` or `Retry`;
  - `RelayAsync(bytes)` returning a `RelayAnswer`.
- `HostSecret` reads the 0600 file, keeps the value only in memory, re-reads on demand, and never logs or prints it.

**Steps:**
- [ ] Tests with a fake HTTP handler for the full §C matrix:

  | Answer | Relay state |
  |---|---|
  | 200 `settled` or `duplicate` | `done` |
  | 202 `quarantined` or `held` | `under_review` (never resent) |
  | 409 `Conflict` or `AlreadySettledAsVoid` | `conflict` (kept) |
  | 409 `AttemptRevoked` | `revoked` (kept) |
  | 409 `WrongAttempt` or `InvalidState` | `refused` (kept) |
  | 409 `SubmissionLimit` | `limited` (kept; an operator import goes past the cap) |
  | 422 | `rejected` (never resent) |
  | 413 or 415 | `transport_refused` (a local anomaly; never resent automatically) |

  401, 429, 500, 503, timeouts, connection refusal and a closed tunnel are **retry**: backoff 1–60 s with full jitter, indefinitely, and a 401 re-reads the secret.
- [ ] Integration tests against a real in-process API and test database (the existing `ApiServer` and `TestCluster` harness): claim, launching, heartbeat, exited and results map exactly as above.
- [ ] Implement until green.

## Task 3: durable claim and launch

**Files:**
- Create: `ClaimLoop.cs`, `LaunchStep.cs`;
- Modify (A10-D2): `HostStore` claim result and claim route, adding `participants`, plus their tests and the README.

**State machine** (persisted per claim key, in `stateDir/claims/<key>.json`):

```
keyed --claim 200--> claimed (response + participants fsynced)
claimed --launching 200 (gen)--> acknowledged
claimed --CredentialSuperseded--> keyed (claim retry, same key: a recovery, new generation)
acknowledged --credential written 0400--> credential_ready
credential_ready --systemctl start ok--> started (credential file deleted)
started --unit seen gone--> exiting (Task 8)
```

**Steps:**
- [ ] Tests with a fake backend and a fake launcher. A crash injected after every transition (a hook throws, and a new agent instance resumes from disk) gives:
  - exactly one `start` per attempt;
  - never a start before an acknowledged `launching`;
  - a recovery that reuses the key and gets a new generation;
  - an `InvalidState` after a launch makes the agent supervise the existing unit instead of starting another;
  - a malformed attempt id in a claim response is refused, logged as an anomaly with the id never used in a path, and not launched.
- [ ] A test that a drained host or a failing heartbeat lease sends no claim.
- [ ] Integration tests against the real API: Stage 8's recovery, superseded and `InvalidState` paths, end to end with the fake launcher.
- [ ] A backend test (A10-D2): the claim response lists exactly the attempt's admitted participants as Base58, on first claim and on a recovery.
- [ ] Implement until green.

## Task 4: the process credential handoff

**Files:**
- Create: `CredentialHandoff.cs`.

**Steps:**
- [ ] Tests:
  - the credential is written to `launchDir/<uuid>/process` as 0400 under a 0700 directory owned by the agent, through `AtomicFile`;
  - it is deleted once `start` returns and the unit's `ActiveState` is `active`, `activating` or already ended;
  - after a crash it is deleted at startup for every attempt past `started`, and rewritten from the persisted response for an attempt at `credential_ready`;
  - it never appears in logs, state files other than the claim record, or exception messages.
- [ ] The static check: no logging call in `MissionHost.Agent` takes a credential, secret or manifest value (as Stage 8's M8).
- [ ] Implement until green.

## Task 5: systemd launcher, inventory and status

**Files:**
- Create: `Systemd/SystemctlRunner.cs`, `SystemdLauncher.cs`, `UnitInventory.cs`, `UnitStatus.cs`, and their fakes in the test project.

**Steps:**
- [ ] Tests of the argument vectors. They are built only from canonical ids and never through a shell, and a unit name outside the pattern is refused before any process is started.
- [ ] Parse tests with recorded `systemctl list-units` samples (JSON, or the strict plain form), including a partial document, garbage, an empty list, an unknown unit, a non-zero exit and a timeout. Only a zero exit with the whole document parsed gives `complete: true`.
- [ ] Mapping tests from `ExecMainCode`/`ExecMainStatus` to the exit report: `CLD_EXITED` gives `exitCode`; `CLD_KILLED` and `CLD_DUMPED` give `signal` (the name, such as `SIGKILL`).
- [ ] Tests of enumeration serialized with launches. A launch holds the inventory lock from its acknowledged `launching` until the unit is listed or `start` fails, and every acknowledged-but-unlisted attempt is added to the inventory, so a `complete` inventory never omits a live unit.
- [ ] Implement until green.

## Task 6: the heartbeat

**Files:**
- Create: `HeartbeatLoop.cs`.

**Steps:**
- [ ] Tests:
  - every 10 s, the agent sends `{buildRevision, catalogueVersion, inventory:{complete, units}, outboxPending}`, where `outboxPending` counts published files without a definitive relay state, plus outbox disk usage;
  - `drained: true` stops claims;
  - every attempt in `stop` is stopped (`systemctl stop`), and an id outside the agent's own claims is still validated and stopped only if its unit exists;
  - a 401 stops claims, re-reads the secret and keeps supervising and relaying locally, with no unit stopped;
  - an outage keeps everything running, and the next successful heartbeat carries the current inventory.
- [ ] Integration test: the real API frees a settled attempt's slot only after a complete inventory without it.
- [ ] Implement until green.

## Task 7: outbox scanning and relay (§H, A10-D1)

**Files:**
- Create: `OutboxScanner.cs`, `Relayer.cs`.

**Steps:**
- [ ] Scanner tests on a temporary outbox:
  - the classes `.tmp`, `.written` and `.ready` per Base58 account;
  - a file name that is not a canonical Base58 account plus a known suffix is ignored and reported as an anomaly;
  - `complete` is true only if every directory entry was read without an error;
  - an unreadable file (simulated with mode 000), an `EIO` from a fake reader or a directory that vanishes gives `complete: false`;
  - a file read twice with different size or content is not relayed in that round.
- [ ] Relay tests:
  - `.ready` files are relayed while the unit is alive;
  - `.written` files are untouched while it is alive, and relayed after a verified exit (recorded as published by exit);
  - `.tmp` files are never relayed;
  - each definitive answer is recorded once in `relayDir/<uuid>/<account>.state` and never resent;
  - retries back off with jitter and continue indefinitely;
  - nothing in the outbox is ever written, renamed or deleted (the test outbox is made read-only to the agent's user);
  - a file over 4 MiB is a local anomaly and never sent.
- [ ] Integration tests against the real API:
  - relay across API downtime (the test stops and restarts the API server): the result settles once, with no void and no duplicate effect;
  - a `202` quarantine is never resent;
  - `409 AttemptRevoked` after a disable keeps the file and the state.
- [ ] Implement until green.

## Task 8: exit supervision and exit reports

**Files:**
- Create: `ExitSupervisor.cs`.

**Steps:**
- [ ] Tests:
  - when a started unit is gone (`systemctl show` says `inactive` or `failed`, with an exit status), the agent scans, relays every published file (`.ready`, then `.written` per A10-D1) until each has a definitive answer, and only then sends `exited`;
  - the report carries `{exitCode | signal, results: [{accountId, present}] for every participant in the claim's roster, outbox: {complete}}`, where `present` means a `.written` or `.ready` file exists;
  - an incomplete scan sends `complete: false`, and a later successful rescan sends the same report with `complete: true` (the supplementary observation);
  - an identical repeat is idempotent, and a `409 ConflictingReport` is an anomaly that is not retried;
  - a unit that never started (start failed) is reported with its status, which cancels before start.
- [ ] Integration test: results relayed before the exit report, then the exit report frees the slot. A fake-launcher crash with no file present is reported `present: false` with a complete scan, and the backend voids it (exit evidence). The same crash with the API down: nothing is reported until the API returns, and no void happens before that.
- [ ] Implement until green.

## Task 9: restart, reboot and long outages

**Steps:**
- [ ] Tests with a new agent instance over the same state, outbox and fake systemd:
  - restart at every point of Tasks 3–8 resumes without a duplicate start, relay or report;
  - at startup, everything without a definitive relay state is relayed **before** any exit report;
  - units that ended while the agent was down are found through `systemctl show` and reported after their relays;
  - a VM reboot (the fake systemd has lost every unit, and `/run` is empty) is handled from the persisted claims.
- [ ] A long-outage test: with the API unreachable for a simulated hour (fake clock), there are no claims, relays and reports are only retried, nothing reports absence early, and after recovery every result settles and no void is applied.
- [ ] Implement until green.

## Task 10: deployment artifacts (written, not installed)

**Files:**
- Create in `Backend/deploy/mission/`:
  - `blackglass-agent.service`;
  - `blackglass-mission@.service`;
  - `60-blackglass-mission.rules`;
  - `sysusers.d/blackglass.conf`;
  - `tmpfiles.d/blackglass.conf`;
  - `blackglass-tunnel.service` (A10-D3);
  - `install.sh` and `uninstall.sh` (both idempotent, both requiring an explicit `--yes`);
  - `verify.sh` (the VM security checks).

**Steps:**
- [ ] Static tests that read the files:
  - **Template:** `User=blackglass-mission`, `Restart=no`, `UMask=0027`, `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`, `PrivateDevices=yes`, `RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX`, an empty `CapabilityBoundingSet=`, `LoadCredential=process:/run/blackglass/launch/%i/process`, the two fixed `ExecStartPre=+/usr/bin/install …` lines, `TemporaryFileSystem=/var/lib/blackglass/outbox:ro`, `BindPaths=` and `ReadWritePaths=` for `%i` only, and the resource limits.
  - **Agent unit:** `User=blackglass-agent`, `Restart=always`, `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ReadWritePaths=` limited to its state, relay and launch directories, read-only access to the outbox, and the resource limits.
  - **Polkit:** the rule's regex and verbs are tested against a C# reading of the same pattern:
    - it accepts only `start` and `stop` of `^blackglass-mission@<uuid>\.service$`;
    - it refuses `enable`, `mask`, `restart`, other units, upper case, and appended text.
  - **Paths:** tmpfiles give the outbox root `root:blackglass-agent 0750`, the relay, state and launch directories `blackglass-agent 0700`, and the log root as specified.
- [ ] Implement until green.

## Task 11: hardening, observability and the emergency stop

**Steps:**
- [ ] Tests:
  - `blackglass-agent status` prints its claims, units, relay counts and the last heartbeat, with no secrets;
  - `blackglass-agent emergency-stop` (A10-D5) refuses unless it runs as root, then stops every listed `blackglass-mission@*` unit and prints the list (fake systemd);
  - logs are structured, one line per event, with hash prefixes only;
  - a source audit: no `Process.Start` with a shell, no `/bin/sh -c`, no string-built unit names.
- [ ] Implement until green.

## Task 12: the VM step (needs the owner's approval and presence)

This task changes `bg-dev-mission01` (users, directories, units, polkit, agent) and `bg-dev-services01` (one restricted key for the tunnel). It runs only when the owner authorizes it.
- [ ] Record the VM state before: listeners, users, units and packages.
- [ ] Install with `install.sh --yes`, then run `verify.sh`:
  - two units, A and B, from the real template, running the stub (A10-D7):
    - A can't read, write, rename or delete in B's directory (`ENOENT`), and can write its own;
    - the agent can read both and write neither;
  - polkit refuses the agent `enable`, `mask`, `restart`, a malformed unit name and any other unit, and allows `start` and `stop` of a canonical mission unit;
  - the output of `systemd-analyze security` for both units is recorded;
  - the credential file is gone after start, and the unit read it via `$CREDENTIALS_DIRECTORY`.
- [ ] End to end through the tunnel against a **throwaway** accounts stack on services01 (the smoke project's pattern, never the live stack):
  - claim, launch and relay with the stub at each §H crash point, then settle;
  - the API stopped for ten minutes during a mission's end: no void, and everything settles after;
  - an agent restart and a VM reboot mid-mission;
  - the emergency stop;
  - a host disable: claims stop, the files are kept and no unit is stopped by the agent;
  - after a rotation and `SIGHUP`, the agent resumes.
- [ ] Record the VM state after, plus the off-host probe of both VMs. Keep `uninstall.sh` and a verified rollback.

## Task 13: reviews, "As built" and stop

- [ ] Checkpoint review after Task 6. Final whole-branch review on the most capable model. Fix every Critical and Important finding test-first.
- [ ] Full backend suite on the VM runner, the agent suite three times running, then smoke and the restore drill (the claim response change).
- [ ] "As built" (counts, transcripts, rulings and obligations for Stages 11, 12, 16 and 17). Stop before merging, pushing or deploying.

## Acceptance criteria

- Every Stage 8 and Stage 9 agent obligation above has a passing test named in "As built".
- No crash point creates a second unit, launches with an unacknowledged generation, leaves a credential file behind after start, relays a `.tmp` file, touches a `.written` file while its process is alive, or reports `exited` before its relays.
- No backend outage, however long, causes a void. Tested end to end with the API stopped.
- The VM step's isolation, polkit and `systemd-analyze security` evidence is recorded with the owner present.
- `0000`–`0009` are unchanged. Only the listed paths changed. The owner's two Unity files and `.claude/` are untouched.

## Out of scope

- The Unity server's registration, the manifest writer and the `recorded` call (Stages 11–12).
- Co-op attempts.
- Prewarming.
- HAProxy, a private VLAN, Docker for mission servers and orchestration.
- Outbox cleanup (Phase 14).
- Production deployment.
