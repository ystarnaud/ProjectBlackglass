# Dedicated server (Checkpoint A)

How to build, deploy and check the Linux dedicated server. Design: decision 045 and `Docs/superpowers/specs/2026-10-11-linux-dedicated-server-design.md`. The server starts one fixture mission from `Assets/_Project/Scenes/ServerMission.unity`. Networking is optional: it starts only with `-net` (see "Networked mode").

## Prerequisites

- **Unity 6000.3.25f1** with **Linux Build Support (Mono)** and **Linux Dedicated Server Build Support**. If either is missing, the build stops and prints this command:
  `"C:\Program Files\Unity Hub\Unity Hub.exe" -- --headless install-modules --version 6000.3.25f1 -m linux-mono -m linux-server`
- The Unity Editor closed. Unity allows one instance per project.
- Git Bash.
- SSH access to the VM through the `bg-dev-mission01` alias in `~/.ssh/config`.

## Build and package (Windows)

```bash
Tools/build-server.sh
```

- **Build:** `Builds/LinuxServer/`, containing `BlackglassServer.x86_64`, `BlackglassServer_Data/`, `build-info.txt` and the VM scripts.
- **Package:** `Builds/blackglass-server-linux-<sha>[-dirty].tar.gz`. The script prints its SHA-256.
- **Log:** `Logs/ServerBuild.log`.
- The script switches to the Linux Server target, builds and switches back. The first build re-imports assets for the Linux target and is slow.
- `dirty` means tracked files differed from the commit when the build was made.
- From the Editor: **Blackglass ▸ Server ▸ Build Linux Dedicated Server**. The scene itself is rebuilt with **Blackglass ▸ Server ▸ Build Server Scene**.

## Deploy

```bash
Tools/deploy-server.sh            # newest package to bg-dev-mission01
```

The new build is extracted into `/opt/blackglass/server.new/`, checked, then swapped into `/opt/blackglass/server/`. Logs and measurements go to `/opt/blackglass/logs/`.

## Diagnostic (navigation and combat check)

```bash
ssh bg-dev-mission01 /opt/blackglass/server/run-diagnostic.sh        # optional argument: timeout in s (default 180)
```

Passes with `BLACKGLASS DIAGNOSTIC PASS` and exit 0. The full log is `/opt/blackglass/logs/<UTC>-<rev>-diagnostic.log`.

To run it by hand:

```bash
/opt/blackglass/server/BlackglassServer.x86_64 -batchmode -nographics -logFile - -diagnostic
```

## Smoke run and measurement

```bash
ssh bg-dev-mission01 'nohup /opt/blackglass/server/smoke-run.sh 600 > /opt/blackglass/logs/smoke-run.out 2>&1 < /dev/null &'
# about 11 minutes later
ssh bg-dev-mission01 'cat $(ls -t /opt/blackglass/logs/*-smoke-summary.txt | head -1)'
```

For a long run, pass the seconds (for example `smoke-run.sh 7200`) and start it with `setsid` as well as `nohup`, so a dropped SSH session cannot stop it. An idle mission's RSS climbs for about the first 25 minutes and then levels off near 106 MB, so judge memory stability on runs longer than that.

The run writes three files:
- `-smoke.log`: the server's log;
- `-smoke.csv`: samples every 5 s, with CPU as a percentage of one core, from `/proc` utime and stime, plus `VmRSS` and `VmHWM`;
- `-smoke-summary.txt`.

## Networked mode

The networking experiment's first slice (decision 046). The server takes orders from remote clients through the server gate; everything below is off unless `-net` is given.

```bash
# on the VM, once: random development tokens (mode 600, never printed)
ssh bg-dev-mission01 '/opt/blackglass/server/make-dev-tokens.sh'
# start, check and stop the server (loopback only); ssh -f -n because a plain session stays open while the server runs
ssh -f -n bg-dev-mission01 '/opt/blackglass/server/run-net.sh start > /opt/blackglass/logs/run-net.out 2>&1'
ssh bg-dev-mission01 'cat /opt/blackglass/logs/run-net.out'      # listening on 127.0.0.1:7770
ssh bg-dev-mission01 '/opt/blackglass/server/run-net.sh status'
ssh bg-dev-mission01 '/opt/blackglass/server/run-net.sh stop'
```

The release token file (`/opt/blackglass/dev-tokens.txt`) has one line per account: `account token squad|none [attempt]`. A `squad` account owns the mission's friendlies; a `none` account owns nothing; an entry with an attempt id is only valid for that mission instance (the `account-ticket` entry names a made-up one, to show that a ticket for another attempt is refused). These are development fixtures, not an identity scheme. The fixture has its own file (`/opt/blackglass/dev-tokens-fixture.txt`, made by `make-dev-tokens.sh --fixture` with fresh tokens, never the release ones): the same three accounts, but account A is `squad+fixture`, which lets a connection drive the test fixture below. `run-net.sh` hands that file only to a server started with `NET_FIXTURE=1`, so the release token file never contains `+fixture`. Either file is replaced with `--force` (`--fixture --force` for the fixture file); the flags may come in either order.

