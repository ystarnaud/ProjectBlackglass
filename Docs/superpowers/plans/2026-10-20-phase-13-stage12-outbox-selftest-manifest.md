# Unity Linux Outbox Self-Test — Host Manifest for `bg-dev-mission01`

**Status (2026-10-20): revision 4, for the owner's review; not yet approvable and not executed.** The source revision, the VM script and EditMode are final. **PlayMode at this revision and the clean build were stopped by Claude Code for critically low system memory**, so every binary and hash row is PENDING. When memory allows, the build (from the clean worktree, after removing the interrupted run's ignored `Assets/InitTestScene…` files, which the provenance check would otherwise count as dirty) fills them, and the owner then approves the filled manifest. Execution needs the owner's explicit approval of this exact text; any change needs a new approval.

History:
- **Revision 1** was withdrawn (review 1, C1: the player would have run as root, outside the sandbox).
- **Revision 3** was superseded by security review 2:
  - I-N1: the provenance also covers ignored files Unity imports, index flags and content;
  - I-N2: the journal identity reads only the unit's stdout stream and is described accurately, and an automatic owner check gives the independent uid evidence;
  - M6–M10: key=value output, no link-following, a wider baseline, tests rerun in the clean worktree, IPv6 redaction.

**What it is:** a non-networked diagnostic of Stage 11's native outbox inside Unity's **Linux Mono** dedicated-server player. It covers:
- the libc calls through `NativeLibc` and the `blackglass-outbox` worker thread;
- the `.tmp` → `.written` → `.ready` protocol, a `SIGKILL` between `.written` and `.ready`, and the native no-replace rename.

**What it is not:** a Unity mission.
- No mission is generated and no FishNet listener starts.
- The unit has no network.
- No backend secret or process credential exists or is read.
- It runs as an unprivileged dynamic user, refuses root itself, and the actual process uid is confirmed independently.

## 1. The binary, its source and the script

| Item | Value |
|---|---|
| Source revision | `91d51850d404ba814971aa1f7e0828c0726c3097` on branch `phase13-outbox-selftest`: the self-test, the provenance correction and the review fixes, on top of `main` `075fcd4`. Not merged, not pushed |
| Source tree | `PENDING (the build at this revision was not run: the session stopped for low memory)`, `dirty=false`, content hash `PENDING (the build at this revision was not run: the session stopped for low memory)`, as captured before Unity started (`source-provenance.txt` in the package) |
| Build | `Tools/build-server.sh` (release variant) in the dedicated clean worktree `.claude/scratchpad/sb`, detached at that revision. The owner's working project and its two modified settings files were never involved. Unity 6000.3.25f1, Linux Dedicated Server, Mono. The build at this revision is PENDING. The provenance check requires 0 status lines and no ignored file under `Assets`, `Packages` or `ProjectSettings` before it, and the same afterwards. The worktree currently holds the interrupted PlayMode run's ignored `Assets/InitTestScene…` files, which must be removed first |
| Package | `blackglass-server-linux-91d5185.tar.gz`, SHA-256 `PENDING (the build at this revision was not run: the session stopped for low memory)` |
| `files.sha256` (every shipped file, in the package) | PENDING files, SHA-256 of the list `PENDING (the build at this revision was not run: the session stopped for low memory)` (Appendix A) |
| `build-info.txt` | `revision=91d51850d404ba814971aa1f7e0828c0726c3097`, `sourceTree=PENDING (the build at this revision was not run: the session stopped for low memory)`, `dirty=false`, `variant=release`, `backend=Mono`, `binarySha256=PENDING (the build at this revision was not run: the session stopped for low memory)`, `provenance=verified` |
| Game code | `BlackglassServer_Data/Managed/Blackglass.dll` `PENDING (the build at this revision was not run: the session stopped for low memory)`; `Blackglass.Backend.Core.dll` `PENDING (the build at this revision was not run: the session stopped for low memory)` |
| Player executable | `BlackglassServer.x86_64` `PENDING (the build at this revision was not run: the session stopped for low memory)`. This is Unity's generic player, identical across builds, so the game is identified by the list, not by this hash |
| VM-side script | `Tools/LinuxServer/outbox-selftest/selftest-vm.sh` at the same revision, SHA-256 `ccf01c86619ede465bf329c612aa8b811aac2d539adf467592f7cc944a341b69` as sent (LF) |
| Tests at the source revision | Run in the same clean worktree at `91d51850d404ba814971aa1f7e0828c0726c3097`, the Editor closed: EditMode **1864/1864**, PlayMode **not verified: the run was stopped by Claude Code for critically low system memory (2026-10-20 17:43); it must be rerun**. Build provenance tests (`Tools/tests/build-server-provenance-test.sh`): 8/8 |

