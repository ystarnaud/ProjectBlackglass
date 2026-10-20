# Unity Linux Outbox Self-Test — Host Manifest for `bg-dev-mission01`

**Status (2026-10-20): revision 2, for the owner's review; not yet approvable and not executed.** The binary rows (`@REV@`, `@REV7@`, `@ARCHIVE_SHA256@`, `@BINARY_SHA256@`) are filled by the build, which waits for one change: a clean build must stamp `dirty=false` (today `dirty` is read after `BuildPlayer`; the fix is drafted, not yet applied, because this session's Unity runs were stopped for low system memory). The owner then approves the filled manifest. Nothing in this manifest has run on the mission VM. It runs only after the owner approves this exact text: the binary and its hashes, the script and its hash, and the invocations. Any change needs a new approval. Revision 1 was withdrawn after an independent review: its shell quoting would have run the player as root without the sandbox (review C1); this revision sends one script over `ssh … bash -s` with a bash array, and the self-test itself refuses to run as root.

**Purpose:** prove Stage 11's native outbox inside Unity's **Linux Mono** dedicated-server player, which the CoreCLR tests (`vm-test.sh --unity-core`) don't cover. That means the libc calls through `NativeLibc`, the `blackglass-outbox` worker thread, the `.tmp` → `.written` → `.ready` protocol, a `SIGKILL` between `.written` and `.ready`, and the native no-replace rename. **It is not a mission:**
- no mission is generated and no FishNet listener starts;
- the unit has no network;
- no backend secret or process credential exists or is read.

## 1. The binary and the script

