# Checkpoint A: Linux Dedicated-Server Startup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A reproducible Linux Dedicated Server (Mono) build of Blackglass that starts one small fixture mission without a local player and proves navigation, movement and combat with a bounded PASS/FAIL diagnostic, verified in the Editor, built on Windows and run on `bg-dev-mission01`.

**Architecture:**
- **New server-only scene.** `ServerMission.unity` holds only the simulation systems and a `MissionDirector` carrying the fixture. It is built by an idempotent editor builder.
- **`ServerBootstrap`** (runtime, `Blackglass` assembly):
  - parses the command line;
  - caps the frame rate;
  - logs the build identity;
  - generates the mission;
  - disables the units' view components;
  - then runs the `ServerDiagnostic` step machine or logs a heartbeat.
- **Tooling.** An editor build entry, Git Bash wrappers, and VM scripts produce, deploy and measure the build.
- **No change to shared gameplay code** except one timing field for the NavMesh bake.

**Tech Stack:**
- Unity 6000.3.25f1 with the Linux Build Support (Mono) and Linux Dedicated Server modules (installed 2026-10-11).
- C# in the `Blackglass` assembly and `Assembly-CSharp-Editor`.
- Unity Test Framework (EditMode and PlayMode).
- Bash: Git Bash on Windows, bash with coreutils, procps and awk on Ubuntu 24.04.

**Spec:** `Docs/superpowers/specs/2026-10-11-linux-dedicated-server-design.md` (approved 2026-10-11). Read it before any task.

## Global Constraints

- Unity stays at **6000.3.25f1**. No package is added, removed or updated.
- Build target: **StandaloneLinux64, subtarget Server, scripting backend Mono**. The build never changes Player Settings.
- **No networking, accounts, APIs, databases, settlement changes, reconnect, Docker, orchestration or Asset Studio changes.**
- **Client scenes, prefabs, data assets and `EditorBuildSettings.asset` are never modified.** `ProceduralMission.unity`, `Prototype.unity` and the other client scenes stay byte-identical.
- **The owner's two uncommitted edits are never staged:** `Assets/Settings/Mobile_RPAsset.asset` and `ProjectSettings/URPProjectSettings.asset`. The untracked `.claude/` is never staged either. Stage files by explicit path only, never with `git add -A` or `git add .`.
- **Nothing generated is committed.** Builds, archives, logs and measurement files go in `Builds/` and `Logs/` (both git-ignored) or in `.claude/scratchpad/checkpoint-a/`.
- **VM:** reach it only as the SSH alias `bg-dev-mission01`. Never write its IP address or any key file name into the repo. Remote paths: `/opt/blackglass/server/` holds the complete build; `/opt/blackglass/logs/` holds diagnostic logs and measurement output. Make no other change on the VM: no packages, users or services.
- **Frame cap default 60.** The diagnostic's deadlines (move 30 s, attack 45 s, overall 120 s) use real time.
- **Exit codes:** 0 = diagnostic PASS or normal stop; 1 = diagnostic FAIL; 2 = startup failure.
- **Unity allows one instance per project.** Before any `Tools/run-tests.sh`, `Tools/run-method.sh` or `Tools/build-server.sh`, check that no `Unity.exe` has the project open (`tasklist | grep -i "^Unity.exe"`). Never run two Unity commands at once.
- **Code style:**
  - match the surrounding code: XML `<summary>` comments on types, `internal` seams for tests;
  - no `?.` or `??` on `UnityEngine.Object` references;
  - `FormattableString.Invariant` or `CultureInfo.InvariantCulture` for numbers in logs.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Argument edge cases.** A flag followed directly by another flag (`-seed -diagnostic`), a negative seed (`-seed -5`), a repeated flag (`-fps 30 -fps 90`, last wins), and Unity's own arguments mixed in must all parse predictably. Task 1 pins each one.
2. **A refused or failed build must never package a stale build.** That covers the Editor still open, a compile error, and missing modules. `Tools/build-server.sh` deletes `Builds/LinuxServer/` before Unity runs and refuses to package without a fresh `build-info.txt`. Task 5, Step 7 proves it with a deliberately bad `UNITY_EXE`.
3. **Starting from another working directory** (a supervisor, `cd /`). `build-info.txt` and every server path resolve from the executable, never the current directory. Task 6, Step 4 runs the diagnostic from `/`.
4. **A paused or stalled simulation must end by a real-time deadline**, never hang. Task 3 (diagnostic level) and Task 4 (through the bootstrap) pin it.
5. **Windows CRLF line endings in the VM scripts** would break bash on Ubuntu. `.gitattributes` pins `Tools/LinuxServer/*.sh` to LF and the packager strips CR. Task 5, Step 8 checks the packaged scripts contain no CR.

---

## File map

| File | Responsibility | Task |
|---|---|---|
| `Assets/_Project/Scripts/Server/ServerOptions.cs` (new) | Pure command-line parsing, defaults, ranges and warnings | 1 |
| `Assets/_Project/Scripts/Server/ServerBuildInfo.cs` (new) | Pure `build-info.txt` parsing and formatting, plus the file name | 1 |
| `Assets/_Project/Tests/EditMode/ServerOptionsTests.cs` (new) | E1 | 1 |
| `Assets/_Project/Tests/EditMode/ServerBuildInfoTests.cs` (new) | Parse and format | 1 |
| `Assets/_Project/Scripts/Mission/MissionBuilder.cs` (modify) | Time the bake into `GeneratedMission.NavMeshBakeMs` | 2 |
| `Assets/_Project/Scripts/Mission/MissionDirector.cs` (modify) | `MissionReport.NavMeshBakeMs`, filled in `Fill` | 2 |
| `Assets/_Project/Tests/PlayMode/MissionDirectorPlayModeTests.cs` (modify) | E5 | 2 |
| `Assets/_Project/Scripts/Server/ServerDiagnostic.cs` (new) | The move and attack step machine with real-time deadlines | 3 |
| `Assets/_Project/Tests/PlayMode/ServerDiagnosticPlayModeTests.cs` (new) | Diagnostic pass, pause timeout, not-ready, freeze scope | 3 |
| `Assets/_Project/Scripts/Server/ServerBootstrap.cs` (new) | Startup, logs, Ready handling, view disabling, heartbeat, exit | 4 |
| `Assets/_Project/Editor/Server/ServerSceneBuilder.cs` (new) | Builds `ServerMission.unity` with the fixture | 4 |
| `Assets/_Project/Scenes/ServerMission.unity` (+ `.meta`, generated) | The server scene | 4 |
| `Assets/_Project/Tests/PlayMode/ServerMissionPlayModeTests.cs` (new) | E2, E3, E4 and the startup-failure exit | 4 |
| `Assets/_Project/Editor/Server/ServerBuild.cs` (new) | Preflight, Linux server build, `build-info.txt`, target restore | 5 |
| `Tools/build-server.sh` (new) | Documented build and package entry point | 5 |
| `Tools/deploy-server.sh` (new) | Upload and swap into `/opt/blackglass/server/` | 5 |
| `Tools/LinuxServer/run-diagnostic.sh` (new) | Diagnostic run on the VM | 5 |
| `Tools/LinuxServer/smoke-run.sh` (new) | Ten-minute smoke and measurement on the VM | 5 |
| `.gitattributes` (modify) | `Tools/LinuxServer/*.sh eol=lf` | 5 |
| `CLAUDE.md` (modify) | Two sections, from spec Appendix A | 8 |
| `Docs/OnlineReadinessAudit.md` (modify) | Pointer sentence, then a new §9 | 8 |
| `Docs/Decisions.md` (modify) | Decision 045 | 8 |
| `Docs/DedicatedServer.md` (new) | The run guide | 8 |

Results that aren't committed go to `.claude/scratchpad/checkpoint-a/results.md`. It is the single running record that Task 8 copies numbers from.

---

### Task 0: Fresh baselines (E0)

**Files:** `.claude/scratchpad/checkpoint-a/results.md` (scratch, not committed).

- [ ] **Step 1: Confirm Unity is not running**

Run: `tasklist | grep -i "^Unity.exe" || echo "no editor"`
Expected: `no editor`. If an Editor is running, ask the owner to close it and stop.

- [ ] **Step 2: Full EditMode baseline**

Run: `Tools/run-tests.sh EditMode`
Expected: `result="Passed"` and `EXIT=0`. Record the total. The last known total is 1249; record the real number, whatever it is.

- [ ] **Step 3: Full PlayMode baseline**

This takes about 12 minutes, so run it in the background and wait for the notification.
Run: `Tools/run-tests.sh PlayMode`
Expected: `result="Passed"` and `EXIT=0`. Record the total (last known 1354).

- [ ] **Step 4: Record**

Create `.claude/scratchpad/checkpoint-a/results.md` with these sections:
- `## Baseline` (date, HEAD sha, EditMode n/n, PlayMode n/n);
- `## Editor`;
- `## Build`;
- `## VM`.

If either baseline is not green, stop and report to the owner before changing anything.

---

### Task 1: `ServerOptions` and `ServerBuildInfo` (pure C#)