**Local verification at the build (PENDING):**
- The source is captured before Unity starts.
- After Unity exits, the source, its content hash and HEAD must be exactly as captured.
- The embedded `revision`, `sourceTree` and `dirty` must equal the capture.
- Only then are `binarySha256`, `provenance=verified` and `files.sha256` written and the package written and hashed. Revision 3's build at `310e9aa` did all of this; it is superseded because the VM script changed.

## 2. How every step is run

From the workstation, at the repository root on revision `91d5185`:

```sh
step() { tr -d '\r' < Tools/LinuxServer/outbox-selftest/selftest-vm.sh | ssh bg-dev-mission01 bash -s -- "$@" 2>&1 \
  | sed -E -e 's/[0-9]{1,3}(\.[0-9]{1,3}){3}/<addr>/g' \
           -e 's/([0-9a-fA-F]{1,4}:){4,7}[0-9a-fA-F]{1,4}/<addr6>/g' -e 's/([0-9a-fA-F]{0,4}:){1,6}:[0-9a-fA-F:]*/<addr6>/g' \
  | tee -a .claude/scratchpad/phase13/selftest/vm/transcript.txt; }
```

- The two IPv6 expressions redact full and `::`-compressed addresses, while timestamps (three groups, no `::`) are left intact.
- Before each step, `tr -d '\r' < Tools/LinuxServer/outbox-selftest/selftest-vm.sh | sha256sum` must print `ccf01c86619ede465bf329c612aa8b811aac2d539adf467592f7cc944a341b69`.
- The transcript stays local and is never committed.
- The script runs as root over the existing alias with `set -u` (no `set -e`; every probe prints its value). The unit properties are one bash array.

## 3. The temporary units (exact definitions, from the script)

Each phase is a **transient** service started by `systemd-run --quiet --unit=blackglass-selftest-<phase> --collect` (with `--wait` for `publish`, `resume` and `location`). Nothing is written under `/etc/systemd`, and the unit is gone once collected. Properties, verbatim:

| Group | Properties |
|---|---|
| Identity | `DynamicUser=yes`, `User=blackglass-selftest` (systemd allocates a uid and gid from its dynamic range, 61184–65519, for the run), `NoNewPrivileges=yes`, `CapabilityBoundingSet=` (empty), `AmbientCapabilities=` (empty) |
| Network | `PrivateNetwork=yes` (its own network namespace with only a loopback), `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6` (the mission template's) |
| Filesystem | `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`, `Environment=HOME=/tmp`, `PrivateDevices=yes`, `PrivateIPC=yes`, `UMask=0022`, `StateDirectory=blackglass-selftest/<id>`, `StateDirectoryMode=0755`, `InaccessiblePaths=-/opt/blackglass -/etc/blackglass -/var/lib/blackglass -/run/blackglass -/var/log/blackglass -/var/lib/private/blackglass-outbox` |
| Kernel surface | `RestrictNamespaces=yes`, `RestrictRealtime=yes`, `RestrictSUIDSGID=yes`, `LockPersonality=yes`, `ProtectKernelTunables=yes`, `ProtectKernelModules=yes`, `ProtectKernelLogs=yes`, `ProtectControlGroups=yes`, `ProtectClock=yes`, `ProtectHostname=yes`, `ProtectProc=invisible`, `SystemCallArchitectures=native` |
| Resources | `LimitCORE=0`, `MemoryMax=2G`, `CPUQuota=200%`, `TasksMax=256`, `RuntimeMaxSec=300` |
| Evidence | `LogExtraFields=BLACKGLASS_SELFTEST=<a fresh random UUID per run>` |

The command line is `/opt/blackglass-selftest/91d5185/BlackglassServer.x86_64 -batchmode -nographics -logFile - -outboxSelftest <phase> -selftestId <id>`. stdout goes to the journal.

The fixed UUIDv7 ids:
- `ID1=019a0000-0000-7000-8000-00000000a001`
- `ID2=019a0000-0000-7000-8000-00000000a002`
- `ID3=019a0000-0000-7000-8000-00000000a003`

**How the actual process uid is confirmed, beyond the self-test's own word:**
- **(1) The self-test's own check** `uid ok euid=<n>`: it refuses 0. This is the process's statement.
- **(2) `owner_matches_euid=yes uid=<n>`,** in `publish`, `hold` and `resume`: the uid the kernel recorded as owner of every file the process wrote equals the euid it reported. This is independent of the process's output.
- **(3) For the held run, `ps` and `/proc/<pid>/status`:** `Uid`, `Gid`, `NoNewPrivs`, `CapEff` and `Seccomp`, read by root while the process runs.
- **(4) `stdout_stream_identity`:** the `_UID` and `_GID` journald holds for the unit's stdout stream (`_TRANSPORT=stdout`, so PID 1's own lines, which carry `_UID=0`, are excluded). These are the credentials systemd connected the stream with, after switching to the unit's user and before exec. They corroborate the unit's user; they don't prove the running process's uid.