| Item | Value |
|---|---|
| Source | branch `phase13-outbox-selftest`, commit `@REV@` (the self-test only, on top of `main` `075fcd4`; not merged) |
| Build | `Tools/build-server.sh` (release variant), run in a clean git worktree of that commit (the owner's two modified settings files are never part of it). Unity 6000.3.25f1, Linux Dedicated Server, Mono |
| Package | `blackglass-server-linux-@REV7@.tar.gz`, SHA-256 `@ARCHIVE_SHA256@` |
| Player | `BlackglassServer.x86_64`, SHA-256 `@BINARY_SHA256@` |
| `build-info.txt` | `revision=@REV@`, `dirty=false`, `variant=release` (the install step refuses anything else) |
| VM-side script | `Tools/LinuxServer/outbox-selftest/selftest-vm.sh`, SHA-256 `ca6271dab51cda5a8756cbebde1cc302dfe7dd12a31383c8b20ba461a8058865` as sent (LF; at `cc053b5`; re-checked at `@REV@`) |

## 2. How every step is run

From the workstation, at the repository root on that commit:

```sh
step() { tr -d '\r' < Tools/LinuxServer/outbox-selftest/selftest-vm.sh | ssh bg-dev-mission01 bash -s -- "$@" 2>&1 \
  | sed -E 's/[0-9]{1,3}(\.[0-9]{1,3}){3}/<addr>/g' | tee -a .claude/scratchpad/phase13/selftest/vm/transcript.txt; }
```

Before each step, the script's SHA-256 is checked: `tr -d '\r' < …/selftest-vm.sh | sha256sum` must print `ca6271dab51cda5a8756cbebde1cc302dfe7dd12a31383c8b20ba461a8058865`. The transcript stays local and is never committed.

Inside the script, as root on the VM:
- `set -u` is on; there is no `set -e`, and every probe prints its own value;
- the unit properties are one bash array;
- every phase is a **transient** unit (`systemd-run`), so nothing is written under `/etc/systemd`;
- `RuntimeMaxSec=300` bounds every phase.

Properties of every phase unit (from the script):
- **Identity:** `DynamicUser=yes`, `User=blackglass-selftest`; no capabilities; `NoNewPrivileges=yes`.
- **Network:** `PrivateNetwork=yes`, so the unit has only its own loopback. The address families are the mission template's (`AF_UNIX AF_INET AF_INET6`).
- **Filesystem:**
  - `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`, `HOME=/tmp` (private and disposable);
  - `PrivateDevices=yes`, `PrivateIPC=yes`;
  - `InaccessiblePaths=-/opt/blackglass -/etc/blackglass -/var/lib/blackglass -/run/blackglass -/var/log/blackglass -/var/lib/private/blackglass-outbox`: the installed server, the agent, the tunnel's files and any credential are invisible.
- **Kernel and resources:**
  - the template's kernel-surface protections (`Protect*`, `Restrict*`, `LockPersonality`, `SystemCallArchitectures=native`);
  - `LimitCORE=0`, `MemoryMax=2G`, `CPUQuota=200%`, `TasksMax=256`.
- **State:** `StateDirectory=blackglass-selftest/<id>`, mode 0755.

The fixed UUIDv7 ids:
- `ID1=019a0000-0000-7000-8000-00000000a001`
- `ID2=019a0000-0000-7000-8000-00000000a002`
- `ID3=019a0000-0000-7000-8000-00000000a003`

## 3. Paths, owners and permissions

| Path | Owner, mode | Lifetime |
|---|---|---|
| `/root/selftest-before.txt` | root 0644: the integrity baseline (SHA-256 of every file under `/opt/blackglass/{server,agent,mission}` and `/etc/blackglass`, every `blackglass-*` unit file, the two `current` links) | removed at cleanup |
| `/root/selftest-rev` | root 0644: the installed revision | removed at cleanup |
| `/root/blackglass-selftest-@REV7@.tar.gz` | root 0600 (the upload) | removed after extraction |
| `/opt/blackglass-selftest/@REV7@/` | root:root, dirs 0755, files 0644, the player 0755 | removed at cleanup |
| `/var/lib/private/blackglass-selftest/` and its `<id>/` directories | the dynamic user, 0755 (systemd) | removed at cleanup |
| `/var/lib/blackglass-selftest/` and its `<id>` links into `private/` | root (systemd) | removed at cleanup |

**Never touched:**
- `/opt/blackglass/`, including `server/`, `agent/`, `mission/` and `logs/`;
- `/etc/blackglass*`, `/var/lib/blackglass/`, `/var/lib/private/blackglass-outbox/`, `/run/blackglass/`;
- every installed unit, the SSH configuration, the tunnel and the agent.

The self-test refuses any parent naming `blackglass-outbox` (`production_outbox`) and any `$STATE_DIRECTORY` that isn't exactly `/var/lib/blackglass-selftest/<id>` (`wrong_attempt`, `outside_parent`).

## 4. Steps and expected output

**4.0 Preflight** (`step preflight`). It is read-only, except that it writes the baseline file. Expected:
- `kernel=6.8.0-…` (recorded);
- `systemd=systemd 255 (255.4-1ubuntu8…)`;
- `tunnel=active`;
- `agent_enabled=disabled`, `agent_active=inactive`;
- `mission_units=0`, `selftest_units=0`;
- no `present=` line;
- the `tunnel_identity=MainPID=… ActiveEnterTimestamp=… NRestarts=…` line (recorded);
- `disk=` at least 1 GiB free;
- `baseline_lines=` more than 0;
- `preflight-done`.

**Stop** on any other value.

**4.1 Upload and install.**
1. Upload: `scp <worktree>/Builds/blackglass-server-linux-@REV7@.tar.gz bg-dev-mission01:/root/blackglass-selftest-@REV7@.tar.gz`
2. Install: `step install @REV@ @ARCHIVE_SHA256@ @BINARY_SHA256@`

Expected: two `: OK` lines, then `installed=/opt/blackglass-selftest/@REV7@`. A `STOP:` line means stop, then cleanup.

**4.2 `step publish`** (fresh directory ID1). Expected, in order:
- `[Server] … outbox self-test phase=publish`
- `[Selftest] uid ok euid=<n ≠ 0>`
- `[Selftest] publish_a ok stage=Published durable=True failure=- errno=0`
- `[Selftest] republish_a ok stage=Published durable=True failure=- errno=0`
- `[Selftest] conflict_b ok stage=Conflict durable=False failure=- errno=0`
- `[Selftest] noreplace ok code=rename_failed errno=17`
- `[Selftest] main_thread ok maxStepMs=<≤ 50>`
- `[Selftest] frame_loop ok maxFrameMs=<≤ 250>`
- `[Selftest] result=pass phase=publish checks=7 …`
- `[Server] exit code=0 …`
- `exit=0`

The directory listing must show exactly two files, mode `-rw-r--r--`, owned by the dynamic uid:
- `JG.manifest.ready`, SHA-256 `0c66150575e4fd8efe83d199f4ba056183c3363609ad835d7ef02c94ddddd122` (manifest-v1 `01-minimal`);
- `JG.manifest.tmp`, the no-replace check's leftover B. A leftover `.tmp` is rewritten by the writer, never promoted.

**4.3 `step hold`** (fresh directory ID2). Expected:
- `held=yes`;
- `unit=User=blackglass-selftest DynamicUser=yes PrivateNetwork=yes NoNewPrivileges=yes`;
- `process=<uid ≠ 0> blackglass-selftest BlackglassServe…`;
- `active_after_kill=inactive` (or `failed`);
- the journal lines `uid ok …`, `hold_a ok stage=Written durable=True failure=- errno=0` and `[Selftest] held`, with no `result=` line (the process was killed);
- exactly one file, `JG.manifest.written`, mode 0644, SHA-256 `0c661505…d122`: complete, not torn, with no `.tmp` and no `.ready`.

**4.4 `step resume`** (the directory ID2 the kill left). Expected:
- `uid ok`;
- `conflict_b ok stage=Conflict …` (B never replaced the held A);
- `resume_a ok stage=Published durable=True failure=- errno=0`;
- `main_thread ok`, `frame_loop ok`;
- `result=pass phase=resume checks=5`, `exit=0`;
- exactly one file, `JG.manifest.ready`, SHA-256 `0c661505…d122`: the held bytes, published once, and no `.written` left (it was renamed, never copied).

**4.5 `step location`** (directory ID3, told ID1). Expected:
- `uid ok`;
- `[Selftest] exception FAIL OutboxIOException code=wrong_attempt errno=0`;
- `result=fail phase=publish checks=2 …`, `[Server] exit code=3 …`, `exit=3`;
- `entries=0`: nothing was written.

**4.6 `step after`.** Expected:
- `integrity=unchanged`;
- `tunnel=active`;
- `tunnel_identity=` identical to the preflight's, the same MainPID and NRestarts (the tunnel never restarted);
- `agent_enabled=disabled`;
- `selftest_units=0`.

## 5. Cleanup (always, also after any stop)

`step cleanup`. Expected: `cleaned`, `tunnel=active`.

It stops and resets any self-test unit, then removes every path in §3. The journal keeps the self-test units' log lines (harmless, and not removed).

## 6. Rollback

Nothing persistent is installed: no unit file, package, permanent user (the dynamic user exists only while a unit runs), configuration or firewall change. Cleanup is the complete rollback.
- **A hung unit:** `RuntimeMaxSec=300` ends it, or `systemctl kill --signal=SIGKILL blackglass-selftest-<phase>` ends it sooner.
- **Verification:** `step after` checks that every installed file, link and unit is unchanged and that the tunnel kept its process.

## 7. What passing proves, and what it doesn't

**Proves:** under Unity's Linux Mono player:
- `NativeLibc` resolves `libc.so.6`, and `open`, `openat`, `read`, `write`, `fsync`, `close`, `fstat`, `fchmod`, `ftruncate`, `renameat2` (with `RENAME_NOREPLACE`, refused with errno 17) and `geteuid` marshal correctly;
- the worker thread runs, and the main thread spends at most 50 ms per frame while files are written and synced;
- a result survives `SIGKILL` as a complete `.written`, and a later process publishes exactly those bytes once and never replaces them;
- the location check refuses a mismatched directory, and the player runs as a dynamic, unprivileged user without a network.

**Doesn't prove:**
- errno values other than 17 and `ENOENT`;
- any mission, networking, B24 or Stage 13 behavior.

It is not a real Unity mission workload.