On this PC, build the client and run the harness through the tunnel:

```bash
Tools/build-netclient.sh            # Builds/NetTestClient/NetTestClient.exe (the Editor's target must be Windows)
Tools/run-net-harness.sh            # opens ssh -L, runs the seven roles (see the next section), closes the tunnel; logs in Builds/NetHarnessLogs/
```

The part 2 roles are `a` (the owner: steering, queued moves, an attack, six bad requests), `b` (no ownership: every order refused), `ticket` (refused for another attempt) and `badtoken`; the hidden-information slice adds `visibility-a`, `visibility-b`, `visibility-fault` and `fixture-denied` (next section). Each prints `BLACKGLASS NET HARNESS PASS|FAIL role=...` and exits 0 (pass), 1 (a check failed) or 2 (no connection). To play by hand, start the server as above, open `ssh -L 7770:127.0.0.1:7770 bg-dev-mission01`, and run `NetTestClient.exe -server 127.0.0.1 -port 7770 -token <account-a token>`: WASD steers, a click moves (Shift queues), a click on a red unit attacks, X stops, 1 and 2 pick the unit.

### Hidden information and the fixture (decision 047)

With `-net` the server runs the real intelligence with fog on and sends each connection only what it may see (`visibility=squad` in the listening line); it exits with code 2 if the intelligence service, its model or fog is missing. The fixture is a test aid that replaces real sight with scripted observation grants, a scripted hostile and one container.

```bash
Tools/build-server.sh --fixture                  # Builds/blackglass-server-linux-<sha>[-dirty]-fixture.tar.gz, variant=fixture
Tools/deploy-server.sh                           # deploys the newest archive; check variant= in its output
ssh bg-dev-mission01 '/opt/blackglass/server/make-dev-tokens.sh --fixture --force'
ssh -f -n bg-dev-mission01 'NET_FIXTURE=1 /opt/blackglass/server/run-net.sh start > /opt/blackglass/logs/run-net.out 2>&1'
Tools/run-net-harness.sh                         # seven roles, two clients at once for the first two (see below); reads the fixture token file
```