**Files:**
- Create: `Assets/_Project/Scripts/Server/ServerOptions.cs`
- Create: `Assets/_Project/Scripts/Server/ServerBuildInfo.cs`
- Test: `Assets/_Project/Tests/EditMode/ServerOptionsTests.cs`
- Test: `Assets/_Project/Tests/EditMode/ServerBuildInfoTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class ServerOptions`, with:
    - `int? Seed`, `int FrameRate`, `bool Diagnostic`;
    - `int DiagnosticTimeoutSeconds`, `int StartupTimeoutSeconds`, `int HeartbeatSeconds`;
    - `static ServerOptions Default`;
    - `static ServerOptions Parse(IReadOnlyList<string> args, out List<string> warnings)`;
    - `string Describe(int fixtureSeed)`;
    - the constants `DefaultFrameRate = 60`, `DefaultDiagnosticTimeoutSeconds = 120`, `DefaultStartupTimeoutSeconds = 60`, `DefaultHeartbeatSeconds = 60`;
    - `internal ServerOptions With(int? seed = null, bool? diagnostic = null, int? diagnosticTimeoutSeconds = null)`, for tests and the bootstrap.
  - `public static class ServerBuildInfo`, with:
    - `const string FileName = "build-info.txt"`;
    - `static Dictionary<string,string> Parse(string text)`;
    - `static Dictionary<string,string> ReadBesideExecutable()`;
    - `static string Describe(IReadOnlyDictionary<string,string> values)`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/ServerOptionsTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class ServerOptionsTests
    {
        static ServerOptions Parse(params string[] args) => ServerOptions.Parse(args, out _);

        static ServerOptions Parse(out List<string> warnings, params string[] args) => ServerOptions.Parse(args, out warnings);

        [Test]
        public void NoArguments_GiveTheDefaults()
        {
            var options = Parse(out var warnings);
            Assert.That(options.Seed, Is.Null, "no override: the scene's fixture seed is used");
            Assert.That(options.FrameRate, Is.EqualTo(60));
            Assert.That(options.Diagnostic, Is.False);
            Assert.That(options.DiagnosticTimeoutSeconds, Is.EqualTo(120));
            Assert.That(options.StartupTimeoutSeconds, Is.EqualTo(60));
            Assert.That(options.HeartbeatSeconds, Is.EqualTo(60));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void NullArguments_GiveTheDefaults()
        {
            var options = ServerOptions.Parse(null, out var warnings);
            Assert.That(options.FrameRate, Is.EqualTo(60));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void EveryArgument_IsRead()
        {
            var options = Parse(out var warnings, "-seed", "42", "-fps", "30", "-diagnostic", "-diagnosticTimeout", "90",
                "-startupTimeout", "45", "-heartbeat", "10");
            Assert.That(options.Seed, Is.EqualTo(42));
            Assert.That(options.FrameRate, Is.EqualTo(30));
            Assert.That(options.Diagnostic, Is.True);
            Assert.That(options.DiagnosticTimeoutSeconds, Is.EqualTo(90));
            Assert.That(options.StartupTimeoutSeconds, Is.EqualTo(45));
            Assert.That(options.HeartbeatSeconds, Is.EqualTo(10));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void UnitysOwnArguments_AreIgnored()
        {
            var options = Parse(out var warnings, "/opt/blackglass/server/BlackglassServer.x86_64", "-batchmode", "-nographics",
                "-logFile", "-", "-diagnostic");
            Assert.That(options.Diagnostic, Is.True);
            Assert.That(options.FrameRate, Is.EqualTo(60));
            Assert.That(warnings, Is.Empty);
        }

        [TestCase("-fps", "0")]
        [TestCase("-fps", "1001")]
        [TestCase("-diagnosticTimeout", "4")]
        [TestCase("-startupTimeout", "3601")]
        [TestCase("-heartbeat", "3")]
        [TestCase("-fps", "sixty")]
        public void AnOutOfRangeOrNonNumericValue_KeepsTheDefault_AndWarns(string name, string value)
        {
            var options = Parse(out var warnings, name, value);
            Assert.That(options.FrameRate, Is.EqualTo(60));
            Assert.That(options.DiagnosticTimeoutSeconds, Is.EqualTo(120));
            Assert.That(options.StartupTimeoutSeconds, Is.EqualTo(60));
            Assert.That(options.HeartbeatSeconds, Is.EqualTo(60));
            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.Contains(name, warnings[0]);
        }

        [Test]
        public void AMissingValue_AtTheEnd_Warns()
        {
            var options = Parse(out var warnings, "-seed");
            Assert.That(options.Seed, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void AFlagFollowedByAnotherFlag_DoesNotSwallowIt()
        {
            var options = Parse(out var warnings, "-seed", "-diagnostic");
            Assert.That(options.Seed, Is.Null);
            Assert.That(options.Diagnostic, Is.True, "-diagnostic is still read");
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void ANegativeSeed_IsAValue_NotAFlag()
        {
            Assert.That(Parse("-seed", "-5").Seed, Is.EqualTo(-5));
        }

        [Test]
        public void ARepeatedArgument_LastOneWins()
        {
            Assert.That(Parse("-fps", "30", "-fps", "90").FrameRate, Is.EqualTo(90));
        }

        [Test]
        public void HeartbeatZero_TurnsItOff()
        {
            var options = Parse(out var warnings, "-heartbeat", "0");
            Assert.That(options.HeartbeatSeconds, Is.EqualTo(0));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void ArgumentNames_AreCaseSensitive()
        {
            Assert.That(Parse("-Diagnostic").Diagnostic, Is.False);
        }

        [Test]
        public void Describe_NamesTheSeedSource()
        {
            Assert.That(Parse().Describe(7), Does.StartWith("seed=7(fixture) fps=60 diagnostic=no"));
            Assert.That(Parse("-seed", "9", "-heartbeat", "0").Describe(7), Does.Contain("seed=9(override)").And.Contain("heartbeat=off"));
        }

        [Test]
        public void With_ReplacesOnlyWhatItIsGiven()
        {
            var options = Parse("-fps", "30").With(diagnostic: true, diagnosticTimeoutSeconds: 5);
            Assert.That(options.FrameRate, Is.EqualTo(30));
            Assert.That(options.Diagnostic, Is.True);
            Assert.That(options.DiagnosticTimeoutSeconds, Is.EqualTo(5));
        }
    }
}
```

`Assets/_Project/Tests/EditMode/ServerBuildInfoTests.cs`:

```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class ServerBuildInfoTests
    {
        [Test]
        public void Parse_ReadsKeyValueLines_AndSkipsTheRest()
        {
            var values = ServerBuildInfo.Parse("revision=abc123\r\n\n# comment\nnot a pair\ndirty=true\n builtUtc = 2026-10-11T17:00:00Z \n");
            Assert.That(values["revision"], Is.EqualTo("abc123"));
            Assert.That(values["dirty"], Is.EqualTo("true"));
            Assert.That(values["builtUtc"], Is.EqualTo("2026-10-11T17:00:00Z"));
            Assert.That(values.Count, Is.EqualTo(3));
        }

        [Test]
        public void Parse_OfNothing_IsEmpty()
        {
            Assert.That(ServerBuildInfo.Parse(null), Is.Empty);
        }

        [Test]
        public void Describe_ShowsRevisionDirtyAndTime()
        {
            var values = ServerBuildInfo.Parse("revision=abc123\ndirty=true\nbuiltUtc=2026-10-11T17:00:00Z\nbuildGuid=g1");
            Assert.That(ServerBuildInfo.Describe(values), Is.EqualTo("revision=abc123+dirty built=2026-10-11T17:00:00Z buildGuid=g1"));
        }

        [Test]
        public void Describe_WithoutAFile_SaysUnknown()
        {
            Assert.That(ServerBuildInfo.Describe(ServerBuildInfo.Parse("")), Is.EqualTo("revision=unknown built=unknown buildGuid=unknown"));
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ServerOptionsTests;Blackglass.Tests.ServerBuildInfoTests"`
Expected: `EXIT=1` with `error CS0246: The type or namespace name 'ServerOptions' could not be found`. A compile failure counts as the failing test here.

- [ ] **Step 3: Implement `ServerOptions`**

`Assets/_Project/Scripts/Server/ServerOptions.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Blackglass
{
    /// <summary>
    /// The dedicated server's command-line options (Checkpoint A, decision 045). Pure: parsed from the argument list, never
    /// from Unity. Unknown arguments (Unity's own -batchmode, -nographics, -logFile x) are ignored; a missing, non-numeric or
    /// out-of-range value keeps the default and adds a warning. Names are exact and case-sensitive; a repeated argument's
    /// last value wins.
    /// </summary>
    public sealed class ServerOptions
    {
        public const int DefaultFrameRate = 60;
        public const int DefaultDiagnosticTimeoutSeconds = 120;
        public const int DefaultStartupTimeoutSeconds = 60;
        public const int DefaultHeartbeatSeconds = 60;

        /// <summary>The seed to generate; null uses the scene's fixture seed.</summary>
        public int? Seed { get; private set; }
        public int FrameRate { get; private set; } = DefaultFrameRate;
        public bool Diagnostic { get; private set; }
        public int DiagnosticTimeoutSeconds { get; private set; } = DefaultDiagnosticTimeoutSeconds;
        public int StartupTimeoutSeconds { get; private set; } = DefaultStartupTimeoutSeconds;
        /// <summary>Seconds between heartbeat lines; 0 turns the heartbeat off.</summary>
        public int HeartbeatSeconds { get; private set; } = DefaultHeartbeatSeconds;

        public static ServerOptions Default => new ServerOptions();

        public static ServerOptions Parse(IReadOnlyList<string> args, out List<string> warnings)
        {
            var options = new ServerOptions();
            warnings = new List<string>();
            if (args == null)
                return options;
            for (var i = 0; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "-diagnostic":
                        options.Diagnostic = true;
                        break;
                    case "-seed":
                        if (TryValue(args, ref i, int.MinValue, int.MaxValue, warnings, out var seed))
                            options.Seed = seed;
                        break;
                    case "-fps":
                        if (TryValue(args, ref i, 1, 1000, warnings, out var fps))
                            options.FrameRate = fps;
                        break;
                    case "-diagnosticTimeout":
                        if (TryValue(args, ref i, 5, 3600, warnings, out var diagnostic))
                            options.DiagnosticTimeoutSeconds = diagnostic;
                        break;
                    case "-startupTimeout":
                        if (TryValue(args, ref i, 5, 3600, warnings, out var startup))
                            options.StartupTimeoutSeconds = startup;
                        break;
                    case "-heartbeat":
                        if (TryValue(args, ref i, 0, 3600, warnings, out var heartbeat))
                        {
                            if (heartbeat != 0 && heartbeat < 5)
                                warnings.Add(FormattableString.Invariant($"-heartbeat {heartbeat} is out of range (0 or 5..3600); keeping {options.HeartbeatSeconds}"));
                            else
                                options.HeartbeatSeconds = heartbeat;
                        }
                        break;
                }
            }
            return options;
        }

        // Reads the value after args[i]. A next token that is an integer is the value (so "-seed -5" is a seed); another
        // flag is left alone for the loop to read, and a missing value warns.
        static bool TryValue(IReadOnlyList<string> args, ref int i, int min, int max, List<string> warnings, out int value)
        {
            var name = args[i];
            value = 0;
            if (i + 1 >= args.Count)
            {
                warnings.Add($"{name} has no value; keeping the default");
                return false;
            }
            var text = args[i + 1];
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                if (text.StartsWith("-", StringComparison.Ordinal))
                {
                    warnings.Add($"{name} has no value (next is {text}); keeping the default");
                    return false;
                }
                i++;
                warnings.Add($"{name} {text} is not an integer; keeping the default");
                return false;
            }
            i++;
            if (value < min || value > max)
            {
                warnings.Add(FormattableString.Invariant($"{name} {value} is out of range ({min}..{max}); keeping the default"));
                return false;
            }
            return true;
        }

        /// <summary>A copy with the given values replaced (tests, and the bootstrap's diagnostic hand-off).</summary>
        internal ServerOptions With(int? seed = null, bool? diagnostic = null, int? diagnosticTimeoutSeconds = null)
        {
            var copy = (ServerOptions)MemberwiseClone();
            if (seed.HasValue)
                copy.Seed = seed;
            if (diagnostic.HasValue)
                copy.Diagnostic = diagnostic.Value;
            if (diagnosticTimeoutSeconds.HasValue)
                copy.DiagnosticTimeoutSeconds = diagnosticTimeoutSeconds.Value;
            return copy;
        }

        /// <summary>One log line; `fixtureSeed` is the scene's seed, shown when there is no override.</summary>
        public string Describe(int fixtureSeed) => FormattableString.Invariant(
            $"seed={Seed ?? fixtureSeed}({(Seed.HasValue ? "override" : "fixture")}) fps={FrameRate} diagnostic={(Diagnostic ? "yes" : "no")} " +
            $"diagnosticTimeout={DiagnosticTimeoutSeconds}s startupTimeout={StartupTimeoutSeconds}s heartbeat={(HeartbeatSeconds > 0 ? HeartbeatSeconds + "s" : "off")}");
    }
}
```

- [ ] **Step 4: Implement `ServerBuildInfo`**

`Assets/_Project/Scripts/Server/ServerBuildInfo.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The `build-info.txt` the server build writes beside the executable (key=value lines: revision, dirty, builtUtc,
    /// buildGuid, ...). The server reads it to log which revision is running; in the Editor there is no file and every value
    /// is "unknown".
    /// </summary>
    public static class ServerBuildInfo
    {
        public const string FileName = "build-info.txt";

        public static Dictionary<string, string> Parse(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text))
                return values;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
            }
            return values;
        }

        /// <summary>The file beside the player executable (the folder above `*_Data`); empty when missing or unreadable.</summary>
        public static Dictionary<string, string> ReadBesideExecutable()
        {
            try
            {
                var folder = Path.GetDirectoryName(Application.dataPath);
                var path = Path.Combine(folder ?? ".", FileName);
                return File.Exists(path) ? Parse(File.ReadAllText(path)) : Parse(null);
            }
            catch (Exception)
            {
                return Parse(null);
            }
        }

        public static string Describe(IReadOnlyDictionary<string, string> values)
        {
            var revision = Value(values, "revision");
            if (Value(values, "dirty") == "true")
                revision += "+dirty";
            return $"revision={revision} built={Value(values, "builtUtc")} buildGuid={Value(values, "buildGuid")}";
        }

        static string Value(IReadOnlyDictionary<string, string> values, string key) =>
            values != null && values.TryGetValue(key, out var value) && value.Length > 0 ? value : "unknown";
    }
}
```

- [ ] **Step 5: Run the tests to see them pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ServerOptionsTests;Blackglass.Tests.ServerBuildInfoTests"`
Expected: 22/22 pass, `EXIT=0`. `ServerOptionsTests` has 12 `[Test]` methods plus 6 `TestCase` rows (18 tests), and `ServerBuildInfoTests` has 4.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Server/ServerOptions.cs Assets/_Project/Scripts/Server/ServerOptions.cs.meta \
  Assets/_Project/Scripts/Server/ServerBuildInfo.cs Assets/_Project/Scripts/Server/ServerBuildInfo.cs.meta \
  Assets/_Project/Scripts/Server.meta \
  Assets/_Project/Tests/EditMode/ServerOptionsTests.cs Assets/_Project/Tests/EditMode/ServerOptionsTests.cs.meta \
  Assets/_Project/Tests/EditMode/ServerBuildInfoTests.cs Assets/_Project/Tests/EditMode/ServerBuildInfoTests.cs.meta
git commit -m "Server: command-line options and build-info parsing" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

The `.meta` files are generated by the Step 5 Unity run. Check `git status --short` shows them before adding.

---

### Task 2: NavMesh bake time in the mission report (E5)

**Files:**
- Modify: `Assets/_Project/Scripts/Mission/MissionBuilder.cs`. This touches the `GeneratedMission` class (top of the file) and the bake at about lines 101–104.
- Modify: `Assets/_Project/Scripts/Mission/MissionDirector.cs`. This touches `MissionReport` (about lines 17–47) and `Fill` (about lines 489–521).
- Test: `Assets/_Project/Tests/PlayMode/MissionDirectorPlayModeTests.cs`

**Interfaces:**
- Produces:
  - `GeneratedMission.NavMeshBakeMs` (`double`, internal set);
  - `MissionReport.NavMeshBakeMs` (`double`, a public field like its neighbours).

- [ ] **Step 1: Write the failing test**

Add this to `MissionDirectorPlayModeTests`, after `Generate_ReachesReady_WithTheSquadAndHostilesOnTheNavMesh_AndWiredToTheSystems`:

```csharp
        // Checkpoint A's server logs the bake time; the director records the successful attempt's bake.
        [UnityTest]
        public IEnumerator Generate_RecordsTheNavMeshBakeTime()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            Assert.That(rig.Director.Current.NavMeshBakeMs, Is.GreaterThan(0d));
            Assert.That(rig.Director.Report.NavMeshBakeMs, Is.EqualTo(rig.Director.Current.NavMeshBakeMs));
        }
```

- [ ] **Step 2: Run it to see it fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDirectorPlayModeTests.Generate_RecordsTheNavMeshBakeTime"`
Expected: `EXIT=1`, `error CS1061: 'GeneratedMission' does not contain a definition for 'NavMeshBakeMs'`.

- [ ] **Step 3: Implement**

In `MissionBuilder.cs`, add this to `GeneratedMission` after `Surface`:

```csharp
        /// <summary>Wall-clock milliseconds the NavMesh bake took (diagnostics; the dedicated server logs it).</summary>
        public double NavMeshBakeMs { get; internal set; }
```

In `MissionBuilder.Build`, replace the bake and the return so they read:

```csharp
            var surface = geometry.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            var bake = System.Diagnostics.Stopwatch.StartNew();
            surface.BuildNavMesh();
            bake.Stop();

            return new GeneratedMission
            {
                Root = root,
                Geometry = geometry.transform,
                Actors = actors.transform,
                Surface = surface,
                NavMeshBakeMs = bake.Elapsed.TotalMilliseconds,
                Layout = layout,
                Visuals = visuals,
            };
```

Keep every other initializer line already in that object initializer. Only add the `NavMeshBakeMs` line and the three stopwatch lines.

In `MissionDirector.cs`, add this to `MissionReport` after `public float NavArea;`:

```csharp
        /// <summary>Milliseconds the successful attempt's NavMesh bake took.</summary>
        public double NavMeshBakeMs;
```

`Fill` does not receive the mission, so set the value at the call site. In `TryAttempt`, change `Fill(Report, layout, navigation, plan);` to:

```csharp
            Fill(Report, layout, navigation, plan);
            Report.NavMeshBakeMs = mission.NavMeshBakeMs;
```

- [ ] **Step 4: Run it to see it pass, plus the director suite**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDirectorPlayModeTests"`
Expected: all pass, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Mission/MissionBuilder.cs Assets/_Project/Scripts/Mission/MissionDirector.cs Assets/_Project/Tests/PlayMode/MissionDirectorPlayModeTests.cs
git commit -m "Mission: record the NavMesh bake time in the mission report" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `ServerDiagnostic`, the move and attack step machine

**Files:**
- Create: `Assets/_Project/Scripts/Server/ServerDiagnostic.cs`
- Test: `Assets/_Project/Tests/PlayMode/ServerDiagnosticPlayModeTests.cs`

**Interfaces:**
- Consumes:
  - `MissionDirector` (`State`, `Friendlies`, `Hostiles`);
  - `CommandableUnit.Issue(UnitCommand, IssueMode)`, `CurrentCommand`, `IsAlive`, `AttackPhase`;
  - `UnitMover.TrySnap(Vector3, out Vector3)`;
  - `UnitAttacker.HitRoll` (internal), `Hits`, `ShotsFired`, `IsTargetInCover(Health)`, `CanAttack(Health)`;
  - `Health.Current`;
  - `SimulationTime.IsRunning`;
  - `MoveCommand(Vector3)`, `AttackCommand(Health)`, `StopCommand()`.
- Produces `internal sealed class ServerDiagnostic`, with:
  - `ServerDiagnostic(MissionDirector director, double timeoutSeconds, Action<string> log)`;
  - `void Begin(double now)`;
  - `void Tick(double now)`;
  - `bool IsFinished`, `bool Passed`, `string Summary`;
  - `CommandableUnit Mover`, `Health Target`;
  - the constants `Prefix = "[Diagnostic] "`, `MoveDistance = 6f`, `ArrivalTolerance = 0.75f`, `MinimumTravel = 3f`, `MoveDeadlineSeconds = 30d`, `AttackDeadlineSeconds = 45d`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/ServerDiagnosticPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The Checkpoint A diagnostic on a MissionRig mission: real generation, real units, real Issue path.</summary>
    public class ServerDiagnosticPlayModeTests
    {
        MissionRig rig;
        readonly List<string> lines = new List<string>();

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            lines.Clear();
        }

        // Ranged first, as in the server fixture: the mover then needs line of sight to hit.
        IEnumerator Ready()
        {
            rig = new MissionRig();
            var slots = MissionRig.FriendlySlots();
            rig.SetFriendlySlots(new[] { slots[2], slots[0] });
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
        }

        static double Now => Time.realtimeSinceStartupAsDouble;

        IEnumerator RunToEnd(ServerDiagnostic diagnostic, float limitSeconds)
        {
            var limit = Now + limitSeconds;
            while (!diagnostic.IsFinished && Now < limit)
            {
                diagnostic.Tick(Now);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Diagnostic_MovesThenHits_ThroughTheCommandPath_AndPasses()
        {
            yield return Ready();
            var diagnostic = new ServerDiagnostic(rig.Director, 120, lines.Add);
            diagnostic.Begin(Now);
            var target = diagnostic.Target;
            Assert.That(target, Is.Not.Null, string.Join("\n", lines));
            var before = target.Current;

            yield return RunToEnd(diagnostic, 120f);

            Assert.That(diagnostic.IsFinished, Is.True, "finished within the deadline");
            Assert.That(diagnostic.Passed, Is.True, diagnostic.Summary + "\n" + string.Join("\n", lines));
            StringAssert.StartsWith("BLACKGLASS DIAGNOSTIC PASS", diagnostic.Summary);
            Assert.That(target.Current, Is.LessThan(before));
            Assert.That(lines.Any(l => l.Contains("move PASS")), Is.True);
            Assert.That(lines.Any(l => l.Contains("attack PASS")), Is.True);
        }

        [UnityTest]
        public IEnumerator Diagnostic_FreezesOnlyTheSpawnedInstances_AndLeavesTheMoverAlone()
        {
            yield return Ready();
            var diagnostic = new ServerDiagnostic(rig.Director, 120, lines.Add);
            diagnostic.Begin(Now);

            foreach (var hostile in rig.Director.Hostiles)
            {
                Assert.That(hostile.GetComponent<EnemyAI>().enabled, Is.False, hostile.name);
                Assert.That(hostile.GetComponent<AutoRetaliate>().enabled, Is.False, hostile.name);
            }
            foreach (var friendly in rig.Director.Friendlies.Skip(1))
            {
                Assert.That(friendly.GetComponent<CompanionAI>().enabled, Is.False, friendly.name);
                Assert.That(friendly.GetComponent<AutoRetaliate>().enabled, Is.False, friendly.name);
            }
            var mover = rig.Director.Friendlies[0];
            Assert.That(diagnostic.Mover, Is.EqualTo(mover));
            Assert.That(mover.GetComponent<CompanionAI>().enabled, Is.True, "the mover's own components are untouched");
            Assert.That(mover.GetComponent<UnitAttacker>().HitRoll(), Is.EqualTo(0f), "the forced roll lands even in cover");
            Assert.That(rig.Director.Friendlies[1].GetComponent<UnitAttacker>().HitRoll, Is.Not.SameAs(mover.GetComponent<UnitAttacker>().HitRoll),
                "only the mover's roll is replaced");
        }

        [UnityTest]
        public IEnumerator Diagnostic_WhileSimulationIsPaused_FailsByItsRealTimeDeadline_InsteadOfHanging()
        {
            yield return Ready();
            rig.Pause.Pause();
            var diagnostic = new ServerDiagnostic(rig.Director, 5, lines.Add);
            var started = Now;
            diagnostic.Begin(started);

            yield return RunToEnd(diagnostic, 15f);

            Assert.That(diagnostic.IsFinished, Is.True);
            Assert.That(diagnostic.Passed, Is.False);
            Assert.That(Now - started, Is.LessThan(10d), "ended near the 5 s overall timeout");
            StringAssert.StartsWith("BLACKGLASS DIAGNOSTIC FAIL step=move", diagnostic.Summary);
            StringAssert.Contains("timed out", diagnostic.Summary);
            StringAssert.Contains("simulationRunning=False", diagnostic.Summary);
        }

        [UnityTest]
        public IEnumerator Diagnostic_OnAMissionThatIsNotReady_FailsAtPrepare()
        {
            rig = new MissionRig();
            yield return null;
            var diagnostic = new ServerDiagnostic(rig.Director, 120, lines.Add);
            diagnostic.Begin(Now);

            Assert.That(diagnostic.IsFinished, Is.True);
            Assert.That(diagnostic.Passed, Is.False);
            StringAssert.StartsWith("BLACKGLASS DIAGNOSTIC FAIL step=prepare", diagnostic.Summary);
        }
    }
}
#endif
```

- [ ] **Step 2: Run them to see them fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.ServerDiagnosticPlayModeTests"`
Expected: `EXIT=1`, `error CS0246: ... 'ServerDiagnostic'`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Server/ServerDiagnostic.cs`:

```csharp
using System;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>
    /// Checkpoint A's bounded navigation and combat check on a ready mission (decision 045). Prepare picks the first friendly
    /// (the active character) and the living hostile with the shortest complete NavMesh path, then removes chance from the
    /// run on the spawned instances only: hostiles' EnemyAI and AutoRetaliate and the other friendlies' CompanionAI and
    /// AutoRetaliate are disabled, and the mover's hit roll is forced to land (cover cannot turn the shot into a miss). Move
    /// issues a MoveCommand to a point 6 m along that path and needs the order finished, the unit within 0.75 m and at
    /// least 3 m travelled. Attack issues an AttackCommand and needs the target's health to drop and the mover's hit count to
    /// rise. Every step has a real-time deadline inside the overall timeout, so a paused or stalled simulation fails instead
    /// of hanging. Nothing here is a game rule: both orders go through CommandableUnit.Issue.
    /// </summary>
    internal sealed class ServerDiagnostic
    {
        public const string Prefix = "[Diagnostic] ";
        public const float MoveDistance = 6f;
        public const float ArrivalTolerance = 0.75f;
        public const float MinimumTravel = 3f;
        public const double MoveDeadlineSeconds = 30d;
        public const double AttackDeadlineSeconds = 45d;

        enum Step
        {
            Prepare,
            Move,
            Attack,
            Done,
        }

        readonly MissionDirector director;
        readonly double timeoutSeconds;
        readonly Action<string> log;
        readonly NavMeshPath path = new NavMeshPath();
        Step step = Step.Prepare;
        double overallDeadline;
        double stepStarted;
        double moveSeconds;
        UnitAttacker attacker;
        Vector3 start;
        Vector3 destination;
        int healthBefore;
        int hitsBefore;
        int shotsBefore;

        public ServerDiagnostic(MissionDirector director, double timeoutSeconds, Action<string> log)
        {
            this.director = director;
            this.timeoutSeconds = timeoutSeconds;
            this.log = log ?? (_ => { });
        }

        public bool IsFinished { get; private set; }
        public bool Passed { get; private set; }
        /// <summary>The final line: BLACKGLASS DIAGNOSTIC PASS ... or BLACKGLASS DIAGNOSTIC FAIL step=... reason=...</summary>
        public string Summary { get; private set; }
        public CommandableUnit Mover { get; private set; }
        public Health Target { get; private set; }

        /// <summary>Prepare and issue the move. Call once, in the frame the mission became ready.</summary>
        public void Begin(double now)
        {
            overallDeadline = now + timeoutSeconds;
            if (director == null || director.State != MissionState.Ready)
            {
                Fail("prepare", "the mission is not ready");
                return;
            }
            if (director.Friendlies.Count == 0 || director.Hostiles.Count == 0)
            {
                Fail("prepare", "the mission has no friendly or no hostile");
                return;
            }
            Mover = director.Friendlies[0];
            attacker = Mover.GetComponent<UnitAttacker>();
            var mover = Mover.GetComponent<UnitMover>();
            if (!TryChooseTarget(mover, out var length))
            {
                Fail("prepare", $"unreachable: no living hostile has a complete NavMesh path from {Mover.name}");
                return;
            }
            Freeze();
            attacker.HitRoll = () => 0f;
            log(Prefix + FormattableString.Invariant(
                $"prepare mover={Mover.name} target={Target.name} pathM={length:0.0} targetInCover={attacker.IsTargetInCover(Target)} simulationRunning={SimulationTime.IsRunning}"));

            var along = PointAlong(path, Mathf.Min(MoveDistance, length * 0.5f));
            if (!mover.TrySnap(along, out destination))
            {
                Fail("move", "the point along the path is not on the NavMesh");
                return;
            }
            start = Mover.transform.position;
            if (!Mover.Issue(new MoveCommand(destination)))
            {
                Fail("move", "CommandableUnit.Issue refused the move");
                return;
            }
            step = Step.Move;
            stepStarted = now;
            log(Prefix + FormattableString.Invariant($"move issued to ({destination.x:0.0}, {destination.z:0.0}), {Flat(start, destination):0.0} m away"));
        }

        public void Tick(double now)
        {
            if (IsFinished)
                return;
            switch (step)
            {
                case Step.Move:
                    TickMove(now);
                    break;
                case Step.Attack:
                    TickAttack(now);
                    break;
            }
        }

        void TickMove(double now)
        {
            if (Mover == null || !Mover.IsAlive)
            {
                Fail("move", "the mover died or was destroyed");
                return;
            }
            var position = Mover.transform.position;
            var travelled = Flat(start, position);
            var remaining = Flat(position, destination);
            if (Mover.CurrentCommand == null)
            {
                if (remaining <= ArrivalTolerance && travelled >= MinimumTravel)
                {
                    moveSeconds = now - stepStarted;
                    log(Prefix + FormattableString.Invariant($"move PASS travelled={travelled:0.0}m remaining={remaining:0.00}m seconds={moveSeconds:0.0}"));
                    BeginAttack(now);
                    return;
                }
                Fail("move", FormattableString.Invariant($"the order ended short: travelled={travelled:0.0}m remaining={remaining:0.00}m"));
                return;
            }
            if (TimedOut(now, MoveDeadlineSeconds, out var which))
                Fail("move", FormattableString.Invariant(
                    $"timed out ({which}): travelled={travelled:0.0}m remaining={remaining:0.00}m simulationRunning={SimulationTime.IsRunning} command={Mover.CurrentCommand.GetType().Name}"));
        }

        void BeginAttack(double now)
        {
            healthBefore = Target.Current;
            hitsBefore = attacker.Hits;
            shotsBefore = attacker.ShotsFired;
            if (!Mover.Issue(new AttackCommand(Target)))
            {
                Fail("attack", "CommandableUnit.Issue refused the attack");
                return;
            }
            step = Step.Attack;
            stepStarted = now;
            log(Prefix + FormattableString.Invariant($"attack issued on {Target.name} health={healthBefore} distance={Flat(Mover.transform.position, Target.transform.position):0.0}m"));
        }

        void TickAttack(double now)
        {
            if (Target == null || Mover == null)
            {
                Fail("attack", "the mover or the target was destroyed");
                return;
            }
            if (Target.Current < healthBefore && attacker.Hits > hitsBefore)
            {
                var attackSeconds = now - stepStarted;
                log(Prefix + FormattableString.Invariant(
                    $"attack PASS health={healthBefore}->{Target.Current} hits={attacker.Hits - hitsBefore} shots={attacker.ShotsFired - shotsBefore} seconds={attackSeconds:0.0}"));
                Mover.Issue(new StopCommand());
                step = Step.Done;
                IsFinished = true;
                Passed = true;
                Summary = FormattableString.Invariant($"BLACKGLASS DIAGNOSTIC PASS moveS={moveSeconds:0.0} attackS={attackSeconds:0.0}");
                return;
            }
            if (TimedOut(now, AttackDeadlineSeconds, out var which))
                Fail("attack", FormattableString.Invariant(
                    $"timed out ({which}): health={healthBefore}->{Target.Current} hits={attacker.Hits - hitsBefore} phase={Mover.AttackPhase} " +
                    $"distance={Flat(Mover.transform.position, Target.transform.position):0.0}m canAttack={attacker.CanAttack(Target)} simulationRunning={SimulationTime.IsRunning}"));
        }

        bool TimedOut(double now, double stepDeadline, out string which)
        {
            if (now > overallDeadline)
            {
                which = FormattableString.Invariant($"overall timeout {timeoutSeconds:0} s");
                return true;
            }
            if (now - stepStarted > stepDeadline)
            {
                which = FormattableString.Invariant($"step deadline {stepDeadline:0} s");
                return true;
            }
            which = null;
            return false;
        }

        bool TryChooseTarget(UnitMover mover, out float bestLength)
        {
            bestLength = float.MaxValue;
            Health best = null;
            if (!mover.TrySnap(Mover.transform.position, out var from))
                return false;
            foreach (var hostile in director.Hostiles)
            {
                if (hostile == null || !hostile.IsAlive || !hostile.TryGetComponent<Health>(out var health))
                    continue;
                if (!mover.TrySnap(hostile.transform.position, out var to))
                    continue;
                if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    continue;
                var length = Length(path);
                if (length < bestLength)
                {
                    bestLength = length;
                    best = health;
                }
            }
            if (best == null)
                return false;
            Target = best;
            // Leave `path` holding the chosen target's path for PointAlong.
            mover.TrySnap(Target.transform.position, out var chosen);
            NavMesh.CalculatePath(from, chosen, NavMesh.AllAreas, path);
            return true;
        }

        // Diagnostic-only: instance components, never assets; the mover keeps all of its own.
        void Freeze()
        {
            foreach (var hostile in director.Hostiles)
            {
                Disable<EnemyAI>(hostile);
                Disable<AutoRetaliate>(hostile);
            }
            foreach (var friendly in director.Friendlies)
            {
                if (friendly == Mover)
                    continue;
                Disable<CompanionAI>(friendly);
                Disable<AutoRetaliate>(friendly);
            }
        }

        static void Disable<T>(Component unit) where T : Behaviour
        {
            if (unit != null && unit.TryGetComponent<T>(out var component))
                component.enabled = false;
        }

        static float Length(NavMeshPath navPath)
        {
            var corners = navPath.corners;
            var length = 0f;
            for (var i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);
            return length;
        }

        static Vector3 PointAlong(NavMeshPath navPath, float distance)
        {
            var corners = navPath.corners;
            for (var i = 1; i < corners.Length; i++)
            {
                var segment = Vector3.Distance(corners[i - 1], corners[i]);
                if (distance <= segment)
                    return Vector3.Lerp(corners[i - 1], corners[i], segment > 0f ? distance / segment : 0f);
                distance -= segment;
            }
            return corners.Length > 0 ? corners[corners.Length - 1] : Vector3.zero;
        }

        static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        void Fail(string failedStep, string reason)
        {
            step = Step.Done;
            IsFinished = true;
            Passed = false;
            Summary = $"BLACKGLASS DIAGNOSTIC FAIL step={failedStep} reason={reason}";
            log(Prefix + $"{failedStep} FAIL {reason}");
        }
    }
}
```

- [ ] **Step 4: Run them to see them pass**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.ServerDiagnosticPlayModeTests"`
Expected: 4/4 pass, `EXIT=0`.

If the pass test fails, do not loosen a threshold. Read its log lines:
- "the order ended short" means the destination snapped somewhere the agent couldn't reach: check `TrySnap` and the path corners.
- An attack timeout with `canAttack=False` means the approach or reposition logic stalled. That is a real finding, so record it and stop for the owner.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Server/ServerDiagnostic.cs Assets/_Project/Scripts/Server/ServerDiagnostic.cs.meta \
  Assets/_Project/Tests/PlayMode/ServerDiagnosticPlayModeTests.cs Assets/_Project/Tests/PlayMode/ServerDiagnosticPlayModeTests.cs.meta
git commit -m "Server: bounded move and attack diagnostic with real-time deadlines" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `ServerBootstrap`, the server scene and the fixture seed (E2–E4)

**Files:**
- Create: `Assets/_Project/Scripts/Server/ServerBootstrap.cs`
- Create: `Assets/_Project/Editor/Server/ServerSceneBuilder.cs`
- Generate: `Assets/_Project/Scenes/ServerMission.unity` (+ `.meta`)
- Test: `Assets/_Project/Tests/PlayMode/ServerMissionPlayModeTests.cs`
- Temporary, deleted before commit: `Assets/_Project/Tests/PlayMode/ServerFixtureSeedProbe.cs`

**Interfaces:**
- Consumes:
  - `ServerOptions` and `ServerBuildInfo` (Task 1);
  - `ServerDiagnostic` (Task 3);
  - `MissionReport.NavMeshBakeMs` (Task 2);
  - `MissionDirector.Initialize(...)` and `Generate(int)`, `StateChanged`, `Report`, `Settings`, `Friendlies`, `Hostiles`, `Current`, `Phase`, `InstanceId`;
  - `ActiveCharacter.Initialize(CommandableUnit, TacticalPause, UnitSelection)`;
  - `CoverDiscovery.Initialize(CoverRegistry, bool, CoverGenerationSettings)`.
- Produces:
  - `public static class ServerExitCodes { Success = 0, DiagnosticFailed = 1, StartupFailed = 2 }`;
  - `public sealed class ServerBootstrap : MonoBehaviour`, with:
    - `internal Action<int> Exit`;
    - `internal ServerOptions Options`;
    - `internal int ExitCode` (−1 until exit) and `internal string ExitReason`, `internal bool HasExited`;
    - `internal int UnitViewsDisabled`, `internal int UnitViewsActive`;
    - `internal void Initialize(MissionDirector, TacticalPause)`;
    - `internal void BeginDiagnostic(ServerOptions)`;
    - `internal void OnStateChanged(MissionState)`;
    - `internal static bool IsView(Behaviour)`;
    - `const string Prefix = "[Server] "`.
  - `Blackglass.EditorTools.ServerSceneBuilder`, with `ScenePath`, `FixtureSeed`, `Build()`, `BuildFromCommandLine()` and `FixtureSettings()`.

- [ ] **Step 1: Write the failing scene tests**

`Assets/_Project/Tests/PlayMode/ServerMissionPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>Checkpoint A's server scene in the Editor: startup, composition, frame cap, diagnostic and exit codes.</summary>
    public class ServerMissionPlayModeTests
    {
        const string ScenePath = "Assets/_Project/Scenes/ServerMission.unity";

        static readonly Type[] Allowed =
        {
            typeof(ServerBootstrap), typeof(TacticalPause), typeof(Encounter), typeof(UnitSelection), typeof(ActiveCharacter),
            typeof(CoverRegistry), typeof(CoverDiscovery), typeof(InteractableRegistry), typeof(MissionDirector),
        };

        Scene scene;
        ServerBootstrap bootstrap;
        MissionDirector director;
        int frameRateBefore;
        int vSyncBefore;
        int exitCode;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            frameRateBefore = Application.targetFrameRate;
            vSyncBefore = QualitySettings.vSyncCount;
            exitCode = -1;
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(scene.isLoaded, Is.True);
            var roots = scene.GetRootGameObjects();
            bootstrap = roots.Single(g => g.name == "Server").GetComponent<ServerBootstrap>();
            director = roots.Single(g => g.name == "Mission").GetComponent<MissionDirector>();
            bootstrap.Exit = code => exitCode = code;
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 60f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", director.Report.Failures));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (director != null)
                director.Clear();
            yield return null;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
            Application.targetFrameRate = frameRateBefore;
            QualitySettings.vSyncCount = vSyncBefore;
            Time.timeScale = 1f;
        }

        [Test]
        public void Startup_GeneratesTheFixtureOnTheFirstAttempt_WithTheFrameCapAndNoOverride()
        {
            Assert.That(bootstrap.Options.Seed, Is.Null, "the Editor's own arguments carry no -seed");
            Assert.That(director.Report.Seed, Is.EqualTo(director.Settings.seed), "the scene's fixture seed");
            Assert.That(director.Report.Attempt, Is.EqualTo(1));
            Assert.That(director.Friendlies, Has.Count.EqualTo(2));
            Assert.That(director.Hostiles, Has.Count.EqualTo(1));
            Assert.That(director.Report.NavMeshBakeMs, Is.GreaterThan(0d));
            Assert.That(Application.targetFrameRate, Is.EqualTo(60));
            Assert.That(QualitySettings.vSyncCount, Is.EqualTo(0));
            Assert.That(bootstrap.HasExited, Is.False);
        }

        [Test]
        public void TheScene_HoldsOnlySimulationSystems_AndNoCameraAudioCanvasOrEventSystem()
        {
            var behaviours = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MonoBehaviour>(true)).ToArray();
            foreach (var behaviour in behaviours)
                Assert.That(Allowed, Has.Member(behaviour.GetType()), behaviour.GetType().Name + " on " + behaviour.name);
            Assert.That(scene.GetRootGameObjects().Select(g => g.name).OrderBy(n => n), Is.EqualTo(new[] { "Mission", "Server", "Systems" }));
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).ToArray();
            Assert.That(all.OfType<Camera>(), Is.Empty);
            Assert.That(all.OfType<AudioListener>(), Is.Empty);
            Assert.That(all.OfType<Canvas>(), Is.Empty);
            Assert.That(all.OfType<EventSystem>(), Is.Empty);
        }

        [Test]
        public void AtReady_EveryUnitViewIsDisabled_AndTheSimulationComponentsAreNot()
        {
            Assert.That(bootstrap.UnitViewsDisabled, Is.GreaterThan(0));
            Assert.That(bootstrap.UnitViewsActive, Is.EqualTo(0));
            foreach (var unit in director.Friendlies.Concat(director.Hostiles))
            {
                foreach (var behaviour in unit.GetComponentsInChildren<Behaviour>(true).Where(ServerBootstrap.IsView))
                    Assert.That(behaviour.enabled, Is.False, behaviour.GetType().Name + " on " + unit.name);
                Assert.That(unit.enabled, Is.True, unit.name);
                Assert.That(unit.GetComponent<UnitAttacker>().enabled, Is.True, unit.name);
                Assert.That(unit.GetComponent<UnitMover>().enabled, Is.True, unit.name);
                Assert.That(unit.GetComponent<Collider>().enabled, Is.True, unit.name);
            }
        }

        [UnityTest]
        public IEnumerator Diagnostic_OnTheFixture_ExitsZero()
        {
            bootstrap.BeginDiagnostic(bootstrap.Options.With(diagnostic: true));
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 120f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.Success), bootstrap.ExitReason);
            StringAssert.StartsWith("BLACKGLASS DIAGNOSTIC PASS", bootstrap.ExitReason);
        }

        [UnityTest]
        public IEnumerator Diagnostic_WhilePaused_ExitsOne_ByTheRealTimeDeadline()
        {
            var pause = scene.GetRootGameObjects().Single(g => g.name == "Systems").GetComponent<TacticalPause>();
            pause.Pause();
            var started = Time.realtimeSinceStartupAsDouble;
            bootstrap.BeginDiagnostic(bootstrap.Options.With(diagnostic: true, diagnosticTimeoutSeconds: 5));
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 15f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.DiagnosticFailed), bootstrap.ExitReason);
            StringAssert.Contains("timed out", bootstrap.ExitReason);
            Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(10d));
        }

        [Test]
        public void AFailedGeneration_ExitsTwo()
        {
            bootstrap.OnStateChanged(MissionState.Failed);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("generation failed", bootstrap.ExitReason);
        }
    }
}
#endif
```

- [ ] **Step 2: Run to see them fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.ServerMissionPlayModeTests"`
Expected: `EXIT=1`, `error CS0246: ... 'ServerBootstrap'`.

- [ ] **Step 3: Implement `ServerBootstrap`**

`Assets/_Project/Scripts/Server/ServerBootstrap.cs`:

```csharp
using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;
using Debug = UnityEngine.Debug;

namespace Blackglass
{
    /// <summary>Exit codes of the dedicated server (decision 045).</summary>
    public static class ServerExitCodes
    {
        /// <summary>The diagnostic passed (a normal stop also exits with Unity's own 0).</summary>
        public const int Success = 0;
        public const int DiagnosticFailed = 1;
        /// <summary>The mission never became ready: generation failed, the startup deadline passed, or the scene is unwired.</summary>
        public const int StartupFailed = 2;
    }

    /// <summary>
    /// The dedicated server's entry point (the ServerMission scene, Checkpoint A, decision 045). Reads the command line, caps
    /// the frame rate, logs which build is running, and generates the scene's fixture mission without a local player. When
    /// the mission is ready it disables the units' view components (the server shows nothing), logs readiness and a
    /// self-check, then either runs the bounded diagnostic (-diagnostic) or logs a heartbeat until the process is stopped.
    /// Holds no game rule: the mission comes from MissionDirector and every order from CommandableUnit.Issue.
    /// </summary>
    public sealed class ServerBootstrap : MonoBehaviour
    {
        public const string Prefix = "[Server] ";

#if UNITY_SERVER
        const bool UnityServerDefine = true;
#else
        const bool UnityServerDefine = false;
#endif

        [SerializeField] MissionDirector director;
        [SerializeField] TacticalPause pause;

        ServerOptions options = ServerOptions.Default;
        Stopwatch generation;
        double startupDeadline;
        bool started;
        bool ready;
        ServerDiagnostic diagnostic;
        double heartbeatStart;
        double nextHeartbeat;
        int heartbeatFrames;
        TimeSpan heartbeatCpu;

        /// <summary>How the process ends. Application.Quit does nothing in the Editor; tests replace this.</summary>
        internal Action<int> Exit = code => Application.Quit(code);
        internal ServerOptions Options => options;
        internal bool HasExited { get; private set; }
        internal int ExitCode { get; private set; } = -1;
        internal string ExitReason { get; private set; }
        internal int UnitViewsDisabled { get; private set; }
        internal int UnitViewsActive { get; private set; }

        internal void Initialize(MissionDirector missionDirector, TacticalPause tacticalPause)
        {
            director = missionDirector;
            pause = tacticalPause;
        }

        static double Now => Time.realtimeSinceStartupAsDouble;

        void Start()
        {
#if !UNITY_EDITOR
            // One line per log call in the player's stdout; warnings and errors keep their stack traces.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
#endif
            options = ServerOptions.Parse(Environment.GetCommandLineArgs(), out var warnings);
            foreach (var warning in warnings)
                Debug.LogWarning(Prefix + "option ignored: " + warning);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = options.FrameRate;
            Debug.Log(Prefix + IdentityLine());
            if (director == null || pause == null)
            {
                Finish(ServerExitCodes.StartupFailed, "the scene's ServerBootstrap is not wired to a MissionDirector and a TacticalPause");
                return;
            }
            Debug.Log(Prefix + "options " + options.Describe(director.Settings.seed));
            director.StateChanged += OnStateChanged;
            started = true;
            startupDeadline = Now + options.StartupTimeoutSeconds;
            generation = Stopwatch.StartNew();
            var seed = options.Seed ?? director.Settings.seed;
            if (!director.Generate(seed))
            {
                Finish(ServerExitCodes.StartupFailed, "the director refused to generate");
                return;
            }
            Debug.Log(Prefix + "generating " + director.Settings.Describe());
        }

        void OnDestroy()
        {
            if (director != null)
                director.StateChanged -= OnStateChanged;
        }

        void OnApplicationQuit() => Debug.Log(Prefix + "quitting");

        internal void OnStateChanged(MissionState state)
        {
            if (state == MissionState.Failed)
            {
                Finish(ServerExitCodes.StartupFailed, "mission generation failed: " + (director != null ? director.Report.Failure : "no director"));
                return;
            }
            if (state != MissionState.Ready)
                return;
            ready = true;
            generation?.Stop();
            DisableUnitViews();
            LogReady();
            LogSelfCheck();
            StartHeartbeat(Now);
            if (options.Diagnostic)
                BeginDiagnostic(options);
        }

        /// <summary>Starts the diagnostic on the ready mission (from the Ready handler with -diagnostic; tests call it directly).</summary>
        internal void BeginDiagnostic(ServerOptions diagnosticOptions)
        {
            diagnostic = new ServerDiagnostic(director, diagnosticOptions.DiagnosticTimeoutSeconds, line => Debug.Log(line));
            diagnostic.Begin(Now);
        }

        void Update()
        {
            if (HasExited || !started)
                return;
            var now = Now;
            if (!ready)
            {
                if (now > startupDeadline)
                    Finish(ServerExitCodes.StartupFailed,
                        FormattableString.Invariant($"the mission was not ready within {options.StartupTimeoutSeconds} s (state {director.State})"));
                return;
            }
            if (diagnostic != null)
            {
                diagnostic.Tick(now);
                if (!diagnostic.IsFinished)
                    return;
                if (diagnostic.Passed)
                    Debug.Log(diagnostic.Summary);
                else
                    Debug.LogWarning(diagnostic.Summary);
                Finish(diagnostic.Passed ? ServerExitCodes.Success : ServerExitCodes.DiagnosticFailed, diagnostic.Summary);
                return;
            }
            Heartbeat(now);
        }

        void Finish(int code, string reason)
        {
            if (HasExited)
                return;
            HasExited = true;
            ExitCode = code;
            ExitReason = reason;
            var line = Prefix + $"exit code={code} reason={reason}";
            if (code == ServerExitCodes.Success)
                Debug.Log(line);
            else
                Debug.LogWarning(line);
            Exit(code);
        }

        /// <summary>Per-unit components that only show something (decision 014 debug views, the selection ring, the hit flash).</summary>
        internal static bool IsView(Behaviour behaviour) =>
            behaviour is AttackLineView || behaviour is CommandQueueView || behaviour is SelectionIndicator || behaviour is HitFlash;

        // Instances only: GameObjects, renderers and colliders stay; nothing in a prefab changes.
        void DisableUnitViews()
        {
            var disabled = 0;
            var active = 0;
            foreach (var unit in director.Friendlies)
                Disable(unit, ref disabled);
            foreach (var unit in director.Hostiles)
                Disable(unit, ref disabled);
            foreach (var unit in director.Friendlies)
                active += CountActive(unit);
            foreach (var unit in director.Hostiles)
                active += CountActive(unit);
            UnitViewsDisabled = disabled;
            UnitViewsActive = active;
        }

        static void Disable(Component unit, ref int disabled)
        {
            if (unit == null)
                return;
            foreach (var behaviour in unit.GetComponentsInChildren<Behaviour>(true))
            {
                if (!IsView(behaviour) || !behaviour.enabled)
                    continue;
                behaviour.enabled = false;
                disabled++;
            }
        }

        static int CountActive(Component unit)
        {
            if (unit == null)
                return 0;
            var count = 0;
            foreach (var behaviour in unit.GetComponentsInChildren<Behaviour>(true))
            {
                if (IsView(behaviour) && behaviour.enabled)
                    count++;
            }
            return count;
        }

        void LogReady()
        {
            var r = director.Report;
            Debug.Log(Prefix + FormattableString.Invariant(
                $"MISSION READY instance={director.InstanceId} seed={r.Seed} attempt={r.Attempt}/{r.MaxAttempts} rooms={r.Rooms} navArea={r.NavArea:0} " +
                $"paths={r.PathsChecked} cover={r.CoverTotal}(low {r.CoverLow}, tall {r.CoverTall}) friendlies={director.Friendlies.Count} " +
                $"hostiles={director.Hostiles.Count} layoutHash={r.LayoutHash:x16} generationMs={(generation != null ? generation.Elapsed.TotalMilliseconds : 0d):0} " +
                $"navMeshBakeMs={r.NavMeshBakeMs:0.0} sinceStartupS={Now:0.0}"));
        }

        void LogSelfCheck()
        {
            var cameras = FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var listeners = FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var eventSystems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            Debug.Log(Prefix + $"self-check cameras={cameras} audioListeners={listeners} canvases={canvases} eventSystems={eventSystems} " +
                $"unitViewsDisabled={UnitViewsDisabled} unitViewsActive={UnitViewsActive}");
        }

        string IdentityLine() =>
            $"identity product={Application.productName} version={Application.version} {ServerBuildInfo.Describe(ServerBuildInfo.ReadBesideExecutable())} " +
            $"unity={Application.unityVersion} platform={Application.platform} UNITY_SERVER={(UnityServerDefine ? "yes" : "no")} " +
            $"batchmode={(Application.isBatchMode ? "yes" : "no")} graphics={SystemInfo.graphicsDeviceType} buildGUID={Application.buildGUID}";

        void StartHeartbeat(double now)
        {
            heartbeatStart = now;
            nextHeartbeat = now + options.HeartbeatSeconds;
            heartbeatFrames = 0;
            TrySample(out heartbeatCpu, out _);
        }

        void Heartbeat(double now)
        {
            if (options.HeartbeatSeconds <= 0)
                return;
            heartbeatFrames++;
            if (now < nextHeartbeat)
                return;
            var elapsed = now - heartbeatStart;
            var fps = elapsed > 0d ? heartbeatFrames / elapsed : 0d;
            string cpu = "n/a", rss = "n/a";
            if (TrySample(out var processor, out var workingSet))
            {
                cpu = FormattableString.Invariant($"{100d * (processor - heartbeatCpu).TotalSeconds / Math.Max(elapsed, 0.001d):0.0}");
                rss = FormattableString.Invariant($"{workingSet / 1048576d:0.0}");
                heartbeatCpu = processor;
            }
            Debug.Log(Prefix + FormattableString.Invariant(
                $"heartbeat uptimeS={now:0} fps={fps:0.0} cpuPct={cpu} rssMB={rss} managedMB={GC.GetTotalMemory(false) / 1048576d:0.0} state={director.State} phase={director.Phase}"));
            heartbeatStart = now;
            nextHeartbeat = now + options.HeartbeatSeconds;
            heartbeatFrames = 0;
        }

        static bool TrySample(out TimeSpan processor, out long workingSet)
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    processor = process.TotalProcessorTime;
                    workingSet = process.WorkingSet64;
                    return true;
                }
            }
            catch (Exception)
            {
                processor = TimeSpan.Zero;
                workingSet = 0;
                return false;
            }
        }
    }
}
```

`generation?.Stop()` is allowed because `Stopwatch` is a plain C# object, not a `UnityEngine.Object`.

- [ ] **Step 4: Implement `ServerSceneBuilder`**

`Assets/_Project/Editor/Server/ServerSceneBuilder.cs`:

```csharp
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the dedicated server's scene (Checkpoint A, decision 045): the simulation systems a mission needs and a
    /// MissionDirector holding the fixture, nothing else (no camera, light, audio, UI, input or debug view). Rebuilt from
    /// scratch every run, so it is idempotent; the client scenes are never opened.
    /// </summary>
    public static class ServerSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/ServerMission.unity";
        /// <summary>The fixture seed: the lowest seed in 1..1000 that generates on attempt 1 and passes the Editor diagnostic.</summary>
        public const int FixtureSeed = 1;
        const string Root = "Assets/_Project/";

        [MenuItem("Blackglass/Server/Build Server Scene")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        /// <summary>The small fixture mission: 2x2 grid, 3 rooms, two friendlies, one hostile, no terminal, no guards.</summary>
        public static MissionSettings FixtureSettings() => new MissionSettings
        {
            seed = FixtureSeed,
            gridColumns = 2,
            gridRows = 2,
            cellSize = 14,
            roomCount = 3,
            corridorWidth = 3,
            extraLoops = 0,
            bafflesPerRoom = 1,
            lowCoverDensity = 3f,
            friendlyCount = 2,
            hostileCount = 1,
            minTeamSeparation = 16f,
            maxAttempts = 20,
            guardCount = 0,
            interactionSeconds = 2f,
            extractionUnits = 1,
            eliminateHostiles = true,
            hackTerminal = false,
            intelligence = new IntelligenceSettings(),
        };

        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var server = new GameObject("Server");
            var systems = new GameObject("Systems");
            var mission = new GameObject("Mission");

            var pause = systems.AddComponent<TacticalPause>();
            var encounter = systems.AddComponent<Encounter>();
            var selection = systems.AddComponent<UnitSelection>();
            var active = systems.AddComponent<ActiveCharacter>();
            var registry = systems.AddComponent<CoverRegistry>();
            var discovery = systems.AddComponent<CoverDiscovery>();
            var interactables = systems.AddComponent<InteractableRegistry>();
            active.Initialize(null, pause, selection);
            discovery.Initialize(registry, discoverAtStart: false);

            var director = mission.AddComponent<MissionDirector>();
            director.Initialize(FixtureSettings(), FriendlySlots(), HostileSlots(), new MissionSystems
                {
                    encounter = encounter,
                    selection = selection,
                    activeCharacter = active,
                    coverRegistry = registry,
                    coverDiscovery = discovery,
                    pause = pause,
                    interactables = interactables,
                },
                Load<Material>("Materials/Ground.mat"), Load<Material>("Materials/Obstacle.mat"),
                generateAtStart: false, randomSeedAtStart: false, theme: null);

            server.AddComponent<ServerBootstrap>().Initialize(director, pause);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Could not save " + ScenePath + ".");
            Debug.Log($"ServerSceneBuilder: saved {ScenePath} (fixture seed {FixtureSeed}).");
        }

        // Ranged first: the diagnostic's mover is friendly 0 and must need line of sight. No abilities: the fixture keeps
        // its asset references (and so the server build's content) to the simulation.
        static FriendlySlot[] FriendlySlots()
        {
            var prefab = Load<GameObject>("Prefabs/FriendlyUnit.prefab");
            return new[]
            {
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Ranged.asset") },
                new FriendlySlot { prefab = prefab, archetype = Load<CombatArchetype>("Data/Archetypes/Melee.asset") },
            };
        }

        static HostileSlot[] HostileSlots() => new[] { new HostileSlot { prefab = Load<GameObject>("Prefabs/HostileUnit.prefab") } };

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(Root + path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + Root + path);
            return asset;
        }
    }
}
#endif
```

- [ ] **Step 5: Build the scene**

Run: `Tools/run-method.sh Blackglass.EditorTools.ServerSceneBuilder.BuildFromCommandLine`
Expected: `EXIT=0` and no `error CS`. `Logs/Method-Blackglass_EditorTools_ServerSceneBuilder_BuildFromCommandLine.log` contains `ServerSceneBuilder: saved Assets/_Project/Scenes/ServerMission.unity`. Then `git status --short` lists `Assets/_Project/Scenes/ServerMission.unity`, its `.meta`, the `Editor/Server` folder and the new scripts. **No other scene or asset should appear modified.**

- [ ] **Step 6: Find the fixture seed (temporary probe)**

Create `Assets/_Project/Tests/PlayMode/ServerFixtureSeedProbe.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    // TEMPORARY (Checkpoint A, Task 4 Step 6): finds the lowest seed that generates the fixture on attempt 1 and passes the
    // diagnostic. Deleted before the commit.
    public class ServerFixtureSeedProbe
    {
        [UnityTest]
        public IEnumerator FindFixtureSeed()
        {
            const string path = "Assets/_Project/Scenes/ServerMission.unity";
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            var scene = SceneManager.GetSceneByPath(path);
            var director = scene.GetRootGameObjects().Single(g => g.name == "Mission").GetComponent<MissionDirector>();
            yield return TestWorld.WaitUntil(() => director.State != MissionState.Generating && director.State != MissionState.Idle, 60f);
            for (var seed = 1; seed <= 1000; seed++)
            {
                director.Generate(seed);
                yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 60f);
                if (director.State != MissionState.Ready || director.Report.Attempt != 1)
                {
                    Debug.Log($"PROBE seed {seed}: state {director.State} attempt {director.Report.Attempt}");
                    continue;
                }
                var diagnostic = new ServerDiagnostic(director, 120, Debug.Log);
                diagnostic.Begin(Time.realtimeSinceStartupAsDouble);
                while (!diagnostic.IsFinished)
                {
                    diagnostic.Tick(Time.realtimeSinceStartupAsDouble);
                    yield return null;
                }
                Debug.Log($"PROBE seed {seed}: {diagnostic.Summary}");
                if (diagnostic.Passed)
                {
                    Debug.Log($"PROBE FIXTURE SEED {seed}");
                    director.Clear();
                    yield return null;
                    yield return SceneManager.UnloadSceneAsync(scene);
                    Assert.Pass($"fixture seed {seed}");
                }
            }
            Assert.Fail("no seed in 1..1000 generates on attempt 1 and passes");
        }
    }
}
#endif
```

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.ServerFixtureSeedProbe"` and then `grep "PROBE" Logs/TestRun-PlayMode.log`.
Expected: a `PROBE FIXTURE SEED <n>` line. Record `n` and the per-seed lines in `results.md`.

If the probe fails for every seed, stop and report to the owner. The likely cause is the 16 m separation in a 2×2 grid. Propose a 3×2 grid, and do not change it without approval.

Then:
1. Set `public const int FixtureSeed = <n>;` in `ServerSceneBuilder.cs`.
2. Re-run Step 5.
3. Delete `ServerFixtureSeedProbe.cs` and its `.meta`.

- [ ] **Step 7: Run the scene tests to see them pass**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.ServerMissionPlayModeTests"`
Expected: 6/6 pass, `EXIT=0`.

- [ ] **Step 8: Confirm the client scenes are untouched (E7, partial)**

Run: `git status --short` and `git diff --stat`
Expected: only the new server files, plus the owner's two pre-existing edits. Nothing under `Scenes/` changes except the new `ServerMission.unity`, and `ProjectSettings/EditorBuildSettings.asset` is unchanged.

- [ ] **Step 9: Commit**

```bash
git add Assets/_Project/Scripts/Server/ServerBootstrap.cs Assets/_Project/Scripts/Server/ServerBootstrap.cs.meta \
  Assets/_Project/Editor/Server.meta Assets/_Project/Editor/Server/ServerSceneBuilder.cs Assets/_Project/Editor/Server/ServerSceneBuilder.cs.meta \
  Assets/_Project/Scenes/ServerMission.unity Assets/_Project/Scenes/ServerMission.unity.meta \
  Assets/_Project/Tests/PlayMode/ServerMissionPlayModeTests.cs Assets/_Project/Tests/PlayMode/ServerMissionPlayModeTests.cs.meta
git commit -m "Server: bootstrap and ServerMission scene with the fixture mission" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Build entry point, packaging and VM scripts (B1, B2)

**Files:**
- Create: `Assets/_Project/Editor/Server/ServerBuild.cs`
- Create: `Tools/build-server.sh`, `Tools/deploy-server.sh`
- Create: `Tools/LinuxServer/run-diagnostic.sh`, `Tools/LinuxServer/smoke-run.sh`
- Modify: `.gitattributes` (append one rule)

**Interfaces:**
- Consumes: `ServerSceneBuilder.ScenePath`, `ServerBuildInfo.FileName`.
- Produces:
  - `Blackglass.EditorTools.ServerBuild`, with:
    - `BuildFromCommandLine()`;
    - `ReportActiveTargetFromCommandLine()`;
    - `Build(out string message)`;
    - the constants `OutputFolder = "Builds/LinuxServer"`, `ExecutableName = "BlackglassServer.x86_64"`.
  - `build-info.txt` with the keys `product`, `version`, `revision`, `dirty`, `builtUtc`, `unity`, `target`, `subtarget`, `backend`, `buildGuid`.
  - The archive `Builds/blackglass-server-linux-<sha7>[-dirty].tar.gz`, whose top-level folder is `LinuxServer/`.

- [ ] **Step 1: Write `ServerBuild.cs`**

`Assets/_Project/Editor/Server/ServerBuild.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the Linux Dedicated Server (Mono) player from ServerMission.unity only (Checkpoint A, decision 045). Refuses
    /// without changing anything when a module is missing or the Server scripting backend is not Mono; never edits Player
    /// Settings. Writes build-info.txt beside the executable, and always restores the Editor's active build target, so the
    /// Editor and Tools/run-tests.sh never reopen on the server target with UNITY_SERVER defined.
    /// </summary>
    public static class ServerBuild
    {
        public const string OutputFolder = "Builds/LinuxServer";
        public const string ExecutableName = "BlackglassServer.x86_64";
        const string ServerVariation = "linux64_server_nondevelopment_mono";
        const string Prefix = "[ServerBuild] ";

        [MenuItem("Blackglass/Server/Build Linux Dedicated Server")]
        public static void BuildMenu()
        {
            var ok = Build(out var message);
            Debug.Log(Prefix + message);
            EditorUtility.DisplayDialog("Linux dedicated server", (ok ? "Built. " : "Failed. ") + message, "OK");
        }

        /// <summary>Unity -batchmode -executeMethod entry; exits 0 on success, 1 otherwise.</summary>
        public static void BuildFromCommandLine()
        {
            var ok = false;
            string message;
            try
            {
                ok = Build(out message);
            }
            catch (Exception exception)
            {
                message = "exception: " + exception;
            }
            Debug.Log(Prefix + (ok ? "SUCCEEDED " : "FAILED ") + message);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Unity -batchmode -executeMethod entry: logs the active build target (checks that a build restored it).</summary>
        public static void ReportActiveTargetFromCommandLine()
        {
            Debug.Log(Prefix + $"active target={EditorUserBuildSettings.activeBuildTarget} subtarget={EditorUserBuildSettings.standaloneBuildSubtarget}");
            EditorApplication.Exit(0);
        }

        public static bool Build(out string message)
        {
            if (!Preflight(out message))
                return false;
            var previousTarget = EditorUserBuildSettings.activeBuildTarget;
            var previousGroup = BuildPipeline.GetBuildTargetGroup(previousTarget);
            var previousSubtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
            try
            {
                var root = Path.GetDirectoryName(Application.dataPath);
                var output = Path.Combine(root, OutputFolder);
                if (Directory.Exists(output))
                    Directory.Delete(output, true);
                Directory.CreateDirectory(output);
                var executable = Path.Combine(output, ExecutableName);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ServerSceneBuilder.ScenePath },
                    target = BuildTarget.StandaloneLinux64,
                    subtarget = (int)StandaloneBuildSubtarget.Server,
                    locationPathName = executable,
                    options = BuildOptions.StrictMode,
                });
                var summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    message = $"build {summary.result} with {summary.totalErrors} error(s); see the Editor log";
                    return false;
                }
                var data = Path.Combine(output, Path.GetFileNameWithoutExtension(ExecutableName) + "_Data");
                if (!File.Exists(executable) || !Directory.Exists(data))
                {
                    message = $"the build reported success but {executable} or {data} is missing";
                    return false;
                }
                WriteBuildInfo(root, output, summary);
                message = FormattableString.Invariant(
                    $"platform={summary.platform} subtarget=Server backend={PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server)} " +
                    $"sizeMB={summary.totalSize / 1048576d:0.0} seconds={summary.totalTime.TotalSeconds:0} output={output}");
                return true;
            }
            finally
            {
                Restore(previousGroup, previousTarget, previousSubtarget);
            }
        }

        static bool Preflight(out string message)
        {
            var install = $"Install with: \"C:\\Program Files\\Unity Hub\\Unity Hub.exe\" -- --headless install-modules --version {Application.unityVersion} -m linux-mono -m linux-server";
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
            {
                message = "Linux Build Support (Mono) is not installed for this Editor. " + install;
                return false;
            }
            string engine;
            try
            {
                engine = BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.StandaloneLinux64, BuildOptions.None);
            }
            catch (Exception)
            {
                engine = null;
            }
            if (string.IsNullOrEmpty(engine) || !Directory.Exists(Path.Combine(engine, "Variations", ServerVariation)))
            {
                message = "Linux Dedicated Server Build Support (Mono variation) is not installed. " + install;
                return false;
            }
            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server);
            if (backend != ScriptingImplementation.Mono2x)
            {
                message = $"the Server scripting backend is {backend}; Checkpoint A builds with Mono. Change it deliberately in Player Settings (this build never does).";
                return false;
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ServerSceneBuilder.ScenePath) == null)
            {
                message = $"{ServerSceneBuilder.ScenePath} is missing; run Blackglass/Server/Build Server Scene first.";
                return false;
            }
            message = null;
            return true;
        }

        static void Restore(BuildTargetGroup group, BuildTarget target, StandaloneBuildSubtarget subtarget)
        {
            if (EditorUserBuildSettings.standaloneBuildSubtarget != subtarget)
                EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
            if (EditorUserBuildSettings.activeBuildTarget != target)
                EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
            Debug.Log(Prefix + $"active target after build: {EditorUserBuildSettings.activeBuildTarget} subtarget={EditorUserBuildSettings.standaloneBuildSubtarget}");
        }

        // LF line endings: the file is read on Linux.
        static void WriteBuildInfo(string root, string output, BuildSummary summary)
        {
            var revision = Git(root, "rev-parse HEAD") ?? "unknown";
            var status = Git(root, "status --porcelain --untracked-files=no");
            var dirty = status == null ? "unknown" : status.Length > 0 ? "true" : "false";
            var lines = new[]
            {
                "product=" + PlayerSettings.productName,
                "version=" + PlayerSettings.bundleVersion,
                "revision=" + revision,
                "dirty=" + dirty,
                "builtUtc=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                "unity=" + Application.unityVersion,
                "target=StandaloneLinux64",
                "subtarget=Server",
                "backend=Mono",
                "buildGuid=" + summary.guid,
            };
            File.WriteAllText(Path.Combine(output, ServerBuildInfo.FileName), string.Join("\n", lines) + "\n");
        }

        static string Git(string root, string arguments)
        {
            try
            {
                var info = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var process = Process.Start(info))
                {
                    var text = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(10000);
                    return process.ExitCode == 0 ? text : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
#endif
```

- [ ] **Step 2: Write `Tools/build-server.sh`**

```bash
#!/usr/bin/env bash
# Builds the Linux Dedicated Server (Mono) from ServerMission.unity and packages it for a VM (Checkpoint A).
# The Unity Editor must be closed. Usage: Tools/build-server.sh
# Output: Builds/LinuxServer/ (the build) and Builds/blackglass-server-linux-<sha>[-dirty].tar.gz (the package).
set -u
root_unix="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$root_unix" && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
out="$root_unix/Builds/LinuxServer"
log="$root/Logs/ServerBuild.log"
mkdir -p "$root_unix/Logs" "$root_unix/Builds"
# A refused or failed run must never leave an old build behind to be packaged.
rm -rf "$out"

"$unity" -batchmode -quit -projectPath "$root" \
  -executeMethod Blackglass.EditorTools.ServerBuild.BuildFromCommandLine -logFile "$log"
code=$?

if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
grep -h "\[ServerBuild\]" "$log" 2>/dev/null
if [ "$code" -ne 0 ] || [ ! -f "$out/BlackglassServer.x86_64" ] || [ ! -f "$out/build-info.txt" ]; then
  echo "BUILD FAILED (Unity exit $code); nothing was packaged. See $log"
  echo "EXIT=1"
  exit 1
fi

# The VM scripts travel with the build; strip any CR so bash on Linux can run them.
for script in "$root_unix"/Tools/LinuxServer/*.sh; do
  tr -d '\r' < "$script" > "$out/$(basename "$script")"
done

rev=$(awk -F= '$1=="revision"{print substr($2,1,7)}' "$out/build-info.txt")
dirty=$(awk -F= '$1=="dirty"{print $2}' "$out/build-info.txt")
suffix=""
if [ "$dirty" = "true" ]; then suffix="-dirty"; fi
archive="$root_unix/Builds/blackglass-server-linux-${rev}${suffix}.tar.gz"
rm -f "$archive"
tar -C "$root_unix/Builds" -czf "$archive" LinuxServer
echo "build-info:"
sed 's/^/  /' "$out/build-info.txt"
echo "archive=$archive"
echo "sizeMB=$(du -m "$archive" | cut -f1)"
sha256sum "$archive"
echo "EXIT=0"
```

- [ ] **Step 3: Write `Tools/deploy-server.sh`**

```bash
#!/usr/bin/env bash
# Uploads a packaged server build and swaps it into /opt/blackglass/server/ on the VM (Checkpoint A).
# Usage: Tools/deploy-server.sh [host] [archive]   (defaults: bg-dev-mission01, the newest Builds/blackglass-server-linux-*.tar.gz)
# The previous build stays in place until the new one is fully extracted and checked.
set -u
root_unix="$(cd "$(dirname "$0")/.." && pwd)"
host="${1:-bg-dev-mission01}"
archive="${2:-$(ls -t "$root_unix"/Builds/blackglass-server-linux-*.tar.gz 2>/dev/null | head -1)}"
if [ -z "$archive" ] || [ ! -f "$archive" ]; then
  echo "No archive found. Run Tools/build-server.sh first."
  exit 1
fi
name="$(basename "$archive")"
echo "deploying $name to $host"
scp -q "$archive" "$host:/tmp/$name" || { echo "upload failed"; exit 1; }
ssh "$host" "set -e
  mkdir -p /opt/blackglass/logs
  rm -rf /opt/blackglass/server.new
  mkdir -p /opt/blackglass/server.new
  tar -xzf /tmp/$name -C /opt/blackglass/server.new --strip-components=1
  chmod +x /opt/blackglass/server.new/BlackglassServer.x86_64 /opt/blackglass/server.new/*.sh
  test -x /opt/blackglass/server.new/BlackglassServer.x86_64
  test -f /opt/blackglass/server.new/build-info.txt
  rm -rf /opt/blackglass/server
  mv /opt/blackglass/server.new /opt/blackglass/server
  rm -f /tmp/$name
  echo deployed:
  sed 's/^/  /' /opt/blackglass/server/build-info.txt"
code=$?
echo "EXIT=$code"
exit $code
```

- [ ] **Step 4: Write `Tools/LinuxServer/run-diagnostic.sh`**

```bash
#!/usr/bin/env bash
# Runs the server's bounded navigation and combat diagnostic on this machine (Checkpoint A).
# Usage: run-diagnostic.sh [timeoutSeconds]   (default 180; `timeout` ends a hung run with exit 124)
# Log: $BLACKGLASS_LOGS (default /opt/blackglass/logs)/<UTC>-<rev>-diagnostic.log. Exits with the server's exit code.
set -u
dir="$(cd "$(dirname "$0")" && pwd)"
logs="${BLACKGLASS_LOGS:-/opt/blackglass/logs}"
limit="${1:-180}"
rev=$(awk -F= '$1=="revision"{print substr($2,1,7)}' "$dir/build-info.txt" 2>/dev/null)
stamp=$(date -u +%Y%m%dT%H%M%SZ)
mkdir -p "$logs"
log="$logs/$stamp-${rev:-unknown}-diagnostic.log"
timeout --kill-after=10 "$limit" "$dir/BlackglassServer.x86_64" -batchmode -nographics -logFile - -diagnostic > "$log" 2>&1
code=$?
grep -E "\[Server\]|\[Diagnostic\]|BLACKGLASS" "$log"
echo "log=$log"
echo "exit=$code"
exit $code
```

- [ ] **Step 5: Write `Tools/LinuxServer/smoke-run.sh`**

```bash
#!/usr/bin/env bash
# Runs the server in normal mode for a while and measures it (Checkpoint A initial smoke run; not a stability proof).
# Usage: smoke-run.sh [seconds]   (default 600). Samples CPU and memory every 5 s from /proc, then sends SIGTERM
# (SIGKILL after 30 s if still running). Writes to $BLACKGLASS_LOGS (default /opt/blackglass/logs):
#   <UTC>-<rev>-smoke.log, -smoke.csv and -smoke-summary.txt.
set -u
dir="$(cd "$(dirname "$0")" && pwd)"
logs="${BLACKGLASS_LOGS:-/opt/blackglass/logs}"
duration="${1:-600}"
interval=5
rev=$(awk -F= '$1=="revision"{print substr($2,1,7)}' "$dir/build-info.txt" 2>/dev/null)
stamp=$(date -u +%Y%m%dT%H%M%SZ)
base="$logs/$stamp-${rev:-unknown}-smoke"
log="$base.log"; csv="$base.csv"; summary="$base-summary.txt"
mkdir -p "$logs"
hz=$(getconf CLK_TCK)
cores=$(nproc)

start_ms=$(date +%s%3N)
"$dir/BlackglassServer.x86_64" -batchmode -nographics -logFile "$log" &
pid=$!
echo "elapsed_s,cpu_pct_one_core,rss_kb,hwm_kb" > "$csv"
prev_ticks=""; prev_ms=""; ready_s=""; early=""
while :; do
  now_ms=$(date +%s%3N)
  elapsed_ms=$((now_ms - start_ms))
  if [ ! -r "/proc/$pid/stat" ]; then early="$((elapsed_ms / 1000))"; break; fi
  # Fields 14 and 15 are utime and stime; the process name has no spaces, so plain field numbers are safe.
  ticks=$(awk '{print $14 + $15}' "/proc/$pid/stat")
  rss=$(awk '/^VmRSS:/{print $2}' "/proc/$pid/status")
  hwm=$(awk '/^VmHWM:/{print $2}' "/proc/$pid/status")
  cpu=""
  if [ -n "$prev_ticks" ]; then
    cpu=$(awk -v d=$((ticks - prev_ticks)) -v t=$((elapsed_ms - prev_ms)) -v hz="$hz" 'BEGIN { if (t > 0) printf "%.1f", 100 * d / hz / (t / 1000); else print "" }')
  fi
  echo "$(awk -v e="$elapsed_ms" 'BEGIN { printf "%.1f", e / 1000 }'),$cpu,$rss,$hwm" >> "$csv"
  prev_ticks=$ticks; prev_ms=$elapsed_ms
  if [ -z "$ready_s" ] && grep -q "MISSION READY" "$log" 2>/dev/null; then
    ready_s=$(awk -v e="$elapsed_ms" 'BEGIN { printf "%.1f", e / 1000 }')
  fi
  if [ "$elapsed_ms" -ge $((duration * 1000)) ]; then break; fi
  sleep "$interval"
done

stop="exited early at ${early}s"
if [ -z "$early" ]; then
  kill -TERM "$pid" 2>/dev/null
  stop="SIGTERM"
  for _ in $(seq 1 30); do
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
  done
  if kill -0 "$pid" 2>/dev/null; then
    kill -KILL "$pid" 2>/dev/null
    stop="SIGTERM ignored for 30 s, then SIGKILL"
  fi
fi
wait "$pid"
status=$?

{
  echo "smoke run: duration=${duration}s interval=${interval}s cores=$cores host=$(hostname) kernel=$(uname -r)"
  echo "build: $(tr '\n' ' ' < "$dir/build-info.txt")"
  echo "stop: $stop, exit status $status"
  echo "launch to MISSION READY (wall clock, sampled every ${interval}s): ${ready_s:-never}s"
  echo "ready line: $(grep -m1 "MISSION READY" "$log" 2>/dev/null)"
  awk -F, -v ready="${ready_s:-0}" -v duration="$duration" '
    NR == 1 { next }
    { t = $1 + 0; if ($4 + 0 > hwm) hwm = $4 + 0 }
    t >= ready && $2 != "" { n++; sum += $2; if ($2 + 0 > max) max = $2 + 0 }
    t >= duration - 300 && $3 != "" { s++; ssum += $3; if (first == "") first = $3; last = $3 }
    END {
      printf "cpu after ready (%% of one core): mean %.1f, max %.1f over %d samples\n", (n ? sum / n : 0), max, n
      printf "rss peak (VmHWM): %.1f MB\n", hwm / 1024
      printf "rss steady (mean of the last 300 s): %.1f MB over %d samples\n", (s ? ssum / s / 1024 : 0), s
      printf "rss drift over the last 300 s: %+.1f MB (first %.1f MB, last %.1f MB)\n", (last - first) / 1024, first / 1024, last / 1024
    }' "$csv"
  fps=$(grep -o "heartbeat .*fps=[0-9.]*" "$log" | grep -o "fps=[0-9.]*" | cut -d= -f2)
  if [ -n "$fps" ]; then
    echo "heartbeat fps: min $(echo "$fps" | sort -n | head -1), max $(echo "$fps" | sort -n | tail -1), lines $(echo "$fps" | wc -l)"
  else
    echo "heartbeat fps: no heartbeat lines"
  fi
  echo "log lines containing 'Exception': $(grep -c "Exception" "$log")"
  echo "log lines containing 'Error' or 'error': $(grep -ci "error" "$log")"
  echo "files: $log $csv"
} > "$summary"
cat "$summary"
echo "summary=$summary"
```

- [ ] **Step 6: Pin the VM scripts to LF**

Append to `.gitattributes`:

```
# Shell scripts that run on the Linux server must keep LF in the working tree too.
Tools/LinuxServer/*.sh  eol=lf
```

- [ ] **Step 7: Prove a refused build packages nothing (Review Focus 2)**

Run: `mkdir -p Builds/LinuxServer && touch Builds/LinuxServer/BlackglassServer.x86_64 Builds/LinuxServer/build-info.txt && UNITY_EXE=/c/does-not-exist/Unity.exe Tools/build-server.sh; ls Builds/`
Expected:
- the script prints `BUILD FAILED` and `EXIT=1`;
- `Builds/LinuxServer` no longer exists, because the fake old build was deleted;
- no new `.tar.gz` was created.

- [ ] **Step 8: Build for real (B1)**

Run `Tools/build-server.sh` in the background. The first switch to the Linux Server target re-imports assets and can take several minutes; wait for the notification.
Expected:
- `[ServerBuild] SUCCEEDED platform=StandaloneLinux64 subtarget=Server backend=Mono2x ...`;
- `[ServerBuild] active target after build: StandaloneWindows64 subtarget=Player`;
- the `build-info` lines, `archive=...tar.gz`, a SHA-256 line and `EXIT=0`.

Then check the package:

```bash
tar -tzf Builds/blackglass-server-linux-*.tar.gz | head -20
tar -xzOf Builds/blackglass-server-linux-*.tar.gz LinuxServer/run-diagnostic.sh | grep -c $'\r'   # expected 0
tar -xzOf Builds/blackglass-server-linux-*.tar.gz LinuxServer/smoke-run.sh | grep -c $'\r'       # expected 0
```

Expected:
- the listing shows `LinuxServer/BlackglassServer.x86_64`, `LinuxServer/BlackglassServer_Data/`, `LinuxServer/build-info.txt`, `LinuxServer/run-diagnostic.sh` and `LinuxServer/smoke-run.sh`;
- both CR counts are `0`.

Record the archive name, its size, its SHA-256 and the build time in `results.md` under `## Build`.

- [ ] **Step 9: Check that the active target was restored (B2)**

Run: `Tools/run-method.sh Blackglass.EditorTools.ServerBuild.ReportActiveTargetFromCommandLine; grep "\[ServerBuild\]" Logs/Method-Blackglass_EditorTools_ServerBuild_ReportActiveTargetFromCommandLine.log`
Expected: `active target=StandaloneWindows64 subtarget=Player`. If it shows Linux or Server, stop and fix `Restore` before anything else.

- [ ] **Step 10: Confirm nothing generated is staged**

Run: `git status --short`
Expected:
- the new `Assets/_Project/Editor/Server/ServerBuild.cs(.meta)`, `Tools/*.sh`, `Tools/LinuxServer/*` and `.gitattributes` appear;
- `Builds/` and `Logs/` do **not** appear, because they are git-ignored;
- `ProjectSettings/ProjectSettings.asset` and `ProjectSettings/EditorBuildSettings.asset` are **not** modified.

If `ProjectSettings.asset` changed, the build touched Player Settings. Find out why before committing.

- [ ] **Step 11: Commit**

```bash
git add Assets/_Project/Editor/Server/ServerBuild.cs Assets/_Project/Editor/Server/ServerBuild.cs.meta \
  Tools/build-server.sh Tools/deploy-server.sh Tools/LinuxServer/run-diagnostic.sh Tools/LinuxServer/smoke-run.sh .gitattributes
git commit -m "Server: Linux dedicated-server build, packaging and VM scripts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Linux runtime on `bg-dev-mission01` (L1–L3)

**Files:** nothing in the repo. Results go to `.claude/scratchpad/checkpoint-a/vm/` and `results.md`.

- [ ] **Step 1: VM baseline**

Run: `ssh -o BatchMode=yes bg-dev-mission01 'date -u; uptime; free -m; nproc; df -h /opt | tail -1; ls -la /opt/blackglass 2>/dev/null || echo "no /opt/blackglass yet"'`
Record the idle memory and load in `results.md` under `## VM`.

- [ ] **Step 2: Deploy (L1)**

Run: `Tools/deploy-server.sh`
Expected:
- `deployed:` followed by the `build-info.txt` lines, whose `revision` matches the archive name;
- `EXIT=0`.

- [ ] **Step 3: Diagnostic (L2)**

Run: `ssh bg-dev-mission01 /opt/blackglass/server/run-diagnostic.sh`
Expected:
- exit 0;
- the output contains `UNITY_SERVER=yes`, `platform=LinuxServer`, `graphics=Null` and `MISSION READY`;
- it contains `self-check cameras=0 audioListeners=0 canvases=0 eventSystems=0` and `unitViewsActive=0`;
- it contains `[Diagnostic] move PASS`, `[Diagnostic] attack PASS` and `BLACKGLASS DIAGNOSTIC PASS`.

Copy the log back: `scp bg-dev-mission01:<log path printed> .claude/scratchpad/checkpoint-a/vm/`.

If the diagnostic fails, do not change thresholds. Copy the log back, then read it for errors, a missing shader or mesh, or a `-nographics` problem. Record the finding and fix it through a new task with the owner.

- [ ] **Step 4: Start from another working directory (Review Focus 3)**

Run: `ssh bg-dev-mission01 'cd / && /opt/blackglass/server/run-diagnostic.sh 180 | grep -E "identity|BLACKGLASS|exit="'`
Expected: the identity line shows the same `revision=` as Step 2 (not `unknown`), then `BLACKGLASS DIAGNOSTIC PASS` and `exit=0`.

- [ ] **Step 5: Ten-minute smoke run (L3)**

Launch it detached, so an SSH drop cannot end it:
`ssh bg-dev-mission01 'nohup /opt/blackglass/server/smoke-run.sh 600 > /opt/blackglass/logs/smoke-run.out 2>&1 < /dev/null & echo started'`

Then wait about 11 minutes. Do not poll in a tight loop: use a single background `sleep`, or a Monitor `until` loop, that checks `ssh bg-dev-mission01 'ls /opt/blackglass/logs/*-smoke-summary.txt'`.

Run: `ssh bg-dev-mission01 'cat $(ls -t /opt/blackglass/logs/*-smoke-summary.txt | head -1)'`

Expected:
- `stop: SIGTERM` (or the recorded fallback), with exit status 0;
- `launch to MISSION READY` well under 60 s;
- heartbeat FPS min and max between 55 and 61;
- the 'Exception' line count is 0.

Copy the summary, CSV and log to `.claude/scratchpad/checkpoint-a/vm/`. Record the numbers in `results.md`:
- startup to Ready;
- `navMeshBakeMs`;
- CPU mean and maximum;
- peak RSS, steady RSS and drift;
- the FPS range;
- how the run stopped.

Label them **initial evidence, not proof of long-term stability**.

If the FPS falls outside 55–61, record it as a finding. A cap that doesn't hold in a dedicated-server player is exactly what this check exists to find. Do not change code to hide it.

- [ ] **Step 6: Leave the VM tidy**

Run: `ssh bg-dev-mission01 'pgrep -fa BlackglassServer || echo "no server running"; ls -la /opt/blackglass /opt/blackglass/logs'`
Expected: no server process is left running, and only `server/` and `logs/` exist under `/opt/blackglass`.

---

### Task 7: Regression verification (E6, E7)

**Files:** none. Results go to `results.md`.

- [ ] **Step 1: Focused suites**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDirectorPlayModeTests;Blackglass.Tests.ProceduralMissionSceneTests;Blackglass.Tests.MissionRuntimePlayModeTests;Blackglass.Tests.ServerDiagnosticPlayModeTests;Blackglass.Tests.ServerMissionPlayModeTests"`
Expected: all pass, `EXIT=0`.

- [ ] **Step 2: Full EditMode**

Run: `Tools/run-tests.sh EditMode`
Expected: baseline + 22 (18 `ServerOptionsTests` + 4 `ServerBuildInfoTests`), all passing.

- [ ] **Step 3: Full PlayMode (background, about 12 minutes)**

Run: `Tools/run-tests.sh PlayMode`
Expected: baseline + 11, all passing. The 11 are:
- 1 in `MissionDirectorPlayModeTests`;
- 4 in `ServerDiagnosticPlayModeTests`;
- 6 in `ServerMissionPlayModeTests`.

- [ ] **Step 4: E7 diff check**

Run: `git diff --stat main...HEAD -- ProjectSettings Assets/_Project/Scenes Assets/_Project/Prefabs Assets/_Project/Data`
Expected: exactly one file, `Assets/_Project/Scenes/ServerMission.unity`, plus its `.meta`. No other scene, prefab, data or ProjectSettings file.

---

### Task 8: Documentation (CLAUDE.md, audit §9, decision 045, run guide)

**Files:**
- Modify: `CLAUDE.md`. This replaces the `## Current Development Stage` section (from its heading through the line "Do not spend time on final graphics, … production content yet.") and the `## Current Priority` section (from its heading to the end of the file).
- Modify: `Docs/OnlineReadinessAudit.md`. This adds one sentence after the labels list at the top, and appends a new §9 at the end.
- Modify: `Docs/Decisions.md`. This appends `## 045`.
- Create: `Docs/DedicatedServer.md`

- [ ] **Step 1: `CLAUDE.md`**

Replace the two sections with the exact text of spec Appendix A, leaving out the `> ` quote markers. Change no other line. Then run `git diff CLAUDE.md` and check the diff touches only those two sections.

- [ ] **Step 2: Audit pointer and §9**

After the line `- **[R]** a risk that nothing has exercised yet.`, add:

```markdown

§1–§8 are the original audit record and are unchanged. Later checkpoints append their results in §9.
```

Append the following at the end of the file. Fill every `<...>` from `results.md`, where each value was actually measured. Where something wasn't run, say "not run".

```markdown

---

## 9. Follow-up record

### 9.1 Steps after the audit

| Step | Scope | Status |
|---|---|---|
| **Checkpoint A: headless Linux server startup** | Server-only scene and bootstrap, fixture mission, frame cap, identity and readiness logs, bounded move and attack diagnostic, Linux Dedicated Server (Mono) build, VM smoke run. **No networking, no backend.** | Done 2026-10-11 (§9.2) |
| Networking experiment | The §7 proof: library choice (FishNet is a candidate only), server gate, per-connection visibility, two clients. | Not started; needs the owner's authorization and a library decision |
| Phase 13 | Authenticated between-mission flow and trusted settlement (§8). | Not started |

### 9.2 Checkpoint A results (2026-10-11)

**Revision and environment actually tested:**
- branch `checkpoint-a-linux-server` @ `<sha>`, built as `<archive name>` (`dirty=<true|false>`; `dirty` counts the owner's two uncommitted settings files);
- Editor host: Windows 10 build 19041, Unity 6000.3.25f1 with Linux Build Support (Mono) and Linux Dedicated Server Build Support, installed 2026-10-11 through Unity Hub;
- VM `bg-dev-mission01`: Ubuntu 24.04.5 LTS, kernel 6.8, 4 vCPU, 7.9 GiB RAM.

**Editor validation** (Windows, batch mode):
- baseline before changes: EditMode <n>/<n>, PlayMode <n>/<n>;
- after: EditMode <n>/<n>, PlayMode <n>/<n>;
- new tests: `ServerOptionsTests`, `ServerBuildInfoTests`, `ServerDiagnosticPlayModeTests`, `ServerMissionPlayModeTests`, and `MissionDirectorPlayModeTests.Generate_RecordsTheNavMeshBakeTime`;
- fixture seed <n>, which generates on attempt 1.

**Linux build** (Windows host): `Tools/build-server.sh` → StandaloneLinux64, Server subtarget, Mono, <MB> MB archive in <s> s, SHA-256 `<hash>`. The active target was restored to Windows Standalone (checked).

**Linux runtime** (`bg-dev-mission01`):
- **Diagnostic:** exit <code>; `UNITY_SERVER=yes`, `platform=LinuxServer`, `graphics=Null`; self-check <counts>; move PASS in <s> s, attack PASS in <s> s; `navMeshBakeMs=<ms>`, `generationMs=<ms>`, `sinceStartupS=<s>`. It also passed when started from `/`.
- **Smoke run** (600 s, normal mode, initial evidence only, not a stability proof):
  - launch to Ready <s> s;
  - CPU after Ready mean <x> % / max <y> % of one core;
  - RSS peak <MB> MB, steady <MB> MB, drift <MB> MB over the last 5 minutes;
  - heartbeat FPS <min>–<max>;
  - <n> exception lines;
  - stopped by <SIGTERM…>, exit <status>.
  - The VM had <MB> MB in use before the run.

**What this resolves:**
- F3 [R] "A Linux dedicated-server player build is unverified": verified for startup, generation, NavMesh bake, movement and a basic attack. `UNITY_SERVER` stripping and an audio-free runtime were exercised. Frame pacing was measured (above).
- F16 [O] "no Linux or dedicated-server module, no build script": modules installed, `Tools/build-server.sh` added. There is still no CI.

**Still unverified:**
- all networking;
- multiple processes;
- disconnect and reconnect;
- runs longer than 10 minutes;
- abilities, intelligence, inventory and loot in the server build (not in the fixture);
- IL2CPP;
- running as a non-root service user;
- Docker.
```

- [ ] **Step 3: Decision 045**

Append to `Docs/Decisions.md`, in the existing decision format:

```markdown

## 045 — Dedicated-server startup path (Checkpoint A): a server-only scene, a fixture mission, a bounded diagnostic

- **Decided (Checkpoint A, 2026-10-11):**
  - **A separate server scene.** `ServerMission.unity` holds only `TacticalPause`, `Encounter`, `UnitSelection`, `ActiveCharacter`, `CoverRegistry`, `CoverDiscovery`, `InteractableRegistry`, a `MissionDirector` and the `ServerBootstrap`. It has no camera, light, audio, UI, input reader or debug view. It is built by `Blackglass/Server/Build Server Scene` (`ServerSceneBuilder`, idempotent). It is not in `EditorBuildSettings`: only the server build includes it, and the client scenes are unchanged.
  - **The fixture:**
    - a 2×2 grid with 3 rooms;
    - two `FriendlyUnit` capsules (slot 0 Ranged, slot 1 Melee, no abilities) and one `HostileUnit`;
    - no guards, terminal, loot, roster, inventory or intelligence; the objective is to eliminate the hostile;
    - seed <n>, the lowest seed that generates on attempt 1 and passes the diagnostic;
    - `-seed` overrides the seed.
  - **The bootstrap** sets `vSyncCount = 0` and `targetFrameRate` (`-fps`, default 60). It logs:
    - the identity: version, revision from `build-info.txt`, `buildGUID`, Unity version, platform, `UNITY_SERVER`, batch mode and graphics device;
    - the options;
    - `MISSION READY`, with the instance, seed, attempt, rooms, NavMesh area, cover, spawns, layout hash, generation time, bake time and time since startup;
    - a self-check;
    - a heartbeat: FPS, CPU %, RSS, managed heap.
  - At Ready it **disables the per-unit view components** (`AttackLineView`, `CommandQueueView`, `SelectionIndicator`, `HitFlash`) **on the spawned instances only**. Prefabs, renderers and colliders are unchanged.
  - **The diagnostic** (`-diagnostic`) issues one `MoveCommand` and one `AttackCommand` through `CommandableUnit.Issue`. PASS needs:
    - arrival within 0.75 m after at least 3 m of travel;
    - a health drop credited to the ordered unit's hit count.

    To remove chance, it disables `EnemyAI` and `AutoRetaliate` on the hostiles and `CompanionAI` and `AutoRetaliate` on the other friendlies, and forces the mover's `HitRoll` to land, all on the instances only. Real-time deadlines (move 30 s, attack 45 s, overall `-diagnosticTimeout`, default 120 s) end a paused or stalled run.
  - **Exit codes:** 0 PASS; 1 diagnostic FAIL; 2 startup failure (generation failed, the startup deadline passed, the scene is unwired).
  - **The build** (`Blackglass/Server/Build Linux Dedicated Server`, `Tools/build-server.sh`) targets StandaloneLinux64 with the Server subtarget and Mono, and contains only `ServerMission.unity`.
    - It refuses, without changing anything, when a module is missing or the Server backend isn't Mono. It never edits Player Settings.
    - It writes `build-info.txt`.
    - It always restores the Editor's active build target.
  - **Packaging and runs.** `Tools/deploy-server.sh` swaps the build into `/opt/blackglass/server/`. `run-diagnostic.sh` and `smoke-run.sh` write to `/opt/blackglass/logs/`.
  - **One mission per server process** (audit F5).
- **Why:** this proves the existing simulation (generation, runtime NavMesh, movement, combat, mission lifecycle) runs in a built Linux dedicated server before any networking is added. The audit found every presenter, camera and intelligence hook already optional, so the server needs a scene, not a fork of gameplay. The only shared-code change is the NavMesh bake time in `MissionReport`.
- **Rejected:**
  - A server mode inside `ProceduralMission`: it would ship input, HUD and camera objects in the server and put the client scene at risk.
  - A code-only bootstrap under `UNITY_SERVER`: the composition would be hidden in code and would still need runtime asset references.
  - Server-specific unit prefabs: duplicate prefabs to maintain. Disabling the views on the instances is enough.
  - Seeding `UnityEngine.Random` to make the diagnostic repeatable: brittle, and global. The per-instance `HitRoll` seam is exact.
  - Leaving the active build target on Linux Server after a build: the Editor and the test runner would then compile with `UNITY_SERVER`.
- **Implications:**
  - The networking experiment adds its transport and server gate to this scene and bootstrap, not to the client scene.
  - Anything new the server must simulate must work with `MissionSystems`' optional slots left null, or be wired here explicitly.
  - New per-unit view components should be added to `ServerBootstrap.IsView`.
  - The fixture deliberately leaves out abilities, intelligence and inventory. Adding them to the server is a later, explicit step.
- **Known limitations:**
  - Runs as root on the VM.
  - The VM smoke run is 10 minutes of an idle mission: initial evidence only.
  - The heartbeat CPU figure is a percentage of one core.
  - The archive is marked `dirty` while the owner's two settings edits are uncommitted.
  - The Linux build has been run only on `bg-dev-mission01`.
```

Replace `<n>` with the fixture seed.

- [ ] **Step 4: `Docs/DedicatedServer.md`**

Create this file:

````markdown
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
````

- [ ] **Step 5: Check the docs against the results**

Every number in §9.2 and decision 045 must appear in `results.md`, and every run the docs mention must have been run. Then run `git diff --stat`.
Expected: the only files changed are `CLAUDE.md`, `Docs/OnlineReadinessAudit.md`, `Docs/Decisions.md` and `Docs/DedicatedServer.md`.

- [ ] **Step 6: Commit**

```bash
git add CLAUDE.md Docs/OnlineReadinessAudit.md Docs/Decisions.md Docs/DedicatedServer.md
git commit -m "Docs: Checkpoint A guidance, decision 045, server run guide and audit follow-up" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Final verification and report

- [ ] **Step 1:** Invoke `superpowers:verification-before-completion`, then re-read `results.md` against E0–E7, B1–B2 and L1–L3. Mark each one passed, failed or not run, with evidence.
- [ ] **Step 2:** Run `git log --oneline main..HEAD` and `git status --short`. Confirm that the owner's two settings edits are still unstaged, and that `.claude/`, `Builds/` and `Logs/` are not committed.
- [ ] **Step 3:** Update memory with a `checkpoint-a-state` project memory and its `MEMORY.md` line. It records the branch, the commits, what has not been pushed, the open items, and the next concrete action.
- [ ] **Step 4:** Give the completion report the brief asks for:
  - what changed and why;
  - the build command and where the artifact is;
  - the tests actually run, with results;
  - Editor validation, Linux build and Linux runtime as three separate sections;
  - remaining blockers;
  - the next concrete action.

  Do not push or merge without the owner.