The `location` phase writes nothing, so it has (1) and (4), and the identical unit shape. It writes no file, which `entries=0` shows.

## 4. Paths, owners and permissions

| Path | Owner, mode | Lifetime |
|---|---|---|
| `/root/selftest-before.txt` | root 0644. The integrity baseline: SHA-256 of every file under `/opt/blackglass/{server,agent,mission}` and `/etc/blackglass`; every blackglass unit file, drop-in and `.wants` link under `/etc/systemd/system`; the targets of both `current` links; every `sshd_config*` under `/etc/ssh`; the names of the loaded kernel modules; `kernel.modules_disabled`. Hashes stay on the VM; only the line count is printed | removed at cleanup |
| `/root/selftest-rev` | root 0644, the installed revision | removed at cleanup |
| `/root/blackglass-selftest-91d5185.tar.gz` | root 0600 (the upload) | removed after extraction |
| `/opt/blackglass-selftest/91d5185/` | root:root, dirs 0755, files 0644, the player 0755 | removed at cleanup |
| `/var/lib/private/blackglass-selftest/` and its `<id>/` directories | the run's dynamic user, 0755 (systemd) | removed at cleanup |
| `/var/lib/blackglass-selftest/` and its `<id>` links into `private/` | root (systemd) | removed at cleanup |

**Never touched:**
- `/opt/blackglass/` (`server/`, `agent/`, `mission/`, `logs/`);
- `/etc/blackglass*`, `/var/lib/blackglass/`, `/var/lib/private/blackglass-outbox/`, `/run/blackglass/`;
- every installed unit (including the Stage 10 mission templates), the SSH configuration, the tunnel, the agent and the kernel's modules.

The self-test refuses:
- a parent naming `blackglass-outbox` (`production_outbox`);
- a `$STATE_DIRECTORY` that isn't exactly `/var/lib/blackglass-selftest/<id>` (`wrong_attempt`, `outside_parent`).

## 5. Steps and expected output

**5.0 `step preflight`** (read-only, except the baseline file). Expected:
- `kernel=6.8.0-…` and `systemd=systemd 255 (255.4-1ubuntu8…)`, both recorded;
- `tunnel=active`;
- `agent_enabled=disabled`, `agent_active=inactive`;
- `mission_units=0`, `selftest_units=0`;
- no `present=` line;
- `tunnel_identity=ActiveEnterTimestamp=<t> MainPID=<p> NRestarts=<n>` (recorded);
- at least 1 GiB on `disk=`;
- `baseline_lines=` more than 0;
- `preflight-done`.

**Stop** on any other value.

**5.1 Upload and install.**
1. Upload: `scp .claude/scratchpad/sb/Builds/blackglass-server-linux-91d5185.tar.gz bg-dev-mission01:/root/blackglass-selftest-91d5185.tar.gz`
2. Install: `step install 91d51850d404ba814971aa1f7e0828c0726c3097 PENDING (the build at this revision was not run: the session stopped for low memory) PENDING (the build at this revision was not run: the session stopped for low memory)`

The install step checks, in order:
- the archive hash;
- the list's hash;
- every shipped file against the list;
- that no file exists outside the list;
- `provenance=verified`, `revision=`, `dirty=false` and `variant=release` in `build-info.txt`.

Expected: two `: OK` lines, `files_verified=PENDING`, `installed=/opt/blackglass-selftest/91d5185`. Any `STOP:` line means stop, then cleanup.

**5.2 `step publish`** (fresh directory ID1). Expected, in order:
- `[Server] … outbox self-test phase=publish`
- `[Selftest] uid ok euid=<n>`, where 61184 ≤ n ≤ 65519
- `[Selftest] publish_a ok stage=Published durable=True failure=- errno=0`
- `[Selftest] republish_a ok stage=Published durable=True failure=- errno=0`
- `[Selftest] conflict_b ok stage=Conflict durable=False failure=- errno=0`
- `[Selftest] noreplace ok code=rename_failed errno=17`
- `[Selftest] main_thread ok maxStepMs=<≤ 50>`
- `[Selftest] frame_loop ok maxFrameMs=<≤ 250>`
- `[Selftest] result=pass phase=publish checks=7 …`
- `[Server] exit code=0 …`
- `stdout_stream_identity=_GID=<g> _UID=<n>`: one value each, the same `n`, never 0
- `exit=0`
- `entry type=f mode=644 uid=<n> size=… name=JG.manifest.ready`
- `entry type=f mode=644 uid=<n> size=… name=JG.manifest.tmp`
- `0c66150575e4fd8efe83d199f4ba056183c3363609ad835d7ef02c94ddddd122  JG.manifest.ready` (manifest-v1 `01-minimal`), then the `.tmp`'s hash (variant B)
- `owner_matches_euid=yes uid=<n>`

