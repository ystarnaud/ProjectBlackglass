# Dedicated server (Checkpoint A)

How to build, deploy and check the Linux dedicated server. Design: decision 045 and `Docs/superpowers/specs/2026-10-11-linux-dedicated-server-design.md`. The server starts one fixture mission from `Assets/_Project/Scenes/ServerMission.unity`. There is no networking yet.

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

The run writes three files:
- `-smoke.log`: the server's log;
- `-smoke.csv`: samples every 5 s, with CPU as a percentage of one core, from `/proc` utime and stime, plus `VmRSS` and `VmHWM`;
- `-smoke-summary.txt`.

## Options

| Argument | Default | Range | Meaning |
|---|---|---|---|
| `-seed <int>` | fixture seed | any | Generate this seed instead of the fixture's |
| `-fps <int>` | 60 | 1..1000 | Frame cap (`targetFrameRate`, vSync off) |
| `-diagnostic` | off | | Run the diagnostic, then exit |
| `-diagnosticTimeout <s>` | 120 | 5..3600 | Overall diagnostic deadline (real time) |
| `-startupTimeout <s>` | 60 | 5..3600 | Deadline for the mission to become ready |
| `-heartbeat <s>` | 60 | 0 or 5..3600 | Heartbeat interval; 0 turns it off |

Bad values keep the default and log `[Server] option ignored: ...`. Unity's own arguments are passed through.

## Log lines

- `[Server] identity ...`: product, version, revision, build time, build GUID, Unity version, `platform=LinuxServer`, `UNITY_SERVER=yes`, `graphics=Null`.
- `[Server] options ...`, then `[Server] generating ...`.
- `[Server] MISSION READY ...`: instance id, seed, attempt, layout figures, `generationMs`, `navMeshBakeMs`, `sinceStartupS`.
- `[Server] self-check cameras=0 audioListeners=0 canvases=0 eventSystems=0 unitViewsDisabled=<n> unitViewsActive=0`.
- `[Server] heartbeat uptimeS= fps= cpuPct= rssMB= managedMB= state= phase=`, in normal mode.
- `[Diagnostic] prepare|move|attack ...`, then `BLACKGLASS DIAGNOSTIC PASS|FAIL ...`.
- `[Server] exit code=<n> reason=<text>`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Diagnostic passed, or a normal stop |
| 1 | Diagnostic failed (the reason is in the `BLACKGLASS DIAGNOSTIC FAIL` line) |
| 2 | Startup failed: generation failed, not ready within `-startupTimeout`, or the scene is unwired |
| 124 | `run-diagnostic.sh`'s `timeout` killed a hung server |

## Troubleshooting

- **"The Unity Editor has this project open"**: close it and run again.
- **`revision=unknown`**: `build-info.txt` is missing beside the executable. Redeploy the package rather than copying single files.
- **A diagnostic step timed out**: the FAIL line shows the distance travelled, the attack phase, whether the target can be attacked, and whether the simulation was running.
- **Permission denied**: the deploy script sets the executable bits. If you extract by hand, run `chmod +x`.
