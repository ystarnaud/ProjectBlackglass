# Unity Linux Outbox Self-Test — Host Manifest for `bg-dev-mission01`

**Status (2026-10-20): revision 5, complete, for the owner's review; not executed.** Every binary, revision and hash row is filled from the verified clean build at `5551a00`, with EditMode and PlayMode green on that exact source tree. Execution needs the owner's explicit approval of this exact text; any change needs a new approval.

History:
- **Revision 4** had every binary row PENDING: PlayMode and the build had been stopped for low system memory. Revision 5 fills them from the clean build at the same revision; nothing else changed.
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
| Source revision | `5551a0095150320a87cf0117467d4cc1b7900f38` on branch `phase13-outbox-selftest`: the self-test, the provenance correction and the review fixes, on top of `main` `075fcd4`. Not merged, not pushed |
| Source tree | `8f7eab29a57b069b34ca04663b7129362c16e881` (equal to `git rev-parse 5551a00^{tree}`), `dirty=false`, content hash `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` (the hash of no change), as captured before Unity started (`source-provenance.txt` in the package) |
| Build | `Tools/build-server.sh` (release variant) in the dedicated clean worktree `.claude/scratchpad/sb`, detached at that revision, 2026-10-21 01:03 UTC (`buildGuid=582ea8992eee426da1a19147391ff421`). The owner's working project and its two modified settings files were never involved. Unity 6000.3.25f1, Linux Dedicated Server, Mono. Before it: 0 status lines and no ignored file under `Assets`, `Packages` or `ProjectSettings`, and the same afterwards. Preparation, all in that disposable worktree: the interrupted run's ignored `Assets/InitTestScene00af4bfb-….unity` and `.meta` (Unity test-runner scenes) were removed, and the 36 renders PlayMode's presentation tests write to `.claude/scratchpad/phase125/` were moved to the run's evidence folder |
| Package | `blackglass-server-linux-5551a00.tar.gz`, SHA-256 `39acdc08d0cc1a8a47c3fc08a9214ef4acd720e500ac98b43b32f3eec481abe4` |
| `files.sha256` (every shipped file, in the package) | 176 files, SHA-256 of the list `4f2f79235cc55eb8abec4cfe8e426d40302448df3ff942c35ca9ee5704de2110` (Appendix A). Rechecked from the extracted archive: every line OK, 176 files present, none unlisted, no `*_DoNotShip` folder |
| `build-info.txt` | `revision=5551a0095150320a87cf0117467d4cc1b7900f38`, `sourceTree=8f7eab29a57b069b34ca04663b7129362c16e881`, `dirty=false`, `variant=release`, `backend=Mono`, `binarySha256=61c388a1cd661a6230c857fd66de0cec86a1ec497d5f6df1b7f1d585263be19f`, `provenance=verified` |
| Game code | `BlackglassServer_Data/Managed/Blackglass.dll` `3da206ac8254b144ca7d984fb34d66d9bf92c730eca4be5bed014e9303508532`; `Blackglass.Backend.Core.dll` `0df91030d828b025e46fcaac3554a567077220fb3bcf6b91747b12f8ab2c39c0` (unchanged from `main`'s core) |
| Player executable | `BlackglassServer.x86_64` `61c388a1cd661a6230c857fd66de0cec86a1ec497d5f6df1b7f1d585263be19f`. This is Unity's generic player, identical across builds, so the game is identified by the list, not by this hash |
| VM-side script | `Tools/LinuxServer/outbox-selftest/selftest-vm.sh` at the same revision, SHA-256 `16326faf0f73889c0095918faf29f0519132b1b517ab771f9476fa56da844c8f` as sent (LF) |
| Tests at the source revision | The Unity source (`Assets`, `Packages`, `ProjectSettings`) at `5551a00` is identical to `91d5185`'s; every later commit changes only `Tools/`. At `5551a00` itself, in the clean worktree, the Editor closed, one Unity process at a time (2026-10-20): EditMode **1864/1864**, PlayMode **1434/1434**. Build provenance tests (`Tools/tests/build-server-provenance-test.sh`): 10/10 |

**Local verification at the build (done; every check passed):**
- The source is captured before Unity starts.
- After Unity exits, the source, its content hash and HEAD must be exactly as captured.
- The embedded `revision`, `sourceTree` and `dirty` must equal the capture.
- Only then are `binarySha256`, `provenance=verified` and `files.sha256` written and the package written and hashed. Revision 3's build at `310e9aa` is superseded and not reused.

## 2. How every step is run

From the workstation, at the repository root on revision `5551a00`:

```sh
step() { tr -d '\r' < Tools/LinuxServer/outbox-selftest/selftest-vm.sh | ssh bg-dev-mission01 bash -s -- "$@" 2>&1 \
  | sed -E -e 's/[0-9]{1,3}(\.[0-9]{1,3}){3}/<addr>/g' \
           -e 's/([0-9a-fA-F]{1,4}:){4,7}[0-9a-fA-F]{1,4}/<addr6>/g' -e 's/([0-9a-fA-F]{0,4}:){1,6}:[0-9a-fA-F:]*/<addr6>/g' \
  | tee -a .claude/scratchpad/phase13/selftest/vm/transcript.txt; }
```

- The two IPv6 expressions redact full and `::`-compressed addresses, while timestamps (three groups, no `::`) are left intact.
- Before each step, `tr -d '\r' < Tools/LinuxServer/outbox-selftest/selftest-vm.sh | sha256sum` must print `16326faf0f73889c0095918faf29f0519132b1b517ab771f9476fa56da844c8f`.
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

The command line is `/opt/blackglass-selftest/5551a00/BlackglassServer.x86_64 -batchmode -nographics -logFile - -outboxSelftest <phase> -selftestId <id>`. stdout goes to the journal.

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
| `/root/blackglass-selftest-5551a00.tar.gz` | root 0600 (the upload) | removed after extraction |
| `/opt/blackglass-selftest/5551a00/` | root:root, dirs 0755, files 0644, the player 0755 | removed at cleanup |
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
1. Upload: `scp .claude/scratchpad/sb/Builds/blackglass-server-linux-5551a00.tar.gz bg-dev-mission01:/root/blackglass-selftest-5551a00.tar.gz`
2. Install: `step install 5551a0095150320a87cf0117467d4cc1b7900f38 39acdc08d0cc1a8a47c3fc08a9214ef4acd720e500ac98b43b32f3eec481abe4 4f2f79235cc55eb8abec4cfe8e426d40302448df3ff942c35ca9ee5704de2110`

The install step checks, in order:
- the archive hash;
- the list's hash;
- every shipped file against the list;
- that no file exists outside the list;
- `provenance=verified`, `revision=`, `dirty=false` and `variant=release` in `build-info.txt`.

Expected: two `: OK` lines, `files_verified=176`, `installed=/opt/blackglass-selftest/5551a00`. Any `STOP:` line means stop, then cleanup.

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
- `owner_matches_euid=yes uid=<m>`. This line is weaker than in `publish` and `hold`: systemd re-owns the state directory for each run's user at start, so it shows the directory's re-owning, not the resume process writing as `<m>`. For `resume`, the uid evidence is (1) and (4) plus the identical unit shape; `/proc` for the same unit shape comes from the held run.

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

## Appendix A — every shipped file (`files.sha256`, 176 lines, SHA-256 of this list `4f2f79235cc55eb8abec4cfe8e426d40302448df3ff942c35ca9ee5704de2110`)

```
61c388a1cd661a6230c857fd66de0cec86a1ec497d5f6df1b7f1d585263be19f  ./BlackglassServer.x86_64
0df91030d828b025e46fcaac3554a567077220fb3bcf6b91747b12f8ab2c39c0  ./BlackglassServer_Data/Managed/Blackglass.Backend.Core.dll
3da206ac8254b144ca7d984fb34d66d9bf92c730eca4be5bed014e9303508532  ./BlackglassServer_Data/Managed/Blackglass.dll
e56a24a6fe023c324d65dfc4c79b19492f052d011e8bbd45051846be6e05092f  ./BlackglassServer_Data/Managed/FishNet.CodeAnalysis.dll
734acdb2acb0eaed3c939472c7867c858720269ef3a6224eadc4e19a47530a04  ./BlackglassServer_Data/Managed/FishNet.Demos.dll
5ee86c554e9873571c391b2af6c515c4601cf9d1061710d328191bab7d420f29  ./BlackglassServer_Data/Managed/FishNet.Runtime.dll
89b7b5b7aab8b8fa2729216761a4a095dfe6dd3807db7923fcd81c2c698d1bda  ./BlackglassServer_Data/Managed/FishyUnityTransport.dll
34edcb91a36163609031338274965fd3f9d5f6acc473c734b8f5d7781eaa9c41  ./BlackglassServer_Data/Managed/GameKit.Dependencies.dll
0a8e5d5634ab713fccb805791b69bf0927b9a2d3afbc7e7d8971e2fb7edff51f  ./BlackglassServer_Data/Managed/Mono.Security.dll
a56146202232958f46bd6a28b5a7da166aea123ee0d646735a46e5c341dfbf1f  ./BlackglassServer_Data/Managed/Newtonsoft.Json.dll
822df2b3bd2108ba0950e2a7c81fc684349a009d947d1009dbe7186ed2894a92  ./BlackglassServer_Data/Managed/SynapseSocket.dll
389b74da3573ddba29fb9e2383cab998622541b25d6f71646db03c994c4e4bfe  ./BlackglassServer_Data/Managed/System.ComponentModel.Composition.dll
dda8639ef1b083f264517608f8eddcebd2583b79f35b84ce0ad3ca5b90044203  ./BlackglassServer_Data/Managed/System.Configuration.dll
2a610cfa0e1fb016225b96035330e9df8363d532594dd1c943e5a1bfd453b651  ./BlackglassServer_Data/Managed/System.Core.dll
03802f3b6f26d4c27b93a06a6bf4cd22114407d17222e43d347a3787ec76b923  ./BlackglassServer_Data/Managed/System.Data.DataSetExtensions.dll
3af5b3767bb370c3e854886d1061e261ec018984c163d3bde5227057795c4280  ./BlackglassServer_Data/Managed/System.Data.dll
10b6f3994bd9f9a5fb2c240a84dfb04e0f2e2fc7cad6710ce0a6b446d1d34892  ./BlackglassServer_Data/Managed/System.Drawing.dll
8b59e4ae91020d79b6226ae5ff4adcac5f068148fa0d06ed083b34d49ca8a03f  ./BlackglassServer_Data/Managed/System.EnterpriseServices.dll
9f3707f223852d79958f97bdb299c8744be4673ccf6f69426b2b07f02f1e5e2f  ./BlackglassServer_Data/Managed/System.IO.Compression.FileSystem.dll
eb6f885fa258df61af2d6489488bb226387091c7ddd53eafc06d517e96c621ac  ./BlackglassServer_Data/Managed/System.IO.Compression.dll
6a5d95a55270bfb3ee7897cf968201c8a3a9363323974cc50338845fad221456  ./BlackglassServer_Data/Managed/System.Net.Http.dll
a6f32936a2685a817a523a12c0736cb2a799e542d6b813e05b1d3e0eb415778f  ./BlackglassServer_Data/Managed/System.Numerics.dll
ab4b10bf89aa7c44b6796584e9c45c000af4d86ae819cccd9ebafe853eb99b64  ./BlackglassServer_Data/Managed/System.Runtime.Serialization.dll
fbc8607d8c4312460f5234c1950a74d098ff8cab5abcc1a093e55b2bbed75d30  ./BlackglassServer_Data/Managed/System.Runtime.dll
308dcf9ebea40815a08427b8777728ab95d415ab19a1680411ad1e211ec6bbc7  ./BlackglassServer_Data/Managed/System.Security.dll
08e6a20e5c08af8b6e23c99ddb8ec622b6430e2062548bd93351c50c77f5f618  ./BlackglassServer_Data/Managed/System.ServiceModel.Internals.dll
a29e81d7b726483603e3045b285dc2c17544990ce688694c7d9bf449a428ddfb  ./BlackglassServer_Data/Managed/System.Transactions.dll
5a8cb7689b351f4ec264f74836178f454b79ab5c7bf534fd890decb1bb1b4c40  ./BlackglassServer_Data/Managed/System.Xml.Linq.dll
dcffb3e54ed98e25c9bf1a1cfbddb47069e05ca7de71840ed9e9bc687093b2aa  ./BlackglassServer_Data/Managed/System.Xml.dll
8a1c54a4f651b79c49309d2df2db78852d8b33991beca4f8ecca241449a053dc  ./BlackglassServer_Data/Managed/System.dll
f1e3de57c866ce2594192555fa0d58c6013e2adcfb57dce0f63562d217f3aaeb  ./BlackglassServer_Data/Managed/Unity.AI.Navigation.dll
9d9c1ff52411decbee5d9f0d1cd61ebeaa222077aa77a52101ea3debafae98e2  ./BlackglassServer_Data/Managed/Unity.Burst.Unsafe.dll
ec0788b9d0ecba53baedaf8d3263046d25a4f11ad477283edb036b8abc1ae8f8  ./BlackglassServer_Data/Managed/Unity.Burst.dll
ff50383f807f9692e4913b97e47d86fefcb8faa33d6d48cd3e3fd6fc7ffb448d  ./BlackglassServer_Data/Managed/Unity.Collections.LowLevel.ILSupport.dll
b716c8c05bcac77cdf4b64e84981079de66e20654cf3984f216bc6d565cdae0c  ./BlackglassServer_Data/Managed/Unity.Collections.dll
d0c7c6d319ab8e2e5263c4a9729cb80b235de6aa69d00d591a576e3338809892  ./BlackglassServer_Data/Managed/Unity.InputSystem.ForUI.dll
ac7a2cfced1d72d98c2c16c487f977e255fd45bf9d29918ef9c19ae02902b303  ./BlackglassServer_Data/Managed/Unity.InputSystem.dll
a159232605f0319294619a069378b5760a945f3196fef5a854f5af1e9a6a23a2  ./BlackglassServer_Data/Managed/Unity.InternalAPIEngineBridge.004.dll
85cf6d49aa28f7e41f670b9a8040b5db99bf4387ed7d76dd98e269e3f55e4d31  ./BlackglassServer_Data/Managed/Unity.Mathematics.dll
1bf6cfe71927a1b8e65e502f5aa369591a9f7732118ccb042cee1869f5294cb0  ./BlackglassServer_Data/Managed/Unity.Networking.Transport.dll
3b39b163d535432274da5c7bce763951d5032725c3ea1ed0d89420192162d956  ./BlackglassServer_Data/Managed/Unity.RenderPipeline.Universal.ShaderLibrary.dll
0dd4b3b39bca021add9a72ce7c43c0476ac526f27b2a86b70102a0998297e0c2  ./BlackglassServer_Data/Managed/Unity.RenderPipelines.Core.Runtime.Shared.dll
2f7dd951cec503879450a7856208f9c00004ec97ed0f0f18253f3490edaf612d  ./BlackglassServer_Data/Managed/Unity.RenderPipelines.Core.Runtime.dll
c34f0765da4a44a5ed949fbc9c601b61486d7fa4e02c446f705ca414bc0365fa  ./BlackglassServer_Data/Managed/Unity.RenderPipelines.Core.ShaderLibrary.dll
a3bd31a4964c362ba074c47ee47a4db07e4779ff0c7b848cfcaeb3590803e72c  ./BlackglassServer_Data/Managed/Unity.RenderPipelines.GPUDriven.Runtime.dll
e4b77d9e10aec98226c852e600e400b1dd785492e81ac460f54022c6eb7f6e4d  ./BlackglassServer_Data/Managed/Unity.RenderPipelines.ShaderGraph.ShaderGraphLibrary.dll
c7cdb5d63ba875fa7648fcdd441004f3221f20bc7634d8671186b30e752e7479  ./BlackglassServer_Data/Managed/Unity.RenderPipelines.Universal.2D.Runtime.dll
0763ebfa2d0563623b0778cec5b05e21e67f0502221f13c9c7f68fc25fb4eef7  ./BlackglassServer_Data/Managed/Unity.RenderPipelines.Universal.Config.Runtime.dll
78a0c8c36f5632bc2ed5d54875d7eb318dd035d8629b2b35095dbaad9fe0789b  ./BlackglassServer_Data/Managed/Unity.RenderPipelines.Universal.Runtime.dll
45f5f743138ab4b5e713649764b2a9891c00a931c714f55bde5fa5283cd0cc44  ./BlackglassServer_Data/Managed/Unity.RenderPipelines.Universal.Shaders.dll
c4e6ad4bbebeec5f3243d4d3b25305e0f428054dae7321b5fc6bf2c728cdbc1b  ./BlackglassServer_Data/Managed/Unity.TextMeshPro.dll
e273b1af70d5ffd8c8252b0ab77a61cb8b7b09d1e4185cc4ebca1c147b502023  ./BlackglassServer_Data/Managed/Unity.UnifiedRayTracing.Runtime.dll
6a907d061b45c48c91605c0b46e3f35eeafd0e9387094b604728640bc5572040  ./BlackglassServer_Data/Managed/UnityEngine.AIModule.dll
d6265b724cd8f3cb1216fff9db702baf8c55f0920c0d837b11d1410e7bc79752  ./BlackglassServer_Data/Managed/UnityEngine.AccessibilityModule.dll
38fc500188ca60190d73f3c26d2ecbdc961eca12e11670c135a241f8ba434050  ./BlackglassServer_Data/Managed/UnityEngine.AdaptivePerformanceModule.dll
398e27d55b934390803ba11a60071674c670fbc148b7e1932728c809b7c4e4cc  ./BlackglassServer_Data/Managed/UnityEngine.AndroidJNIModule.dll
a7636a4340217a483ba8e1abb0630750d2e6ec375fb56fc2321adf8f73d4d5d3  ./BlackglassServer_Data/Managed/UnityEngine.AnimationModule.dll
37e4cce27be3d0a46e5bc11f0106c81a5589858d49d78267b60654a16c283a9f  ./BlackglassServer_Data/Managed/UnityEngine.AssetBundleModule.dll
82abd84d471920edcc7f671973318c9bd2d0fa73b32fb5b65ea729bfe3b26254  ./BlackglassServer_Data/Managed/UnityEngine.AudioModule.dll
e2477e0d273284c9abf9568c052cd4f91d1f80c14cc72648b1a3db5370cf8528  ./BlackglassServer_Data/Managed/UnityEngine.ClothModule.dll
a2d4e097789635a46fd8bd69155b750fd6cd959997ff9f341532d02276364532  ./BlackglassServer_Data/Managed/UnityEngine.ClusterInputModule.dll
72b11ce49ac0a65f82c9f316ab1c2a8bfc1cb044e72c855719e497963a2ca212  ./BlackglassServer_Data/Managed/UnityEngine.ClusterRendererModule.dll
71c18970320b7bc441911a9833efbc7b4b955392da19a8856a2b11da03a07677  ./BlackglassServer_Data/Managed/UnityEngine.ContentLoadModule.dll
742eda2c9ee69cac117f25be762e8dee9eef2a311e04366f8f8e66c910057ad5  ./BlackglassServer_Data/Managed/UnityEngine.CoreModule.dll
c76babbabae5406fa85e57399ca0b2549cddaf3ae7e5a388d54e2e62d13a9fcd  ./BlackglassServer_Data/Managed/UnityEngine.CrashReportingModule.dll
74981d8f72fb6c3d2724199b13c07df7892d4f530006ac9fd7d51e98701bd8e7  ./BlackglassServer_Data/Managed/UnityEngine.DSPGraphModule.dll
3c0c555cf0726029b7092308fb47aa084fdca0cd88043a4fbdaf4abcfc05705b  ./BlackglassServer_Data/Managed/UnityEngine.DirectorModule.dll
a34689a29a2fcb587942f37f5eeed670ef59538242469f34fd6a308b3cedfebe  ./BlackglassServer_Data/Managed/UnityEngine.GIModule.dll
2d6ef442d9c44256ca61dd27b3013f351280b8fc2a440484aa591135c92f9cc5  ./BlackglassServer_Data/Managed/UnityEngine.GameCenterModule.dll
a6d53b1ede90862d2551dccb4f75c139008b655c5643c2aa5f66f20bae74ae31  ./BlackglassServer_Data/Managed/UnityEngine.GraphicsStateCollectionSerializerModule.dll
69caba9422cb0901e0bc67f1e76401f1201464d9427b5a8d02f2f57a7886d6ca  ./BlackglassServer_Data/Managed/UnityEngine.GridModule.dll
b6671ab0f262b1fb5fccc8a04ae4ca9c698062e69df74ee6750238616c6a656b  ./BlackglassServer_Data/Managed/UnityEngine.HierarchyCoreModule.dll
a41c2e9aed5a7a468e1ce9a46f9c241e899639e644f3ac4b679355659229aff4  ./BlackglassServer_Data/Managed/UnityEngine.HierarchyModule.dll
f3f24df4a9d77bb306705e8c6d82ac067c544142de5f1c7aaeb72a5143fe11a8  ./BlackglassServer_Data/Managed/UnityEngine.HotReloadModule.dll
e8282fb04759af5e90d43f488871649fba683db3c8a894091ae269433b1741da  ./BlackglassServer_Data/Managed/UnityEngine.IMGUIModule.dll
615eb5e17fafd92e9a0cc6aee4a89b7e211b896a1a980ecc2cdfa41aab6c6d69  ./BlackglassServer_Data/Managed/UnityEngine.IdentifiersModule.dll
4ba6b502faa02a5d4ba829d430554f1e49e8db67506e7d9d525693f987e5f27c  ./BlackglassServer_Data/Managed/UnityEngine.ImageConversionModule.dll
bad5a65da7387b0c27ab08e1238036989b4068df0001e2470e63e494082095fb  ./BlackglassServer_Data/Managed/UnityEngine.InputForUIModule.dll
391b5ef90b12d2ca9b13875888cb7b7a746c6b45cb0e3d0004d713751d3c0472  ./BlackglassServer_Data/Managed/UnityEngine.InputLegacyModule.dll
7c82ca6b5f5ec17627fe948c1046194edc57ef45253ee4e345ceb38c35e3a86d  ./BlackglassServer_Data/Managed/UnityEngine.InputModule.dll
162cfba3b9a6f87ce7374daf65150befd3775b75a5f617b1000f1a730baf3696  ./BlackglassServer_Data/Managed/UnityEngine.JSONSerializeModule.dll
b03d8ff255a0dad5a594a62f942f1f155ccad4425e609452ad8905315969e4f5  ./BlackglassServer_Data/Managed/UnityEngine.LocalizationModule.dll
9e899b86a5a5407f22dc29502f8455fe4ff7b1854884e12e3c75a885ede955d5  ./BlackglassServer_Data/Managed/UnityEngine.MarshallingModule.dll
7c5a308d6aec37ba58fc7567f1a2b845310c76e46a40e1698edfd8468c93ad26  ./BlackglassServer_Data/Managed/UnityEngine.MultiplayerModule.dll
d767ad3fdf475351396e7f4291706f158a18fc29798d445e703b1f3e72e1b7f0  ./BlackglassServer_Data/Managed/UnityEngine.ParticleSystemModule.dll
5707fed8481d430aeb96fe64422da2c431574ec78522076aefaccc74689d2dac  ./BlackglassServer_Data/Managed/UnityEngine.PerformanceReportingModule.dll
46963218e9d68b3a525a3cfa9e458859e59202c565ee58aae11263d917e97193  ./BlackglassServer_Data/Managed/UnityEngine.Physics2DModule.dll
053f1c6010847daade084dc2871e9e291dfcfc3ba4da79bb94a662b63c2e1d84  ./BlackglassServer_Data/Managed/UnityEngine.PhysicsBackendPhysXModule.dll
5071109fb537625adee24926221f7c4c2dfd663c52e7aec20ca40c0e77adfeb6  ./BlackglassServer_Data/Managed/UnityEngine.PhysicsModule.dll
9ced77877ceaddceef7537932898a9fdb9ca5b6e100a247c72769450a446b25d  ./BlackglassServer_Data/Managed/UnityEngine.PropertiesModule.dll
0b567ee2bd805ca51170cbc9799640acc5690c7efb9e1d9c94365796665a5f1f  ./BlackglassServer_Data/Managed/UnityEngine.RenderAs2DModule.dll
bc7382a68d1e7680ef3f4c37e4f5b10dbf2c3ef71bfc91730d4c4addf6be000b  ./BlackglassServer_Data/Managed/UnityEngine.RuntimeInitializeOnLoadManagerInitializerModule.dll
acf272c478fc4600a6117e2411ff9f7bf7332c2ff81223171035a51b39be7298  ./BlackglassServer_Data/Managed/UnityEngine.ScreenCaptureModule.dll
356a882096247bb37632a344ed80ee02e09457584f6ebd12943b26b7bd194784  ./BlackglassServer_Data/Managed/UnityEngine.ShaderRuntimeModule.dll
cc32839abd91f65d97e244903a4b3e261fbdfa1a676ff9d411bec8691ab27feb  ./BlackglassServer_Data/Managed/UnityEngine.ShaderVariantAnalyticsModule.dll
38f8901c47ae6d8ba497ace7f7d45cd9769c9e793d91e66368a32c67b0c2dcd1  ./BlackglassServer_Data/Managed/UnityEngine.SharedInternalsModule.dll
533ba15fc29dab9703bad19de0677721060e90009a3de9a22da2886c78f173f1  ./BlackglassServer_Data/Managed/UnityEngine.SpriteMaskModule.dll
7d21200cdf9c4bd874a1b686e404ededaf113ac69a0bff4b10a1743960a8f158  ./BlackglassServer_Data/Managed/UnityEngine.SpriteShapeModule.dll
9fffc187e95b900635069a8dbf07d5a5bff698c3942af9bd4d09c2096ec57448  ./BlackglassServer_Data/Managed/UnityEngine.StreamingModule.dll
184ba70170fa5cedba3acda6e246239a8d3429d7967db004731cf096bdc04e5c  ./BlackglassServer_Data/Managed/UnityEngine.SubstanceModule.dll
d5e176a461604f9a9284f5ca31b65bf781e00a3871fab6abe1294b479c311f32  ./BlackglassServer_Data/Managed/UnityEngine.SubsystemsModule.dll
339ce210de0203a632c16dc99f18f72797cfa691ababafadb5768ec9585e6e35  ./BlackglassServer_Data/Managed/UnityEngine.TLSModule.dll
0dda36caf3a61a2d06928300d68fd695af8f3ccb3feeb250d4d21b9b48a48345  ./BlackglassServer_Data/Managed/UnityEngine.TerrainModule.dll
8ed843f0bebecab4dac16a2e98a064f9a543b6ef784c9f2f8c3b0f651470e26f  ./BlackglassServer_Data/Managed/UnityEngine.TerrainPhysicsModule.dll
d2150ccdfd61aaad77c32563df1eb36d44d7f3acfdb290f86eebc498645b20f6  ./BlackglassServer_Data/Managed/UnityEngine.TextCoreFontEngineModule.dll
14f09512e4f1e228dd34657056c08e4a69eed857fa81ee538838a1b822b7b062  ./BlackglassServer_Data/Managed/UnityEngine.TextCoreTextEngineModule.dll
88c76c4990294405dcaf8680678ca58eebaa73fa8b43de348e9acd553070c31b  ./BlackglassServer_Data/Managed/UnityEngine.TextRenderingModule.dll
d7b24c3c9057df7718e9e57c0df36f28e1d4e32fa23e6f4b315892fdb573408b  ./BlackglassServer_Data/Managed/UnityEngine.TilemapModule.dll
89adbe69c72ad323045c93053d3c39daf7b989ead229af39d185683d9ff08560  ./BlackglassServer_Data/Managed/UnityEngine.UI.dll
6e30abc6760fd2f39f2616e583bd593c51a7dd51d7bb995623eca2a94a410b8c  ./BlackglassServer_Data/Managed/UnityEngine.UIElementsModule.dll
54616c649e25f9949248307cd2e5fd22c3d4ab0872f2b368ecd7e970643a67aa  ./BlackglassServer_Data/Managed/UnityEngine.UIModule.dll
d4422ee6cb69b4bb027e0fae6dd542c961ebaba6d347b25a5e880b67fad2263d  ./BlackglassServer_Data/Managed/UnityEngine.UmbraModule.dll
cb18758e5b660064322f758f7e14b136b7351d9ea183dbab2937ca9c1ecca66c  ./BlackglassServer_Data/Managed/UnityEngine.UnityAnalyticsCommonModule.dll
78d892a7fd0f85e01875b60a93035c86e16d81238fe64cd734bd0321ee570c33  ./BlackglassServer_Data/Managed/UnityEngine.UnityAnalyticsModule.dll
9a8b1f76f7f251e8587cd74320c1caae77c7ed7f4bd3d9181142096ac3ea9e07  ./BlackglassServer_Data/Managed/UnityEngine.UnityConnectModule.dll
63746b8af7b87d2e4be5bffda1be71df46f035ba06b4595bca523e75877c9fba  ./BlackglassServer_Data/Managed/UnityEngine.UnityConsentModule.dll
059177bde8c0d889c3202675e6bfa5cf594d414438aaaf44ecf72e8787888e9d  ./BlackglassServer_Data/Managed/UnityEngine.UnityCurlModule.dll
7ba30992274ad7b126e92fe2347bb8f459560520ed1b916d193d8ce58fe237bf  ./BlackglassServer_Data/Managed/UnityEngine.UnityWebRequestAssetBundleModule.dll
37c6dbcde1c948e56d8acbe5583ddf2cf2b83b938c6179820f83fb5199e915c8  ./BlackglassServer_Data/Managed/UnityEngine.UnityWebRequestAudioModule.dll
4d1b0a824ec2436331c6066f4963dbeeed460d581e98947404faf8b965afeabf  ./BlackglassServer_Data/Managed/UnityEngine.UnityWebRequestModule.dll
35abf7e1e179e11cf584c786e76c0d81577b77261356258ab0cf0a8f378db284  ./BlackglassServer_Data/Managed/UnityEngine.UnityWebRequestTextureModule.dll
a218c092b165de833166583880c4623cc9e0b7755d89c3ac1d538b00bbe558f7  ./BlackglassServer_Data/Managed/UnityEngine.UnityWebRequestWWWModule.dll
573405f859d236b9f5c2a6a9d2cf85c623b10d24e94a923599f502203f54c353  ./BlackglassServer_Data/Managed/UnityEngine.VFXModule.dll
eba81a9ec78299c288277153e9b121a71456600fe6d7b7e9aeb92d005a5e1f29  ./BlackglassServer_Data/Managed/UnityEngine.VRModule.dll
50c2bf7dbb59a512bfdd2de2dbb71111ed1dea62fc498389f9dc0c1239f8aaa2  ./BlackglassServer_Data/Managed/UnityEngine.VectorGraphicsModule.dll
851adafb94a4469d5b779e39f112ff4890ef08ea19d7328e36e4bc84a3eefccd  ./BlackglassServer_Data/Managed/UnityEngine.VehiclesModule.dll
aa0bbd997f0e60102b5de841d2dfa145c60d52e7b6e9fe88520cc94ab8eb54d2  ./BlackglassServer_Data/Managed/UnityEngine.VideoModule.dll
0513fdf08ee19bc41561f97774b7242dea98da15873844acfd148f89046906bd  ./BlackglassServer_Data/Managed/UnityEngine.VirtualTexturingModule.dll
624cd766de2a21532c9da20ec8796ada2d89d78a8ee48d10266af579f5c813b8  ./BlackglassServer_Data/Managed/UnityEngine.WindModule.dll
227bff4b91aa73f82ad7bc9761ee25b8e0f1ac4ea4dcde78ba1f698f1df3e099  ./BlackglassServer_Data/Managed/UnityEngine.XRModule.dll
a6710949b062e76f892b73df09f2d68200ab23efc8dc423f6d9c0ade8ac643e9  ./BlackglassServer_Data/Managed/UnityEngine.dll
7e85ece08b13c2d73aff07bb49c49c314ff962ff173c3feafffa11b2c34a1239  ./BlackglassServer_Data/Managed/mscorlib.dll
bf0b7eac9010b75413e4ec1a07a3453bc6a68eeb8e916fbed7c1f2777841f7c7  ./BlackglassServer_Data/Managed/netstandard.dll
8a1082057ac5681dcd4e9c227ed7fb8eb42ac1618963b5de3b65739dd77e2741  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/2.0/Browsers/Compat.browser
751861040b69ea63a3827507b7c8da9c7f549dc181c1c8af4b7ca78cc97d710a  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/2.0/DefaultWsdlHelpGenerator.aspx
e91782a27fa39fc6c1d6ee8b08529f5d35052310d0006034b878eb04b8f2af30  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/2.0/machine.config
49c6160f9d54af4270a3b4e997fc4a8301f79b9e2070118fa46ddbcbbc44f9a2  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/2.0/settings.map
cf2b54339e069b9e42f7d6795eecbf562c50c1011f4ca8f65f578fe52d97ff92  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/2.0/web.config
8a1082057ac5681dcd4e9c227ed7fb8eb42ac1618963b5de3b65739dd77e2741  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.0/Browsers/Compat.browser
751861040b69ea63a3827507b7c8da9c7f549dc181c1c8af4b7ca78cc97d710a  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.0/DefaultWsdlHelpGenerator.aspx
e60aec2c5115d65b3acb3c55ea21576dbd770f579166c017125571e46ae560ed  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.0/machine.config
ce1db1ad8a9512073164e3eccdc193f7eda036e1a9733caec4635de21b2865c8  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.0/settings.map
e38bb8cc68fe5b4edecdfd288d094b9e8ced7629039b2a347682aba0d8bd7492  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.0/web.config
8a1082057ac5681dcd4e9c227ed7fb8eb42ac1618963b5de3b65739dd77e2741  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.5/Browsers/Compat.browser
751861040b69ea63a3827507b7c8da9c7f549dc181c1c8af4b7ca78cc97d710a  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.5/DefaultWsdlHelpGenerator.aspx
ee950004b576fb28dc85f4b0435ed04bf96612de2e8b53be84d07afe85a0de6c  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.5/machine.config
ce1db1ad8a9512073164e3eccdc193f7eda036e1a9733caec4635de21b2865c8  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.5/settings.map
c4cbddd4fd9347b58cc5a72b36dc4ba1ad2bb699e65869d05cd3fb9865f0d824  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/4.5/web.config
4ddd50f31fb968f30bedefc253a46dc3f2890192d05cdaa9e0a64a056eee807e  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/browscap.ini
b12f4e32436f1b417451eb5f69433433ae1f732b0c9e80b039073e1b6daa24da  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/config
0c56e34c69124510fa8c19e7b4c2ca6c1c4ff460ae19f798dd0ca035809e396d  ./BlackglassServer_Data/MonoBleedingEdge/etc/mono/mconfig/config.xml
2a1014074bbfdc0b151d0a0fa830a1fcdb281eb2660d68667e05e489c4516aed  ./BlackglassServer_Data/MonoBleedingEdge/x86_64/libMonoPosixHelper.so
a32af01f8354367e1ded025486c6d78feda6826c0418d094413c019c4f51275d  ./BlackglassServer_Data/MonoBleedingEdge/x86_64/libmono-native.so
3d5c50d4cb5ce5de2352fe5f5e9be28cc6eb8dca8993cdad2692549431fe5c69  ./BlackglassServer_Data/MonoBleedingEdge/x86_64/libmonobdwgc-2.0.so
237cbdc84462d575ae40865eddf8d8ef9745b0b2f7d26753bcec30c96d1de98f  ./BlackglassServer_Data/Plugins/lib_burst_generated.so
7ea94a5d57a58d58c52618e592fd73d9f8487e6026b2531ae2e7b4ee440367b4  ./BlackglassServer_Data/Resources/UnityPlayer.png
32b58bdb9cf090212b46a8ca7436dcdae89e4ef43e9d55a835233c21d1347734  ./BlackglassServer_Data/Resources/unity default resources
f0d8fb1d2e6039419610d6bc79c4057d622f4f3b448b56f69b8939d67138be0c  ./BlackglassServer_Data/RuntimeInitializeOnLoads.json
ac7274d5c5803d7679114a046a860be51626918856fd2126ab955315745a0c4f  ./BlackglassServer_Data/ScriptingAssemblies.json
be888e7a0101493ef6658cfff9c99e92eebb7c6b674b26ec721966844d8c87ec  ./BlackglassServer_Data/app.info
62ea6260f5585535b136f2c85a6772cd99f846738d5fc7463455f8e94ca28259  ./BlackglassServer_Data/boot.config
f3e49b27bcae29efc58c68f3045747fa9ff7b7ea394be776883a487328830bea  ./BlackglassServer_Data/globalgamemanagers
2a2b2326878d97e1b108afc9efbe05ff8ca4c640e40d0c1d8465104853af9fa9  ./BlackglassServer_Data/globalgamemanagers.assets
c0f6ccd506df011926f70d46d2680a19d4e4b1c60cf8c271f3b277a3fe748584  ./BlackglassServer_Data/level0
92782a7e3eac2aacaafc698828403e62da0661937b6c053f22dd0dd707aadb5a  ./BlackglassServer_Data/resources.assets
fe96b7461a4c8abeea6b9a5ebac17ff35d0f82e3d5b87f57338c10d222afc314  ./BlackglassServer_Data/sharedassets0.assets
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  ./FishNet.SDK.Id
c4377f56475d2fc857f17e0087eae467cd5f5a4a39c69935a93c2c9f3ed179ad  ./UnityPlayer.so
fbe9854742ea0c11e5d1c0f7b696d9ff6b77150ffd070f2b058f64330b551b3e  ./build-info.txt
c624974348a9c91034a17430fa684119d8bb71d828cd8921e54198a7c9c57eef  ./libdecor-0.so.0
4a2c5bb1399eed40528e5d34049694d6409ec24e60b41f89264d423fa7058a3a  ./libdecor-cairo.so
055eac8ed367e80a6dcef31728521cdee4b606e6573b2d657a65e826a98afcb0  ./make-dev-tokens.sh
0dc3bcb48699d37ada0e36779e569607b953a3458f93861fcb49e4ef1a60a742  ./run-diagnostic.sh
346a5e5244d355e349a588e5d36386c91752e2fdef22d9012c88d47013359656  ./run-net.sh
715ace7cb407506984e31cc464dc568c9767e7e80a7ef23fd468ad89b2ce57df  ./smoke-run.sh
e465470bdb02af85fe46fb9aa89e251aa4848cc4c9f5441f6e20f97fef4ff37c  ./source-provenance.txt
```