**5.3 `step hold`** (fresh directory ID2). Expected:
- `held=yes`;
- `unit_User=blackglass-selftest`, `unit_DynamicUser=yes`, `unit_PrivateNetwork=yes`, `unit_NoNewPrivileges=yes`;
- `process_uid=<n>`, `process_gid=<g>` (both in the dynamic range, never 0), `process_user=blackglass-selftest`;
- `proc_Uid=<n> <n> <n> <n>`, `proc_Gid=<g> <g> <g> <g>`, `proc_NoNewPrivs=1`, `proc_CapEff=0000000000000000`, `proc_Seccomp=2`;
- `active_after_kill=inactive` (or `failed`);
- the journal lines `uid ok euid=<n>`, `hold_a ok stage=Written durable=True failure=- errno=0` and `[Selftest] held`, with no `result=` line (the process was killed);
- `stdout_stream_identity=_GID=<g> _UID=<n>`;
- exactly one entry, `entry type=f mode=644 uid=<n> … name=JG.manifest.written`, SHA-256 `0c661505…d122`: complete, not torn;
- `owner_matches_euid=yes uid=<n>`.

**5.4 `step resume`** (the directory ID2 left by the kill). Expected:
- `uid ok euid=<m>`. `m` may differ from the hold run's `n`; systemd then re-owns the directory for the new run's user;
- `conflict_b ok stage=Conflict …`, `resume_a ok stage=Published durable=True failure=- errno=0`;
- `main_thread ok`, `frame_loop ok`;
- `result=pass phase=resume checks=5`;
- `stdout_stream_identity=` (non-zero);
- `exit=0`;
- exactly one entry, `name=JG.manifest.ready`, SHA-256 `0c661505…d122`, with no `.written` left;
- `owner_matches_euid=yes uid=<m>`.

**5.5 `step location`** (directory ID3, told ID1). Expected:
- `uid ok`;
- `[Selftest] exception FAIL OutboxIOException code=wrong_attempt errno=0`;
- `result=fail phase=publish checks=2 …`, `[Server] exit code=3 …`;
- `stdout_stream_identity=` (non-zero);
- `exit=3`;
- `entries=0`.

**5.6 `step after`.** Expected:
- `integrity=unchanged`, covering the files, units, drop-ins, links, SSH configuration and loaded modules;
- `tunnel=active`;
- `tunnel_identity=` identical to the preflight's (the tunnel never restarted);
- `agent_enabled=disabled`;
- `selftest_units=0`.

## 6. Cleanup (always, also after any stop)

`step cleanup`: it stops and resets any self-test unit, then removes every path in §4. Expected: `cleaned`, `tunnel=active`. The journal keeps the units' lines (harmless, not removed).

## 7. Rollback

Nothing persistent is installed: no unit file, package, permanent user, configuration, firewall change or kernel module. Cleanup is the complete rollback.
- **A hung unit:** `RuntimeMaxSec=300` ends it; `systemctl kill --signal=SIGKILL blackglass-selftest-<phase>` ends it sooner.
- **Verification:** `step after` shows everything in the baseline unchanged, and the tunnel still on its process.

## 8. What passing proves, and what it doesn't

**Proves:** under Unity's Linux Mono player:
- `NativeLibc` resolves `libc.so.6`, and `open`, `openat`, `read`, `write`, `fsync`, `close`, `fstat`, `fchmod`, `ftruncate`, `renameat2` (with `RENAME_NOREPLACE`, refused with errno 17) and `geteuid` (the diagnostic's own import) marshal correctly;
- the worker thread runs, and the main thread spends at most 50 ms per frame while files are written and synced;
- a result survives `SIGKILL` as a complete `.written`, and a later process publishes exactly those bytes once, never replacing them;
- the location check refuses a mismatched directory;
- the player ran as a dynamic, unprivileged user without a network, as the file owners and `/proc` confirm.

**Doesn't prove:**
- errno values other than 17 and `ENOENT`;
- any mission, networking, B24 or Stage 13 behavior.

## Appendix A — every shipped file (`files.sha256`, PENDING lines, SHA-256 of this list `PENDING (the build at this revision was not run: the session stopped for low memory)`)

```
PENDING: written from the clean build at this revision.
```