- The fixture build is the release build plus the `BLACKGLASS_NET_FIXTURE` define, not a Development build. In the run recorded in audit §9.4 its process owned only TCP `127.0.0.1:7770`.
- A release build refuses `-netFixture` (exit code 2, `-netFixture is not available in a release build`), so `NET_FIXTURE=1 run-net.sh start` against it prints `the server exited` and the server is not running.
- `Tools/run-net-harness.sh` runs `visibility-b` and `visibility-a` at the same time (B must see nothing of what A is shown), then `a`, `b`, `ticket` and `badtoken`, then `visibility-fault` alone: seven roles. `visibility-fault` sends the fixture's `Fault` verb, which makes the visibility answer for that connection throw; the hostile must disappear (leaving one last-known record) and stay gone, and the server logs `visibility fails closed: the intel source threw InvalidOperationException: fixture fault; hostiles it cannot answer for are hidden` once.
- `Tools/run-net-harness.sh --denied` runs the `fixture-denied` role against the release build started with plain `run-net.sh start`. Account A asks for the fixture and must receive nothing. The refusal is over-determined here: the release token file has no `+fixture` identity, so the token alone is refused. The build-level guarantee comes from the release build refusing `-netFixture` (exit code 2) and the source audits.
- Both end with `leftover clients=<n> tunnel=<pid|none>`. The runner stops the clients it started and the tunnel on every exit path, including a failure. Exit status: 0 all roles passed, 1 a role failed, 2 setup failed, 3 a client or the tunnel was left running (an earlier non-zero status is kept).
- Validated at `cc25114` (2026-10-13, `dirty` from the owner's two settings files only): the fixture build ran all seven roles PASS and the release build ran `--denied` PASS, each server owning only TCP `127.0.0.1:7770`; EditMode 1436/1436 and PlayMode 1434/1434. Details in audit §9.4 and decision 047.
- The VM is left on the release build: redeploy the release archive (`Tools/build-server.sh` then `Tools/deploy-server.sh`) after a fixture run.

To check that nothing is exposed: `ssh bg-dev-mission01 'ss -lntu | grep -E ":7770|:7777"'` must show only `tcp LISTEN ... 127.0.0.1:7770`. To see every socket the server process owns, filter `ss -lntup` by its pid; it must show only that row.

## Options

| Argument | Default | Range | Meaning |
|---|---|---|---|
| `-seed <int>` | fixture seed | any | Generate this seed instead of the fixture's |
| `-fps <int>` | 60 | 1..1000 | Frame cap (`targetFrameRate`, vSync off) |
| `-diagnostic` | off | | Run the diagnostic, then exit |
| `-diagnosticTimeout <s>` | 120 | 5..3600 | Overall diagnostic deadline (real time) |
| `-startupTimeout <s>` | 60 | 5..3600 | Deadline for the mission to become ready |
| `-heartbeat <s>` | 60 | 0 or 5..3600 | Heartbeat interval; 0 turns it off |
| `-net` | off | | Start the networking adapter after the mission is ready (ignored with `-diagnostic`) |
| `-netPort <int>` | 7770 | 1024..65535 | TCP port, bound to 127.0.0.1 only |
| `-netTokens <file>` | none | | Development token file; required with `-net` |
| `-netQuiet` | off | | Freeze hostile AI and the friendlies' own autonomy and force their hits, so a networked run is repeatable |
| `-netFixture` | off | | The hidden-information test fixture. Fixture builds only (a release build exits with code 2); needs `-net`, `-netQuiet` and the loopback bind; only a connection admitted with a `+fixture` token may drive it |

Bad values keep the default and log `[Server] option ignored: ...`. Unity's own arguments are passed through.

## Log lines

- `[Server] identity ...`: product, version, revision, build time, build GUID, Unity version, `platform=LinuxServer`, `UNITY_SERVER=yes`, `graphics=Null`.
- `[Server] options ...`, then `[Server] generating ...`.
- `[Server] MISSION READY ...`: instance id, seed, attempt, layout figures, `generationMs`, `navMeshBakeMs`, `sinceStartupS`.
- `[Server] self-check cameras=0 audioListeners=0 canvases=0 eventSystems=0 unitViewsDisabled=<n> unitViewsActive=0`.
- `[Server] heartbeat uptimeS= fps= cpuPct= rssMB= managedMB= state= phase=`, in normal mode.
- With `-net`: `[Net] server Starting|Started`, `[Net] listening 127.0.0.1:<port> websockets attempt=<id> accounts=<n> quiet=<yes|no> visibility=<squad|fixture>`, `[Net] client <id> connected|welcomed account=<a> owns=<n>|left`, `[Net] hello refused client=<id> reason=<UnknownToken|WrongAttempt|AlreadyConnected>`, `[Net] rejected client=<id> account=<a> reason=<...> command=<n>` (at most one per client per second), and `net=<clients> accepted=<n> rejected=<n> dropped=<n>` at the end of the heartbeat. Tokens never appear. With the fixture: `[Net] fixture step=<n> verb=<Observe|Unobserve|MoveHostile|Search|Fault> account=<a>`, `[Net] visibility client=<id> unit=<n> <Observed|LastKnown>` (fixture runs only) and `[Net] fixture refused client=<id> account=<a>` (a connection without the `+fixture` identity, or a release build). A visibility source that throws is logged once per server process as an error, `[Net] visibility fails closed: the intel source threw <Type>: <message>; hostiles it cannot answer for are hidden`, and the hostiles it could not answer for stay hidden (the fixture's `Fault` verb triggers it on purpose).
- `[Diagnostic] prepare|move|attack ...`, then `BLACKGLASS DIAGNOSTIC PASS|FAIL ...`.
- `[Server] exit code=<n> reason=<text>`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Diagnostic passed, or a normal stop |
| 1 | Diagnostic failed (the reason is in the `BLACKGLASS DIAGNOSTIC FAIL` line) |
| 2 | Startup failed: generation failed, not ready within `-startupTimeout`, the scene is unwired, or `-net` could not start (no or unreadable token file, no valid entry, the port in use) |
| 124 | `run-diagnostic.sh`'s `timeout` killed a hung server |

## Troubleshooting

- **"The Unity Editor has this project open"**: close it and run again.
- **`revision=unknown`**: `build-info.txt` is missing beside the executable. Redeploy the package rather than copying single files.
- **A diagnostic step timed out**: the FAIL line shows the distance travelled, the attack phase, whether the target can be attacked, and whether the simulation was running.
- **Permission denied**: the deploy script sets the executable bits. If you extract by hand, run `chmod +x`.
- **`-net` exits with code 2 and "net: ..."**: the reason follows (`-net needs -netTokens <file>`, `the token file does not exist`, `the token file has no valid entry`, `the transport would not start`). A bad token-file line is skipped with a `[Net] token file: line <n>: ...` warning.
- **The harness cannot open the tunnel**: another `ssh -L` already holds local port 7770 (a leftover from an earlier run). Find it with `ps | grep ssh` and close it; `Tools/run-net-harness.sh` closes the tunnel it opens.
