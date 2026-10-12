# Networking experiment, first slice, part 1: spike, protocol and server gate (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decide the networking library with evidence (a FishNet spike), then build and test everything in the slice that does not touch the library: the `-net*` options, the request protocol, net ids, the `ServerCommandGate` in front of `CommandableUnit.Issue` and `SetMoveIntent`, and the shared deterministic-server controls.

**Architecture:** A library-free core under `Assets/_Project/Scripts/Net/Core/` (plain C# plus the existing game types): requests as data, a registry of net ids, a per-connection state object, and a gate that checks and then calls the existing command path. A source-audit test keeps the core free of networking-library references. The FishNet adapter, client, harness, tunnel run and acceptance are **part 2**, written after this part's spike evidence (see "What part 2 covers").

**Tech Stack:** Unity 6000.3.25f1, C#, NUnit (EditMode and PlayMode), the existing `MissionRig` test support, Git Bash scripts, FishNet (installed by the spike if it passes).

**Spec:** `Docs/superpowers/specs/2026-10-11-networking-gate-design.md` (approved by the owner on 2026-10-11).

## Global Constraints

- Unity **6000.3.25f1**; the Linux server target is Linux Dedicated Server with **Mono**.
- The gate and messages reference **no networking library** (source-audit test, Task 4).
- **No client scene, HUD, input or gameplay behaviour changes.** All new code goes under `Assets/_Project/Scripts/Net/Core/`, `Assets/_Project/Scripts/Server/` (Task 7 only) and `Assets/_Project/Tests/`.
- The server listens on **`127.0.0.1` only**; clients use an SSH tunnel through the existing `bg-dev-mission01` alias. Nothing is opened on the VM.
- **Never committed:** `Builds/`, build output, the token file, tunnel logs, keys, credentials, the spike's throwaway code, and the owner's two unrelated modified files (`Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset`) and `.claude/`.
- Work stays on branch **`networking-gate-slice`**. Do not push or merge; the owner decides.
- Commit messages end with the attribution trailer the session's system reminder gives. At the time of writing it is `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. `git add` includes the `.meta` files Unity creates beside new files.
- Baselines on `main` (2026-10-11): **EditMode 1271/1271, PlayMode 1367/1367**. Re-measure with a real run before trusting them (Task 2, Step 3).
- Unity runs one instance per project: tests, builds and the spike's Editor steps run one at a time, with the Editor closed. `Tools/run-tests.sh EditMode|PlayMode [filter]` prints a result line and `EXIT=`; EXIT 0 is all passed, 1 a compile error, 2 test failures.
- Unity prints an OS warning on the same line as the result. Read results with `... | sed 's/Unity requires.*behavior\.//'`.
- Never use `?.` or `??` on UnityEngine objects; use `== null`.
- Use `FormattableString.Invariant(...)` for numbers in log or message text, one call per interpolated string (never concatenate interpolated strings inside one call).

## Review Focus

The spec implies these input classes but no task's main tests exercise them; each is pinned by a named test.

1. **A replayed request** (same sequence or command id) must have no second effect. Pinned by `AReplayedRequest_HasNoSecondEffect` (Task 5) and `TheCommandIdWindow_ForgetsTheOldest` (Task 5).
2. **A unit that died or was destroyed after it was valid:** orders for it, and attacks on it, are `UnknownUnit` and never throw. Pinned by `UnknownAndDestroyedUnits_AreUnknownUnit` (Task 5), `AnOrderForADeadUnit_IsUnknownUnit` (Task 6) and `TickIgnoresDestroyedUnits` (Task 6).
3. **Non-finite numbers** (`NaN`, infinity) in a direction or destination must be `Malformed`, never reach the NavMesh. Pinned by `MalformedRequests_AreRejected` (Task 5).
4. **A client that stops refreshing a held intent** must not leave a unit walking. Pinned by `AnIntentExpiresWithoutARefresh` and `ARefreshKeepsTheIntent` (Task 6).
5. **Hostile or extreme ids** (zero, negative, `int.MaxValue`, an id nobody owns) must resolve to `UnknownUnit` and never index out of range. Pinned by `UnknownAndDestroyedUnits_AreUnknownUnit` (Task 5) and `TryGet_UnknownIds_ResolveToNothing` (Task 4).

## Rulings made while planning

- **Two plans.** Part 1 (this file) ends at the library-free core; part 2 is written after the spike, because its code depends on FishNet's installed API. Cost if wrong: one more review round before the adapter work.
- **Spike thresholds.** Spec criterion 5 ("no meaningful regression") is made measurable as: heartbeat fps never below 59.0, CPU after Ready mean at most 3.0 % of one core, RSS peak at most 150 MB in 10 minutes, zero `Exception` lines. Cost if wrong: a spike that passes these but is judged too heavy.
- **An unexpected exception inside the gate** is returned as `OrderRefused` and logged through `Log`, because the spec's reason list is closed and the gate must not throw. Cost if wrong: a bug in the gate looks like an ordinary refusal; the log line is the only trace.
- **"An append flag on a non-move request"** (spec section 3.3, check 1) cannot be written in these types, because only `NetMoveTo` carries it. The wire codec in part 2 rejects it at decode. Cost if wrong: none for this part.
- **Attack check order.** An attack with an unknown attacker or target is `UnknownUnit` before `NotOwner`, then `WrongSide`, so a probing client learns nothing about ownership of units that do not exist. Cost if wrong: a different reason code for the same refusal.

## What part 2 covers (not in this plan)

Written as its own plan once Task 2's evidence is in and the owner has seen it: the `NetServer` adapter and its bootstrap wiring (`-net` starts it after Ready; log lines for connections and rejections), the wire codec if the library needs one, the scene builder change and the allowed-type list in `ServerMissionPlayModeTests`, the `NetTestClient` scene and Input System controls, the `-netharness` mode, the Windows client build, `run-net.sh` and the local harness script, the VM acceptance run (A2, A3 six cases, A4, A9, the 10-minute idle run), decision 046's final text, audit §9.3, and the `DedicatedServer.md` section. It exists as a separate plan because its code depends on FishNet's installed API, which cannot be verified before the spike.

## File structure

Create:
- `Assets/_Project/Scripts/Net/Core/NetRequests.cs`: `NetRequest` and its four payloads, and `NetEnvelope`.
- `Assets/_Project/Scripts/Net/Core/NetResult.cs`: `NetRejectReason` and `NetResult`.
- `Assets/_Project/Scripts/Net/Core/NetUnits.cs`: the net id registry.
- `Assets/_Project/Scripts/Net/Core/NetConnection.cs`: per-connection identity and freshness state.
- `Assets/_Project/Scripts/Net/Core/ServerCommandGate.cs`: the gate.
- `Assets/_Project/Scripts/Server/ServerDeterminism.cs`: shared diagnostic/net-quiet controls.
- Tests: `Tests/EditMode/NetTestKit.cs`, `NetProtocolTests.cs`, `NetSourceAuditTests.cs`, `NetGateRejectionTests.cs`, `NetGateIntentTests.cs`; `Tests/PlayMode/NetGatePlayModeTests.cs`, `ServerDeterminismPlayModeTests.cs`.

Modify:
- `Assets/_Project/Scripts/Server/ServerOptions.cs`: the `-net*` options.
- `Assets/_Project/Scripts/Server/ServerDiagnostic.cs`: use `ServerDeterminism`.
- `Assets/_Project/Tests/EditMode/ServerOptionsTests.cs`: new cases.
- `CLAUDE.md`, `Docs/Decisions.md`, `Packages/manifest.json`, `Packages/packages-lock.json`, `Assets/_Project/Scripts/Blackglass.asmdef` (the last three only if the spike passes).

Expected totals after Task 7: EditMode **1306** (1271 + 7 + 10 + 14 + 4), PlayMode **1376** (1367 + 6 + 3).

---

### Task 1: Authorize the slice in `CLAUDE.md`

**Files:**
- Modify: `CLAUDE.md` ("Current Development Stage" and "Current Priority" only)

**Interfaces:**
- Consumes: spec Appendix A.
- Produces: the repository's written authorization for this slice.

- [ ] **Step 1: Replace the authorized-step paragraph and its list**

In "Current Development Stage", replace the paragraph beginning `The step currently authorized is **Checkpoint A: Linux dedicated-server startup**.` and the sentence and list that follow it (up to and including the line `- final production content (graphics, animation, audio, story).`) with exactly:

```
The step currently authorized is **the networking experiment's first slice: a server command gate and two clients** (`Docs/superpowers/specs/2026-10-11-networking-gate-design.md`). A FishNet spike decides the library. The server listens on loopback only and clients reach it through an SSH tunnel.

Each of these stays out of scope until the owner authorizes it:
- hidden information and per-connection visibility;
- accounts, backend APIs and databases;
- settlement redesign and reconnect;
- Docker packaging and orchestration;
- changes to the client scenes' input, HUD or gameplay for remote play;
- final production content (graphics, animation, audio, story).
```

Leave the line `Placeholder art remains the default.` and everything above the replaced paragraph unchanged.

- [ ] **Step 2: Replace "Current Priority"**

Replace everything under the `## Current Priority` heading, up to the end of the file, with exactly:

```
The networking experiment's first slice: a FishNet spike with go/no-go criteria, then a plain-C# server gate in front of `CommandableUnit.Issue` and `SetMoveIntent`, a thin server adapter, and a minimal net-test client, proven by a headless harness against the Linux server on `bg-dev-mission01`.

- Keep the gate and the messages free of networking-library types.
- Keep client scenes and gameplay behaviour unchanged.

Later steps, each authorized separately: hidden information and per-connection visibility, then Phase 13 (authenticated between-mission flow and trusted settlement).

Do not begin later-stage systems until specifically directed.
```

- [ ] **Step 3: Verify the old text is gone and nothing else moved**

Run: `git diff --stat CLAUDE.md` and `grep -c "Checkpoint A: Linux dedicated-server startup" CLAUDE.md`
Expected: only `CLAUDE.md` changed; the grep prints `0`.

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "Docs: CLAUDE.md authorizes the networking experiment's first slice

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: FishNet spike and library decision

**This task is exploratory by nature.** FishNet's API cannot be verified before the package is installed, so the steps fix the **observable results** and the stop rule, and the code is written against the installed package's own documentation and source. Everything the spike writes under `Assets/_Project/Spike/` is **throwaway and never committed**. Only a passing result changes committed files: the package reference, the assembly reference, and decision 046.

**Files:**
- Modify (kept only if the spike passes): `Packages/manifest.json`, `Packages/packages-lock.json`, `Assets/_Project/Scripts/Blackglass.asmdef`, `Docs/Decisions.md`
- Throwaway (deleted at the end): `Assets/_Project/Spike/*`, a temporary change to `Assets/_Project/Scenes/ServerMission.unity`
- Scratch record: `.claude/scratchpad/networking/spike-results.md`

**Interfaces:**
- Consumes: the Checkpoint A server (`Tools/build-server.sh`, `Tools/deploy-server.sh`, `Tools/LinuxServer/smoke-run.sh`), the `bg-dev-mission01` alias.
- Produces: a **Go** or **No-go**; decision 046; on Go, FishNet installed and referenced by `Blackglass.asmdef`; the name of the chosen TCP-based transport and whether the library's transform sync works headless (part 2 reads both from decision 046).

**Pass rule (all five, from spec §3.1):**
1. Installs and compiles on 6000.3.25f1 with a clean console; suites unchanged (1271/1367).
2. A Linux Dedicated Server (Mono) build with FishNet starts on the VM and a Windows client connects through the SSH tunnel on a **TCP-based** transport.
3. FishNet timing does not depend on `Time.timeScale`, and transform sync works in the headless server build.
4. The per-connection visibility API exists and compiles (not exercised).
5. A 10-minute idle server run with FishNet listening shows: heartbeat fps never below 59.0, CPU after Ready mean at most 3.0 % of one core, RSS peak at most 150 MB, zero `Exception` lines. (These thresholds are this plan's ruling for "no meaningful regression against 60 fps, about 1.4 %, about 106 MB".)

**Stop rule:** if any criterion fails, or installation needs FishNet's source under `Assets/` instead of a package reference, **stop**. Revert the committed-file changes (`git checkout -- Packages Assets/_Project/Scripts/Blackglass.asmdef`), delete the throwaway code, write the evidence and a recommended fallback to the scratch record, and report to the owner. Do not install another library. Tasks 3 to 7 do not depend on the outcome and may proceed only if the owner says so.

- [ ] **Step 1: Prepare the scratch record and re-measure the baseline**

```bash
mkdir -p .claude/scratchpad/networking
Tools/run-tests.sh EditMode > .claude/scratchpad/networking/baseline-editmode.txt 2>&1; tail -3 .claude/scratchpad/networking/baseline-editmode.txt | sed 's/Unity requires.*behavior\.//'
Tools/run-tests.sh PlayMode > .claude/scratchpad/networking/baseline-playmode.txt 2>&1; tail -3 .claude/scratchpad/networking/baseline-playmode.txt | sed 's/Unity requires.*behavior\.//'
```
Expected: `total="1271" passed="1271"` and `total="1367" passed="1367"`, both `EXIT=0`. The PlayMode run takes over ten minutes: run it in the background and wait for its notification. If the totals differ, use the measured totals in every later step and in "Expected totals" above.

- [ ] **Step 2: Install FishNet by package reference**

Read FishNet's installation page (`https://fish-networking.gitbook.io/docs/guides/getting-started/installing-fish-networking`) with WebFetch and use the Git URL it documents. Add it to `Packages/manifest.json` `dependencies` as a git dependency, then let Unity resolve it once:

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -projectPath "$(pwd -W)" -quit -logFile .claude/scratchpad/networking/resolve.log
```
Record in the scratch record: the exact URL and the resolved package version from `Packages/packages-lock.json`.
Expected: the log shows the package resolved and no `error CS`. If the page says Asset Store or copy-into-`Assets` only, apply the stop rule.

- [ ] **Step 3: Reference FishNet from the game assembly and re-run the suites**

Find the FishNet runtime assembly definition in the resolved package (`Library/PackageCache/<fishnet folder>/**/*.asmdef`; use the runtime one, not the editor one) and add its `name` to the `references` array of `Assets/_Project/Scripts/Blackglass.asmdef`. Then:

```bash
Tools/run-tests.sh EditMode > .claude/scratchpad/networking/fishnet-editmode.txt 2>&1; tail -3 .claude/scratchpad/networking/fishnet-editmode.txt | sed 's/Unity requires.*behavior\.//'
```
Expected: `total="1271" passed="1271"`, `EXIT=0`, and `grep -c "error CS" Logs/TestRun-EditMode.log` prints `0`. Run PlayMode the same way in the background: expect 1367/1367. Record both. Criterion 1 holds only if both are unchanged.

- [ ] **Step 4: Read what the library offers (criteria 3 and 4, from the installed source and docs)**

From the installed package and its documentation pages (`https://fish-networking.gitbook.io/docs/fishnet-building-blocks/transports` and the `TimeManager`, `NetworkObserver` and `NetworkTransform` pages), write to the scratch record:
- which transports are TCP-based and work for Windows and Linux standalone players (Tugboat is UDP, so a TCP-based one is needed for the SSH tunnel), and for each whether it ships in the package or needs another download;
- how `TimeManager` derives its tick (does it read `Time.timeScale`?);
- that a per-connection visibility API (observers) exists, with its type names.

Expected: a written answer for each. If no TCP-based transport can serve a non-WebGL Windows client and a Linux server, criterion 2 fails: apply the stop rule. If the chosen transport needs another package, name it in the record; the owner sees it in the report.

- [ ] **Step 5: Write the throwaway spike server and client**

Under `Assets/_Project/Spike/` (never committed), write against the installed docs:
- **`NetSpikeServer`** (MonoBehaviour): when the command line has `-net`, start FishNet's server bound to `127.0.0.1` on port 7770 with the chosen TCP-based transport; reply to each received message with an incrementing counter; spawn one networked object with the library's transform sync that moves in a circle; log `[Spike] client connected`, `[Spike] message n`; when the command line also has `-spikeTimeScaleZero`, set `Time.timeScale = 0` five seconds after the first client connects.
- **`NetSpikeClient`** (a throwaway scene `Assets/_Project/Spike/NetSpikeClient.unity` with a component): connects to `127.0.0.1:7770` (the tunnel's local end), sends a message every 100 ms, logs `[SpikeClient] reply n`, and logs `[SpikeClient] synced position x z` once a second for the synced object; after 30 s logs `[SpikeClient] DONE replies=<n> positionsChanged=<m>` and quits.
- A throwaway editor method that adds `NetSpikeServer` to the `Server` root of `ServerMission.unity` and a throwaway method that builds the client scene as a StandaloneWindows64 player into `Builds/NetSpikeClient/`.

Expected: both compile (`grep -c "error CS"` is 0 in the batch log).

- [ ] **Step 6: Build the Linux server with the spike and deploy it**

```bash
Tools/build-server.sh 2>&1 | tail -15
Tools/deploy-server.sh 2>&1 | tail -10
```
Expected: the archive builds and deploys (`Builds/blackglass-server-linux-<rev>-dirty.tar.gz` plus its sha256). The active target is restored to Windows afterwards (check `Logs` shows the Windows target on the next launch).

- [ ] **Step 7: Run the spike through the tunnel (criteria 2 and 3)**

```bash
# terminal A: start the server on the VM (loopback only), 60 s
ssh bg-dev-mission01 'cd / && nohup setsid timeout 90 /opt/blackglass/server/BlackglassServer.x86_64 -batchmode -nographics -logFile /opt/blackglass/logs/spike-server.log -net -spikeTimeScaleZero > /dev/null 2>&1 < /dev/null &'
# terminal B: open the tunnel, run the Windows client
ssh -N -L 7770:127.0.0.1:7770 bg-dev-mission01 &
Builds/NetSpikeClient/NetSpikeClient.exe -batchmode -nographics -logFile .claude/scratchpad/networking/spike-client.log
```
Expected, read from `spike-client.log` and `spike-server.log`:
- `DONE replies=` at least 200 (a reply for most of the 300 sends), proving the TCP-based transport works through the tunnel (criterion 2);
- replies **continue after** `timeScale=0` is logged on the server (the replies' counter keeps rising in the 25 s after it), proving timing is independent of `Time.timeScale` (criterion 3a);
- `positionsChanged=` at least 20, proving transform sync works headless (criterion 3b).

Stop the tunnel afterwards (`kill %1` or end the `ssh -N` process). If any expectation fails, apply the stop rule.

- [ ] **Step 8: Run the idle measurement (criterion 5)**

Copy `Tools/LinuxServer/smoke-run.sh` to the scratchpad, add `-net` to its server launch line, deploy that copy to the VM beside the server, and run it for 600 s with the spike scene's server listening (no client connected):

```bash
ssh bg-dev-mission01 'cd / && nohup setsid /opt/blackglass/server/smoke-run-net.sh 600 > /opt/blackglass/logs/smoke-run-net.out 2>&1 < /dev/null &'
# about 11 minutes later
ssh bg-dev-mission01 'cat $(ls -t /opt/blackglass/logs/*-smoke-summary.txt | head -1)'
```
Expected: heartbeat fps minimum at least 59.0, CPU mean at most 3.0, RSS peak at most 150 MB, `Exception` count 0. Record the summary in the scratch record next to the Checkpoint A figures (fps 60.0, CPU mean 1.4, RSS peak 102.5 at 10 minutes).

- [ ] **Step 9: Clean up the throwaway work**

```bash
git checkout -- Assets/_Project/Scenes/ServerMission.unity
rm -rf Assets/_Project/Spike Assets/_Project/Spike.meta Builds/NetSpikeClient
git status --short
```
Expected: `git status` shows only `Packages/manifest.json`, `Packages/packages-lock.json`, `Assets/_Project/Scripts/Blackglass.asmdef` as the slice's changes (plus the owner's two unrelated files and `.claude/`). Redeploy the clean Checkpoint A server later in part 2; the VM currently runs the spike build, so run `Tools/build-server.sh` and `Tools/deploy-server.sh` again now and confirm `ssh bg-dev-mission01 /opt/blackglass/server/run-diagnostic.sh` prints `BLACKGLASS DIAGNOSTIC PASS` and exits 0.

- [ ] **Step 10: Write decision 046 and commit (Go only)**

Read decision 045 in `Docs/Decisions.md` and match its layout and heading style. Add decision 046 with: **Decided** (FishNet, the version from `packages-lock.json`, installed by package reference, the chosen TCP-based transport and whether it needed another package); **Why** (the spike's five results, quoted from the scratch record); **Rejected** (Netcode for GameObjects and a thin Unity Transport layer: not tested, why they stayed candidates only; Tugboat for this slice: UDP cannot ride the SSH tunnel); **Implications** (the gate and messages stay library-free; transform sync works or not headless, so part 2 uses library sync or its own snapshots; the visibility API names for the hidden-information slice); **Known limitations** (spike was one client, 30 s, through a tunnel).

```bash
git add Docs/Decisions.md Packages/manifest.json Packages/packages-lock.json Assets/_Project/Scripts/Blackglass.asmdef
git commit -m "Networking: FishNet passes the spike; decision 046 records the library and transport

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```
On No-go instead: no commit; write the report and stop as the stop rule says.

- [ ] **Step 11: Confirm the suites are unchanged after cleanup**

Run both suites once more as in Step 1. Expected: 1271 and 1367 passing, `EXIT=0`.

---

### Task 3: `-net*` server options

**Files:**
- Modify: `Assets/_Project/Scripts/Server/ServerOptions.cs`
- Test: `Assets/_Project/Tests/EditMode/ServerOptionsTests.cs`

**Interfaces:**
- Consumes: the existing `ServerOptions.Parse(IReadOnlyList<string>, out List<string>)`, `TryValue`, `Describe(int)`.
- Produces: `ServerOptions.Net` (bool), `NetPort` (int, default `DefaultNetPort` = 7770, range 1024..65535), `NetTokensPath` (string, default null), `NetQuiet` (bool); `Describe` appends ` net=no` or ` net=yes port=<n> quiet=<yes|no>`.

- [ ] **Step 1: Write the failing tests**

Add inside the class `ServerOptionsTests` in `ServerOptionsTests.cs`, before the final two closing braces:

```csharp
        [Test]
        public void NetArguments_HaveTheirDefaults()
        {
            var options = Parse(out var warnings);
            Assert.That(options.Net, Is.False);
            Assert.That(options.NetPort, Is.EqualTo(7770));
            Assert.That(options.NetTokensPath, Is.Null);
            Assert.That(options.NetQuiet, Is.False);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void NetArguments_AreRead()
        {
            var options = Parse(out var warnings, "-net", "-netPort", "7801", "-netTokens", "/opt/blackglass/dev-tokens.txt", "-netQuiet");
            Assert.That(options.Net, Is.True);
            Assert.That(options.NetPort, Is.EqualTo(7801));
            Assert.That(options.NetTokensPath, Is.EqualTo("/opt/blackglass/dev-tokens.txt"));
            Assert.That(options.NetQuiet, Is.True);
            Assert.That(warnings, Is.Empty);
        }

        [TestCase("1023")]
        [TestCase("65536")]
        [TestCase("abc")]
        public void ABadNetPort_KeepsTheDefault_AndWarns(string value)
        {
            var options = Parse(out var warnings, "-net", "-netPort", value);
            Assert.That(options.NetPort, Is.EqualTo(7770));
            Assert.That(options.Net, Is.True);
            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.Contains("-netPort", warnings[0]);
        }

        [Test]
        public void NetTokens_WithoutAValue_KeepsNull_AndDoesNotSwallowTheNextFlag()
        {
            var missing = Parse(out var warnings, "-netTokens");
            Assert.That(missing.NetTokensPath, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(1));

            var flagNext = Parse(out warnings, "-netTokens", "-net");
            Assert.That(flagNext.NetTokensPath, Is.Null);
            Assert.That(flagNext.Net, Is.True, "the next flag is left for the loop to read");
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void Describe_ShowsTheNetOptions()
        {
            Assert.That(Parse().Describe(7), Does.EndWith("net=no"));
            Assert.That(Parse("-net", "-netQuiet").Describe(7), Does.EndWith("net=yes port=7770 quiet=yes"));
            Assert.That(Parse("-net", "-netPort", "7801").Describe(7), Does.EndWith("net=yes port=7801 quiet=no"));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode ServerOptionsTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -15`
Expected: `EXIT=1` with `error CS1061` for `Net`, `NetPort`, `NetTokensPath` and `NetQuiet` (a compile failure is the red state).

- [ ] **Step 3: Implement**

In `ServerOptions.cs`:

Add after `DefaultHeartbeatSeconds`:
```csharp
        public const int DefaultNetPort = 7770;
```

Add after the `HeartbeatSeconds` property:
```csharp
        /// <summary>Start the networking adapter (the networking experiment, decision 046). Off by default.</summary>
        public bool Net { get; private set; }
        public int NetPort { get; private set; } = DefaultNetPort;
        /// <summary>Path of the development token file on the server; null when none was given.</summary>
        public string NetTokensPath { get; private set; }
        /// <summary>Apply the diagnostic's controls (hostile AI off, friendlies' autonomy off, forced hits) so a networked run is repeatable.</summary>
        public bool NetQuiet { get; private set; }
```

Add these cases before the closing brace of the `switch` in `Parse`:
```csharp
                    case "-net":
                        options.Net = true;
                        break;
                    case "-netQuiet":
                        options.NetQuiet = true;
                        break;
                    case "-netPort":
                        if (TryValue(args, ref i, 1024, 65535, warnings, out var netPort))
                            options.NetPort = netPort;
                        break;
                    case "-netTokens":
                        if (TryText(args, ref i, warnings, out var tokens))
                            options.NetTokensPath = tokens;
                        break;
```

Add after `TryValue`:
```csharp
        // Reads the text after args[i]. A missing value, or another flag, warns and leaves the flag for the loop.
        static bool TryText(IReadOnlyList<string> args, ref int i, List<string> warnings, out string value)
        {
            var name = args[i];
            value = null;
            if (i + 1 >= args.Count)
            {
                warnings.Add($"{name} has no value; keeping the default");
                return false;
            }
            var text = args[i + 1];
            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                warnings.Add($"{name} has no value (next is {text}); keeping the default");
                return false;
            }
            i++;
            value = text;
            return true;
        }
```

Replace the body of `Describe` so it ends with the net part (keep the two existing parts unchanged):
```csharp
        public string Describe(int fixtureSeed) =>
            FormattableString.Invariant($"seed={Seed ?? fixtureSeed}({(Seed.HasValue ? "override" : "fixture")}) fps={FrameRate} diagnostic={(Diagnostic ? "yes" : "no")} ") +
            FormattableString.Invariant($"diagnosticTimeout={DiagnosticTimeoutSeconds}s startupTimeout={StartupTimeoutSeconds}s heartbeat={(HeartbeatSeconds > 0 ? HeartbeatSeconds + "s" : "off")} ") +
            (Net ? FormattableString.Invariant($"net=yes port={NetPort} quiet={(NetQuiet ? "yes" : "no")}") : "net=no");
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode ServerOptionsTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6`
Expected: `result="Passed"`, all `ServerOptionsTests` passing (the 18 existing plus 7 new: `total="25"`), `EXIT=0`. The existing `Describe_NamesTheSeedSource` test still passes because the new text is appended.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Server/ServerOptions.cs Assets/_Project/Tests/EditMode/ServerOptionsTests.cs
git commit -m "Server: -net, -netPort, -netTokens and -netQuiet options

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Request protocol, net ids and the library-independence audit

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetRequests.cs`, `NetResult.cs`, `NetUnits.cs`
- Test: `Assets/_Project/Tests/EditMode/NetProtocolTests.cs`, `NetSourceAuditTests.cs`

**Interfaces:**
- Consumes: `CommandableUnit`, `MissionDirector.Friendlies` / `Hostiles` (both `IReadOnlyList<CommandableUnit>`), `MissionDirector.InstanceId`.
- Produces:
  - `abstract class NetRequest`; `NetMoveIntent(int unitId, Vector3 direction)`, `NetMoveTo(int unitId, Vector3 destination, bool append = false)`, `NetAttack(int unitId, int targetId)`, `NetStop(int unitId)`, each with get-only properties named as the constructor parameters in PascalCase (`UnitId`, `Direction`, `Destination`, `Append`, `TargetId`).
  - `NetEnvelope(string attemptId, uint sequence, uint commandId, NetRequest request)` with `AttemptId`, `Sequence`, `CommandId`, `Request`.
  - `enum NetRejectReason { None, Malformed, WrongAttempt, StaleSequence, DuplicateCommand, UnknownUnit, NotOwner, WrongSide, OrderRefused }`; `readonly struct NetResult` with `static Accepted`, `static Rejected(NetRejectReason)`, `Reason`, `IsAccepted`.
  - `sealed class NetUnits`: `int Register(CommandableUnit unit, bool hostile)` (ids start at 1, never reused), `bool TryGet(int id, out CommandableUnit unit)` (false for unknown ids and destroyed units), `bool IsHostile(int id)`, `int IdOf(CommandableUnit unit)` (0 when unregistered), `int Count`, `static NetUnits FromMission(MissionDirector director)` and `static NetUnits FromMission(IReadOnlyList<CommandableUnit> friendlies, IReadOnlyList<CommandableUnit> hostiles)` (friendlies first).

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetTestKit.cs`:
```csharp
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>Builds plain units for the networking tests: a GameObject with Health and CommandableUnit, no NavMesh.</summary>
    internal static class NetTestKit
    {
        public static CommandableUnit MakeUnit(List<GameObject> hosts, string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            host.AddComponent<Health>();
            return host.AddComponent<CommandableUnit>();
        }

        public static void DestroyAll(List<GameObject> hosts)
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }
    }
}
```

`Assets/_Project/Tests/EditMode/NetProtocolTests.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class NetProtocolTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        CommandableUnit Unit(string name) => NetTestKit.MakeUnit(hosts, name);

        [Test]
        public void RequestTypes_AreExactlyTheFour_SoNoMessageCanSetHealthOrPosition()
        {
            var types = typeof(NetRequest).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(NetRequest).IsAssignableFrom(t))
                .Select(t => t.Name).OrderBy(n => n).ToArray();
            Assert.That(types, Is.EqualTo(new[] { "NetAttack", "NetMoveIntent", "NetMoveTo", "NetStop" }));
        }

        [Test]
        public void Register_NumbersUnitsFromOne_AndKeepsTheirSides()
        {
            var units = new NetUnits();
            var friend = Unit("FriendlyUnit_1");
            var hostile = Unit("HostileUnit_1");
            Assert.That(units.Register(friend, false), Is.EqualTo(1));
            Assert.That(units.Register(hostile, true), Is.EqualTo(2));
            Assert.That(units.Count, Is.EqualTo(2));
            Assert.That(units.IsHostile(1), Is.False);
            Assert.That(units.IsHostile(2), Is.True);
            Assert.That(units.IdOf(hostile), Is.EqualTo(2));
            Assert.That(units.IdOf(Unit("Stranger")), Is.EqualTo(0));
            Assert.That(units.TryGet(1, out var found), Is.True);
            Assert.That(found, Is.SameAs(friend));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MaxValue)]
        [TestCase(99)]
        public void TryGet_UnknownIds_ResolveToNothing(int id)
        {
            var units = new NetUnits();
            units.Register(Unit("FriendlyUnit_1"), false);
            Assert.That(units.TryGet(id, out var unit), Is.False);
            Assert.That(unit == null, Is.True);
            Assert.That(units.IsHostile(id), Is.False);
        }

        [Test]
        public void TryGet_ADestroyedUnit_ResolvesToNothing()
        {
            var units = new NetUnits();
            var friend = Unit("FriendlyUnit_1");
            var id = units.Register(friend, false);
            Object.DestroyImmediate(friend.gameObject);
            Assert.That(units.TryGet(id, out _), Is.False);
        }

        [Test]
        public void Ids_AreNeverReused()
        {
            var units = new NetUnits();
            var first = Unit("A");
            var firstId = units.Register(first, false);
            units.Register(Unit("B"), false);
            Object.DestroyImmediate(first.gameObject);
            Assert.That(units.Register(Unit("C"), false), Is.EqualTo(3));
            Assert.That(units.TryGet(firstId, out _), Is.False);
        }

        [Test]
        public void FromMission_RegistersFriendliesThenHostiles()
        {
            var friendlies = new List<CommandableUnit> { Unit("F1"), Unit("F2") };
            var hostiles = new List<CommandableUnit> { Unit("H1") };
            var units = NetUnits.FromMission(friendlies, hostiles);
            Assert.That(units.IdOf(friendlies[0]), Is.EqualTo(1));
            Assert.That(units.IdOf(friendlies[1]), Is.EqualTo(2));
            Assert.That(units.IdOf(hostiles[0]), Is.EqualTo(3));
            Assert.That(units.IsHostile(3), Is.True);
        }
    }
}
```

`Assets/_Project/Tests/EditMode/NetSourceAuditTests.cs`:
```csharp
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>Keeps the networking core independent of any networking library (spec section 3.2, CLAUDE.md).</summary>
    public class NetSourceAuditTests
    {
        const string Folder = "Assets/_Project/Scripts/Net/Core";

        [Test]
        public void TheCoreFiles_ReferenceNoNetworkingLibrary()
        {
            var files = Directory.GetFiles(Folder, "*.cs");
            Assert.That(files, Is.Not.Empty, "the audit must not pass vacuously");
            string[] forbidden = { "FishNet", "Unity.Netcode", "Unity.Networking", "Mirror", "LiteNetLib", "System.Net.Sockets" };
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (var token in forbidden.Where(text.Contains))
                    Assert.Fail(Path.GetFileName(file) + " must not reference '" + token + "'");
            }
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode "NetProtocolTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -10`
Expected: `EXIT=1`, `error CS0246` for `NetRequest`, `NetUnits` and the other missing types.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Net/Core/NetRequests.cs`:
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What a remote client may ask for (the networking experiment, spec section 3.2). Plain data with no networking-library
    /// types. There are exactly four requests; none sets health, position, cooldowns or items, so a client cannot change
    /// state except by asking the server to carry out an order. Units and targets are named by net id (NetUnits).
    /// </summary>
    public abstract class NetRequest { }

    /// <summary>Hold a steering direction (maps to CommandableUnit.SetMoveIntent). Zero releases it.</summary>
    public sealed class NetMoveIntent : NetRequest
    {
        public NetMoveIntent(int unitId, Vector3 direction)
        {
            UnitId = unitId;
            Direction = direction;
        }

        public int UnitId { get; }
        public Vector3 Direction { get; }
    }

    /// <summary>Walk to a point; Append queues it behind the unit's orders (IssueMode.Append).</summary>
    public sealed class NetMoveTo : NetRequest
    {
        public NetMoveTo(int unitId, Vector3 destination, bool append = false)
        {
            UnitId = unitId;
            Destination = destination;
            Append = append;
        }

        public int UnitId { get; }
        public Vector3 Destination { get; }
        public bool Append { get; }
    }

    public sealed class NetAttack : NetRequest
    {
        public NetAttack(int unitId, int targetId)
        {
            UnitId = unitId;
            TargetId = targetId;
        }

        public int UnitId { get; }
        public int TargetId { get; }
    }

    public sealed class NetStop : NetRequest
    {
        public NetStop(int unitId) => UnitId = unitId;

        public int UnitId { get; }
    }

    /// <summary>One request with the identity the gate checks: the attempt, a per-connection sequence and a command id.</summary>
    public sealed class NetEnvelope
    {
        public NetEnvelope(string attemptId, uint sequence, uint commandId, NetRequest request)
        {
            AttemptId = attemptId;
            Sequence = sequence;
            CommandId = commandId;
            Request = request;
        }

        public string AttemptId { get; }
        public uint Sequence { get; }
        public uint CommandId { get; }
        public NetRequest Request { get; }
    }
}
```

`Assets/_Project/Scripts/Net/Core/NetResult.cs`:
```csharp
namespace Blackglass
{
    /// <summary>Why the server gate refused a request (spec section 3.2).</summary>
    public enum NetRejectReason
    {
        /// <summary>Not a refusal.</summary>
        None,
        /// <summary>No request, no attempt id, an unknown request type, or a non-finite number.</summary>
        Malformed,
        WrongAttempt,
        /// <summary>The sequence is not greater than the connection's last accepted one.</summary>
        StaleSequence,
        DuplicateCommand,
        /// <summary>A net id that maps to nothing, or to a dead or destroyed unit.</summary>
        UnknownUnit,
        NotOwner,
        /// <summary>An attack on a unit of the attacker's own side.</summary>
        WrongSide,
        /// <summary>The request was valid but the existing command path would not carry it out (CommandableUnit.Issue returned false).</summary>
        OrderRefused,
    }

    public readonly struct NetResult
    {
        NetResult(NetRejectReason reason) => Reason = reason;

        public static NetResult Accepted => new NetResult(NetRejectReason.None);

        public static NetResult Rejected(NetRejectReason reason) => new NetResult(reason);

        public NetRejectReason Reason { get; }

        public bool IsAccepted => Reason == NetRejectReason.None;
    }
}
```

`Assets/_Project/Scripts/Net/Core/NetUnits.cs`:
```csharp
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// The net ids of one mission attempt's units (audit F2). Ids start at 1 and are never reused, so a stale id can never
    /// name a different unit. Create one per attempt; sides are fixed at registration. A destroyed unit resolves to nothing.
    /// </summary>
    public sealed class NetUnits
    {
        readonly List<CommandableUnit> units = new List<CommandableUnit>();
        readonly List<bool> hostile = new List<bool>();

        public int Count => units.Count;

        /// <summary>Registers the unit and returns its id.</summary>
        public int Register(CommandableUnit unit, bool isHostile)
        {
            units.Add(unit);
            hostile.Add(isHostile);
            return units.Count;
        }

        public bool TryGet(int id, out CommandableUnit unit)
        {
            unit = null;
            if (id < 1 || id > units.Count)
                return false;
            var found = units[id - 1];
            if (found == null)
                return false;
            unit = found;
            return true;
        }

        /// <summary>True for a registered hostile; false for a friendly and for an unknown id.</summary>
        public bool IsHostile(int id) => id >= 1 && id <= units.Count && hostile[id - 1];

        /// <summary>The unit's id, or 0 when it is not registered.</summary>
        public int IdOf(CommandableUnit unit)
        {
            if (unit == null)
                return 0;
            for (var i = 0; i < units.Count; i++)
            {
                if (ReferenceEquals(units[i], unit))
                    return i + 1;
            }
            return 0;
        }

        public static NetUnits FromMission(MissionDirector director) => FromMission(director.Friendlies, director.Hostiles);

        /// <summary>Friendlies first, so their ids are 1..n; then the hostiles.</summary>
        public static NetUnits FromMission(IReadOnlyList<CommandableUnit> friendlies, IReadOnlyList<CommandableUnit> hostiles)
        {
            var result = new NetUnits();
            foreach (var unit in friendlies)
                result.Register(unit, false);
            foreach (var unit in hostiles)
                result.Register(unit, true);
            return result;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "NetProtocolTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6`
Expected: `total="10" passed="10"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Scripts/Net.meta Assets/_Project/Tests/EditMode/NetTestKit.cs Assets/_Project/Tests/EditMode/NetTestKit.cs.meta Assets/_Project/Tests/EditMode/NetProtocolTests.cs Assets/_Project/Tests/EditMode/NetProtocolTests.cs.meta Assets/_Project/Tests/EditMode/NetSourceAuditTests.cs Assets/_Project/Tests/EditMode/NetSourceAuditTests.cs.meta
git commit -m "Net: request protocol, net ids and the library-independence audit

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `NetConnection` and the gate's checks

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetConnection.cs`, `ServerCommandGate.cs`
- Test: `Assets/_Project/Tests/EditMode/NetGateRejectionTests.cs`

**Interfaces:**
- Consumes: Task 4's types; `CommandableUnit.SetMoveIntent(Vector3)`, `.MoveIntent`, `.CurrentCommand`, `.PendingCommands`, `.IsAlive`.
- Produces:
  - `sealed class NetConnection`: `NetConnection(string account, IEnumerable<int> ownedUnitIds)`, `string Account`, `bool Owns(int unitId)`, `const int CommandIdWindow = 256`, internal `bool IsFresh(uint sequence)`, `bool HasSeen(uint commandId)`, `void Record(uint sequence, uint commandId)`.
  - `sealed class ServerCommandGate`: `ServerCommandGate(NetUnits units, string attemptId)`, `NetResult Handle(NetConnection connection, NetEnvelope envelope, double now)` (never throws), `System.Action<string> Log` (optional, receives the text of an unexpected exception).
  - At the end of this task `Handle` executes only `NetMoveIntent`; the other three payloads pass every check and then return `OrderRefused` until Task 6 adds their execution.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetGateRejectionTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class NetGateRejectionTests
    {
        const string Attempt = "attempt-1";

        sealed class RogueRequest : NetRequest { }

        readonly List<GameObject> hosts = new List<GameObject>();
        CommandableUnit friendA;
        CommandableUnit friendB;
        CommandableUnit hostile;
        int idA;
        int idB;
        int idHostile;
        ServerCommandGate gate;
        NetConnection owner;
        NetConnection observer;
        uint counter;

        [SetUp]
        public void SetUp()
        {
            friendA = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            friendB = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            hostile = NetTestKit.MakeUnit(hosts, "HostileUnit_1");
            var units = new NetUnits();
            idA = units.Register(friendA, false);
            idB = units.Register(friendB, false);
            idHostile = units.Register(hostile, true);
            gate = new ServerCommandGate(units, Attempt);
            owner = new NetConnection("account-a", new[] { idA, idB });
            observer = new NetConnection("account-b", new int[0]);
            counter = 0;
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        NetResult Send(NetConnection connection, NetRequest request, uint? sequence = null, uint? commandId = null, string attempt = Attempt)
        {
            counter++;
            var seq = sequence ?? counter;
            return gate.Handle(connection, new NetEnvelope(attempt, seq, commandId ?? seq, request), 0d);
        }

        static NetMoveIntent Steer(int unitId) => new NetMoveIntent(unitId, Vector3.right);

        static string State(CommandableUnit unit) =>
            $"{unit.CurrentCommand}|{unit.PendingCommands.Count}|{unit.MoveIntent}|{unit.transform.position}";

        [Test]
        public void AMoveForAHostile_IsNotOwner()
        {
            var result = Send(owner, new NetMoveTo(idHostile, new Vector3(1f, 0f, 1f)));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.NotOwner));
            Assert.That(hostile.CurrentCommand, Is.Null);
        }

        [Test]
        public void AnAttackOnAFriendly_IsWrongSide()
        {
            var result = Send(owner, new NetAttack(idA, idB));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.WrongSide));
            Assert.That(friendA.CurrentCommand, Is.Null);
        }

        [Test]
        public void ADuplicateCommandId_IsRejected()
        {
            Assert.That(Send(owner, Steer(idA), 1, 7).IsAccepted, Is.True);
            Assert.That(Send(owner, Steer(idA), 2, 7).Reason, Is.EqualTo(NetRejectReason.DuplicateCommand));
        }

        [Test]
        public void AnOlderOrRepeatedSequence_IsStale()
        {
            Assert.That(Send(owner, Steer(idA), 5, 1).IsAccepted, Is.True);
            Assert.That(Send(owner, Steer(idA), 5, 2).Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(Send(owner, Steer(idA), 4, 3).Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(Send(owner, Steer(idA), 6, 4).IsAccepted, Is.True);
        }

        [Test]
        public void AWrongAttemptId_IsRejected_WithoutConsumingTheSequenceOrCommandId()
        {
            Assert.That(Send(owner, Steer(idA), 1, 1, "another-attempt").Reason, Is.EqualTo(NetRejectReason.WrongAttempt));
            Assert.That(Send(owner, Steer(idA), 1, 1).IsAccepted, Is.True);
        }

        [Test]
        public void MalformedRequests_AreRejected()
        {
            var cases = new List<KeyValuePair<string, NetResult>>
            {
                new KeyValuePair<string, NetResult>("no envelope", gate.Handle(owner, null, 0d)),
                new KeyValuePair<string, NetResult>("no connection", gate.Handle(null, new NetEnvelope(Attempt, 1, 1, Steer(idA)), 0d)),
                new KeyValuePair<string, NetResult>("no request", gate.Handle(owner, new NetEnvelope(Attempt, 1, 1, null), 0d)),
                new KeyValuePair<string, NetResult>("empty attempt", gate.Handle(owner, new NetEnvelope("", 1, 1, Steer(idA)), 0d)),
                new KeyValuePair<string, NetResult>("NaN direction", gate.Handle(owner, new NetEnvelope(Attempt, 1, 1, new NetMoveIntent(idA, new Vector3(float.NaN, 0f, 0f))), 0d)),
                new KeyValuePair<string, NetResult>("infinite destination", gate.Handle(owner, new NetEnvelope(Attempt, 1, 1, new NetMoveTo(idA, new Vector3(0f, float.PositiveInfinity, 0f))), 0d)),
                new KeyValuePair<string, NetResult>("unknown request type", gate.Handle(owner, new NetEnvelope(Attempt, 1, 1, new RogueRequest()), 0d)),
            };
            foreach (var pair in cases)
                Assert.That(pair.Value.Reason, Is.EqualTo(NetRejectReason.Malformed), pair.Key);
            Assert.That(Send(owner, Steer(idA), 1, 1).IsAccepted, Is.True, "a malformed request consumes nothing");
        }

        [Test]
        public void EveryRejection_ChangesNothing_AndNeverThrows()
        {
            Assert.That(Send(owner, Steer(idA), 1, 1).IsAccepted, Is.True);
            var before = new[] { State(friendA), State(friendB), State(hostile) };

            NetResult[] results = null;
            Assert.DoesNotThrow(() => results = new[]
            {
                Send(owner, Steer(idA), 2, 1),
                Send(owner, Steer(idA), 1, 2),
                Send(owner, Steer(idA), 3, 3, "another-attempt"),
                Send(owner, new NetMoveIntent(idA, new Vector3(float.NaN, 0f, 0f)), 4, 4),
                Send(owner, new NetMoveTo(idHostile, Vector3.one), 5, 5),
                Send(owner, new NetAttack(idA, idB), 6, 6),
                Send(owner, new NetStop(999), 7, 7),
                Send(observer, new NetStop(idA), 1, 1),
            });

            var expected = new[]
            {
                NetRejectReason.DuplicateCommand, NetRejectReason.StaleSequence, NetRejectReason.WrongAttempt, NetRejectReason.Malformed,
                NetRejectReason.NotOwner, NetRejectReason.WrongSide, NetRejectReason.UnknownUnit, NetRejectReason.NotOwner,
            };
            for (var i = 0; i < expected.Length; i++)
                Assert.That(results[i].Reason, Is.EqualTo(expected[i]), "request " + i);
            Assert.That(new[] { State(friendA), State(friendB), State(hostile) }, Is.EqualTo(before));
        }

        [Test]
        public void ANonOwner_IsRefusedForEveryPayload()
        {
            var requests = new NetRequest[] { Steer(idA), new NetMoveTo(idA, Vector3.one), new NetAttack(idA, idHostile), new NetStop(idA) };
            foreach (var request in requests)
                Assert.That(Send(observer, request).Reason, Is.EqualTo(NetRejectReason.NotOwner), request.GetType().Name);
            Assert.That(friendA.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(friendA.CurrentCommand, Is.Null);
        }

        [Test]
        public void UnknownAndDestroyedUnits_AreUnknownUnit()
        {
            foreach (var id in new[] { 0, -1, int.MaxValue, 999 })
                Assert.That(Send(owner, new NetStop(id)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit), "id " + id);
            Assert.That(Send(owner, new NetAttack(idA, 999)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit), "unknown target");

            Object.DestroyImmediate(friendB.gameObject);
            Assert.That(Send(owner, new NetStop(idB)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit), "destroyed unit");
            Object.DestroyImmediate(hostile.gameObject);
            Assert.That(Send(owner, new NetAttack(idA, idHostile)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit), "destroyed target");
        }

        [Test]
        public void AnAuthorisationFailure_ConsumesTheSequenceAndCommandId()
        {
            Assert.That(Send(observer, new NetStop(idA), 1, 1).Reason, Is.EqualTo(NetRejectReason.NotOwner));
            Assert.That(Send(observer, new NetStop(idA), 1, 2).Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(Send(observer, new NetStop(idA), 2, 1).Reason, Is.EqualTo(NetRejectReason.DuplicateCommand));
        }

        [Test]
        public void AFreshnessFailure_ConsumesNothing()
        {
            Assert.That(Send(owner, Steer(idA), 5, 1).IsAccepted, Is.True);
            Assert.That(Send(owner, Steer(idA), 5, 9).Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(Send(owner, Steer(idA), 6, 9).IsAccepted, Is.True, "command id 9 was never consumed");
        }

        [Test]
        public void TheCommandIdWindow_ForgetsTheOldest()
        {
            for (uint i = 1; i <= 257; i++)
                Assert.That(Send(owner, Steer(idA), i, i).IsAccepted, Is.True, "request " + i);
            Assert.That(Send(owner, Steer(idA), 258, 1).IsAccepted, Is.True, "command id 1 has left the 256-entry window");
            Assert.That(Send(owner, Steer(idA), 259, 3).Reason, Is.EqualTo(NetRejectReason.DuplicateCommand), "command id 3 is still inside it");
        }

        [Test]
        public void AGoodMoveIntent_IsAccepted_AndSetsTheClampedIntent()
        {
            var result = Send(owner, new NetMoveIntent(idA, new Vector3(2f, 5f, 0f)));
            Assert.That(result.IsAccepted, Is.True);
            Assert.That((friendA.MoveIntent - Vector3.right).magnitude, Is.LessThan(1e-4f));
        }

        [Test]
        public void AReplayedRequest_HasNoSecondEffect()
        {
            Assert.That(Send(owner, Steer(idA), 1, 1).IsAccepted, Is.True);
            friendA.SetMoveIntent(Vector3.zero);
            var replay = Send(owner, Steer(idA), 1, 1);
            Assert.That(replay.Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(friendA.MoveIntent, Is.EqualTo(Vector3.zero), "the replay did not steer the unit again");
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode NetGateRejectionTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -8`
Expected: `EXIT=1`, `error CS0246` for `ServerCommandGate` and `NetConnection`.

- [ ] **Step 3: Implement `NetConnection`**

`Assets/_Project/Scripts/Net/Core/NetConnection.cs`:
```csharp
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// What the server knows about one connected client: its account, the units it owns, the last sequence it got
    /// through, and the most recent command ids (so a replayed request is recognised). Owned by whoever manages the
    /// connection; the gate reads and updates the freshness state.
    /// </summary>
    public sealed class NetConnection
    {
        /// <summary>How many recent command ids a connection remembers.</summary>
        public const int CommandIdWindow = 256;

        readonly HashSet<int> owned;
        readonly Queue<uint> recent = new Queue<uint>();
        readonly HashSet<uint> recentSet = new HashSet<uint>();
        bool hasSequence;
        uint lastSequence;

        public NetConnection(string account, IEnumerable<int> ownedUnitIds)
        {
            Account = account;
            owned = new HashSet<int>(ownedUnitIds ?? new int[0]);
        }

        public string Account { get; }

        public bool Owns(int unitId) => owned.Contains(unitId);

        internal bool IsFresh(uint sequence) => !hasSequence || sequence > lastSequence;

        internal bool HasSeen(uint commandId) => recentSet.Contains(commandId);

        internal void Record(uint sequence, uint commandId)
        {
            hasSequence = true;
            lastSequence = sequence;
            recent.Enqueue(commandId);
            recentSet.Add(commandId);
            if (recent.Count > CommandIdWindow)
                recentSet.Remove(recent.Dequeue());
        }
    }
}
```

- [ ] **Step 4: Implement the gate (checks, and `NetMoveIntent` only)**

`Assets/_Project/Scripts/Net/Core/ServerCommandGate.cs`:
```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The server's gate in front of the existing command path (audit F1, spec section 3.3). Checks a request in a fixed
    /// order: well-formed, the right attempt, a fresh sequence, an unseen command id, a living unit, ownership, side; only
    /// then does it call CommandableUnit. It never throws and a rejected request changes nothing. A request that passes the
    /// freshness checks records its sequence and command id even if it later fails ownership, side or the order itself, so
    /// replaying it is a DuplicateCommand. Holds no game rule: the orders are carried out by CommandableUnit.
    /// </summary>
    public sealed class ServerCommandGate
    {
        readonly NetUnits units;
        readonly string attemptId;

        public ServerCommandGate(NetUnits units, string attemptId)
        {
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.attemptId = attemptId ?? throw new ArgumentNullException(nameof(attemptId));
        }

        /// <summary>Receives the text of an unexpected exception (the request is then refused as OrderRefused).</summary>
        public Action<string> Log { get; set; }

        /// <summary>Checks and carries out one request. `now` is real time in seconds.</summary>
        public NetResult Handle(NetConnection connection, NetEnvelope envelope, double now)
        {
            try
            {
                return Process(connection, envelope, now);
            }
            catch (Exception exception)
            {
                Log?.Invoke($"gate exception {exception.GetType().Name}: {exception.Message}");
                return NetResult.Rejected(NetRejectReason.OrderRefused);
            }
        }

        NetResult Process(NetConnection connection, NetEnvelope envelope, double now)
        {
            if (connection == null || envelope == null || envelope.Request == null || string.IsNullOrEmpty(envelope.AttemptId)
                || !IsKnownAndFinite(envelope.Request))
                return Reject(NetRejectReason.Malformed);
            if (!string.Equals(envelope.AttemptId, attemptId, StringComparison.Ordinal))
                return Reject(NetRejectReason.WrongAttempt);
            if (!connection.IsFresh(envelope.Sequence))
                return Reject(NetRejectReason.StaleSequence);
            if (connection.HasSeen(envelope.CommandId))
                return Reject(NetRejectReason.DuplicateCommand);
            connection.Record(envelope.Sequence, envelope.CommandId);

            switch (envelope.Request)
            {
                case NetMoveIntent intent:
                    return Own(connection, intent.UnitId, out var steered, out var steerFailure)
                        ? Steer(steered, intent)
                        : Reject(steerFailure);
                default:
                    // Task 6 adds the orders that go through CommandableUnit.Issue.
                    return Reject(NetRejectReason.OrderRefused);
            }
        }

        NetResult Steer(CommandableUnit unit, NetMoveIntent intent)
        {
            unit.SetMoveIntent(intent.Direction);
            return NetResult.Accepted;
        }

        // The unit must exist and live (UnknownUnit), then belong to the connection (NotOwner).
        bool Own(NetConnection connection, int unitId, out CommandableUnit unit, out NetRejectReason failure)
        {
            failure = NetRejectReason.None;
            if (!units.TryGet(unitId, out unit) || !unit.IsAlive)
            {
                failure = NetRejectReason.UnknownUnit;
                return false;
            }
            if (!connection.Owns(unitId))
            {
                failure = NetRejectReason.NotOwner;
                return false;
            }
            return true;
        }

        static bool IsKnownAndFinite(NetRequest request)
        {
            switch (request)
            {
                case NetMoveIntent intent:
                    return IsFinite(intent.Direction);
                case NetMoveTo move:
                    return IsFinite(move.Destination);
                case NetAttack _:
                case NetStop _:
                    return true;
                default:
                    return false;
            }
        }

        static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static NetResult Reject(NetRejectReason reason) => NetResult.Rejected(reason);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "NetGateRejectionTests;NetProtocolTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6`
Expected: `total="24" passed="24"` (14 + 10), `EXIT=0`. If `AnAuthorisationFailure_ConsumesTheSequenceAndCommandId` or `AFreshnessFailure_ConsumesNothing` fails, the order of the checks or of `Record` is wrong: it must follow the code above exactly.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Tests/EditMode/NetGateRejectionTests.cs Assets/_Project/Tests/EditMode/NetGateRejectionTests.cs.meta
git commit -m "Net: NetConnection and the gate's checks, with move intent

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The gate's orders through `Issue`, and intent expiry

**Files:**
- Modify: `Assets/_Project/Scripts/Net/Core/ServerCommandGate.cs`
- Test: `Assets/_Project/Tests/PlayMode/NetGatePlayModeTests.cs`, `Assets/_Project/Tests/EditMode/NetGateIntentTests.cs`

**Interfaces:**
- Consumes: Task 5's `ServerCommandGate`; `CommandableUnit.Issue(UnitCommand, IssueMode)`; `MoveCommand(Vector3)`, `AttackCommand(Health)`, `StopCommand`, `IssueMode.Replace|Append`; `UnitMover.TrySnap(Vector3, out Vector3)`; `MissionRig`.
- Produces: `Handle` now executes `NetMoveTo` (Replace or Append), `NetAttack` and `NetStop` through `CommandableUnit.Issue`, returning `OrderRefused` when `Issue` returns false; `public const double IntentExpirySeconds = 0.5;` and `public void Tick(double now)`, which clears every tracked move intent not refreshed within the expiry.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/NetGatePlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The gate on a generated mission: real units, a real NavMesh, the real Issue path.</summary>
    public class NetGatePlayModeTests
    {
        MissionRig rig;
        ServerCommandGate gate;
        NetConnection owner;
        CommandableUnit friend;
        CommandableUnit enemy;
        int friendId;
        int enemyId;
        uint counter;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        IEnumerator Ready()
        {
            rig = new MissionRig();
            var slots = MissionRig.FriendlySlots();
            rig.SetFriendlySlots(new[] { slots[2], slots[0] });
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            var units = NetUnits.FromMission(rig.Director);
            gate = new ServerCommandGate(units, rig.Director.InstanceId);
            friend = rig.Director.Friendlies[0];
            enemy = rig.Director.Hostiles[0];
            friendId = units.IdOf(friend);
            enemyId = units.IdOf(enemy);
            owner = new NetConnection("account-a", new[] { friendId, units.IdOf(rig.Director.Friendlies[1]) });
            counter = 0;
        }

        NetResult Send(NetRequest request)
        {
            counter++;
            return gate.Handle(owner, new NetEnvelope(rig.Director.InstanceId, counter, counter, request), Time.realtimeSinceStartupAsDouble);
        }

        // Where the hostile stands is a point the generator proved walkable, so a move there is a legal order.
        Vector3 Walkable()
        {
            Assert.That(friend.GetComponent<UnitMover>().TrySnap(enemy.transform.position, out var point), Is.True);
            return point;
        }

        [UnityTest]
        public IEnumerator AMoveTo_IsAccepted_AndRunsThroughIssue()
        {
            yield return Ready();
            var result = Send(new NetMoveTo(friendId, Walkable()));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.None));
            Assert.That(friend.CurrentCommand, Is.InstanceOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator AnAppendedMoveTo_QueuesBehindTheCurrentOrder()
        {
            yield return Ready();
            Assert.That(Send(new NetMoveTo(friendId, Walkable())).IsAccepted, Is.True);
            Assert.That(Send(new NetMoveTo(friendId, Walkable(), append: true)).IsAccepted, Is.True);
            Assert.That(friend.PendingCommands, Has.Count.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AnAttackOnAHostile_IsAccepted_ThroughIssue()
        {
            yield return Ready();
            var result = Send(new NetAttack(friendId, enemyId));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.None));
            Assert.That(friend.CurrentCommand, Is.InstanceOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator AStop_ClearsTheOrders()
        {
            yield return Ready();
            Assert.That(Send(new NetMoveTo(friendId, Walkable())).IsAccepted, Is.True);
            Assert.That(Send(new NetStop(friendId)).IsAccepted, Is.True);
            Assert.That(friend.CurrentCommand, Is.Null);
            Assert.That(friend.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator AnUnreachableMoveTo_IsOrderRefused_AndChangesNothing()
        {
            yield return Ready();
            var commandBefore = friend.CurrentCommand;
            var pendingBefore = friend.PendingCommands.Count;
            var result = Send(new NetMoveTo(friendId, new Vector3(10000f, 0f, 10000f)));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.OrderRefused));
            Assert.That(friend.CurrentCommand, Is.SameAs(commandBefore), "the unit's own autonomy may have given it an order; the refused one changed nothing");
            Assert.That(friend.PendingCommands, Has.Count.EqualTo(pendingBefore));
        }

        [UnityTest]
        public IEnumerator AnOrderForADeadUnit_IsUnknownUnit()
        {
            yield return Ready();
            var point = Walkable();
            friend.GetComponent<Health>().TakeDamage(100000);
            var result = Send(new NetMoveTo(friendId, point));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
        }
    }
}
#endif
```

`Assets/_Project/Tests/EditMode/NetGateIntentTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class NetGateIntentTests
    {
        const string Attempt = "attempt-1";

        readonly List<GameObject> hosts = new List<GameObject>();
        CommandableUnit unit;
        int id;
        ServerCommandGate gate;
        NetConnection owner;
        uint counter;

        [SetUp]
        public void SetUp()
        {
            unit = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            var units = new NetUnits();
            id = units.Register(unit, false);
            gate = new ServerCommandGate(units, Attempt);
            owner = new NetConnection("account-a", new[] { id });
            counter = 0;
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        void Steer(Vector3 direction, double now)
        {
            counter++;
            Assert.That(gate.Handle(owner, new NetEnvelope(Attempt, counter, counter, new NetMoveIntent(id, direction)), now).IsAccepted, Is.True);
        }

        [Test]
        public void AnIntentExpiresWithoutARefresh()
        {
            Steer(Vector3.right, 10d);
            gate.Tick(10.4d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.right), "within the 0.5 s expiry");
            gate.Tick(10.6d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "past it");
        }

        [Test]
        public void ARefreshKeepsTheIntent()
        {
            Steer(Vector3.right, 10d);
            Steer(Vector3.right, 10.4d);
            gate.Tick(10.8d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.right), "0.4 s since the refresh");
            gate.Tick(11.0d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "0.6 s since the refresh");
        }

        [Test]
        public void AZeroIntentClearsAtOnce_AndTheGateStopsTrackingIt()
        {
            Steer(Vector3.right, 10d);
            Steer(Vector3.zero, 10.1d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero));
            unit.SetMoveIntent(Vector3.right);
            gate.Tick(20d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.right), "an intent the gate is not tracking is left alone");
        }

        [Test]
        public void TickIgnoresDestroyedUnits()
        {
            Steer(Vector3.right, 10d);
            Object.DestroyImmediate(unit.gameObject);
            Assert.DoesNotThrow(() => gate.Tick(11d));
        }
    }
}
```

- [ ] **Step 2: Run the intent tests to verify they fail**

Run: `Tools/run-tests.sh EditMode NetGateIntentTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6`
Expected: `EXIT=1`, `error CS1061` for `Tick` (a compile failure is the red state).

- [ ] **Step 3: Add intent tracking and `Tick`, and run the intent tests**

In `ServerCommandGate.cs` add `using System.Collections.Generic;` to the usings, then add near the top of the class (after the `attemptId` field):
```csharp
        /// <summary>A held move intent not refreshed for this long is cleared, so a client that drops cannot leave a unit walking.</summary>
        public const double IntentExpirySeconds = 0.5;

        readonly Dictionary<int, double> intentAt = new Dictionary<int, double>();
        readonly List<int> expired = new List<int>();
```
Replace `Steer` with:
```csharp
        NetResult Steer(CommandableUnit unit, NetMoveIntent intent, double now)
        {
            unit.SetMoveIntent(intent.Direction);
            var id = intent.UnitId;
            if (unit.MoveIntent == Vector3.zero)
                intentAt.Remove(id);
            else
                intentAt[id] = now;
            return NetResult.Accepted;
        }
```
Update its call: `? Steer(steered, intent, now)`.
Add after `Handle`:
```csharp
        /// <summary>Clears the move intent of every unit whose client has not refreshed it within IntentExpirySeconds. Call once per frame.</summary>
        public void Tick(double now)
        {
            expired.Clear();
            foreach (var pair in intentAt)
            {
                if (now - pair.Value > IntentExpirySeconds)
                    expired.Add(pair.Key);
            }
            foreach (var id in expired)
            {
                intentAt.Remove(id);
                if (units.TryGet(id, out var unit))
                    unit.SetMoveIntent(Vector3.zero);
            }
        }
```

Run: `Tools/run-tests.sh EditMode NetGateIntentTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6`
Expected: `total="4" passed="4"`, `EXIT=0`.

- [ ] **Step 4: Run the PlayMode file and see it fail**

Run: `Tools/run-tests.sh PlayMode NetGatePlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -12`
Expected: four tests FAIL on `Expected: None But was: OrderRefused` (the move, appended move, attack and stop), `EXIT=2`.

- [ ] **Step 5: Carry the orders out through `Issue`**

Replace the `default:` branch of the `switch` in `Process` with these cases and a default:
```csharp
                case NetMoveTo move:
                    return Own(connection, move.UnitId, out var mover, out var moveFailure)
                        ? Order(mover, new MoveCommand(move.Destination), move.Append ? IssueMode.Append : IssueMode.Replace)
                        : Reject(moveFailure);
                case NetAttack attack:
                    return Attack(connection, attack);
                case NetStop stop:
                    return Own(connection, stop.UnitId, out var stopped, out var stopFailure)
                        ? Order(stopped, new StopCommand(), IssueMode.Replace)
                        : Reject(stopFailure);
                default:
                    return Reject(NetRejectReason.Malformed);
```
Add the helpers after `Steer`:
```csharp
        // The existing command path decides whether the order can be carried out; false means OrderRefused.
        static NetResult Order(CommandableUnit unit, UnitCommand command, IssueMode mode) =>
            unit.Issue(command, mode) ? NetResult.Accepted : Reject(NetRejectReason.OrderRefused);

        // Unknown attacker or target first, then ownership, then side: an attack must name a living hostile of the attacker.
        NetResult Attack(NetConnection connection, NetAttack attack)
        {
            if (!units.TryGet(attack.UnitId, out var attacker) || !attacker.IsAlive
                || !units.TryGet(attack.TargetId, out var target) || !target.IsAlive)
                return Reject(NetRejectReason.UnknownUnit);
            if (!connection.Owns(attack.UnitId))
                return Reject(NetRejectReason.NotOwner);
            if (units.IsHostile(attack.UnitId) == units.IsHostile(attack.TargetId))
                return Reject(NetRejectReason.WrongSide);
            if (!target.TryGetComponent<Health>(out var health))
                return Reject(NetRejectReason.UnknownUnit);
            return Order(attacker, new AttackCommand(health), IssueMode.Replace);
        }
```

- [ ] **Step 6: Run all the networking tests to verify they pass**

```bash
Tools/run-tests.sh EditMode "NetGateIntentTests;NetGateRejectionTests;NetProtocolTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6
Tools/run-tests.sh PlayMode NetGatePlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6
```
Expected: EditMode `total="28" passed="28"` (4 + 14 + 10), `EXIT=0`; PlayMode `total="6" passed="6"`, `EXIT=0`. If `AnAttackOnAHostile_IsAccepted_ThroughIssue` fails with `OrderRefused`, the first friendly has no weapon: confirm the slot order in `Ready()` matches `ServerDiagnosticPlayModeTests.Ready()` (ranged first).

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Tests/PlayMode/NetGatePlayModeTests.cs Assets/_Project/Tests/PlayMode/NetGatePlayModeTests.cs.meta Assets/_Project/Tests/EditMode/NetGateIntentTests.cs Assets/_Project/Tests/EditMode/NetGateIntentTests.cs.meta
git commit -m "Net: the gate carries out moves, attacks and stops through Issue, and expires stale intent

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Share the diagnostic's controls with the networked server

**Files:**
- Create: `Assets/_Project/Scripts/Server/ServerDeterminism.cs`
- Modify: `Assets/_Project/Scripts/Server/ServerDiagnostic.cs`
- Test: `Assets/_Project/Tests/PlayMode/ServerDeterminismPlayModeTests.cs`

**Interfaces:**
- Consumes: `MissionDirector.Hostiles` / `Friendlies`, `EnemyAI`, `AutoRetaliate`, `CompanionAI`, `UnitAttacker.HitRoll` (internal `Func<float>`).
- Produces: `internal static class ServerDeterminism` with `static void Freeze(MissionDirector director, CommandableUnit keepAutonomy)` (disables `EnemyAI` and `AutoRetaliate` on every hostile, and `CompanionAI` and `AutoRetaliate` on every friendly except `keepAutonomy`, which may be null) and `static void ForceHits(CommandableUnit unit)` (sets that unit's `UnitAttacker.HitRoll` to always land). Part 2's `-netQuiet` calls `Freeze(director, null)` and `ForceHits` on each friendly. `ServerDiagnostic` behaves exactly as before.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/ServerDeterminismPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The controls the diagnostic and the networked server share to remove chance from a run (decision 045).</summary>
    public class ServerDeterminismPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        IEnumerator Ready()
        {
            rig = new MissionRig();
            var slots = MissionRig.FriendlySlots();
            rig.SetFriendlySlots(new[] { slots[2], slots[0] });
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
        }

        [UnityTest]
        public IEnumerator Freeze_WithNoUnitKept_SwitchesOffEveryAutonomy()
        {
            yield return Ready();
            ServerDeterminism.Freeze(rig.Director, null);
            foreach (var hostile in rig.Director.Hostiles)
            {
                Assert.That(hostile.GetComponent<EnemyAI>().enabled, Is.False, hostile.name);
                Assert.That(hostile.GetComponent<AutoRetaliate>().enabled, Is.False, hostile.name);
            }
            foreach (var friendly in rig.Director.Friendlies)
            {
                Assert.That(friendly.GetComponent<CompanionAI>().enabled, Is.False, friendly.name);
                Assert.That(friendly.GetComponent<AutoRetaliate>().enabled, Is.False, friendly.name);
            }
        }

        [UnityTest]
        public IEnumerator Freeze_LeavesTheKeptUnitsAutonomyAlone()
        {
            yield return Ready();
            var kept = rig.Director.Friendlies[0];
            ServerDeterminism.Freeze(rig.Director, kept);
            Assert.That(kept.GetComponent<CompanionAI>().enabled, Is.True);
            Assert.That(rig.Director.Friendlies[1].GetComponent<CompanionAI>().enabled, Is.False);
        }

        [UnityTest]
        public IEnumerator ForceHits_LandsEvenInCover_ForThatUnitOnly()
        {
            yield return Ready();
            var forced = rig.Director.Friendlies[0];
            var other = rig.Director.Friendlies[1];
            ServerDeterminism.ForceHits(forced);
            Assert.That(forced.GetComponent<UnitAttacker>().HitRoll(), Is.EqualTo(0f));
            Assert.That(other.GetComponent<UnitAttacker>().HitRoll, Is.Not.SameAs(forced.GetComponent<UnitAttacker>().HitRoll),
                "only the named unit's roll is replaced");
        }
    }
}
#endif
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode ServerDeterminismPlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6`
Expected: `EXIT=1`, `error CS0103` for `ServerDeterminism`.

- [ ] **Step 3: Implement the helper**

`Assets/_Project/Scripts/Server/ServerDeterminism.cs`:
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The controls that remove chance from a server run (decision 045): hostiles stop acting on their own, the friendlies'
    /// own autonomy stops (except for a unit that is meant to keep it), and a unit's shots always land. They change spawned
    /// instances only, never a prefab or an asset. Shared by the bounded diagnostic and the networked server's -netQuiet.
    /// </summary>
    internal static class ServerDeterminism
    {
        /// <summary>
        /// Disables EnemyAI and AutoRetaliate on every hostile, and CompanionAI and AutoRetaliate on every friendly except
        /// `keepAutonomy` (null keeps none).
        /// </summary>
        internal static void Freeze(MissionDirector director, CommandableUnit keepAutonomy)
        {
            foreach (var hostile in director.Hostiles)
            {
                Disable<EnemyAI>(hostile);
                Disable<AutoRetaliate>(hostile);
            }
            foreach (var friendly in director.Friendlies)
            {
                if (keepAutonomy != null && friendly == keepAutonomy)
                    continue;
                Disable<CompanionAI>(friendly);
                Disable<AutoRetaliate>(friendly);
            }
        }

        /// <summary>Makes this unit's shots land: the hit roll is replaced on this instance only.</summary>
        internal static void ForceHits(CommandableUnit unit) => unit.GetComponent<UnitAttacker>().HitRoll = () => 0f;

        static void Disable<T>(Component unit) where T : Behaviour
        {
            if (unit != null && unit.TryGetComponent<T>(out var component))
                component.enabled = false;
        }
    }
}
```

- [ ] **Step 4: Run the new tests to verify they pass, then switch the diagnostic over**

Run: `Tools/run-tests.sh PlayMode ServerDeterminismPlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6`
Expected: `total="3" passed="3"`, `EXIT=0`.

In `ServerDiagnostic.cs`:
- Replace the call `Freeze();` with `ServerDeterminism.Freeze(director, Mover);`.
- Replace the line `attacker.HitRoll = () => 0f;` with `ServerDeterminism.ForceHits(Mover);`.
- Delete the private methods `Freeze()` and `Disable<T>(Component unit)` (the block that begins with the comment `// Diagnostic-only: instance components, never assets; the mover keeps all of its own.` and ends before `static float Length`).
- In the class summary, keep the wording; it still describes what Prepare does.

- [ ] **Step 5: Run the diagnostic and server tests that guard the refactor**

```bash
Tools/run-tests.sh PlayMode "ServerDiagnosticPlayModeTests;ServerMissionPlayModeTests;ServerDeterminismPlayModeTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6
```
Expected: `total="15" passed="15"` (5 + 7 + 3), `EXIT=0`. `Diagnostic_FreezesOnlyTheSpawnedInstances_AndLeavesTheMoverAlone` must still pass: it is the proof the refactor changed nothing.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Server/ServerDeterminism.cs Assets/_Project/Scripts/Server/ServerDeterminism.cs.meta Assets/_Project/Scripts/Server/ServerDiagnostic.cs Assets/_Project/Tests/PlayMode/ServerDeterminismPlayModeTests.cs Assets/_Project/Tests/PlayMode/ServerDeterminismPlayModeTests.cs.meta
git commit -m "Server: share the diagnostic's determinism controls with the networked server

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Verify part 1 and hand over to part 2

**Files:** none changed (verification and report only).

**Interfaces:**
- Consumes: everything above.
- Produces: measured totals and a clean tree, the evidence part 2's plan is written from.

- [ ] **Step 1: Run both full suites**

```bash
Tools/run-tests.sh EditMode > .claude/scratchpad/networking/final-editmode.txt 2>&1; tail -3 .claude/scratchpad/networking/final-editmode.txt | sed 's/Unity requires.*behavior\.//'
Tools/run-tests.sh PlayMode > .claude/scratchpad/networking/final-playmode.txt 2>&1; tail -3 .claude/scratchpad/networking/final-playmode.txt | sed 's/Unity requires.*behavior\.//'
```
Expected: EditMode `total="1306" passed="1306"` and PlayMode `total="1376" passed="1376"`, both `EXIT=0` (the PlayMode run takes over ten minutes: run it in the background). If the baseline measured in Task 2 Step 1 differed from 1271/1367, the expected totals shift by the same amount; any other difference is a failure to investigate.

- [ ] **Step 2: Confirm the tree and the library independence**

```bash
git status --short
git log --oneline main..HEAD
grep -rl "FishNet" Assets/_Project/Scripts/Net/Core || echo "no FishNet reference in the core"
```
Expected: `git status` shows only the owner's two modified settings files and `.claude/`; the log lists the spec commit and the commits from Tasks 1 to 7 (Task 2 only if it passed); the grep prints `no FishNet reference in the core`.

- [ ] **Step 3: Report**

State: the spike's result per criterion with its evidence, the decision-046 summary (library, version, transport, whether library sync works headless, install method), the totals, the commits, and the list of what part 2 will cover. Do not start part 2: the owner reviews the evidence first.
