# Networking experiment, first slice, part 2: server adapter, test client, harness and VM acceptance (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Linux dedicated server take orders from remote clients through the part 1 gate, replicate the result back, and prove it with a headless harness run from this PC through an SSH tunnel to `bg-dev-mission01`.

**Architecture:** Almost all new logic is library-free C# in `Scripts/Net/Core/` (token file, admission, per-connection budgets, the session host that gates every message, client intent pacing) and is tested in EditMode. A thin FishNet adapter in `Scripts/Net/Adapter/` carries four request/response messages (FishNet broadcasts) and replicates each unit through a server-spawned "avatar" object (`NetworkTransform` plus `SyncVar`s). The server starts listening only when `-net` is given, on `127.0.0.1`; the new `NetTestClient` scene (capsules, Input System controls, an on-screen log) and a windowless `-netharness` mode are the clients.

**Tech Stack:** Unity 6000.3.25f1, C#, NUnit, FishNet 4.7.3 with FishyUnityTransport 2.0.0-pre.2 (WebSocket mode) on Unity Transport 2.7.4 (decision 046), Input System, Git Bash scripts, OpenSSH.

**Spec:** `Docs/superpowers/specs/2026-10-11-networking-gate-design.md` (approved 2026-10-11). Part 1 (`Docs/superpowers/plans/2026-10-11-networking-gate-part1.md`) is merged into `main` at `491e052`.

## Global Constraints

- Unity **6000.3.25f1**; the Linux server target is Linux Dedicated Server with **Mono**.
- `Scripts/Net/Core/` references **no networking library** (`NetSourceAuditTests` scans the folder and must stay green). FishNet types live only in `Scripts/Net/Adapter/` and the editor builders.
- The server listens on **`127.0.0.1` only**; clients reach it with `ssh -L` through the existing `bg-dev-mission01` alias. Nothing is opened on the VM and `ufw` is not touched.
- **One MonoBehaviour or NetworkBehaviour per file, named after its class.** A scene that references a component declared in a mismatched file kills the Linux server at start ("level0 is corrupted", decision 046).
- **No client scene, HUD, input asset or gameplay change.** The only new scene is `NetTestClient.unity`; `ServerMission.unity` gains one component (`NetServer`) through its existing builder.
- Without `-net` the server behaves exactly as in Checkpoint A (no FishNet object is created); the Checkpoint A diagnostic must still print `BLACKGLASS DIAGNOSTIC PASS` on the VM.
- **Tokens** are development fixtures: never committed, never written to a log or a warning, held in `/opt/blackglass/dev-tokens.txt` (mode 600) on the VM and passed to the Windows client on its command line from this PC only.
- Never committed: `Builds/`, build output, the token file, tunnel or harness logs, keys, credentials, and the owner's two unrelated modified files (`Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset`) and `.claude/`.
- Work stays on branch **`networking-gate-part2`** (off `main` `491e052`). Do not push or merge; the owner decides.
- Commit messages end with the attribution trailer the session's system reminder gives; at the time of writing it is `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. `git add` includes the `.meta` files Unity creates beside new files.
- Baselines on `main` `491e052`: **EditMode 1306/1306, PlayMode 1376/1377**. The one PlayMode failure is `AreaPresentationPlayModeTests.TheGeneratedExplosion_ScalesItsExtentWithTheRadius` (flaky, unseeded particles; it passes when run alone). Treat a full PlayMode run as green when that test is the only failure and it passes alone.
- Unity runs one instance per project: tests, builds and method runs happen one at a time, with the Editor closed. `Tools/run-tests.sh EditMode|PlayMode [filter]` prints a result line and `EXIT=`; filters join test or class names with `;`. EXIT 0 is all passed, 1 a compile error, 2 test failures. Read results with `... | sed 's/Unity requires.*behavior\.//'`.
- Never use `?.` or `??` on UnityEngine objects (use `== null`). Format numbers in log text with `FormattableString.Invariant`, one call per interpolated string.
- Do not wrap commands in `bash -c '...'` inside Bash tool calls: the environment's safety check rejects such wrappers even when they contain no removal.

## FishNet API facts (read from the installed package, 2026-10-12)

These are the members this plan uses. If one does not compile, read its signature in `Library/PackageCache/com.firstgeargames.fishnet@382980e7eed4/Runtime` (or the transport package), adapt the call, and ledger the change as a ruling.

| Need | Member |
|---|---|
| Build a manager at runtime | `new GameObject()` set inactive; `AddComponent` of `UnityTransport`, `TransportManager` (assign `.Transport`), `ServerManager`, `NetworkManager` (assign `.SpawnablePrefabs`); then `SetActive(true)`. `NetworkManager.Awake` creates the other managers and sets `Initialized`; everything is ready in the same frame. |
| Stop the headless auto-start | `ServerManager.SetStartOnHeadless(false)` on a `ServerManager` added **before** the `NetworkManager`; `NetworkManager.Start` calls `StartForHeadless()` otherwise (decision 046). |
| Frame cap | `ServerManager.SetFrameRate(ushort)`; FishNet re-applies `Application.targetFrameRate` when the server starts. |
| Find an existing manager without a log line | `NetworkManager.Instances` (static `IReadOnlyList<NetworkManager>`); `InstanceFinder.NetworkManager` logs "not found" when there is none. |
| Transport setup | `UnityTransport.UseWebSockets = true`; `SetConnectionData(string address, ushort port, string listenAddress = null)`; `GetServerBindAddress(IPAddressType)`. |
| Server | `ServerManager.StartConnection()` (bool), `StopConnection(bool)`, `Started`, `OnServerConnectionState(ServerConnectionStateArgs)`, `OnRemoteConnectionState(NetworkConnection, RemoteConnectionStateArgs)`, `RegisterBroadcast<T>(Action<NetworkConnection,T,Channel>, bool requireAuthentication)`, `Broadcast<T>(NetworkConnection, T, bool requireAuthenticated, Channel)`, `Spawn(GameObject, NetworkConnection owner = null, Scene scene = default)`, `Despawn(GameObject, DespawnType? = null)`. |
| Connection | `NetworkConnection.ClientId`, `Disconnect(bool immediately)`. |
| Client | `ClientManager.StartConnection()`, `StopConnection()`, `Started`, `OnClientConnectionState(ClientConnectionStateArgs)`, `RegisterBroadcast<T>(Action<T,Channel>)`, `Broadcast<T>(T, Channel)`; `NetworkManager.TimeManager.RoundTripTime` (long, ms). |
| Replication | `NetworkBehaviour` (`FishNet.Object`); `private readonly SyncVar<T> x = new SyncVar<T>();` (`FishNet.Object.Synchronizing`), `.Value`, `.OnChange`; `NetworkTransform` (`FishNet.Component.Transforming`). Prefabs with a `NetworkObject` are added to `Assets/DefaultPrefabObjects.asset` by FishNet's asset postprocessor when imported. |
| Namespaces | `FishNet.Managing`, `.Managing.Server`, `.Managing.Client`, `.Managing.Transporting`, `.Managing.Object`, `.Connection`, `.Transporting`, `.Transporting.UTP`, `.Broadcast`. |

## Review Focus

The spec implies these conditions but no task's main tests exercise them. Each is pinned by a named test.

1. **A client that never says hello, or sends requests before hello,** must get nothing processed and must be disconnected after 5 s. (`NetSessionHostTests.ARequestBeforeHello_IsNotAuthenticated_AndChangesNothing`, `AClientThatNeverSaysHello_ExpiresAfterTheTimeout`.)
2. **A request flood** must be dropped, the flooding connection disconnected, and other connections unaffected. (`NetSessionHostTests.AFlood_IsDropped_ThenTheClientIsDisconnected`, `AFloodingClient_DoesNotStarveAnother`, `RejectedRequests_CountTowardsAbuse`.)
3. **The same token connecting twice:** the second connection is refused and the first is unaffected; the token is reusable after the first leaves. (`NetAdmissionTests.ASecondClientWithTheSameToken_IsRefused_AndTheFirstIsUnaffected`, `ReleasingAClient_FreesItsToken`.)
4. **A malformed token file, and tokens in logs:** bad lines are skipped without echoing a token, and no token ever appears in a warning or a log line. (`NetTokenFileTests.BadLines_AreSkipped_AndNeverEchoTheToken`, `NetLoopbackPlayModeTests.NoTokenIsEverLogged`.)
5. **`-net` without a usable token file** must fail startup with exit code 2 and never start an open server. (`NetServerPlayModeTests.ANetServerWithoutATokenFile_ExitsTwo_AndDoesNotListen`.)

## Rulings made while planning

Each is open to the owner; the cost if wrong is stated.

1. **The `NetworkManager` is created at runtime, not placed in `ServerMission.unity`.** A scene object would exist (and run FishNet) in every server start, would persist across PlayMode tests (`DontDestroyOnLoad`), and spec section 3.4's "added to the scene" would put FishNet inside the Checkpoint A path. `NetServer`, which is in the scene, builds the manager only when `-net` is given. Cost if wrong: a later slice that wants the manager in the scene moves one factory call.
2. **Our own hello/welcome handshake with the development tokens**, over FishNet broadcasts, instead of FishNet's authenticator. It keeps the admission rules (unknown token, attempt mismatch, second connection) in plain C# that EditMode can test. Cost if wrong: swapping to an authenticator later replaces `NetSessionHost.Hello`'s caller only.
3. **Replication uses server-spawned avatar objects** (`NetworkTransform` plus `SyncVar`s in `NetUnitState`), not the simulation units themselves. The units keep their NavMeshAgents, AI and colliders untouched, and the client builds its own visuals. Cost if wrong: part of the avatar sync moves if a later slice replicates the real units.
4. **Interpolation is `NetworkTransform`'s own**, not the separate 100 ms client buffer of spec section 3.5. One less moving part; the spike showed smooth sync. Cost if wrong: add a buffer in `NetClientView` later.
5. **The logic that decides what happens to every message lives in `NetSessionHost` (Core)**, so the spec's test list for the adapter is mostly EditMode tests of plain classes. Cost if wrong: none; the adapter tests are thinner.
6. **Limits** (carried forward from the part 1 review): 60 requests per second per connection with a burst of 60; a connection with more than 200 dropped or rejected messages in 10 s is disconnected; a connection that does not say hello within 5 s is disconnected; a second connection with a token already in use is refused. Cost if wrong: constants in `NetSessionHost`.
7. **`-net` together with `-diagnostic` ignores `-net`** and logs a warning, so the diagnostic stays bounded. Cost if wrong: one condition.
8. **A `squad` token owns friendlies `1..N`** (`NetUnits.FromMission` registers friendlies first), so the welcome message carries `FirstOwnedId` and `OwnedCount` instead of an array. Cost if wrong: the message gains a list.
9. **In-process loopback is FishNet host mode** (one manager, the client connecting to the server it shares). It proves the handshake, the gate, the budgets and the result path; replication across a real network is proven only by the VM run. If host mode cannot work in the Editor (Task 8, Step 3), stop and amend the spec as spec section 4 says.
10. **The harness has four roles:** `a` (owner), `b` (no ownership), `ticket` (a token bound to another attempt) and `badtoken`. The attack step needs the server's `-netQuiet`; the VM script always starts the server that way.
11. **Latency (A2)** is measured as the median of up to 10 cycles from the first steering message to the first replicated movement of 0.05 m, and reported next to the client's measured round-trip time. The 150 ms target is a LAN figure; through the tunnel the result is reported as measured.
12. **`smoke-run.sh` gains an optional `SMOKE_ARGS` environment variable** so the 10-minute idle run can be repeated with `-net`. It is empty by default, so Checkpoint A's usage is unchanged.
13. **Not in this part:** A1's container, hidden information (A5, A6), reconnect (A7), settlement (A8), the three deferred A3 cases, a real geometry stream, and the part 1 deferred minors (the `Attack` Health-lookup order, the audit token list, dead-unit `NetAttack` tests, the decision 046 timing phrase).

## File structure

Create (all under `Assets/_Project/` unless stated):
- Core, library-free (`Scripts/Net/Core/`): `NetTokenFile.cs` (token parsing), `NetBudget.cs`, `NetAdmission.cs` (with `NetRefusal`), `NetSessionHost.cs` (with `NetDisposition`, `NetOutcome`), `NetOrder.cs` (with `NetOrderKind`), `NetIntentSender.cs`, `NetClientOptions.cs`.
- Adapter (`Scripts/Net/Adapter/`): `NetMessages.cs` (five broadcast structs), `NetWire.cs`, `NetManagerFactory.cs`, `NetUnitState.cs`, `NetAvatars.cs`, `NetServer.cs`, `NetClient.cs`, `NetHarness.cs`, `NetClientView.cs`, `NetTestControls.cs`, `NetClientBootstrap.cs`.
- Editor (`Editor/Net/`): `NetAssetBuilder.cs` (avatar prefab), `NetClientSceneBuilder.cs`, `NetClientBuild.cs`.
- Assets: `Prefabs/Net/NetUnitAvatar.prefab`, `Scenes/NetTestClient.unity` (both generated and committed).
- Tests: `Tests/EditMode/` `NetTokenFileTests.cs`, `NetBudgetTests.cs`, `NetAdmissionTests.cs`, `NetSessionHostTests.cs`, `NetOrderTests.cs`, `NetIntentSenderTests.cs`, `NetWireTests.cs`, `NetAvatarPrefabTests.cs`, `NetClientOptionsTests.cs`; `Tests/PlayMode/` `NetManagerFactoryPlayModeTests.cs`, `NetServerPlayModeTests.cs`, `NetLoopbackPlayModeTests.cs`, `NetHarnessPlayModeTests.cs`, `NetClientScenePlayModeTests.cs`.
- Scripts: `Tools/LinuxServer/run-net.sh`, `Tools/LinuxServer/make-dev-tokens.sh`, `Tools/run-net-harness.sh`, `Tools/build-netclient.sh`.

Modify: `Scripts/Blackglass.asmdef`, both test `.asmdef` files, `Scripts/Server/ServerOptions.cs` (`With`), `Scripts/Server/ServerBootstrap.cs`, `Editor/Server/ServerSceneBuilder.cs`, `Scenes/ServerMission.unity` (regenerated), `Tests/PlayMode/ServerMissionPlayModeTests.cs` (allowed types), `Tools/LinuxServer/smoke-run.sh`, `Docs/Decisions.md`, `Docs/OnlineReadinessAudit.md`, `Docs/DedicatedServer.md`, `.gitattributes` (only if a new script needs it).

Expected totals after Task 12: **EditMode 1366** (1306 + 60) and **PlayMode 1403** (1377 + 26; the known flaky test may fail, see Task 12, Step 1).

---
### Task 1: Token file parser and per-connection budget (Core)

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetTokenFile.cs`, `NetBudget.cs`
- Test: `Assets/_Project/Tests/EditMode/NetTokenFileTests.cs`, `NetBudgetTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `sealed class NetTokenEntry` with get-only `string Account`, `string Token`, `bool OwnsSquad`, `string Attempt` (null when the line names none); constructor `NetTokenEntry(string account, string token, bool ownsSquad, string attempt)`.
  - `static class NetTokenFile` with `static List<NetTokenEntry> Parse(string text, out List<string> warnings)`. One entry per line: `<account> <token> <squad|none> [attempt]`, fields separated by spaces or tabs; blank lines and lines starting with `#` are ignored; CRLF is accepted. A bad line is skipped with a warning that names the line number and never contains a token.
  - `sealed class NetBudget`: `NetBudget(double requestsPerSecond, int burst, int abuseLimit, double abuseWindowSeconds)`; `bool TryTake(double now)` (a token bucket, true when a request may proceed); `bool NoteRefusal(double now)` (records a dropped or rejected message and returns true when more than `abuseLimit` of them fall inside `abuseWindowSeconds`). `now` is real time in seconds.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetTokenFileTests.cs`:
```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetTokenFileTests
    {
        [Test]
        public void AValidFile_GivesOneEntryPerLine()
        {
            var text = "# development tokens\r\naccount-a tok-a squad\r\n\r\naccount-b tok-b none\r\naccount-t tok-t none other-attempt\r\n";
            var entries = NetTokenFile.Parse(text, out var warnings);

            Assert.That(warnings, Is.Empty);
            Assert.That(entries, Has.Count.EqualTo(3));
            Assert.That(entries[0].Account, Is.EqualTo("account-a"));
            Assert.That(entries[0].Token, Is.EqualTo("tok-a"));
            Assert.That(entries[0].OwnsSquad, Is.True);
            Assert.That(entries[0].Attempt, Is.Null);
            Assert.That(entries[1].OwnsSquad, Is.False);
            Assert.That(entries[2].Attempt, Is.EqualTo("other-attempt"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   \n# only a comment\n")]
        public void NoEntries_GiveAnEmptyList(string text)
        {
            var entries = NetTokenFile.Parse(text, out var warnings);
            Assert.That(entries, Is.Empty);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void BadLines_AreSkipped_AndNeverEchoTheToken()
        {
            const string secret = "s3cr3t-token-value";
            var text = "only-two " + secret + "\n" +
                       "acct " + secret + "-b maybe\n" +
                       "a b c d e\n" +
                       "ok tok-ok squad\n";
            var entries = NetTokenFile.Parse(text, out var warnings);

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Token, Is.EqualTo("tok-ok"));
            Assert.That(warnings, Has.Count.EqualTo(3));
            foreach (var warning in warnings)
            {
                StringAssert.DoesNotContain(secret, warning);
                StringAssert.DoesNotContain("tok-ok", warning);
                StringAssert.StartsWith("line ", warning);
            }
        }

        [Test]
        public void ADuplicateToken_IsSkippedWithAWarning()
        {
            var entries = NetTokenFile.Parse("a dup-7f3a squad\nb dup-7f3a none\n", out var warnings);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Account, Is.EqualTo("a"));
            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.DoesNotContain("dup-7f3a", warnings[0]);
        }

        [Test]
        public void TabsAndExtraSpaces_SeparateFields()
        {
            var entries = NetTokenFile.Parse("a\t tok-a \t squad\n", out var warnings);
            Assert.That(warnings, Is.Empty);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Token, Is.EqualTo("tok-a"));
        }
    }
}
```

`Assets/_Project/Tests/EditMode/NetBudgetTests.cs`:
```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetBudgetTests
    {
        [Test]
        public void TheBurst_IsAllowed_ThenRequestsAreDropped()
        {
            var budget = new NetBudget(requestsPerSecond: 10, burst: 5, abuseLimit: 100, abuseWindowSeconds: 10);
            for (var i = 0; i < 5; i++)
                Assert.That(budget.TryTake(0d), Is.True, "request " + i);
            Assert.That(budget.TryTake(0d), Is.False);
        }

        [Test]
        public void Tokens_RefillWithTime()
        {
            var budget = new NetBudget(10, 5, 100, 10);
            for (var i = 0; i < 5; i++)
                budget.TryTake(0d);
            for (var i = 0; i < 3; i++)
                Assert.That(budget.TryTake(0.3d), Is.True, "refilled " + i);
            Assert.That(budget.TryTake(0.3d), Is.False);
        }

        [Test]
        public void TheBucketNeverExceedsTheBurst()
        {
            var budget = new NetBudget(10, 5, 100, 10);
            budget.TryTake(0d);
            for (var i = 0; i < 5; i++)
                Assert.That(budget.TryTake(100d), Is.True, "request " + i);
            Assert.That(budget.TryTake(100d), Is.False);
        }

        [Test]
        public void AbuseIsCountedInAWindow()
        {
            var budget = new NetBudget(10, 5, abuseLimit: 3, abuseWindowSeconds: 10);
            Assert.That(budget.NoteRefusal(0d), Is.False);
            Assert.That(budget.NoteRefusal(1d), Is.False);
            Assert.That(budget.NoteRefusal(2d), Is.False);
            Assert.That(budget.NoteRefusal(3d), Is.True, "the fourth inside the window passes the limit of 3");
        }

        [Test]
        public void OldRefusalsLeaveTheWindow()
        {
            var budget = new NetBudget(10, 5, abuseLimit: 2, abuseWindowSeconds: 10);
            budget.NoteRefusal(0d);
            budget.NoteRefusal(1d);
            Assert.That(budget.NoteRefusal(20d), Is.False, "the first two are older than the window");
            Assert.That(budget.NoteRefusal(21d), Is.False);
            Assert.That(budget.NoteRefusal(22d), Is.True);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode "NetTokenFileTests;NetBudgetTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6`
Expected: `EXIT=1` with `error CS0246` for `NetTokenFile` and `NetBudget`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Net/Core/NetTokenFile.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>One line of the development token file: an account, its token, whether it owns the squad, and an optional attempt id.</summary>
    public sealed class NetTokenEntry
    {
        public NetTokenEntry(string account, string token, bool ownsSquad, string attempt)
        {
            Account = account;
            Token = token;
            OwnsSquad = ownsSquad;
            Attempt = attempt;
        }

        public string Account { get; }
        public string Token { get; }
        /// <summary>True for a `squad` entry: the account owns the mission's friendlies.</summary>
        public bool OwnsSquad { get; }
        /// <summary>When set, the entry is only valid for a mission whose instance id equals this value (null: any).</summary>
        public string Attempt { get; }
    }

    /// <summary>
    /// Parser for the development token file (networking experiment, spec section 3.4): one line per account,
    /// `account token squad|none [attempt]`, separated by spaces or tabs. Blank lines and `#` comments are ignored. The tokens
    /// are fixtures, not an identity scheme; a warning names the line and never contains a token.
    /// </summary>
    public static class NetTokenFile
    {
        public static List<NetTokenEntry> Parse(string text, out List<string> warnings)
        {
            var entries = new List<NetTokenEntry>();
            warnings = new List<string>();
            if (string.IsNullOrEmpty(text))
                return entries;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var number = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3 || fields.Length > 4)
                {
                    warnings.Add("line " + number + ": expected '<account> <token> <squad|none> [attempt]'; skipped");
                    continue;
                }
                bool squad;
                if (fields[2] == "squad")
                    squad = true;
                else if (fields[2] == "none")
                    squad = false;
                else
                {
                    warnings.Add("line " + number + ": the third field must be 'squad' or 'none'; skipped");
                    continue;
                }
                if (!seen.Add(fields[1]))
                {
                    warnings.Add("line " + number + ": this token is already used by an earlier line; skipped");
                    continue;
                }
                entries.Add(new NetTokenEntry(fields[0], fields[1], squad, fields.Length == 4 ? fields[3] : null));
            }
            return entries;
        }
    }
}
```

`Assets/_Project/Scripts/Net/Core/NetBudget.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// One connection's allowance (networking experiment, decision 046): a token bucket that drops requests beyond a steady
    /// rate plus a burst, and a sliding count of dropped or rejected messages that tells the host when a connection is abusive.
    /// Pure: the caller passes real time in seconds.
    /// </summary>
    public sealed class NetBudget
    {
        readonly double perSecond;
        readonly double burst;
        readonly int abuseLimit;
        readonly double abuseWindow;
        readonly Queue<double> refusals = new Queue<double>();
        double tokens;
        double last;
        bool started;

        public NetBudget(double requestsPerSecond, int burst, int abuseLimit, double abuseWindowSeconds)
        {
            perSecond = requestsPerSecond;
            this.burst = burst;
            this.abuseLimit = abuseLimit;
            abuseWindow = abuseWindowSeconds;
            tokens = burst;
        }

        /// <summary>True when this request may proceed; false when the connection is over its rate and the request is dropped.</summary>
        public bool TryTake(double now)
        {
            if (started)
                tokens = Math.Min(burst, tokens + Math.Max(0d, now - last) * perSecond);
            started = true;
            last = now;
            if (tokens < 1d)
                return false;
            tokens -= 1d;
            return true;
        }

        /// <summary>Records one dropped or rejected message. True when more than the limit fall inside the window: disconnect.</summary>
        public bool NoteRefusal(double now)
        {
            refusals.Enqueue(now);
            while (refusals.Count > 0 && now - refusals.Peek() > abuseWindow)
                refusals.Dequeue();
            return refusals.Count > abuseLimit;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "NetTokenFileTests;NetBudgetTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="13" passed="13"` (7 + 5 + the existing audit test), `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Tests/EditMode/NetTokenFileTests.cs Assets/_Project/Tests/EditMode/NetTokenFileTests.cs.meta Assets/_Project/Tests/EditMode/NetBudgetTests.cs Assets/_Project/Tests/EditMode/NetBudgetTests.cs.meta
git commit -m "Net: token file parser and per-connection budget

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Admission (Core)

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetAdmission.cs`
- Test: `Assets/_Project/Tests/EditMode/NetAdmissionTests.cs`

**Interfaces:**
- Consumes: Task 1's `NetTokenEntry`; part 1's `NetConnection(string account, IEnumerable<int> ownedUnitIds)`.
- Produces:
  - `enum NetRefusal : byte { None = 0, UnknownToken = 1, WrongAttempt = 2, AlreadyConnected = 3 }`.
  - `sealed class NetAdmission`: constructor `NetAdmission(IEnumerable<NetTokenEntry> entries, string attemptId, int friendlyCount)`; `string AttemptId`; `int Admitted`; `NetRefusal TryAdmit(int clientId, string token, out NetConnection connection, out int firstOwnedId, out int ownedCount)` (checks in this order: unknown or empty token, an entry bound to another attempt, a token or client already admitted; a `squad` entry owns ids `1..friendlyCount` with `firstOwnedId` 1, a `none` entry owns nothing with `ownedCount` 0; `connection` is null unless admitted); `void Release(int clientId)` (frees the client's token; unknown clients are ignored).

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetAdmissionTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetAdmissionTests
    {
        const string Attempt = "attempt-1";

        static NetAdmission Make() => new NetAdmission(new List<NetTokenEntry>
        {
            new NetTokenEntry("account-a", "tok-a", true, null),
            new NetTokenEntry("account-b", "tok-b", false, null),
            new NetTokenEntry("account-t", "tok-t", false, "other-attempt"),
            new NetTokenEntry("account-s", "tok-s", true, Attempt),
        }, Attempt, friendlyCount: 2);

        [Test]
        public void ASquadToken_OwnsEveryFriendly()
        {
            var admission = Make();
            var refusal = admission.TryAdmit(1, "tok-a", out var connection, out var first, out var count);
            Assert.That(refusal, Is.EqualTo(NetRefusal.None));
            Assert.That(first, Is.EqualTo(1));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(connection.Account, Is.EqualTo("account-a"));
            Assert.That(connection.Owns(1), Is.True);
            Assert.That(connection.Owns(2), Is.True);
            Assert.That(connection.Owns(3), Is.False, "the hostile is nobody's");
            Assert.That(admission.Admitted, Is.EqualTo(1));
        }

        [Test]
        public void ANoneToken_OwnsNothing()
        {
            var refusal = Make().TryAdmit(1, "tok-b", out var connection, out var first, out var count);
            Assert.That(refusal, Is.EqualTo(NetRefusal.None));
            Assert.That(count, Is.EqualTo(0));
            Assert.That(connection.Owns(1), Is.False);
        }

        [TestCase("nope")]
        [TestCase("")]
        [TestCase(null)]
        public void AnUnknownOrEmptyToken_IsRefused(string token)
        {
            var admission = Make();
            var refusal = admission.TryAdmit(1, token, out var connection, out _, out _);
            Assert.That(refusal, Is.EqualTo(NetRefusal.UnknownToken));
            Assert.That(connection, Is.Null);
            Assert.That(admission.Admitted, Is.EqualTo(0));
        }

        [Test]
        public void AnEntryBoundToAnotherAttempt_IsRefused()
        {
            var refusal = Make().TryAdmit(1, "tok-t", out var connection, out _, out _);
            Assert.That(refusal, Is.EqualTo(NetRefusal.WrongAttempt));
            Assert.That(connection, Is.Null);
        }

        [Test]
        public void AnEntryBoundToThisAttempt_IsAdmitted()
        {
            Assert.That(Make().TryAdmit(1, "tok-s", out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void ASecondClientWithTheSameToken_IsRefused_AndTheFirstIsUnaffected()
        {
            var admission = Make();
            admission.TryAdmit(1, "tok-a", out var first, out _, out _);
            var refusal = admission.TryAdmit(2, "tok-a", out var second, out _, out _);
            Assert.That(refusal, Is.EqualTo(NetRefusal.AlreadyConnected));
            Assert.That(second, Is.Null);
            Assert.That(admission.Admitted, Is.EqualTo(1));
            Assert.That(first.Owns(1), Is.True);
        }

        [Test]
        public void ReleasingAClient_FreesItsToken()
        {
            var admission = Make();
            admission.TryAdmit(1, "tok-a", out _, out _, out _);
            admission.Release(1);
            Assert.That(admission.Admitted, Is.EqualTo(0));
            Assert.That(admission.TryAdmit(2, "tok-a", out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void ReleasingAnUnknownClient_IsHarmless()
        {
            var admission = Make();
            admission.TryAdmit(1, "tok-a", out _, out _, out _);
            Assert.DoesNotThrow(() => admission.Release(99));
            Assert.That(admission.Admitted, Is.EqualTo(1));
        }

        [Test]
        public void TheSameClientAdmittedTwice_IsRefused()
        {
            var admission = Make();
            admission.TryAdmit(1, "tok-a", out _, out _, out _);
            Assert.That(admission.TryAdmit(1, "tok-b", out _, out _, out _), Is.EqualTo(NetRefusal.AlreadyConnected));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode NetAdmissionTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS0246` for `NetAdmission` and `NetRefusal`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Net/Core/NetAdmission.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Blackglass
{
    /// <summary>Why a hello was refused (the wire carries this byte in NetRefusedMessage).</summary>
    public enum NetRefusal : byte
    {
        None = 0,
        UnknownToken = 1,
        /// <summary>The token's entry names another mission attempt (a ticket for another attempt).</summary>
        WrongAttempt = 2,
        /// <summary>The token (or the client) is already admitted.</summary>
        AlreadyConnected = 3,
    }

    /// <summary>
    /// Decides who may connect (networking experiment, spec section 3.4): a hello token must match a token-file entry, the entry
    /// must not be bound to another attempt, and a token has at most one connection at a time. A `squad` entry owns friendlies
    /// 1..friendlyCount (NetUnits registers friendlies first); a `none` entry owns nothing. Holds no connection objects.
    /// </summary>
    public sealed class NetAdmission
    {
        readonly Dictionary<string, NetTokenEntry> byToken;
        readonly int friendlyCount;
        readonly Dictionary<int, string> tokenByClient = new Dictionary<int, string>();
        readonly HashSet<string> inUse = new HashSet<string>(StringComparer.Ordinal);

        public NetAdmission(IEnumerable<NetTokenEntry> entries, string attemptId, int friendlyCount)
        {
            AttemptId = attemptId ?? throw new ArgumentNullException(nameof(attemptId));
            this.friendlyCount = friendlyCount;
            byToken = entries.ToDictionary(e => e.Token, StringComparer.Ordinal);
        }

        public string AttemptId { get; }

        /// <summary>How many clients are admitted right now.</summary>
        public int Admitted => tokenByClient.Count;

        public NetRefusal TryAdmit(int clientId, string token, out NetConnection connection, out int firstOwnedId, out int ownedCount)
        {
            connection = null;
            firstOwnedId = 0;
            ownedCount = 0;
            if (string.IsNullOrEmpty(token) || !byToken.TryGetValue(token, out var entry))
                return NetRefusal.UnknownToken;
            if (entry.Attempt != null && !string.Equals(entry.Attempt, AttemptId, StringComparison.Ordinal))
                return NetRefusal.WrongAttempt;
            if (tokenByClient.ContainsKey(clientId) || inUse.Contains(token))
                return NetRefusal.AlreadyConnected;

            tokenByClient[clientId] = token;
            inUse.Add(token);
            ownedCount = entry.OwnsSquad ? friendlyCount : 0;
            firstOwnedId = ownedCount > 0 ? 1 : 0;
            connection = new NetConnection(entry.Account, Enumerable.Range(firstOwnedId, ownedCount));
            return NetRefusal.None;
        }

        /// <summary>The client left: its token can be used again. Unknown clients are ignored.</summary>
        public void Release(int clientId)
        {
            if (!tokenByClient.TryGetValue(clientId, out var token))
                return;
            tokenByClient.Remove(clientId);
            inUse.Remove(token);
        }
    }
}
```
Note: `Enumerable.Range(0, 0)` is an empty sequence (valid), so a `none` entry gets an empty owned set.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "NetAdmissionTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="13" passed="13"` (11 + the audit), `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Tests/EditMode/NetAdmissionTests.cs Assets/_Project/Tests/EditMode/NetAdmissionTests.cs.meta
git commit -m "Net: admission by development token, one connection per token

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Session host (Core)

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetSessionHost.cs`
- Test: `Assets/_Project/Tests/EditMode/NetSessionHostTests.cs`

**Interfaces:**
- Consumes: part 1's `ServerCommandGate.Handle(NetConnection, NetEnvelope, double)`, `NetResult`; Task 1's `NetBudget`; Task 2's `NetAdmission`, `NetRefusal`.
- Produces:
  - `enum NetDisposition { NotAuthenticated, OverBudget, Handled, Disconnect }`; `readonly struct NetOutcome` with `NetDisposition Disposition` and `NetResult Result` (valid for `Handled`, and for `Disconnect` when the gate had answered).
  - `sealed class NetSessionHost`: constants `HelloTimeoutSeconds = 5d`, `RequestsPerSecond = 60d`, `Burst = 60`, `AbuseLimit = 200`, `AbuseWindowSeconds = 10d`; constructor `NetSessionHost(ServerCommandGate gate, NetAdmission admission)`; `void Connected(int clientId, double now)`; `NetRefusal Hello(int clientId, string token, double now, out string attemptId, out int firstOwnedId, out int ownedCount)`; `NetOutcome Request(int clientId, NetEnvelope envelope, double now)`; `void Disconnected(int clientId)`; `void CollectExpired(double now, List<int> into)` (adds, and forgets, clients that have not said hello within the timeout); `string AccountOf(int clientId)` (null when not admitted); counters `int Accepted`, `int Rejected`, `int Dropped`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetSessionHostTests.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetSessionHostTests
    {
        const string Attempt = "attempt-1";

        readonly List<GameObject> hosts = new List<GameObject>();
        CommandableUnit friendA;
        NetSessionHost host;
        uint counter;

        [SetUp]
        public void SetUp()
        {
            friendA = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            var friendB = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            var hostile = NetTestKit.MakeUnit(hosts, "HostileUnit_1");
            var units = NetUnits.FromMission(new List<CommandableUnit> { friendA, friendB }, new List<CommandableUnit> { hostile });
            var gate = new ServerCommandGate(units, Attempt);
            var admission = new NetAdmission(new List<NetTokenEntry>
            {
                new NetTokenEntry("account-a", "tok-a", true, null),
                new NetTokenEntry("account-b", "tok-b", false, null),
                new NetTokenEntry("account-c", "tok-c", true, null),
            }, Attempt, friendlyCount: 2);
            host = new NetSessionHost(gate, admission);
            counter = 0;
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        NetEnvelope Steer(int unitId = 1)
        {
            counter++;
            return new NetEnvelope(Attempt, counter, counter, new NetMoveIntent(unitId, Vector3.right));
        }

        NetEnvelope BadStop()
        {
            counter++;
            return new NetEnvelope(Attempt, counter, counter, new NetStop(1));
        }

        void Join(int clientId, string token, double now = 0d)
        {
            host.Connected(clientId, now);
            Assert.That(host.Hello(clientId, token, now, out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void ARequestBeforeHello_IsNotAuthenticated_AndChangesNothing()
        {
            host.Connected(1, 0d);
            var outcome = host.Request(1, Steer(), 0d);
            Assert.That(outcome.Disposition, Is.EqualTo(NetDisposition.NotAuthenticated));
            Assert.That(friendA.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(host.Accepted, Is.EqualTo(0));
        }

        [Test]
        public void AWelcomedClient_CanSteer()
        {
            host.Connected(1, 0d);
            var refusal = host.Hello(1, "tok-a", 0d, out var attempt, out var first, out var count);
            Assert.That(refusal, Is.EqualTo(NetRefusal.None));
            Assert.That(attempt, Is.EqualTo(Attempt));
            Assert.That(first, Is.EqualTo(1));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(host.AccountOf(1), Is.EqualTo("account-a"));

            var outcome = host.Request(1, Steer(), 0d);
            Assert.That(outcome.Disposition, Is.EqualTo(NetDisposition.Handled));
            Assert.That(outcome.Result.IsAccepted, Is.True);
            Assert.That((friendA.MoveIntent - Vector3.right).magnitude, Is.LessThan(1e-4f));
        }

        [Test]
        public void ARefusedHello_LeavesTheClientUnauthenticated()
        {
            host.Connected(1, 0d);
            Assert.That(host.Hello(1, "nope", 0d, out _, out _, out _), Is.EqualTo(NetRefusal.UnknownToken));
            Assert.That(host.AccountOf(1), Is.Null);
            Assert.That(host.Request(1, Steer(), 0d).Disposition, Is.EqualTo(NetDisposition.NotAuthenticated));
        }

        [Test]
        public void AClientThatNeverSaysHello_ExpiresAfterTheTimeout()
        {
            host.Connected(1, 10d);
            host.Connected(2, 10d);
            host.Hello(2, "tok-b", 11d, out _, out _, out _);
            var expired = new List<int>();

            host.CollectExpired(14.9d, expired);
            Assert.That(expired, Is.Empty, "inside the 5 s timeout");

            host.CollectExpired(15.1d, expired);
            Assert.That(expired, Is.EqualTo(new[] { 1 }), "client 2 said hello in time");

            expired.Clear();
            host.CollectExpired(20d, expired);
            Assert.That(expired, Is.Empty, "an expired client is forgotten");
        }

        [Test]
        public void AFlood_IsDropped_ThenTheClientIsDisconnected()
        {
            Join(1, "tok-a");
            var dispositions = new List<NetDisposition>();
            for (var i = 0; i < 1000 && (dispositions.Count == 0 || dispositions.Last() != NetDisposition.Disconnect); i++)
                dispositions.Add(host.Request(1, Steer(), 0d).Disposition);

            Assert.That(dispositions.Take(NetSessionHost.Burst).All(d => d == NetDisposition.Handled), Is.True, "the burst is served");
            Assert.That(dispositions.Skip(NetSessionHost.Burst).Take(NetSessionHost.AbuseLimit).All(d => d == NetDisposition.OverBudget), Is.True,
                "then requests are dropped");
            Assert.That(dispositions.Last(), Is.EqualTo(NetDisposition.Disconnect));
            Assert.That(dispositions, Has.Count.EqualTo(NetSessionHost.Burst + NetSessionHost.AbuseLimit + 1));
            Assert.That(host.Dropped, Is.EqualTo(NetSessionHost.AbuseLimit + 1));
        }

        [Test]
        public void AFloodingClient_DoesNotStarveAnother()
        {
            Join(1, "tok-a");
            Join(2, "tok-c");
            for (var i = 0; i < 400; i++)
                host.Request(1, Steer(), 0d);

            Assert.That(host.Request(2, Steer(), 0d).Disposition, Is.EqualTo(NetDisposition.Handled));
        }

        [Test]
        public void RejectedRequests_CountTowardsAbuse()
        {
            Join(1, "tok-b");
            NetOutcome? first = null;
            var sent = 0;
            for (var second = 0; second < 5 && first == null; second++)
            {
                for (var i = 0; i < 60 && first == null; i++)
                {
                    var outcome = host.Request(1, BadStop(), second);
                    sent++;
                    if (outcome.Disposition == NetDisposition.Disconnect)
                        first = outcome;
                    else
                        Assert.That(outcome.Disposition, Is.EqualTo(NetDisposition.Handled), "request " + sent);
                }
            }

            Assert.That(first.HasValue, Is.True);
            Assert.That(sent, Is.EqualTo(NetSessionHost.AbuseLimit + 1), "the 201st rejection inside 10 s ends the connection");
            Assert.That(first.Value.Result.Reason, Is.EqualTo(NetRejectReason.NotOwner));
        }

        [Test]
        public void ADisconnectedClients_TokenCanBeUsedAgain()
        {
            Join(1, "tok-a");
            host.Disconnected(1);
            host.Connected(2, 1d);
            Assert.That(host.Hello(2, "tok-a", 1d, out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void TheCounters_FollowTheOutcomes()
        {
            Join(1, "tok-b");
            host.Request(1, Steer(), 0d);
            host.Request(1, BadStop(), 0d);
            Assert.That(host.Accepted, Is.EqualTo(0));
            Assert.That(host.Rejected, Is.EqualTo(2), "tok-b owns nothing, so both are refused");

            Join(2, "tok-a");
            host.Request(2, Steer(), 0d);
            Assert.That(host.Accepted, Is.EqualTo(1));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode NetSessionHostTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS0246` for `NetSessionHost`, `NetDisposition` and `NetOutcome`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Net/Core/NetSessionHost.cs`:
```csharp
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>What the host did with one message.</summary>
    public enum NetDisposition
    {
        /// <summary>The client has not said hello (or was refused): the message is ignored and not answered.</summary>
        NotAuthenticated,
        /// <summary>The client is over its rate: the message is dropped and not answered.</summary>
        OverBudget,
        /// <summary>The gate decided; Result says whether it was accepted or why not.</summary>
        Handled,
        /// <summary>The client has become abusive: the adapter must disconnect it.</summary>
        Disconnect,
    }

    public readonly struct NetOutcome
    {
        public NetOutcome(NetDisposition disposition, NetResult result)
        {
            Disposition = disposition;
            Result = result;
        }

        public NetDisposition Disposition { get; }
        public NetResult Result { get; }
    }

    /// <summary>
    /// Everything the server decides about the messages of its connected clients, with no networking library in sight
    /// (networking experiment, decision 046): who may join (NetAdmission), that nothing is processed before hello, that a client
    /// that never says hello is dropped after a few seconds, each client's request budget, and when a client is abusive. The
    /// request itself goes to ServerCommandGate. The FishNet adapter only forwards messages here and carries out the answer.
    /// </summary>
    public sealed class NetSessionHost
    {
        public const double HelloTimeoutSeconds = 5d;
        public const double RequestsPerSecond = 60d;
        public const int Burst = 60;
        public const int AbuseLimit = 200;
        public const double AbuseWindowSeconds = 10d;

        sealed class Session
        {
            public double ConnectedAt;
            public NetConnection Connection;
            public NetBudget Budget;
        }

        readonly ServerCommandGate gate;
        readonly NetAdmission admission;
        readonly Dictionary<int, Session> sessions = new Dictionary<int, Session>();

        public NetSessionHost(ServerCommandGate gate, NetAdmission admission)
        {
            this.gate = gate;
            this.admission = admission;
        }

        public int Accepted { get; private set; }
        public int Rejected { get; private set; }
        public int Dropped { get; private set; }

        public void Connected(int clientId, double now)
        {
            sessions[clientId] = new Session
            {
                ConnectedAt = now,
                Budget = new NetBudget(RequestsPerSecond, Burst, AbuseLimit, AbuseWindowSeconds),
            };
        }

        /// <summary>None means welcomed; anything else means the adapter must tell the client why and disconnect it.</summary>
        public NetRefusal Hello(int clientId, string token, double now, out string attemptId, out int firstOwnedId, out int ownedCount)
        {
            attemptId = admission.AttemptId;
            firstOwnedId = 0;
            ownedCount = 0;
            if (!sessions.TryGetValue(clientId, out var session))
            {
                Connected(clientId, now);
                session = sessions[clientId];
            }
            if (session.Connection != null)
                return NetRefusal.AlreadyConnected;
            var refusal = admission.TryAdmit(clientId, token, out var connection, out firstOwnedId, out ownedCount);
            if (refusal == NetRefusal.None)
                session.Connection = connection;
            return refusal;
        }

        public NetOutcome Request(int clientId, NetEnvelope envelope, double now)
        {
            if (!sessions.TryGetValue(clientId, out var session) || session.Connection == null)
                return new NetOutcome(NetDisposition.NotAuthenticated, default);
            if (!session.Budget.TryTake(now))
            {
                Dropped++;
                return new NetOutcome(session.Budget.NoteRefusal(now) ? NetDisposition.Disconnect : NetDisposition.OverBudget, default);
            }
            var result = gate.Handle(session.Connection, envelope, now);
            if (result.IsAccepted)
            {
                Accepted++;
                return new NetOutcome(NetDisposition.Handled, result);
            }
            Rejected++;
            return new NetOutcome(session.Budget.NoteRefusal(now) ? NetDisposition.Disconnect : NetDisposition.Handled, result);
        }

        public string AccountOf(int clientId) =>
            sessions.TryGetValue(clientId, out var session) && session.Connection != null ? session.Connection.Account : null;

        public void Disconnected(int clientId)
        {
            admission.Release(clientId);
            sessions.Remove(clientId);
        }

        /// <summary>Adds (and forgets) every client that has not said hello within the timeout.</summary>
        public void CollectExpired(double now, List<int> into)
        {
            List<int> found = null;
            foreach (var pair in sessions)
            {
                if (pair.Value.Connection == null && now - pair.Value.ConnectedAt > HelloTimeoutSeconds)
                    (found ?? (found = new List<int>())).Add(pair.Key);
            }
            if (found == null)
                return;
            foreach (var clientId in found)
            {
                sessions.Remove(clientId);
                into.Add(clientId);
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "NetSessionHostTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="10" passed="10"` (9 + the audit), `EXIT=0`. If `AFlood_IsDropped_ThenTheClientIsDisconnected` fails on the counts, re-read `NetBudget.TryTake`: the 61st request at the same instant must find fewer than one token, and each drop is one `NoteRefusal`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Tests/EditMode/NetSessionHostTests.cs Assets/_Project/Tests/EditMode/NetSessionHostTests.cs.meta
git commit -m "Net: session host decides hello, budgets, abuse and expiry for every message

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 4: Order kinds and intent pacing (Core)

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetOrder.cs`, `NetIntentSender.cs`
- Test: `Assets/_Project/Tests/EditMode/NetOrderTests.cs`, `NetIntentSenderTests.cs`

**Interfaces:**
- Consumes: `UnitCommand`, `MoveCommand`, `AttackCommand`, `StopCommand`.
- Produces:
  - `enum NetOrderKind : byte { None = 0, Move = 1, Attack = 2, Other = 3 }` and `static class NetOrder { static NetOrderKind KindOf(UnitCommand command) }` (null is `None`; any other command type is `Other`). The avatar replicates a unit's current order as this byte.
  - `sealed class NetIntentSender` (client pacing of steering messages): `const double RefreshSeconds = 0.1`; `bool Next(Vector3 held, double now, out Vector3 send)` (true when a steering message must go out now: the held direction changed, or it is non-zero and 0.1 s have passed since the last send; releasing sends exactly one zero); `bool ReleaseBeforeOrder(out Vector3 send)` (true, with a zero, only while a non-zero intent is still being held: the client sends it before a ground-click order, because `CommandableUnit` drops an order accepted while steering).

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetOrderTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetOrderTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        [Test]
        public void KindOf_NamesTheCurrentOrder()
        {
            var health = NetTestKit.MakeUnit(hosts, "Target").GetComponent<Health>();

            Assert.That(NetOrder.KindOf(null), Is.EqualTo(NetOrderKind.None));
            Assert.That(NetOrder.KindOf(new MoveCommand(Vector3.zero)), Is.EqualTo(NetOrderKind.Move));
            Assert.That(NetOrder.KindOf(new AttackCommand(health)), Is.EqualTo(NetOrderKind.Attack));
            Assert.That(NetOrder.KindOf(new StopCommand()), Is.EqualTo(NetOrderKind.Other));
        }
    }
}
```

`Assets/_Project/Tests/EditMode/NetIntentSenderTests.cs`:
```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetIntentSenderTests
    {
        [Test]
        public void NothingIsSent_WhileNothingIsHeld()
        {
            var sender = new NetIntentSender();
            Assert.That(sender.Next(Vector3.zero, 0d, out _), Is.False);
            Assert.That(sender.Next(Vector3.zero, 5d, out _), Is.False);
        }

        [Test]
        public void AHeldDirection_IsSentAtOnce_AndRefreshedEveryTenthOfASecond()
        {
            var sender = new NetIntentSender();
            Assert.That(sender.Next(Vector3.right, 1d, out var first), Is.True);
            Assert.That(first, Is.EqualTo(Vector3.right));
            Assert.That(sender.Next(Vector3.right, 1.05d, out _), Is.False, "too soon");
            Assert.That(sender.Next(Vector3.right, 1.1d, out _), Is.True, "refresh");
            Assert.That(sender.Next(Vector3.right, 1.15d, out _), Is.False);
            Assert.That(sender.Next(Vector3.right, 1.21d, out _), Is.True);
        }

        [Test]
        public void AChangedDirection_IsSentImmediately()
        {
            var sender = new NetIntentSender();
            sender.Next(Vector3.right, 1d, out _);
            Assert.That(sender.Next(Vector3.forward, 1.01d, out var send), Is.True);
            Assert.That(send, Is.EqualTo(Vector3.forward));
        }

        [Test]
        public void ReleasingSendsOneZero()
        {
            var sender = new NetIntentSender();
            sender.Next(Vector3.right, 1d, out _);
            Assert.That(sender.Next(Vector3.zero, 1.02d, out var send), Is.True);
            Assert.That(send, Is.EqualTo(Vector3.zero));
            Assert.That(sender.Next(Vector3.zero, 1.5d, out _), Is.False, "released: nothing more to send");
        }

        [Test]
        public void ReleaseBeforeAnOrder_SendsAZeroOnlyWhileSteering()
        {
            var sender = new NetIntentSender();
            Assert.That(sender.ReleaseBeforeOrder(out _), Is.False, "never steered");

            sender.Next(Vector3.right, 1d, out _);
            Assert.That(sender.ReleaseBeforeOrder(out var zero), Is.True);
            Assert.That(zero, Is.EqualTo(Vector3.zero));
            Assert.That(sender.ReleaseBeforeOrder(out _), Is.False, "already released");
            Assert.That(sender.Next(Vector3.zero, 1.2d, out _), Is.False, "the zero was already sent");
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode "NetOrderTests;NetIntentSenderTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS0246` for `NetOrder`, `NetOrderKind` and `NetIntentSender`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Net/Core/NetOrder.cs`:
```csharp
namespace Blackglass
{
    /// <summary>The kind of order a unit is carrying out, as replicated to clients (a byte on the wire).</summary>
    public enum NetOrderKind : byte
    {
        None = 0,
        Move = 1,
        Attack = 2,
        Other = 3,
    }

    public static class NetOrder
    {
        public static NetOrderKind KindOf(UnitCommand command)
        {
            if (command == null)
                return NetOrderKind.None;
            if (command is MoveCommand)
                return NetOrderKind.Move;
            if (command is AttackCommand)
                return NetOrderKind.Attack;
            return NetOrderKind.Other;
        }
    }
}
```

`Assets/_Project/Scripts/Net/Core/NetIntentSender.cs`:
```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// When the client sends steering messages (networking experiment, spec section 3.5): at once when the held direction
    /// changes, then every tenth of a second while it is held (the server drops an intent it has not heard of for 0.5 s), and
    /// exactly one zero when it is released. Pure: the caller passes real time in seconds.
    /// </summary>
    public sealed class NetIntentSender
    {
        public const double RefreshSeconds = 0.1d;

        Vector3 last;
        double lastSent = double.NegativeInfinity;

        /// <summary>True when a steering message with direction `send` must go out now.</summary>
        public bool Next(Vector3 held, double now, out Vector3 send)
        {
            send = held;
            var changed = held != last;
            if (held == Vector3.zero)
            {
                if (!changed)
                    return false;
                Mark(held, now);
                return true;
            }
            if (!changed && now - lastSent < RefreshSeconds)
                return false;
            Mark(held, now);
            return true;
        }

        /// <summary>
        /// A ground-click order is about to be sent: while a non-zero intent is held, a zero must go first, because
        /// CommandableUnit drops an order that is accepted while it is steered (its existing "manual control wins" rule).
        /// </summary>
        public bool ReleaseBeforeOrder(out Vector3 send)
        {
            send = Vector3.zero;
            if (last == Vector3.zero)
                return false;
            last = Vector3.zero;
            return true;
        }

        void Mark(Vector3 direction, double now)
        {
            last = direction;
            lastSent = now;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "NetOrderTests;NetIntentSenderTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="7" passed="7"` (1 + 5 + the audit), `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Tests/EditMode/NetOrderTests.cs Assets/_Project/Tests/EditMode/NetOrderTests.cs.meta Assets/_Project/Tests/EditMode/NetIntentSenderTests.cs Assets/_Project/Tests/EditMode/NetIntentSenderTests.cs.meta
git commit -m "Net: order kinds and client intent pacing

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Wire messages and their conversion (Adapter)

**Files:**
- Create: `Assets/_Project/Scripts/Net/Adapter/NetMessages.cs`, `NetWire.cs`
- Modify: `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef`
- Test: `Assets/_Project/Tests/EditMode/NetWireTests.cs`

**Interfaces:**
- Consumes: part 1's `NetEnvelope`, `NetRequest` and its four payloads; Task 2's `NetRefusal`.
- Produces (namespace `Blackglass`, all `struct ... : IBroadcast`, public fields):
  - `NetHelloMessage { string Token; }`
  - `NetWelcomeMessage { string AttemptId; int FirstOwnedId; int OwnedCount; }`
  - `NetRefusedMessage { byte Reason; }` (a `NetRefusal` value)
  - `NetRequestMessage { string AttemptId; uint Sequence; uint CommandId; byte Kind; int UnitId; int TargetId; Vector3 Vector; bool Append; }`
  - `NetResultMessage { uint CommandId; byte Reason; }` (a `NetRejectReason` value; 0 is accepted)
  - `static class NetWire`: constants `byte MoveIntent = 1, MoveTo = 2, Attack = 3, Stop = 4`; `NetRequest ToRequest(NetRequestMessage)` (null for an unknown kind, or for `Append` on anything but a move-to); `NetEnvelope ToEnvelope(NetRequestMessage)`; `NetRequestMessage ToMessage(string attemptId, uint sequence, uint commandId, NetRequest request)` (`Kind` 0 for a null or unknown request).

- [ ] **Step 1: Give the EditMode test assembly the FishNet reference, then write the failing tests**

In `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef`, add `"FishNet.Runtime"` to `references` (after `"Blackglass"`), keeping the file's existing line endings:
```json
    "references": [
        "Blackglass",
        "FishNet.Runtime",
        "UnityEngine.TestRunner",
```

`Assets/_Project/Tests/EditMode/NetWireTests.cs`:
```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetWireTests
    {
        const string Attempt = "attempt-1";

        [Test]
        public void EveryRequest_RoundTripsThroughTheMessage()
        {
            uint n = 0;
            foreach (var request in new NetRequest[]
            {
                new NetMoveIntent(3, new Vector3(1f, 0f, -0.5f)),
                new NetMoveTo(2, new Vector3(4f, 0f, 5f), true),
                new NetMoveTo(2, new Vector3(4f, 0f, 5f)),
                new NetAttack(1, 7),
                new NetStop(5),
            })
            {
                n++;
                var envelope = NetWire.ToEnvelope(NetWire.ToMessage(Attempt, n, n + 100, request));

                Assert.That(envelope.AttemptId, Is.EqualTo(Attempt));
                Assert.That(envelope.Sequence, Is.EqualTo(n));
                Assert.That(envelope.CommandId, Is.EqualTo(n + 100));
                Assert.That(envelope.Request, Is.TypeOf(request.GetType()));
                switch (request)
                {
                    case NetMoveIntent intent:
                        var steer = (NetMoveIntent)envelope.Request;
                        Assert.That(steer.UnitId, Is.EqualTo(intent.UnitId));
                        Assert.That(steer.Direction, Is.EqualTo(intent.Direction));
                        break;
                    case NetMoveTo move:
                        var walk = (NetMoveTo)envelope.Request;
                        Assert.That(walk.UnitId, Is.EqualTo(move.UnitId));
                        Assert.That(walk.Destination, Is.EqualTo(move.Destination));
                        Assert.That(walk.Append, Is.EqualTo(move.Append));
                        break;
                    case NetAttack attack:
                        var shoot = (NetAttack)envelope.Request;
                        Assert.That(shoot.UnitId, Is.EqualTo(attack.UnitId));
                        Assert.That(shoot.TargetId, Is.EqualTo(attack.TargetId));
                        break;
                    case NetStop stop:
                        Assert.That(((NetStop)envelope.Request).UnitId, Is.EqualTo(stop.UnitId));
                        break;
                }
            }
        }

        [TestCase((byte)0)]
        [TestCase((byte)99)]
        public void AnUnknownKind_HasNoRequest(byte kind)
        {
            var message = new NetRequestMessage { AttemptId = Attempt, Sequence = 1, CommandId = 1, Kind = kind };
            Assert.That(NetWire.ToEnvelope(message).Request, Is.Null);
        }

        [TestCase(NetWire.MoveIntent)]
        [TestCase(NetWire.Attack)]
        [TestCase(NetWire.Stop)]
        public void AppendOnANonMoveRequest_HasNoRequest(byte kind)
        {
            var message = new NetRequestMessage { AttemptId = Attempt, Sequence = 1, CommandId = 1, Kind = kind, UnitId = 1, Append = true };
            Assert.That(NetWire.ToRequest(message), Is.Null, "only a move-to may append");
        }

        [Test]
        public void ANonFiniteVector_PassesThrough_ForTheGateToRefuse()
        {
            var message = NetWire.ToMessage(Attempt, 1, 1, new NetMoveTo(1, new Vector3(float.NaN, 0f, 0f)));
            var request = NetWire.ToRequest(message);
            Assert.That(request, Is.TypeOf<NetMoveTo>());
            Assert.That(float.IsNaN(((NetMoveTo)request).Destination.x), Is.True);
        }

        [Test]
        public void ANullRequest_BecomesKindZero()
        {
            Assert.That(NetWire.ToMessage(Attempt, 1, 1, null).Kind, Is.EqualTo((byte)0));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode NetWireTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS0246` for `NetWire` and `NetRequestMessage`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Net/Adapter/NetMessages.cs`:
```csharp
using FishNet.Broadcast;
using UnityEngine;

namespace Blackglass
{
    // The five messages of the networking experiment (decision 046). Plain public fields so FishNet can serialise them;
    // the meaning of each lives in NetWire and NetSessionHost.

    /// <summary>Client to server, once after connecting: the development token.</summary>
    public struct NetHelloMessage : IBroadcast
    {
        public string Token;
    }

    /// <summary>Server to client: the hello was accepted. The account owns units FirstOwnedId .. FirstOwnedId + OwnedCount - 1.</summary>
    public struct NetWelcomeMessage : IBroadcast
    {
        public string AttemptId;
        public int FirstOwnedId;
        public int OwnedCount;
    }

    /// <summary>Server to client: the hello was refused (a NetRefusal value); the server then disconnects the client.</summary>
    public struct NetRefusedMessage : IBroadcast
    {
        public byte Reason;
    }

    /// <summary>Client to server: one request. Kind is a NetWire constant; Vector is the direction or destination.</summary>
    public struct NetRequestMessage : IBroadcast
    {
        public string AttemptId;
        public uint Sequence;
        public uint CommandId;
        public byte Kind;
        public int UnitId;
        public int TargetId;
        public Vector3 Vector;
        public bool Append;
    }

    /// <summary>Server to client: the answer to one request (a NetRejectReason value; 0 is accepted).</summary>
    public struct NetResultMessage : IBroadcast
    {
        public uint CommandId;
        public byte Reason;
    }
}
```

`Assets/_Project/Scripts/Net/Adapter/NetWire.cs`:
```csharp
namespace Blackglass
{
    /// <summary>Conversion between the request message on the wire and the library-free NetRequest the gate understands.</summary>
    public static class NetWire
    {
        public const byte MoveIntent = 1;
        public const byte MoveTo = 2;
        public const byte Attack = 3;
        public const byte Stop = 4;

        /// <summary>Null for an unknown kind, and for an append flag on anything but a move-to (spec section 3.3, check 1).</summary>
        public static NetRequest ToRequest(NetRequestMessage message)
        {
            switch (message.Kind)
            {
                case MoveIntent:
                    return message.Append ? null : new NetMoveIntent(message.UnitId, message.Vector);
                case MoveTo:
                    return new NetMoveTo(message.UnitId, message.Vector, message.Append);
                case Attack:
                    return message.Append ? null : new NetAttack(message.UnitId, message.TargetId);
                case Stop:
                    return message.Append ? null : new NetStop(message.UnitId);
                default:
                    return null;
            }
        }

        public static NetEnvelope ToEnvelope(NetRequestMessage message) =>
            new NetEnvelope(message.AttemptId, message.Sequence, message.CommandId, ToRequest(message));

        public static NetRequestMessage ToMessage(string attemptId, uint sequence, uint commandId, NetRequest request)
        {
            var message = new NetRequestMessage { AttemptId = attemptId, Sequence = sequence, CommandId = commandId };
            switch (request)
            {
                case NetMoveIntent intent:
                    message.Kind = MoveIntent;
                    message.UnitId = intent.UnitId;
                    message.Vector = intent.Direction;
                    break;
                case NetMoveTo move:
                    message.Kind = MoveTo;
                    message.UnitId = move.UnitId;
                    message.Vector = move.Destination;
                    message.Append = move.Append;
                    break;
                case NetAttack attack:
                    message.Kind = Attack;
                    message.UnitId = attack.UnitId;
                    message.TargetId = attack.TargetId;
                    break;
                case NetStop stop:
                    message.Kind = Stop;
                    message.UnitId = stop.UnitId;
                    break;
            }
            return message;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "NetWireTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="9" passed="9"` (8 + the audit: 1 round trip + 2 unknown kinds + 3 append cases + 1 non-finite + 1 null = 8), `EXIT=0`. A `CS0012` error naming a FishNet assembly means the test asmdef edit in Step 1 is missing.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef Assets/_Project/Tests/EditMode/NetWireTests.cs Assets/_Project/Tests/EditMode/NetWireTests.cs.meta
git commit -m "Net: wire messages and their conversion to gate requests

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Manager factory, avatar state and the avatar prefab (Adapter and Editor)

**Files:**
- Create: `Assets/_Project/Scripts/Net/Adapter/NetManagerFactory.cs`, `NetUnitState.cs`; `Assets/_Project/Editor/Net/NetAssetBuilder.cs`; generated `Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab` (+ meta, and the regenerated `Assets/DefaultPrefabObjects.asset`)
- Modify: `Assets/_Project/Scripts/Blackglass.asmdef`, `Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef`
- Test: `Assets/_Project/Tests/EditMode/NetAvatarPrefabTests.cs`, `Assets/_Project/Tests/PlayMode/NetManagerFactoryPlayModeTests.cs`

**Interfaces:**
- Consumes: Task 4's `NetOrderKind`; the FishNet package.
- Produces:
  - `internal static class NetManagerFactory`: `static NetworkManager Acquire(PrefabObjects prefabs, ushort serverFrameRate, out UnityTransport transport)` (returns the existing manager if one exists, otherwise builds one as in "FishNet API facts"; `serverFrameRate` 0 leaves the frame rate alone); `static void DestroyAll()` (destroys every manager immediately; used by tests and by `NetServer.Stop`).
  - `sealed class NetUnitState : NetworkBehaviour` (the replicated state of one unit; written by the server only): read properties `int UnitId`, `bool Hostile`, `int Health`, `int MaxHealth`, `bool Alive`, `NetOrderKind Order`, `int Queue` (pending orders), `bool Steering`; server-side `internal void Assign(int unitId, bool hostile)` and `internal void Push(int health, int maxHealth, bool alive, NetOrderKind order, int queue, bool steering)`.
  - `Blackglass.EditorTools.NetAssetBuilder`: `const string AvatarPath = "Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab"`, `const string PrefabCollectionPath = "Assets/DefaultPrefabObjects.asset"`, menu `Blackglass/Net/Build Net Assets`, `static void BuildFromCommandLine()` (run with `Tools/run-method.sh Blackglass.EditorTools.NetAssetBuilder.BuildFromCommandLine`).
  - The avatar prefab: a `NetworkObject`, a `NetworkTransform` and a `NetUnitState`, no renderer or collider (the client adds visuals).

- [ ] **Step 1: Give the assemblies their references, then write the failing tests**

In `Assets/_Project/Scripts/Blackglass.asmdef` add the transport assemblies after `"FishNet.Runtime"`:
```json
        "FishNet.Runtime",
        "FishyUnityTransport",
        "Unity.Networking.Transport"
```
(`Unity.Networking.Transport` is needed because `UnityTransport.SetConnectionData` has an overload whose parameter type lives there; without it the compiler reports CS0012.)

In `Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef` add `"FishNet.Runtime"` after `"Blackglass"` in `references`.

`Assets/_Project/Tests/EditMode/NetAvatarPrefabTests.cs`:
```csharp
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetAvatarPrefabTests
    {
        const string AvatarPath = "Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab";
        const string CollectionPath = "Assets/DefaultPrefabObjects.asset";

        [Test]
        public void TheAvatarPrefab_HasTheNetworkComponents_AndNothingVisual()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            Assert.That(prefab, Is.Not.Null, "run Blackglass/Net/Build Net Assets");
            Assert.That(prefab.GetComponent<NetworkObject>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<NetworkTransform>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<NetUnitState>(), Is.Not.Null);
            Assert.That(prefab.GetComponentInChildren<Renderer>(true), Is.Null, "the client builds the visuals; the server has none");
            Assert.That(prefab.GetComponentInChildren<Collider>(true), Is.Null);
        }

        [Test]
        public void TheAvatarPrefab_IsInFishNetsSpawnableCollection()
        {
            var collection = AssetDatabase.LoadAssetAtPath<PrefabObjects>(CollectionPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            Assert.That(collection, Is.Not.Null);
            Assert.That(prefab, Is.Not.Null, "run Blackglass/Net/Build Net Assets");
            var found = false;
            for (var i = 0; i < collection.GetObjectCount(); i++)
                found |= collection.GetObject(true, i) == prefab.GetComponent<NetworkObject>();
            Assert.That(found, Is.True, "FishNet adds a prefab with a NetworkObject to the collection when it is imported");
        }
    }
}
```

`Assets/_Project/Tests/PlayMode/NetManagerFactoryPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using FishNet.Managing;
using FishNet.Managing.Object;
using NUnit.Framework;
using UnityEditor;

namespace Blackglass.Tests
{
    public class NetManagerFactoryPlayModeTests
    {
        [TearDown]
        public void TearDown() => NetManagerFactory.DestroyAll();

        static PrefabObjects Prefabs() => AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset");

        [Test]
        public void Acquire_BuildsAManager_ThatDoesNotStartItself()
        {
            var manager = NetManagerFactory.Acquire(Prefabs(), 60, out var transport);

            Assert.That(manager, Is.Not.Null);
            Assert.That(transport, Is.Not.Null);
            Assert.That(manager.Initialized, Is.True, "NetworkManager.Awake has run");
            Assert.That(manager.ServerManager.GetStartOnHeadless(), Is.False, "only -net may start the server");
            Assert.That(manager.IsServerStarted, Is.False);
            Assert.That(manager.SpawnablePrefabs, Is.Not.Null);
        }

        [Test]
        public void Acquire_ReturnsTheExistingManager()
        {
            var first = NetManagerFactory.Acquire(Prefabs(), 0, out _);
            var second = NetManagerFactory.Acquire(Prefabs(), 0, out var transport);
            Assert.That(second, Is.SameAs(first));
            Assert.That(transport, Is.Not.Null);
        }

        [Test]
        public void DestroyAll_RemovesTheManager()
        {
            NetManagerFactory.Acquire(Prefabs(), 0, out _);
            NetManagerFactory.DestroyAll();
            Assert.That(NetworkManager.Instances.Count, Is.EqualTo(0));
        }
    }
}
#endif
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode NetAvatarPrefabTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS0246` for `NetUnitState` (the types do not exist yet).

- [ ] **Step 3: Implement the avatar state, the factory and the builder**

`Assets/_Project/Scripts/Net/Adapter/NetUnitState.cs`:
```csharp
using FishNet.Object;
using FishNet.Object.Synchronizing;

namespace Blackglass
{
    /// <summary>
    /// The replicated state of one unit (networking experiment, spec section 3.4): who it is, its side, its health and what it
    /// is doing. Lives on the avatar object the server spawns for each unit; only the server writes it. Position and rotation
    /// travel on the avatar's NetworkTransform. A client reads these properties; no message can change them.
    /// </summary>
    public sealed class NetUnitState : NetworkBehaviour
    {
        readonly SyncVar<int> unitId = new SyncVar<int>();
        readonly SyncVar<bool> hostile = new SyncVar<bool>();
        readonly SyncVar<int> health = new SyncVar<int>();
        readonly SyncVar<int> maxHealth = new SyncVar<int>();
        readonly SyncVar<bool> alive = new SyncVar<bool>();
        readonly SyncVar<byte> order = new SyncVar<byte>();
        readonly SyncVar<int> queue = new SyncVar<int>();
        readonly SyncVar<bool> steering = new SyncVar<bool>();

        public int UnitId => unitId.Value;
        public bool Hostile => hostile.Value;
        public int Health => health.Value;
        public int MaxHealth => maxHealth.Value;
        public bool Alive => alive.Value;
        public NetOrderKind Order => (NetOrderKind)order.Value;
        /// <summary>Orders waiting behind the current one.</summary>
        public int Queue => queue.Value;
        public bool Steering => steering.Value;

        // Server only, after the avatar has been spawned.
        internal void Assign(int id, bool isHostile)
        {
            unitId.Value = id;
            hostile.Value = isHostile;
        }

        internal void Push(int currentHealth, int maximumHealth, bool isAlive, NetOrderKind kind, int queued, bool isSteering)
        {
            health.Value = currentHealth;
            maxHealth.Value = maximumHealth;
            alive.Value = isAlive;
            order.Value = (byte)kind;
            queue.Value = queued;
            steering.Value = isSteering;
        }
    }
}
```

`Assets/_Project/Scripts/Net/Adapter/NetManagerFactory.cs`:
```csharp
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Server;
using FishNet.Managing.Transporting;
using FishNet.Transporting.UTP;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Builds FishNet's NetworkManager at runtime (decision 046), so nothing of FishNet exists until -net (the server) or a
    /// connection (the client) asks for it. The manager is created inactive and activated last: NetworkManager.Awake then builds
    /// its other managers and is ready in the same frame. The ServerManager is added first so headless auto-start can be turned
    /// off before NetworkManager.Start would use it, and so the frame cap is set before the server starts.
    /// </summary>
    internal static class NetManagerFactory
    {
        /// <summary>The existing manager (host mode, tests) or a new one. serverFrameRate 0 leaves the frame rate alone.</summary>
        internal static NetworkManager Acquire(PrefabObjects prefabs, ushort serverFrameRate, out UnityTransport transport)
        {
            if (NetworkManager.Instances.Count > 0)
            {
                var existing = NetworkManager.Instances[0];
                transport = existing.TransportManager.Transport as UnityTransport;
                return existing;
            }

            var host = new GameObject("NetworkManager");
            host.SetActive(false);
            transport = host.AddComponent<UnityTransport>();
            host.AddComponent<TransportManager>().Transport = transport;
            var server = host.AddComponent<ServerManager>();
            server.SetStartOnHeadless(false);
            if (serverFrameRate > 0)
                server.SetFrameRate(serverFrameRate);
            var manager = host.AddComponent<NetworkManager>();
            manager.SpawnablePrefabs = prefabs;
            host.SetActive(true);
            return manager;
        }

        /// <summary>Destroys every manager at once (tests, and NetServer.Stop for a manager it created).</summary>
        internal static void DestroyAll()
        {
            while (NetworkManager.Instances.Count > 0)
                Object.DestroyImmediate(NetworkManager.Instances[0].gameObject);
        }
    }
}
```

`Assets/_Project/Editor/Net/NetAssetBuilder.cs`:
```csharp
#if UNITY_EDITOR
using System.IO;
using FishNet.Component.Transforming;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the networked avatar prefab (networking experiment, decision 046): a NetworkObject, a NetworkTransform and a
    /// NetUnitState, with no renderer or collider, because the server shows nothing and the client builds its own visuals.
    /// Importing the prefab makes FishNet's asset postprocessor add it to Assets/DefaultPrefabObjects.asset. Idempotent.
    /// </summary>
    public static class NetAssetBuilder
    {
        public const string AvatarPath = "Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab";
        public const string PrefabCollectionPath = "Assets/DefaultPrefabObjects.asset";

        [MenuItem("Blackglass/Net/Build Net Assets")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        public static void Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AvatarPath));
            var avatar = new GameObject("NetUnitAvatar");
            avatar.AddComponent<NetworkObject>();
            avatar.AddComponent<NetworkTransform>();
            avatar.AddComponent<NetUnitState>();
            PrefabUtility.SaveAsPrefabAsset(avatar, AvatarPath);
            Object.DestroyImmediate(avatar);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("NetAssetBuilder: saved " + AvatarPath + ".");
        }
    }
}
#endif
```

- [ ] **Step 4: Build the prefab, then run the tests**

Run: `Tools/run-method.sh Blackglass.EditorTools.NetAssetBuilder.BuildFromCommandLine 2>&1 | tail -3` (Expected `EXIT=0`), then run it a second time (FishNet's postprocessor adds the prefab to `DefaultPrefabObjects.asset` while importing, which can be after the first run's checks), then:
`Tools/run-tests.sh EditMode NetAvatarPrefabTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="2" passed="2"`, `EXIT=0`.
`Tools/run-tests.sh PlayMode NetManagerFactoryPlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="3" passed="3"`, `EXIT=0`. If `Acquire_BuildsAManager_ThatDoesNotStartItself` fails on `manager.Initialized`, re-read `NetworkManager.Awake` (it returns early when `CanPersist()` is false because another manager already exists: the previous test's manager was not destroyed).
Then check `git status`: FishNet may rewrite `Assets/DefaultPrefabObjects.asset` when a manager starts in the Editor; commit it only if the prefab list changed (it must contain the avatar prefab and nothing else), otherwise `git checkout -- Assets/DefaultPrefabObjects.asset`. Expected new files are the prefab and its meta, `Prefabs/Net.meta`, the changed `Assets/DefaultPrefabObjects.asset`, the editor script and tests. If `ProjectSettings/ProjectSettings.asset` shows a `preloadedAssets` entry for `BlackglassControls.inputactions`, restore it (`preloadedAssets: []`): it is an unrelated Unity side effect seen during the spike and must not be committed.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Blackglass.asmdef Assets/_Project/Scripts/Net Assets/_Project/Editor/Net Assets/_Project/Editor/Net.meta Assets/_Project/Prefabs/Net Assets/_Project/Prefabs/Net.meta Assets/DefaultPrefabObjects.asset Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef Assets/_Project/Tests/EditMode/NetAvatarPrefabTests.cs Assets/_Project/Tests/EditMode/NetAvatarPrefabTests.cs.meta Assets/_Project/Tests/PlayMode/NetManagerFactoryPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetManagerFactoryPlayModeTests.cs.meta
git commit -m "Net: runtime manager factory, replicated unit state and the avatar prefab

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 7: The server adapter, bootstrap wiring and the server scene

**Files:**
- Create: `Assets/_Project/Scripts/Net/Adapter/NetAvatars.cs`, `NetServer.cs`; `Assets/_Project/Tests/PlayMode/TestSupport/NetLoopbackRig.cs`
- Modify: `Assets/_Project/Scripts/Server/ServerOptions.cs`, `Assets/_Project/Scripts/Server/ServerBootstrap.cs`, `Assets/_Project/Editor/Server/ServerSceneBuilder.cs`, `Assets/_Project/Scenes/ServerMission.unity` (regenerated), `Assets/_Project/Tests/PlayMode/ServerMissionPlayModeTests.cs`
- Test: `Assets/_Project/Tests/EditMode/ServerOptionsTests.cs` (one case), `Assets/_Project/Tests/PlayMode/NetServerPlayModeTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 6 (`NetTokenFile`, `NetAdmission`, `NetSessionHost`, `NetWire`, the messages, `NetManagerFactory`, `NetUnitState`, `NetOrder`), part 1's `NetUnits`, `ServerCommandGate`, `ServerDeterminism`, `ServerOptions` net properties.
- Produces:
  - `ServerOptions.With(...)` gains optional `bool? net`, `int? netPort`, `string netTokensPath`, `bool? netQuiet` (existing named arguments keep working).
  - `sealed class NetServer : MonoBehaviour` (on the `Server` root of `ServerMission.unity`; serialized `GameObject avatarPrefab` and `PrefabObjects spawnablePrefabs`): `internal void Initialize(GameObject avatarPrefab, PrefabObjects prefabs)`; `internal string Begin(ServerOptions options, MissionDirector director)` (null on success, otherwise the reason startup must fail); `internal void Stop()`; internal read members `bool Running`, `NetworkManager Manager`, `NetSessionHost Host`, `ServerCommandGate Gate`, `NetUnits Units`, `int AvatarCount`, `IReadOnlyList<NetUnitState> AvatarStates`, `string BindAddress`, `int ConnectionCount`; `const string Prefix = "[Net] "`.
  - `ServerBootstrap`: `internal void BeginNet(ServerOptions options)` (finishes with `ServerExitCodes.StartupFailed` and reason `net: <why>` when `NetServer.Begin` fails or the scene has no `NetServer`), `internal void StartConfiguredMode(ServerOptions options)` (a diagnostic run wins over `-net`, with a warning; otherwise `-net` starts the net server; called from the Ready handler instead of the old `if (options.Diagnostic) BeginDiagnostic(options)`), and a ` net=<connections> accepted=<n> rejected=<n> dropped=<n>` suffix on the heartbeat line while the net server runs.
  - Test support `NetLoopbackRig` (PlayMode): loads `ServerMission.unity`, waits for Ready, writes a token file, begins the net server on a unique port, optionally adds a `NetClient` (Task 8), collects log lines, tears everything down. Members used by later tasks: `IEnumerator Start()`, `void BeginNet(bool quiet = true)`, `IEnumerator Stop()`, `ServerBootstrap Bootstrap`, `NetServer Server`, `MissionDirector Director`, `int Port`, `int ExitCode`, `string TokenPath`, `IReadOnlyList<string> Logs`, `const string TokenA = "tok-a"`, `TokenB = "tok-b"`, `TokenTicket = "tok-t"`; Task 8 adds `NetClient Client`, `NetClient AddClient()` and `IEnumerator Connect(string token)`.

- [ ] **Step 1: Write the failing tests**

Add to `Assets/_Project/Tests/EditMode/ServerOptionsTests.cs`, before the last two closing braces:
```csharp
        [Test]
        public void With_ReplacesTheNetOptions()
        {
            var options = Parse().With(net: true, netPort: 7801, netTokensPath: "/tokens.txt", netQuiet: true);
            Assert.That(options.Net, Is.True);
            Assert.That(options.NetPort, Is.EqualTo(7801));
            Assert.That(options.NetTokensPath, Is.EqualTo("/tokens.txt"));
            Assert.That(options.NetQuiet, Is.True);
            Assert.That(Parse().With(diagnostic: true).Net, Is.False, "untouched values stay as they were");
        }
```

`Assets/_Project/Tests/PlayMode/TestSupport/NetLoopbackRig.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blackglass.Tests
{
    /// <summary>
    /// The server scene running in the Editor with the net server on a loopback port (networking experiment). One FishNet
    /// manager serves both sides (host mode), so the handshake, the gate, the budgets and the result path run end to end; the
    /// replication across a network is proven only by the VM acceptance run.
    /// </summary>
    internal sealed class NetLoopbackRig
    {
        public const string ScenePath = "Assets/_Project/Scenes/ServerMission.unity";
        public const string TokenA = "tok-a";
        public const string TokenB = "tok-b";
        public const string TokenTicket = "tok-t";

        static int nextPort = 7790;

        readonly List<string> logs = new List<string>();
        Scene scene;
        int frameRateBefore;
        int vSyncBefore;

        public ServerBootstrap Bootstrap { get; private set; }
        public NetServer Server { get; private set; }
        public MissionDirector Director { get; private set; }
        public int Port { get; private set; }
        public int ExitCode { get; private set; } = -1;
        public string TokenPath { get; private set; }
        public IReadOnlyList<string> Logs => logs;

        public IEnumerator Start()
        {
            frameRateBefore = Application.targetFrameRate;
            vSyncBefore = QualitySettings.vSyncCount;
            Port = nextPort++;
            Application.logMessageReceived += OnLog;
            TokenPath = Path.Combine(Application.temporaryCachePath, "net-rig-tokens-" + Port + ".txt");
            File.WriteAllText(TokenPath, "account-a " + TokenA + " squad\naccount-b " + TokenB + " none\naccount-t " + TokenTicket + " none other-attempt\n");

            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            var roots = scene.GetRootGameObjects();
            var serverRoot = roots.Single(g => g.name == "Server");
            Bootstrap = serverRoot.GetComponent<ServerBootstrap>();
            Server = serverRoot.GetComponent<NetServer>();
            Director = roots.Single(g => g.name == "Mission").GetComponent<MissionDirector>();
            Bootstrap.Exit = code => ExitCode = code;
            yield return TestWorld.WaitUntil(() => Director.State == MissionState.Ready || Director.State == MissionState.Failed, 60f);
            Assert.That(Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", Director.Report.Failures));
        }

        public void BeginNet(bool quiet = true) =>
            Bootstrap.BeginNet(Bootstrap.Options.With(net: true, netPort: Port, netTokensPath: TokenPath, netQuiet: quiet));

        public IEnumerator Stop()
        {
            Application.logMessageReceived -= OnLog;
            StopClient();
            if (Server != null)
                Server.Stop();
            NetManagerFactory.DestroyAll();
            if (Director != null)
                Director.Clear();
            yield return null;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
            Application.targetFrameRate = frameRateBefore;
            QualitySettings.vSyncCount = vSyncBefore;
            Time.timeScale = 1f;
        }

        // Task 8 replaces this body to disconnect and destroy the client.
        void StopClient() { }

        void OnLog(string condition, string stackTrace, LogType type) => logs.Add(condition);
    }
}
#endif
```

`Assets/_Project/Tests/PlayMode/NetServerPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FishNet.Managing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class NetServerPlayModeTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [UnityTest]
        public IEnumerator ANetServer_ListensOnLoopbackOnly_AndSpawnsOneAvatarPerUnit()
        {
            rig.BeginNet();
            yield return null;
            yield return null;

            Assert.That(rig.ExitCode, Is.EqualTo(-1), rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.True);
            Assert.That(rig.Server.BindAddress, Is.EqualTo("127.0.0.1"), "never a public address");
            Assert.That(rig.Server.Manager.IsServerStarted, Is.True);
            Assert.That(rig.Server.Manager.ServerManager.GetStartOnHeadless(), Is.False);
            Assert.That(rig.Server.AvatarCount, Is.EqualTo(3), "two friendlies and one hostile");
            Assert.That(rig.Server.AvatarStates.Select(s => s.UnitId).OrderBy(i => i), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(rig.Server.AvatarStates.Single(s => s.Hostile).UnitId, Is.EqualTo(3));
        }

        [UnityTest]
        public IEnumerator TheAvatars_MirrorTheirUnits()
        {
            rig.BeginNet();
            for (var i = 0; i < 5; i++)
                yield return null;

            var units = rig.Director.Friendlies.Concat(rig.Director.Hostiles).ToList();
            for (var i = 0; i < units.Count; i++)
            {
                var state = rig.Server.AvatarStates.Single(s => s.UnitId == i + 1);
                var health = units[i].GetComponent<Health>();
                Assert.That(state.Health, Is.EqualTo(health.Current), "unit " + (i + 1));
                Assert.That(state.MaxHealth, Is.EqualTo(health.Max));
                Assert.That(state.Alive, Is.True);
                Assert.That(state.Order, Is.EqualTo(NetOrder.KindOf(units[i].CurrentCommand)));
                Assert.That(state.Queue, Is.EqualTo(units[i].PendingCommands.Count));
                Assert.That((state.transform.position - units[i].transform.position).magnitude, Is.LessThan(0.01f), "unit " + (i + 1));
            }
        }

        [UnityTest]
        public IEnumerator NetQuiet_FreezesTheAutonomy_AndForcesTheFriendliesHits()
        {
            rig.BeginNet(quiet: true);
            yield return null;

            Assert.That(rig.Director.Hostiles[0].GetComponent<EnemyAI>().enabled, Is.False);
            foreach (var friendly in rig.Director.Friendlies)
            {
                Assert.That(friendly.GetComponent<CompanionAI>().enabled, Is.False, friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().HitRoll(), Is.EqualTo(0f), friendly.name);
            }
        }

        [UnityTest]
        public IEnumerator WithoutNetQuiet_TheAutonomyIsLeftAlone()
        {
            rig.BeginNet(quiet: false);
            yield return null;
            Assert.That(rig.Director.Hostiles[0].GetComponent<EnemyAI>().enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator ANetServerWithoutATokenFile_ExitsTwo_AndDoesNotListen()
        {
            var missing = Path.Combine(Application.temporaryCachePath, "does-not-exist-tokens.txt");
            rig.Bootstrap.BeginNet(rig.Bootstrap.Options.With(net: true, netPort: rig.Port, netTokensPath: missing));
            yield return null;

            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("token file", rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.False);
            Assert.That(NetworkManager.Instances.Count, Is.EqualTo(0), "no manager, so nothing can listen");
        }

        [UnityTest]
        public IEnumerator ANetServerWithoutAnyTokenFileArgument_ExitsTwo()
        {
            rig.Bootstrap.BeginNet(rig.Bootstrap.Options.With(net: true, netPort: rig.Port));
            yield return null;

            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("-netTokens", rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.False);
        }

        [UnityTest]
        public IEnumerator ATokenFileWithNoValidEntry_ExitsTwo()
        {
            File.WriteAllText(rig.TokenPath, "# nothing usable\nonly-two fields\n");
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Net\] token file: line 2"));
            rig.BeginNet();
            yield return null;

            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("no valid entry", rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.False);
        }

        [UnityTest]
        public IEnumerator ADiagnosticRun_WinsOverNet_AndTheNetServerDoesNotStart()
        {
            LogAssert.Expect(LogType.Warning, new Regex(@"-net is ignored with -diagnostic"));
            rig.Bootstrap.StartConfiguredMode(rig.Bootstrap.Options.With(diagnostic: true, net: true, netPort: rig.Port, netTokensPath: rig.TokenPath));
            yield return TestWorld.WaitUntil(() => rig.Bootstrap.HasExited, 120f);

            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.Success), rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.False);
            Assert.That(NetworkManager.Instances.Count, Is.EqualTo(0));
        }
    }
}
#endif
```

In `Assets/_Project/Tests/PlayMode/ServerMissionPlayModeTests.cs` add `typeof(NetServer)` to the `Allowed` array:
```csharp
            typeof(CoverRegistry), typeof(CoverDiscovery), typeof(InteractableRegistry), typeof(MissionDirector), typeof(NetServer),
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode ServerOptionsTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS1739` or `CS1061` for the unknown `net:` argument of `With`.
Run: `Tools/run-tests.sh PlayMode NetServerPlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS0246` for `NetServer` and `CS1061` for `BeginNet` and `StartConfiguredMode`.

- [ ] **Step 3: Implement `ServerOptions.With`**

In `Assets/_Project/Scripts/Server/ServerOptions.cs` replace the `With` method with:
```csharp
        /// <summary>A copy with the given values replaced (tests, and the bootstrap's diagnostic hand-off).</summary>
        internal ServerOptions With(int? seed = null, bool? diagnostic = null, int? diagnosticTimeoutSeconds = null,
            bool? net = null, int? netPort = null, string netTokensPath = null, bool? netQuiet = null)
        {
            var copy = (ServerOptions)MemberwiseClone();
            if (seed.HasValue)
                copy.Seed = seed;
            if (diagnostic.HasValue)
                copy.Diagnostic = diagnostic.Value;
            if (diagnosticTimeoutSeconds.HasValue)
                copy.DiagnosticTimeoutSeconds = diagnosticTimeoutSeconds.Value;
            if (net.HasValue)
                copy.Net = net.Value;
            if (netPort.HasValue)
                copy.NetPort = netPort.Value;
            if (netTokensPath != null)
                copy.NetTokensPath = netTokensPath;
            if (netQuiet.HasValue)
                copy.NetQuiet = netQuiet.Value;
            return copy;
        }
```
(Keep the existing doc comment line above it if it differs; the body is what matters.)

- [ ] **Step 4: Implement the avatars and the net server**

`Assets/_Project/Scripts/Net/Adapter/NetAvatars.cs`:
```csharp
using System.Collections.Generic;
using FishNet.Managing.Server;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The server's avatar objects (networking experiment, spec section 3.4): one networked object per simulation unit, spawned
    /// when the server starts. Each frame the avatar copies its unit's transform (the NetworkTransform replicates it) and
    /// pushes the unit's health and orders into NetUnitState. The simulation units themselves are never networked.
    /// </summary>
    internal sealed class NetAvatars
    {
        struct Entry
        {
            public CommandableUnit Unit;
            public Health Health;
            public Transform Avatar;
            public NetUnitState State;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly List<NetUnitState> states = new List<NetUnitState>();

        internal int Count => entries.Count;
        internal IReadOnlyList<NetUnitState> States => states;

        internal void Spawn(ServerManager server, GameObject prefab, NetUnits units)
        {
            Forget();
            for (var id = 1; id <= units.Count; id++)
            {
                if (!units.TryGet(id, out var unit))
                    continue;
                var avatar = Object.Instantiate(prefab, unit.transform.position, unit.transform.rotation);
                avatar.name = "Avatar_" + id;
                var state = avatar.GetComponent<NetUnitState>();
                server.Spawn(avatar);
                state.Assign(id, units.IsHostile(id));
                var entry = new Entry { Unit = unit, Health = unit.GetComponent<Health>(), Avatar = avatar.transform, State = state };
                entries.Add(entry);
                states.Add(state);
                Push(entry);
            }
        }

        internal void Sync()
        {
            foreach (var entry in entries)
                Push(entry);
        }

        internal void Despawn(ServerManager server)
        {
            foreach (var entry in entries)
            {
                if (entry.Avatar != null)
                    server.Despawn(entry.Avatar.gameObject);
            }
            Forget();
        }

        internal void Forget()
        {
            entries.Clear();
            states.Clear();
        }

        static void Push(Entry entry)
        {
            if (entry.Unit == null || entry.Avatar == null)
                return;
            entry.Avatar.SetPositionAndRotation(entry.Unit.transform.position, entry.Unit.transform.rotation);
            var health = entry.Health;
            entry.State.Push(health != null ? health.Current : 0, health != null ? health.Max : 0, health != null && health.IsAlive,
                NetOrder.KindOf(entry.Unit.CurrentCommand), entry.Unit.PendingCommands.Count, entry.Unit.MoveIntent != Vector3.zero);
        }
    }
}
```

`Assets/_Project/Scripts/Net/Adapter/NetServer.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Transporting;
using FishNet.Transporting.UTP;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The dedicated server's networking adapter (networking experiment, decision 046). It starts only when -net is given: reads
    /// the development token file, builds the FishNet manager, listens on 127.0.0.1 with the WebSocket transport, spawns one
    /// avatar per unit, and forwards every hello and request to NetSessionHost, which holds the rules. This class carries out
    /// the host's answers and keeps no rule of its own. Tokens are never logged.
    /// </summary>
    public sealed class NetServer : MonoBehaviour
    {
        public const string Prefix = "[Net] ";
        const string LoopbackAddress = "127.0.0.1";

        [SerializeField] GameObject avatarPrefab;
        [SerializeField] PrefabObjects spawnablePrefabs;

        readonly Dictionary<int, NetworkConnection> connections = new Dictionary<int, NetworkConnection>();
        readonly Dictionary<int, double> nextRejectLog = new Dictionary<int, double>();
        readonly Dictionary<int, int> suppressed = new Dictionary<int, int>();
        readonly List<int> expired = new List<int>();
        readonly NetAvatars avatars = new NetAvatars();
        NetworkManager manager;
        UnityTransport transport;
        bool createdManager;
        bool registered;

        internal void Initialize(GameObject avatar, PrefabObjects prefabs)
        {
            avatarPrefab = avatar;
            spawnablePrefabs = prefabs;
        }

        internal bool Running { get; private set; }
        internal NetworkManager Manager => manager;
        internal NetSessionHost Host { get; private set; }
        internal ServerCommandGate Gate { get; private set; }
        internal NetUnits Units { get; private set; }
        internal int AvatarCount => avatars.Count;
        internal IReadOnlyList<NetUnitState> AvatarStates => avatars.States;
        internal string BindAddress => transport != null ? transport.GetServerBindAddress(IPAddressType.IPv4) : null;
        internal int ConnectionCount => connections.Count;

        static double Now => Time.realtimeSinceStartupAsDouble;

        /// <summary>Null when the server is listening; otherwise the reason startup must fail (the bootstrap exits with code 2).</summary>
        internal string Begin(ServerOptions options, MissionDirector director)
        {
            if (Running)
                return "the net server is already running";
            if (avatarPrefab == null || spawnablePrefabs == null)
                return "the scene's NetServer is not wired to the avatar prefab and the prefab collection";
            if (string.IsNullOrEmpty(options.NetTokensPath))
                return "-net needs -netTokens <file>";
            if (!File.Exists(options.NetTokensPath))
                return "the token file does not exist: " + options.NetTokensPath;
            string text;
            try
            {
                text = File.ReadAllText(options.NetTokensPath);
            }
            catch (Exception exception)
            {
                return "the token file cannot be read: " + exception.GetType().Name;
            }
            var entries = NetTokenFile.Parse(text, out var warnings);
            foreach (var warning in warnings)
                Debug.LogWarning(Prefix + "token file: " + warning);
            if (entries.Count == 0)
                return "the token file has no valid entry";

            Units = NetUnits.FromMission(director);
            Gate = new ServerCommandGate(Units, director.InstanceId) { Log = line => Debug.LogWarning(Prefix + line) };
            Host = new NetSessionHost(Gate, new NetAdmission(entries, director.InstanceId, director.Friendlies.Count));
            if (options.NetQuiet)
            {
                ServerDeterminism.Freeze(director, null);
                foreach (var friendly in director.Friendlies)
                    ServerDeterminism.ForceHits(friendly);
            }

            createdManager = NetworkManager.Instances.Count == 0;
            manager = NetManagerFactory.Acquire(spawnablePrefabs, (ushort)options.FrameRate, out transport);
            if (transport == null)
                return Abort("the NetworkManager has no UnityTransport");
            transport.UseWebSockets = true;
            transport.SetConnectionData(LoopbackAddress, (ushort)options.NetPort, LoopbackAddress);

            var server = manager.ServerManager;
            server.OnServerConnectionState += OnServerState;
            server.OnRemoteConnectionState += OnRemoteState;
            server.RegisterBroadcast<NetHelloMessage>(OnHello, false);
            server.RegisterBroadcast<NetRequestMessage>(OnRequest, false);
            registered = true;
            Running = true;
            if (!server.StartConnection())
                return Abort("the transport would not start on " + LoopbackAddress + ":" + options.NetPort + " (is the port in use?)");

            Debug.Log(Prefix + FormattableString.Invariant(
                $"listening {LoopbackAddress}:{options.NetPort} websockets attempt={director.InstanceId} accounts={entries.Count} quiet={(options.NetQuiet ? "yes" : "no")}"));
            return null;
        }

        string Abort(string reason)
        {
            Stop();
            return reason;
        }

        internal void Stop()
        {
            if (manager != null)
            {
                var server = manager.ServerManager;
                if (server != null)
                {
                    server.OnServerConnectionState -= OnServerState;
                    server.OnRemoteConnectionState -= OnRemoteState;
                    if (registered)
                    {
                        server.UnregisterBroadcast<NetHelloMessage>(OnHello);
                        server.UnregisterBroadcast<NetRequestMessage>(OnRequest);
                    }
                    avatars.Despawn(server);
                    server.StopConnection(true);
                }
            }
            if (createdManager)
                NetManagerFactory.DestroyAll();
            manager = null;
            transport = null;
            createdManager = false;
            registered = false;
            Running = false;
            connections.Clear();
            nextRejectLog.Clear();
            suppressed.Clear();
            avatars.Forget();
        }

        void OnDestroy() => Stop();

        void Update()
        {
            if (!Running || Host == null)
                return;
            var now = Now;
            Gate.Tick(now);
            avatars.Sync();
            Host.CollectExpired(now, expired);
            foreach (var clientId in expired)
            {
                Debug.Log(Prefix + FormattableString.Invariant($"client {clientId} said no hello in time; disconnected"));
                if (connections.TryGetValue(clientId, out var connection))
                    connection.Disconnect(true);
            }
            expired.Clear();
        }

        void OnServerState(ServerConnectionStateArgs args)
        {
            Debug.Log(Prefix + "server " + args.ConnectionState);
            if (args.ConnectionState == LocalConnectionState.Started)
                avatars.Spawn(manager.ServerManager, avatarPrefab, Units);
            else if (args.ConnectionState == LocalConnectionState.Stopped)
                avatars.Forget();
        }

        void OnRemoteState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            var clientId = connection.ClientId;
            if (args.ConnectionState == RemoteConnectionState.Started)
            {
                connections[clientId] = connection;
                Host.Connected(clientId, Now);
                Debug.Log(Prefix + FormattableString.Invariant($"client {clientId} connected"));
                return;
            }
            var account = Host.AccountOf(clientId);
            Host.Disconnected(clientId);
            connections.Remove(clientId);
            nextRejectLog.Remove(clientId);
            suppressed.Remove(clientId);
            Debug.Log(Prefix + FormattableString.Invariant($"client {clientId} left account={account ?? "-"}"));
        }

        void OnHello(NetworkConnection connection, NetHelloMessage message, Channel channel)
        {
            var clientId = connection.ClientId;
            var refusal = Host.Hello(clientId, message.Token, Now, out var attemptId, out var firstOwnedId, out var ownedCount);
            if (refusal != NetRefusal.None)
            {
                Debug.Log(Prefix + FormattableString.Invariant($"hello refused client={clientId} reason={refusal}"));
                manager.ServerManager.Broadcast(connection, new NetRefusedMessage { Reason = (byte)refusal }, false);
                connection.Disconnect(false);
                return;
            }
            manager.ServerManager.Broadcast(connection,
                new NetWelcomeMessage { AttemptId = attemptId, FirstOwnedId = firstOwnedId, OwnedCount = ownedCount }, false);
            Debug.Log(Prefix + FormattableString.Invariant($"client {clientId} welcomed account={Host.AccountOf(clientId)} owns={ownedCount}"));
        }

        void OnRequest(NetworkConnection connection, NetRequestMessage message, Channel channel)
        {
            var clientId = connection.ClientId;
            var outcome = Host.Request(clientId, NetWire.ToEnvelope(message), Now);
            switch (outcome.Disposition)
            {
                case NetDisposition.NotAuthenticated:
                case NetDisposition.OverBudget:
                    return;
                case NetDisposition.Disconnect:
                    Debug.LogWarning(Prefix + FormattableString.Invariant($"client {clientId} is abusive (too many refused or dropped messages); disconnected"));
                    connection.Disconnect(true);
                    return;
            }
            manager.ServerManager.Broadcast(connection,
                new NetResultMessage { CommandId = message.CommandId, Reason = (byte)outcome.Result.Reason }, false);
            if (!outcome.Result.IsAccepted)
                LogRejected(clientId, outcome.Result.Reason, message.CommandId);
        }

        // At most one line per client per second, so a misbehaving client cannot fill the log.
        void LogRejected(int clientId, NetRejectReason reason, uint commandId)
        {
            var now = Now;
            suppressed.TryGetValue(clientId, out var skipped);
            if (nextRejectLog.TryGetValue(clientId, out var next) && now < next)
            {
                suppressed[clientId] = skipped + 1;
                return;
            }
            nextRejectLog[clientId] = now + 1d;
            suppressed[clientId] = 0;
            var more = skipped > 0 ? FormattableString.Invariant($" (+{skipped} more in the last second)") : string.Empty;
            Debug.Log(Prefix + FormattableString.Invariant($"rejected client={clientId} account={Host.AccountOf(clientId)} reason={reason} command={commandId}") + more);
        }
    }
}
```

- [ ] **Step 5: Wire the bootstrap**

In `Assets/_Project/Scripts/Server/ServerBootstrap.cs`:

1. In `OnStateChanged`, replace
```csharp
            if (options.Diagnostic)
                BeginDiagnostic(options);
```
with
```csharp
            StartConfiguredMode(options);
```
2. Add after `BeginDiagnostic`:
```csharp
        /// <summary>
        /// What the ready server does next (from the Ready handler; tests call it directly): a diagnostic run wins over -net, so
        /// the diagnostic stays bounded; otherwise -net starts the networking adapter; otherwise the server just runs.
        /// </summary>
        internal void StartConfiguredMode(ServerOptions modeOptions)
        {
            if (modeOptions.Diagnostic)
            {
                if (modeOptions.Net)
                    Debug.LogWarning(Prefix + "-net is ignored with -diagnostic");
                BeginDiagnostic(modeOptions);
            }
            else if (modeOptions.Net)
            {
                BeginNet(modeOptions);
            }
        }

        /// <summary>Starts the networking adapter on the ready mission; a failure ends the process with the startup exit code.</summary>
        internal void BeginNet(ServerOptions netOptions)
        {
            var net = GetComponent<NetServer>();
            if (net == null)
            {
                Finish(ServerExitCodes.StartupFailed, "net: -net was given but the scene has no NetServer");
                return;
            }
            var failure = net.Begin(netOptions, director);
            if (failure != null)
                Finish(ServerExitCodes.StartupFailed, "net: " + failure);
        }
```
3. In `Heartbeat`, change the final `Debug.Log(... state={director.State} phase={director.Phase}"));` call so the line ends with the net summary: replace
```csharp
                $"heartbeat uptimeS={now:0} fps={fps:0.0} cpuPct={cpu} rssMB={rss} managedMB={GC.GetTotalMemory(false) / 1048576d:0.0} state={director.State} phase={director.Phase}"));
```
with
```csharp
                $"heartbeat uptimeS={now:0} fps={fps:0.0} cpuPct={cpu} rssMB={rss} managedMB={GC.GetTotalMemory(false) / 1048576d:0.0} state={director.State} phase={director.Phase}") + NetSummary());
```
and add the helper next to `TrySample`:
```csharp
        string NetSummary()
        {
            var net = GetComponent<NetServer>();
            if (net == null || !net.Running || net.Host == null)
                return string.Empty;
            return FormattableString.Invariant(
                $" net={net.ConnectionCount} accepted={net.Host.Accepted} rejected={net.Host.Rejected} dropped={net.Host.Dropped}");
        }
```

- [ ] **Step 6: Wire the server scene**

In `Assets/_Project/Editor/Server/ServerSceneBuilder.cs` add `using FishNet.Managing.Object;` to the usings, and after the line `server.AddComponent<ServerBootstrap>().Initialize(director, pause);` add:
```csharp
            // The networking adapter is in the scene but inert: it builds FishNet's manager only when -net is given.
            server.AddComponent<NetServer>().Initialize(
                Load<GameObject>("Prefabs/Net/NetUnitAvatar.prefab"),
                AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset"));
```
Then regenerate the scene: `Tools/run-method.sh Blackglass.EditorTools.ServerSceneBuilder.BuildFromCommandLine 2>&1 | tail -3` (Expected `EXIT=0`; `git diff --stat Assets/_Project/Scenes/ServerMission.unity` shows the scene changed by the new `NetServer` component and its two references).

- [ ] **Step 7: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "ServerOptionsTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="27" passed="27"` (26 + the audit), `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode "NetServerPlayModeTests;ServerMissionPlayModeTests;ServerDiagnosticPlayModeTests;ServerDeterminismPlayModeTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="23" passed="23"` (8 + 7 + 5 + 3), `EXIT=0`. Tests that start the net server open a real WebSocket listener on `127.0.0.1:77xx`; a "port in use" failure means a previous test did not stop its server (check the rig's `Stop`). Note the single-process caveat: FishNet's `NetworkManager` persists across scenes, so every test must end with `NetManagerFactory.DestroyAll()` (the rig does it).

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Scripts/Server Assets/_Project/Editor/Server/ServerSceneBuilder.cs Assets/_Project/Scenes/ServerMission.unity Assets/_Project/Tests/EditMode/ServerOptionsTests.cs Assets/_Project/Tests/PlayMode/ServerMissionPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetServerPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetServerPlayModeTests.cs.meta Assets/_Project/Tests/PlayMode/TestSupport/NetLoopbackRig.cs Assets/_Project/Tests/PlayMode/TestSupport/NetLoopbackRig.cs.meta
git commit -m "Net: the server adapter, bootstrap wiring and the server scene

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: The client and in-process loopback tests

**Files:**
- Create: `Assets/_Project/Scripts/Net/Adapter/NetClient.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/NetLoopbackRig.cs`
- Test: `Assets/_Project/Tests/PlayMode/NetLoopbackPlayModeTests.cs`

**Interfaces:**
- Consumes: Task 5 messages and `NetWire`, Task 6 `NetManagerFactory`, Task 7 `NetServer` and `NetLoopbackRig`.
- Produces:
  - `enum NetClientState { Idle, Connecting, Authenticating, Ready, Refused, Disconnected }`.
  - `sealed class NetClient : MonoBehaviour` (serialized `PrefabObjects spawnablePrefabs`): `internal void Initialize(PrefabObjects prefabs)`; `bool Connect(string address, ushort port, string token)`; `void Disconnect()`; `NetClientState State`; `NetRefusal Refusal`; `string AttemptId`; `int FirstOwnedId`; `int OwnedCount`; `long RoundTripMs`; `uint Send(NetRequest request)` (fills in the next sequence and command id, returns the command id); `uint SendMessage(NetRequestMessage message)` (raw, for the harness; returns `message.CommandId`); `uint NextSequence()`; `uint NextCommandId()`; `bool TryGetResult(uint commandId, out NetRejectReason reason)`; `void ForgetResult(uint commandId)`; `NetRejectReason LastReject` and `uint LastRejectCommand` (the last refused request).
  - `NetLoopbackRig` gains `NetClient Client`, `NetClient AddClient()` and `IEnumerator Connect(string token)` (connects the client to the rig's port and waits until it is `Ready`, `Refused` or `Disconnected`).

- [ ] **Step 1: Write the failing tests**

Replace the `StopClient` placeholder and add the client members in `Assets/_Project/Tests/PlayMode/TestSupport/NetLoopbackRig.cs`. Add `using FishNet.Managing.Object;` and `using UnityEditor;` to the usings, then add:
```csharp
        public NetClient Client { get; private set; }

        public NetClient AddClient()
        {
            var host = new GameObject("NetClient");
            Client = host.AddComponent<NetClient>();
            Client.Initialize(AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset"));
            return Client;
        }

        public IEnumerator Connect(string token)
        {
            if (Client == null)
                AddClient();
            Assert.That(Client.Connect("127.0.0.1", (ushort)Port, token), Is.True);
            yield return TestWorld.WaitUntil(() => Client.State == NetClientState.Ready || Client.State == NetClientState.Refused
                || Client.State == NetClientState.Disconnected, 15f);
        }

        void StopClient()
        {
            if (Client == null)
                return;
            Client.Disconnect();
            Object.DestroyImmediate(Client.gameObject);
            Client = null;
        }
```
and delete the old `// Task 8 replaces this body ...` comment and empty `void StopClient() { }` lines.

`Assets/_Project/Tests/PlayMode/NetLoopbackPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetLoopbackPlayModeTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        IEnumerator Await(uint command, NetRejectReason expected)
        {
            yield return TestWorld.WaitUntil(() => rig.Client.TryGetResult(command, out _), 5f);
            Assert.That(rig.Client.TryGetResult(command, out var reason), Is.True, "no answer to command " + command);
            Assert.That(reason, Is.EqualTo(expected));
        }

        [UnityTest]
        public IEnumerator TheClient_IsWelcomed_WithItsSquad()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);

            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Ready), "state " + rig.Client.State + " " + rig.Client.Refusal);
            Assert.That(rig.Client.AttemptId, Is.EqualTo(rig.Director.InstanceId));
            Assert.That(rig.Client.FirstOwnedId, Is.EqualTo(1));
            Assert.That(rig.Client.OwnedCount, Is.EqualTo(2));
            Assert.That(rig.Server.ConnectionCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AnUnknownToken_IsRefused()
        {
            yield return rig.Connect("tok-secret-probe");
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Refused));
            Assert.That(rig.Client.Refusal, Is.EqualTo(NetRefusal.UnknownToken));
        }

        [UnityTest]
        public IEnumerator ATicketForAnotherAttempt_IsRefused()
        {
            yield return rig.Connect(NetLoopbackRig.TokenTicket);
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Refused));
            Assert.That(rig.Client.Refusal, Is.EqualTo(NetRefusal.WrongAttempt));
        }

        [UnityTest]
        public IEnumerator ASteeringRequest_MovesTheUnit_AndExpiresWithoutARefresh()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var unit = rig.Director.Friendlies[0];

            var command = rig.Client.Send(new NetMoveIntent(1, Vector3.right));
            yield return Await(command, NetRejectReason.None);
            Assert.That(unit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "the intent reached the simulation unit");

            yield return TestWorld.WaitUntil(() => unit.MoveIntent == Vector3.zero, 3f);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "the server let go when the client stopped refreshing");
        }

        [UnityTest]
        public IEnumerator ARequestForAHostile_IsAnsweredNotOwner()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var command = rig.Client.Send(new NetMoveTo(3, Vector3.zero));
            yield return Await(command, NetRejectReason.NotOwner);
            Assert.That(rig.Director.Hostiles[0].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator AnAccountWithoutAUnit_CannotOrderAnyOfThem()
        {
            yield return rig.Connect(NetLoopbackRig.TokenB);
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Ready));
            Assert.That(rig.Client.OwnedCount, Is.EqualTo(0));

            var command = rig.Client.Send(new NetStop(1));
            yield return Await(command, NetRejectReason.NotOwner);
        }

        [UnityTest]
        public IEnumerator NoTokenIsEverLogged()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var command = rig.Client.Send(new NetMoveTo(3, Vector3.zero));
            yield return Await(command, NetRejectReason.NotOwner);
            rig.Client.Disconnect();
            yield return null;
            yield return rig.Connect("tok-secret-probe");
            yield return rig.Connect(NetLoopbackRig.TokenTicket);

            foreach (var token in new[] { NetLoopbackRig.TokenA, NetLoopbackRig.TokenB, NetLoopbackRig.TokenTicket, "tok-secret-probe" })
                Assert.That(rig.Logs.Where(line => line.Contains(token)).ToList(), Is.Empty, "a log line contains the token " + token);
            Assert.That(rig.Logs.Any(line => line.StartsWith(NetServer.Prefix)), Is.True, "the server did log its activity");
        }
    }
}
#endif
```
- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode NetLoopbackPlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS0246` for `NetClient` and `NetClientState`.

- [ ] **Step 3: Implement the client**

`Assets/_Project/Scripts/Net/Adapter/NetClient.cs`:
```csharp
using System.Collections.Generic;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Transporting;
using FishNet.Transporting.UTP;
using UnityEngine;

namespace Blackglass
{
    public enum NetClientState
    {
        Idle,
        Connecting,
        /// <summary>Connected; the hello has been sent and no answer has come yet.</summary>
        Authenticating,
        Ready,
        /// <summary>The server refused the hello; Refusal says why.</summary>
        Refused,
        Disconnected,
    }

    /// <summary>
    /// The client side of the networking experiment: connects with the WebSocket transport, says hello with the development
    /// token, and then sends numbered requests and collects the server's answers. Holds no game rule; the avatars the server
    /// replicates are read by NetClientView and the harness (NetUnitState).
    /// </summary>
    public sealed class NetClient : MonoBehaviour
    {
        const int ResultsKept = 256;

        [SerializeField] PrefabObjects spawnablePrefabs;

        readonly Dictionary<uint, byte> results = new Dictionary<uint, byte>();
        readonly Queue<uint> resultOrder = new Queue<uint>();
        NetworkManager manager;
        UnityTransport transport;
        string token;
        bool createdManager;
        uint sequence;
        uint commandId;

        internal void Initialize(PrefabObjects prefabs) => spawnablePrefabs = prefabs;

        public NetClientState State { get; private set; }
        public NetRefusal Refusal { get; private set; }
        public string AttemptId { get; private set; }
        public int FirstOwnedId { get; private set; }
        public int OwnedCount { get; private set; }
        public long RoundTripMs => manager != null && manager.TimeManager != null ? manager.TimeManager.RoundTripTime : 0L;
        /// <summary>The reason of the last refused request (None before any) and the command it answered; the overlay shows them.</summary>
        public NetRejectReason LastReject { get; private set; }
        public uint LastRejectCommand { get; private set; }

        public bool Connect(string address, ushort port, string clientToken)
        {
            if (State == NetClientState.Connecting || State == NetClientState.Authenticating || State == NetClientState.Ready)
                return false;
            token = clientToken;
            Refusal = NetRefusal.None;
            AttemptId = null;
            FirstOwnedId = 0;
            OwnedCount = 0;
            results.Clear();
            resultOrder.Clear();

            createdManager = NetworkManager.Instances.Count == 0;
            manager = NetManagerFactory.Acquire(spawnablePrefabs, 0, out transport);
            if (transport == null)
            {
                State = NetClientState.Disconnected;
                return false;
            }
            transport.UseWebSockets = true;
            // When the server runs in this process (tests), the transport already holds its connection data.
            if (!manager.IsServerStarted)
                transport.SetConnectionData(address, port);
            var client = manager.ClientManager;
            client.OnClientConnectionState += OnClientState;
            client.RegisterBroadcast<NetWelcomeMessage>(OnWelcome);
            client.RegisterBroadcast<NetRefusedMessage>(OnRefused);
            client.RegisterBroadcast<NetResultMessage>(OnResult);
            State = NetClientState.Connecting;
            if (!client.StartConnection())
            {
                State = NetClientState.Disconnected;
                return false;
            }
            return true;
        }

        public void Disconnect()
        {
            if (manager != null)
            {
                var client = manager.ClientManager;
                if (client != null)
                {
                    client.OnClientConnectionState -= OnClientState;
                    client.UnregisterBroadcast<NetWelcomeMessage>(OnWelcome);
                    client.UnregisterBroadcast<NetRefusedMessage>(OnRefused);
                    client.UnregisterBroadcast<NetResultMessage>(OnResult);
                    client.StopConnection();
                }
                if (createdManager)
                    NetManagerFactory.DestroyAll();
            }
            manager = null;
            transport = null;
            createdManager = false;
            if (State != NetClientState.Refused)
                State = NetClientState.Disconnected;
        }

        void OnDestroy() => Disconnect();

        /// <summary>Sends a request with the next sequence number and command id and returns the command id.</summary>
        public uint Send(NetRequest request)
        {
            var command = ++commandId;
            manager.ClientManager.Broadcast(NetWire.ToMessage(AttemptId, ++sequence, command, request));
            return command;
        }

        /// <summary>Sends a request exactly as given (the harness uses it to send deliberately bad requests).</summary>
        public uint SendMessage(NetRequestMessage message)
        {
            manager.ClientManager.Broadcast(message);
            return message.CommandId;
        }

        public uint NextSequence() => ++sequence;

        public uint NextCommandId() => ++commandId;

        public bool TryGetResult(uint command, out NetRejectReason reason)
        {
            if (results.TryGetValue(command, out var value))
            {
                reason = (NetRejectReason)value;
                return true;
            }
            reason = NetRejectReason.None;
            return false;
        }

        public void ForgetResult(uint command) => results.Remove(command);

        void OnClientState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                State = NetClientState.Authenticating;
                manager.ClientManager.Broadcast(new NetHelloMessage { Token = token });
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped && State != NetClientState.Refused)
            {
                State = NetClientState.Disconnected;
            }
        }

        void OnWelcome(NetWelcomeMessage message, Channel channel)
        {
            AttemptId = message.AttemptId;
            FirstOwnedId = message.FirstOwnedId;
            OwnedCount = message.OwnedCount;
            State = NetClientState.Ready;
        }

        void OnRefused(NetRefusedMessage message, Channel channel)
        {
            Refusal = (NetRefusal)message.Reason;
            State = NetClientState.Refused;
        }

        void OnResult(NetResultMessage message, Channel channel)
        {
            if (!results.ContainsKey(message.CommandId))
                resultOrder.Enqueue(message.CommandId);
            results[message.CommandId] = message.Reason;
            if (message.Reason != (byte)NetRejectReason.None)
            {
                LastReject = (NetRejectReason)message.Reason;
                LastRejectCommand = message.CommandId;
            }
            while (resultOrder.Count > ResultsKept)
                results.Remove(resultOrder.Dequeue());
        }
    }
}
```

- [ ] **Step 4: Host-mode feasibility check, then run the tests**

Run: `Tools/run-tests.sh PlayMode "NetLoopbackPlayModeTests.TheClient_IsWelcomed_WithItsSquad" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="1" passed="1"`. **If this one test cannot be made to pass** (the client never leaves `Authenticating`, or `Connect` returns false because FishNet will not start a client against a server in the same manager), read how FishNet's host mode starts the local client (`ClientManager.StartConnection` with `ServerManager.Started`, the `Client Host` notes in the package) and fix the call order once. If it is still impossible, **stop and report**: spec section 4 says the PlayMode loopback tests then become gate-level tests without a transport and the spec is amended before the plan continues.
Then run all: `Tools/run-tests.sh PlayMode NetLoopbackPlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="7" passed="7"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Tests/PlayMode/TestSupport/NetLoopbackRig.cs Assets/_Project/Tests/PlayMode/NetLoopbackPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetLoopbackPlayModeTests.cs.meta
git commit -m "Net: the client and in-process loopback tests of the whole message path

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 9: The harness

**Files:**
- Create: `Assets/_Project/Scripts/Net/Adapter/NetHarness.cs`
- Test: `Assets/_Project/Tests/PlayMode/NetHarnessPlayModeTests.cs`

**Interfaces:**
- Consumes: Task 8's `NetClient` and `NetLoopbackRig`, Task 6's `NetUnitState`, Task 5's `NetWire` and messages, Task 4's `NetIntentSender` and `NetOrderKind`.
- Produces: `sealed class NetHarness : MonoBehaviour` (serialized `NetClient client`): `internal void Initialize(NetClient client)`; `public void Begin(string role)` (roles `a`, `b`, `ticket`, `badtoken`; runs the script as a coroutine); `internal Action<int> Exit` (default `Application.Quit`); read members `internal int ExitCode` (-1 while running), `string Summary`, `double LatencyMedianMs`, `long RoundTripMs`; constants `ExitPass = 0`, `ExitFailed = 1`, `ExitNoConnection = 2`; `Prefix = "[Harness] "`. The final line is `BLACKGLASS NET HARNESS PASS role=<r> steps=<n> latencyMedianMs=<x> roundTripMs=<y>` or `BLACKGLASS NET HARNESS FAIL role=<r> step=<name> reason=<text>`.
- Role `a` (the owner) runs spec section 3.6 steps 1 to 3: ten steering cycles with a latency sample each (the time from the first steering message to the first replicated movement of 0.05 m; at least five cycles must move the unit), a Shift-queued move pair (the replicated queue shows one pending move, then both finish), an attack that lowers the hostile's replicated health, and the six bad requests (a move for a hostile, an attack on a friendly, a reused command id, an old sequence, a wrong attempt id, a malformed request), each answered with its typed reason with every replicated unit state unchanged. Role `b` orders each of the owner's units with all four request types and needs `NotOwner` every time with no state change. Role `ticket` needs the hello refused with `WrongAttempt`; role `badtoken` with `UnknownToken`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/NetHarnessPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetHarnessPlayModeTests
    {
        NetLoopbackRig rig;
        NetHarness harness;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (harness != null)
                Object.DestroyImmediate(harness.gameObject);
            yield return rig.Stop();
        }

        // Starts the net server (unless the test wants none), connects a client with the token and runs a role to its end.
        IEnumerator Run(string role, string token, bool serverListening = true, float seconds = 40f)
        {
            if (serverListening)
                rig.BeginNet();
            var client = rig.AddClient();
            client.Connect("127.0.0.1", (ushort)rig.Port, token);
            harness = new GameObject("NetHarness").AddComponent<NetHarness>();
            harness.Initialize(client);
            harness.Exit = _ => { };
            harness.Begin(role);
            yield return TestWorld.WaitUntil(() => harness.ExitCode >= 0, seconds);
        }

        [UnityTest]
        public IEnumerator TheBadTokenRole_PassesWhenTheServerRefusesTheToken()
        {
            yield return Run("badtoken", "tok-secret-probe");
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
            StringAssert.StartsWith("BLACKGLASS NET HARNESS PASS role=badtoken", harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheTicketRole_PassesWhenTheServerRefusesTheAttempt()
        {
            yield return Run("ticket", NetLoopbackRig.TokenTicket);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheObserverRole_PassesWhenEveryOrderForTheSquadIsRefused()
        {
            yield return Run("b", NetLoopbackRig.TokenB);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheHarness_Fails_WhenTheExpectedRefusalDoesNotHappen()
        {
            yield return Run("ticket", NetLoopbackRig.TokenA);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitFailed), harness.Summary);
            StringAssert.Contains("FAIL role=ticket step=refusal", harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheHarness_ReportsNoConnection_WhenNothingListens()
        {
            yield return Run("a", NetLoopbackRig.TokenA, serverListening: false, seconds: 30f);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitNoConnection), harness.Summary);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator TheOwnerRole_PassesTheWholeScript()
        {
            yield return Run("a", NetLoopbackRig.TokenA, seconds: 240f);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
            StringAssert.Contains("latencyMedianMs=", harness.Summary);
            Assert.That(harness.LatencyMedianMs, Is.GreaterThan(0d));
        }
    }
}
#endif
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode NetHarnessPlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS0246` for `NetHarness`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Net/Adapter/NetHarness.cs`:
```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The windowless acceptance script of the networking experiment (spec section 3.6): one role per run, a fixed list of
    /// checks with real-time deadlines, and a final PASS or FAIL line with an exit code like the Checkpoint A diagnostic.
    /// It reads only what a client may see: the NetClient's answers and the replicated NetUnitState avatars.
    /// </summary>
    public sealed class NetHarness : MonoBehaviour
    {
        public const int ExitPass = 0;
        public const int ExitFailed = 1;
        public const int ExitNoConnection = 2;
        public const string Prefix = "[Harness] ";

        struct Snap
        {
            public int Id;
            public Vector3 Position;
            public int Health;
            public NetOrderKind Order;
            public int Queue;
        }

        [SerializeField] NetClient client;

        readonly List<string> passed = new List<string>();
        bool waitOk;
        bool noConnection;
        string failedStep;
        string failure;

        internal Action<int> Exit = code => Application.Quit(code);
        internal int ExitCode { get; private set; } = -1;
        internal string Summary { get; private set; }
        internal double LatencyMedianMs { get; private set; }
        internal long RoundTripMs { get; private set; }

        internal void Initialize(NetClient netClient) => client = netClient;

        public void Begin(string role) => StartCoroutine(Run(role));

        static double Now => Time.realtimeSinceStartupAsDouble;

        bool Failed => failure != null;

        IEnumerator Run(string role)
        {
            switch (role)
            {
                case "a":
                    yield return Owner();
                    break;
                case "b":
                    yield return Observer();
                    break;
                case "ticket":
                    yield return Refused(NetRefusal.WrongAttempt);
                    break;
                case "badtoken":
                    yield return Refused(NetRefusal.UnknownToken);
                    break;
                default:
                    Fail("role", "unknown role '" + role + "'");
                    break;
            }
            Finish(role);
        }

        // ---- roles -------------------------------------------------------------------------------------------------

        IEnumerator Refused(NetRefusal expected)
        {
            yield return Until(() => client.State == NetClientState.Refused || client.State == NetClientState.Ready, 10d);
            if (!waitOk)
                noConnection = true;
            Check("refusal", client.State == NetClientState.Refused && client.Refusal == expected,
                "state " + client.State + " refusal " + client.Refusal + ", expected " + expected);
        }

        IEnumerator Observer()
        {
            yield return Connected();
            if (Failed)
                yield break;
            if (!Check("no ownership", client.OwnedCount == 0, "owns " + client.OwnedCount))
                yield break;
            yield return Until(() => States().Count(s => s.UnitId > 0) >= 2, 10d);
            if (!Check("avatars", waitOk, "the client received the units"))
                yield break;

            var hostileId = FindHostile().UnitId;
            var friendlies = States().Where(s => !s.Hostile && s.UnitId > 0).Select(s => s.UnitId).OrderBy(i => i).ToList();
            var before = Snapshot();
            foreach (var id in friendlies)
            {
                var requests = new NetRequest[]
                {
                    new NetMoveIntent(id, Vector3.right),
                    new NetMoveTo(id, Position(id)),
                    new NetAttack(id, hostileId),
                    new NetStop(id),
                };
                foreach (var request in requests)
                {
                    var command = client.Send(request);
                    yield return AwaitResult(command, 3d);
                    client.TryGetResult(command, out var reason);
                    if (!Check("not owner: " + request.GetType().Name + " for unit " + id, waitOk && reason == NetRejectReason.NotOwner, "answered " + reason))
                        yield break;
                }
            }
            yield return Wait(0.5d);
            var difference = Differences(before, Snapshot());
            Check("no state change", difference == null, difference ?? "every replicated unit is as it was");
        }

        IEnumerator Owner()
        {
            yield return Connected();
            if (Failed)
                yield break;
            var own = client.FirstOwnedId;
            var second = own + 1;
            yield return Until(() => State(own) != null && FindHostile() != null, 10d);
            if (!Check("avatars", waitOk, "the client received its units and the hostile"))
                yield break;
            var hostileId = FindHostile().UnitId;

            // 1. Steering, with a latency sample per cycle.
            yield return Settle();
            var samples = new List<double>();
            for (var cycle = 0; cycle < 10; cycle++)
            {
                var direction = cycle % 2 == 0 ? Vector3.right : Vector3.left;
                var before = Position(own);
                var sentAt = Now;
                client.Send(new NetMoveIntent(own, direction));
                var nextRefresh = sentAt + NetIntentSender.RefreshSeconds;
                var deadline = sentAt + 2d;
                while (Now < deadline)
                {
                    if (Now >= nextRefresh)
                    {
                        client.Send(new NetMoveIntent(own, direction));
                        nextRefresh = Now + NetIntentSender.RefreshSeconds;
                    }
                    if ((Position(own) - before).magnitude >= 0.05f)
                    {
                        samples.Add((Now - sentAt) * 1000d);
                        break;
                    }
                    yield return null;
                }
                var hold = Now + 0.3d;
                while (Now < hold)
                {
                    if (Now >= nextRefresh)
                    {
                        client.Send(new NetMoveIntent(own, direction));
                        nextRefresh = Now + NetIntentSender.RefreshSeconds;
                    }
                    yield return null;
                }
                client.Send(new NetMoveIntent(own, Vector3.zero));
                yield return Wait(0.3d);
            }
            if (!Check("steering", samples.Count >= 5, samples.Count + " of 10 cycles moved the unit"))
                yield break;
            LatencyMedianMs = Median(samples);
            RoundTripMs = client.RoundTripMs;
            Debug.Log(Prefix + FormattableString.Invariant($"latency median {LatencyMedianMs:0.0} ms over {samples.Count} samples; client round trip {RoundTripMs} ms"));

            // 2. A Shift-queued move: the replicated queue shows it, and both moves run.
            yield return Settle();
            var start = Position(own);
            // Toward the hostile: a direction the generator left open between the teams, so the 2 m snap finds a walkable point.
            var toward = Position(hostileId) - start;
            toward.y = 0f;
            toward = toward.sqrMagnitude > 0.01f ? toward.normalized * 1.5f : new Vector3(1.5f, 0f, 0f);
            client.Send(new NetMoveTo(own, start + toward));
            client.Send(new NetMoveTo(own, start, append: true));
            var sawQueue = false;
            var farthest = 0f;
            var queueEnd = Now + 25d;
            while (Now < queueEnd)
            {
                var state = State(own);
                if (state.Queue == 1 && state.Order == NetOrderKind.Move)
                    sawQueue = true;
                farthest = Mathf.Max(farthest, (Position(own) - start).magnitude);
                if (sawQueue && state.Order == NetOrderKind.None && state.Queue == 0)
                    break;
                yield return null;
            }
            Check("queue", sawQueue, "the replicated queue showed one pending move behind the current one");
            var back = (Position(own) - start).magnitude;
            Check("queued moves", farthest >= 0.8f && back <= 1f, FormattableString.Invariant($"went {farthest:0.0} m out and ended {back:0.0} m from the start"));
            if (Failed)
                yield break;

            // 3. An attack resolves on the server and the hostile's replicated health falls.
            yield return Settle();
            var healthBefore = State(hostileId).Health;
            client.Send(new NetAttack(own, hostileId));
            yield return Until(() => State(hostileId) != null && State(hostileId).Health < healthBefore, 60d);
            Check("attack", waitOk, "hostile health " + healthBefore + " -> " + (State(hostileId) != null ? State(hostileId).Health.ToString() : "gone"));
            if (Failed)
                yield break;

            // 4. The six bad requests, each answered with its typed reason, and nothing changes.
            yield return Settle();
            var snapshot = Snapshot();
            var attempt = client.AttemptId;
            var position = Position(own);
            var cases = new List<KeyValuePair<string, KeyValuePair<uint, NetRejectReason>>>();
            cases.Add(Case("a move for a hostile", client.Send(new NetMoveTo(hostileId, position)), NetRejectReason.NotOwner));
            cases.Add(Case("an attack on a friendly", client.Send(new NetAttack(own, second)), NetRejectReason.WrongSide));

            var good = client.Send(new NetStop(own));
            yield return AwaitResult(good, 3d);
            client.TryGetResult(good, out var goodReason);
            Check("a good order", waitOk && goodReason == NetRejectReason.None, "a stop was answered " + goodReason);
            client.ForgetResult(good);
            var reused = client.SendMessage(NetWire.ToMessage(attempt, client.NextSequence(), good, new NetStop(own)));
            cases.Add(Case("a reused command id", reused, NetRejectReason.DuplicateCommand));

            var old = client.SendMessage(NetWire.ToMessage(attempt, 1u, client.NextCommandId(), new NetStop(own)));
            cases.Add(Case("an older sequence number", old, NetRejectReason.StaleSequence));

            var wrong = client.SendMessage(NetWire.ToMessage("wrong-attempt", client.NextSequence(), client.NextCommandId(), new NetStop(own)));
            cases.Add(Case("a wrong attempt id", wrong, NetRejectReason.WrongAttempt));

            var malformed = client.SendMessage(new NetRequestMessage
            {
                AttemptId = attempt,
                Sequence = client.NextSequence(),
                CommandId = client.NextCommandId(),
                Kind = 99,
            });
            cases.Add(Case("a malformed request", malformed, NetRejectReason.Malformed));

            foreach (var pair in cases)
            {
                yield return AwaitResult(pair.Value.Key, 3d);
                client.TryGetResult(pair.Value.Key, out var reason);
                if (!Check("rejects " + pair.Key, waitOk && reason == pair.Value.Value, "answered " + reason + ", expected " + pair.Value.Value))
                    yield break;
            }
            yield return Wait(0.5d);
            var difference = Differences(snapshot, Snapshot());
            Check("no state change", difference == null, difference ?? "every replicated unit is as it was");
        }

        static KeyValuePair<string, KeyValuePair<uint, NetRejectReason>> Case(string name, uint command, NetRejectReason expected) =>
            new KeyValuePair<string, KeyValuePair<uint, NetRejectReason>>(name, new KeyValuePair<uint, NetRejectReason>(command, expected));

        // ---- helpers -----------------------------------------------------------------------------------------------

        IEnumerator Connected()
        {
            yield return Until(() => client.State == NetClientState.Ready || client.State == NetClientState.Refused
                || client.State == NetClientState.Disconnected, 10d);
            if (client.State != NetClientState.Ready)
            {
                noConnection = true;
                Fail("connect", "state " + client.State + (client.Refusal != NetRefusal.None ? " refusal " + client.Refusal : string.Empty));
                yield break;
            }
            Check("connect", true, "welcomed; owns " + client.OwnedCount + " from unit " + client.FirstOwnedId);
        }

        // Stops every owned unit and waits, so the next check starts from an idle squad.
        IEnumerator Settle()
        {
            for (var i = 0; i < client.OwnedCount; i++)
                client.Send(new NetStop(client.FirstOwnedId + i));
            yield return Wait(1d);
        }

        IEnumerator AwaitResult(uint command, double seconds) => Until(() => client.TryGetResult(command, out _), seconds);

        IEnumerator Until(Func<bool> condition, double seconds)
        {
            var end = Now + seconds;
            while (!condition() && Now < end)
                yield return null;
            waitOk = condition();
        }

        static IEnumerator Wait(double seconds)
        {
            var end = Now + seconds;
            while (Now < end)
                yield return null;
        }

        bool Check(string step, bool condition, string detail)
        {
            if (Failed)
                return false;
            if (condition)
            {
                passed.Add(step);
                Debug.Log(Prefix + step + " OK " + detail);
                return true;
            }
            failedStep = step;
            failure = detail;
            Debug.LogWarning(Prefix + step + " FAIL " + detail);
            return false;
        }

        void Fail(string step, string detail) => Check(step, false, detail);

        static NetUnitState[] States() => FindObjectsByType<NetUnitState>(FindObjectsSortMode.None);

        static NetUnitState State(int id) => States().FirstOrDefault(s => s.UnitId == id);

        static NetUnitState FindHostile() => States().FirstOrDefault(s => s.Hostile && s.UnitId > 0);

        static Vector3 Position(int id)
        {
            var state = State(id);
            return state != null ? state.transform.position : Vector3.zero;
        }

        static List<Snap> Snapshot() => States().Where(s => s.UnitId > 0).OrderBy(s => s.UnitId)
            .Select(s => new Snap { Id = s.UnitId, Position = s.transform.position, Health = s.Health, Order = s.Order, Queue = s.Queue }).ToList();

        // Null when nothing changed; otherwise the first difference. Positions may differ by 2 cm (interpolation settling).
        static string Differences(List<Snap> before, List<Snap> after)
        {
            if (before.Count != after.Count)
                return "the unit count changed from " + before.Count + " to " + after.Count;
            for (var i = 0; i < before.Count; i++)
            {
                var a = before[i];
                var b = after[i];
                if ((a.Position - b.Position).magnitude > 0.02f)
                    return "unit " + a.Id + " moved";
                if (a.Health != b.Health)
                    return "unit " + a.Id + " health " + a.Health + " -> " + b.Health;
                if (a.Order != b.Order || a.Queue != b.Queue)
                    return "unit " + a.Id + " orders changed";
            }
            return null;
        }

        static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2d;
        }

        void Finish(string role)
        {
            ExitCode = noConnection ? ExitNoConnection : Failed ? ExitFailed : ExitPass;
            if (ExitCode == ExitPass)
            {
                Summary = FormattableString.Invariant(
                    $"BLACKGLASS NET HARNESS PASS role={role} steps={passed.Count} latencyMedianMs={LatencyMedianMs:0.0} roundTripMs={RoundTripMs}");
                Debug.Log(Summary);
            }
            else
            {
                Summary = "BLACKGLASS NET HARNESS FAIL role=" + role + " step=" + failedStep + " reason=" + failure;
                Debug.LogWarning(Summary);
            }
            client.Disconnect();
            Exit(ExitCode);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

(If a `Class.Method` filter matches nothing, use the method name alone.)
Run: `Tools/run-tests.sh PlayMode "NetHarnessPlayModeTests.TheBadTokenRole_PassesWhenTheServerRefusesTheToken;NetHarnessPlayModeTests.TheTicketRole_PassesWhenTheServerRefusesTheAttempt;NetHarnessPlayModeTests.TheObserverRole_PassesWhenEveryOrderForTheSquadIsRefused;NetHarnessPlayModeTests.TheHarness_Fails_WhenTheExpectedRefusalDoesNotHappen;NetHarnessPlayModeTests.TheHarness_ReportsNoConnection_WhenNothingListens" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="5" passed="5"`, `EXIT=0`.
Then the long one: `Tools/run-tests.sh PlayMode NetHarnessPlayModeTests.TheOwnerRole_PassesTheWholeScript 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -6`
Expected: `total="1" passed="1"`, `EXIT=0` (one to three minutes; the attack step waits for the squad to walk to the hostile and shoot). If it fails, the assertion message carries the harness summary with the failing step: fix the cause (for example a unit blocked by a wall in the steering cycles gives fewer than five samples), not the threshold, unless the steps' real behaviour proves the threshold wrong (then ledger the ruling).
Because the loopback shares one process, replication is trivial here; the same script's replication checks are proven on the VM in Task 12.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Tests/PlayMode/NetHarnessPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetHarnessPlayModeTests.cs.meta
git commit -m "Net: the headless harness with owner, observer, ticket and bad-token roles

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: The net-test client: options, view, controls, scene and Windows build

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetClientOptions.cs`; `Assets/_Project/Scripts/Net/Adapter/NetClientBootstrap.cs`, `NetClientView.cs`, `NetTestControls.cs`; `Assets/_Project/Editor/Net/NetClientSceneBuilder.cs`, `NetClientBuild.cs`; generated `Assets/_Project/Scenes/NetTestClient.unity` (+ meta); `Tools/build-netclient.sh`
- Test: `Assets/_Project/Tests/EditMode/NetClientOptionsTests.cs`, `Assets/_Project/Tests/PlayMode/NetClientScenePlayModeTests.cs`

**Interfaces:**
- Consumes: Task 4 `NetIntentSender`, Task 6 `NetUnitState`, Task 8 `NetClient`, Task 9 `NetHarness`.
- Produces:
  - `sealed class NetClientOptions` (Core): `static NetClientOptions Parse(IReadOnlyList<string> args, out List<string> warnings)`; `string Server` (default `127.0.0.1`), `int Port` (default 7770, range 1024..65535), `string Token` (default null), `string Harness` (null, or one of `a`, `b`, `ticket`, `badtoken`). Arguments: `-server <host>`, `-port <n>`, `-token <t>`, `-netharness <role>`. A missing, out-of-range or unknown value keeps the default and adds a warning that never contains the token.
  - `NetClientBootstrap`, `NetClientView`, `NetTestControls` (adapter MonoBehaviours, each with `internal void Initialize(...)` used by the scene builder; `NetClientBootstrap.Exit` is an `internal Action<int>`).
  - `Blackglass.EditorTools.NetClientSceneBuilder` (`Blackglass/Net/Build Net Test Client Scene`, `BuildFromCommandLine`) and `NetClientBuild` (`Blackglass/Net/Build Net Test Client`, `BuildFromCommandLine`; output `Builds/NetTestClient/NetTestClient.exe`).
  - `Tools/build-netclient.sh`: builds the player, prints `EXIT=0` and the path.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetClientOptionsTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetClientOptionsTests
    {
        static NetClientOptions Parse(out List<string> warnings, params string[] args) => NetClientOptions.Parse(args, out warnings);

        [Test]
        public void NoArguments_GiveTheDefaults()
        {
            var options = Parse(out var warnings);
            Assert.That(options.Server, Is.EqualTo("127.0.0.1"));
            Assert.That(options.Port, Is.EqualTo(7770));
            Assert.That(options.Token, Is.Null);
            Assert.That(options.Harness, Is.Null);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void EveryArgument_IsRead()
        {
            var options = Parse(out var warnings, "NetTestClient.exe", "-batchmode", "-server", "example.test", "-port", "7801", "-token", "abc", "-netharness", "a");
            Assert.That(options.Server, Is.EqualTo("example.test"));
            Assert.That(options.Port, Is.EqualTo(7801));
            Assert.That(options.Token, Is.EqualTo("abc"));
            Assert.That(options.Harness, Is.EqualTo("a"));
            Assert.That(warnings, Is.Empty);
        }

        [TestCase("1023")]
        [TestCase("65536")]
        [TestCase("x")]
        public void ABadPort_KeepsTheDefault_AndWarns(string value)
        {
            var options = Parse(out var warnings, "-port", value);
            Assert.That(options.Port, Is.EqualTo(7770));
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [TestCase("a")]
        [TestCase("b")]
        [TestCase("ticket")]
        [TestCase("badtoken")]
        public void TheFourRoles_AreAccepted(string role)
        {
            var options = Parse(out var warnings, "-netharness", role);
            Assert.That(options.Harness, Is.EqualTo(role));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void AnUnknownRole_IsIgnored_WithAWarning()
        {
            var options = Parse(out var warnings, "-netharness", "z");
            Assert.That(options.Harness, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void MissingValues_KeepTheDefaults_AndDoNotSwallowTheNextFlag()
        {
            var options = Parse(out var warnings, "-token", "-server", "host.test", "-netharness");
            Assert.That(options.Token, Is.Null);
            Assert.That(options.Server, Is.EqualTo("host.test"), "the flag after the missing value is still read");
            Assert.That(options.Harness, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(2));
        }
    }
}
```

`Assets/_Project/Tests/PlayMode/NetClientScenePlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using FishNet.Managing;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class NetClientScenePlayModeTests
    {
        const string ScenePath = "Assets/_Project/Scenes/NetTestClient.unity";

        Scene scene;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(scene.isLoaded, Is.True);
            yield return null;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            NetManagerFactory.DestroyAll();
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        [Test]
        public void TheScene_HoldsTheClientAndNothingOfTheGame()
        {
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).ToArray();
            Assert.That(all.OfType<Camera>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetClient>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetHarness>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetClientView>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetTestControls>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetClientBootstrap>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<MissionDirector>(), Is.Empty, "the client holds no simulation");
            Assert.That(all.OfType<CommandableUnit>(), Is.Empty);
            Assert.That(all.OfType<NetworkManager>(), Is.Empty, "the manager is built at runtime, when a connection is asked for");
        }

        [Test]
        public void WithoutAToken_TheClientDoesNotConnect()
        {
            var client = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NetClient>(true)).Single();
            Assert.That(client.State, Is.EqualTo(NetClientState.Idle));
            Assert.That(NetworkManager.Instances.Count, Is.EqualTo(0));
        }
    }
}
#endif
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode NetClientOptionsTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `EXIT=1` with `error CS0246` for `NetClientOptions`.

- [ ] **Step 3: Implement the options**

`Assets/_Project/Scripts/Net/Core/NetClientOptions.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Blackglass
{
    /// <summary>
    /// The net-test client's command line (networking experiment, spec section 3.5): -server, -port, -token and -netharness
    /// with one of the four harness roles. Pure: parsed from the argument list. Unknown arguments (Unity's own) are ignored; a
    /// missing, out-of-range or unknown value keeps the default and adds a warning that never contains the token.
    /// </summary>
    public sealed class NetClientOptions
    {
        public const string DefaultServer = "127.0.0.1";
        public const int DefaultPort = 7770;
        static readonly string[] Roles = { "a", "b", "ticket", "badtoken" };

        public string Server { get; private set; } = DefaultServer;
        public int Port { get; private set; } = DefaultPort;
        public string Token { get; private set; }
        /// <summary>The harness role to run, or null for the interactive client.</summary>
        public string Harness { get; private set; }

        public static NetClientOptions Parse(IReadOnlyList<string> args, out List<string> warnings)
        {
            var options = new NetClientOptions();
            warnings = new List<string>();
            if (args == null)
                return options;
            for (var i = 0; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "-server":
                        if (TryText(args, ref i, warnings, out var server))
                            options.Server = server;
                        break;
                    case "-token":
                        if (TryText(args, ref i, warnings, out var token))
                            options.Token = token;
                        break;
                    case "-port":
                        if (TryText(args, ref i, warnings, out var portText))
                        {
                            if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port >= 1024 && port <= 65535)
                                options.Port = port;
                            else
                                warnings.Add("-port must be an integer from 1024 to 65535; keeping " + options.Port.ToString(CultureInfo.InvariantCulture));
                        }
                        break;
                    case "-netharness":
                        if (TryText(args, ref i, warnings, out var role))
                        {
                            if (Array.IndexOf(Roles, role) >= 0)
                                options.Harness = role;
                            else
                                warnings.Add("-netharness must be one of a, b, ticket, badtoken");
                        }
                        break;
                }
            }
            return options;
        }

        // Reads the text after args[i]. A missing value, or another flag, warns and leaves the flag for the loop.
        static bool TryText(IReadOnlyList<string> args, ref int i, List<string> warnings, out string value)
        {
            var name = args[i];
            value = null;
            if (i + 1 >= args.Count || args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                warnings.Add(name + " has no value; keeping the default");
                return false;
            }
            i++;
            value = args[i];
            return true;
        }
    }
}
```
Run: `Tools/run-tests.sh EditMode "NetClientOptionsTests;NetSourceAuditTests" 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="12" passed="12"` (11 + the audit), `EXIT=0`. (`-port x` warns inside `TryText`'s caller, so a non-numeric port gives exactly one warning.)

- [ ] **Step 4: Implement the adapter components**

`Assets/_Project/Scripts/Net/Adapter/NetClientBootstrap.cs`:
```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Reads the command line, connects the client, and starts either the harness (-netharness) or the interactive controls.</summary>
    public sealed class NetClientBootstrap : MonoBehaviour
    {
        public const string Prefix = "[NetClient] ";

        [SerializeField] NetClient client;
        [SerializeField] NetHarness harness;
        [SerializeField] NetTestControls controls;

        internal Action<int> Exit = code => Application.Quit(code);

        internal void Initialize(NetClient netClient, NetHarness netHarness, NetTestControls netControls)
        {
            client = netClient;
            harness = netHarness;
            controls = netControls;
        }

        void Start()
        {
            var options = NetClientOptions.Parse(Environment.GetCommandLineArgs(), out var warnings);
            foreach (var warning in warnings)
                Debug.LogWarning(Prefix + "option ignored: " + warning);
            if (string.IsNullOrEmpty(options.Token))
            {
                Debug.LogWarning(Prefix + "no -token was given; not connecting");
                if (options.Harness != null)
                    Exit(NetHarness.ExitNoConnection);
                return;
            }
            Debug.Log(Prefix + FormattableString.Invariant($"connecting to {options.Server}:{options.Port}") +
                (options.Harness != null ? " as harness role " + options.Harness : string.Empty));
            if (!client.Connect(options.Server, (ushort)options.Port, options.Token))
            {
                Debug.LogWarning(Prefix + "the connection could not be started");
                if (options.Harness != null)
                    Exit(NetHarness.ExitNoConnection);
                return;
            }
            if (options.Harness != null)
            {
                controls.enabled = false;
                harness.Begin(options.Harness);
            }
        }
    }
}
```

`Assets/_Project/Scripts/Net/Adapter/NetClientView.cs`:
```csharp
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What the net-test client shows: a capsule on every replicated avatar (blue for the player's own units, green for other
    /// friendlies, red for hostiles), a top-down camera that frames them, and a text overlay with the connection, the round-trip
    /// time, the last refused request and each own unit's health and orders. The avatars carry no visuals; the capsules are
    /// built here, with a material the scene references so a player build always has its shader.
    /// </summary>
    public sealed class NetClientView : MonoBehaviour
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] NetClient client;
        [SerializeField] Camera viewCamera;
        [SerializeField] Material unitMaterial;

        readonly Dictionary<NetUnitState, GameObject> visuals = new Dictionary<NetUnitState, GameObject>();
        MaterialPropertyBlock block;

        internal void Initialize(NetClient netClient, Camera camera, Material material)
        {
            client = netClient;
            viewCamera = camera;
            unitMaterial = material;
        }

        void LateUpdate()
        {
            var states = FindObjectsByType<NetUnitState>(FindObjectsSortMode.None);
            var centre = Vector3.zero;
            var counted = 0;
            var extent = 0f;
            foreach (var state in states)
            {
                if (state.UnitId <= 0)
                    continue;
                if (!visuals.TryGetValue(state, out var visual) || visual == null)
                    visuals[state] = visual = Create(state);
                Style(visual, state);
                centre += state.transform.position;
                counted++;
            }
            if (counted == 0 || viewCamera == null)
                return;
            centre /= counted;
            foreach (var state in states)
            {
                if (state.UnitId > 0)
                    extent = Mathf.Max(extent, (state.transform.position - centre).magnitude);
            }
            viewCamera.orthographic = true;
            viewCamera.orthographicSize = Mathf.Max(8f, extent + 4f);
            viewCamera.transform.SetPositionAndRotation(centre + Vector3.up * 40f, Quaternion.Euler(90f, 0f, 0f));
        }

        GameObject Create(NetUnitState state)
        {
            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "Unit_" + state.UnitId;
            capsule.transform.SetParent(state.transform, false);
            capsule.transform.localPosition = new Vector3(0f, 1f, 0f);
            capsule.GetComponent<Renderer>().sharedMaterial = unitMaterial;
            return capsule;
        }

        void Style(GameObject visual, NetUnitState state)
        {
            visual.SetActive(state.Alive);
            var owned = state.UnitId >= client.FirstOwnedId && state.UnitId < client.FirstOwnedId + client.OwnedCount;
            var color = state.Hostile ? new Color(0.85f, 0.2f, 0.2f) : owned ? new Color(0.2f, 0.45f, 0.95f) : new Color(0.25f, 0.75f, 0.3f);
            if (block == null)
                block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, color);
            visual.GetComponent<Renderer>().SetPropertyBlock(block);
        }

        void OnGUI()
        {
            if (client == null)
                return;
            var text = new StringBuilder();
            text.Append("state ").Append(client.State);
            if (client.State == NetClientState.Refused)
                text.Append(" (").Append(client.Refusal).Append(')');
            text.Append("   round trip ").Append(client.RoundTripMs).Append(" ms");
            if (client.LastReject != NetRejectReason.None)
                text.Append("\nlast refused: ").Append(client.LastReject).Append(" (command ").Append(client.LastRejectCommand).Append(')');
            foreach (var state in FindObjectsByType<NetUnitState>(FindObjectsSortMode.None))
            {
                if (state.UnitId < client.FirstOwnedId || state.UnitId >= client.FirstOwnedId + client.OwnedCount)
                    continue;
                text.Append("\n#").Append(state.UnitId).Append("  hp ").Append(state.Health).Append('/').Append(state.MaxHealth)
                    .Append("  ").Append(state.Order).Append("  queue ").Append(state.Queue);
            }
            text.Append("\nWASD steer   click move (Shift queues)   click a red unit attack   X stop   1 2 pick unit");
            GUI.Label(new Rect(10f, 10f, 900f, 200f), text.ToString());
        }
    }
}
```

`Assets/_Project/Scripts/Net/Adapter/NetTestControls.cs`:
```csharp
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// The net-test client's controls (Input System): WASD steers (sent through NetIntentSender), a click on the ground moves
    /// (Shift queues it), a click on a red unit attacks it, X stops, 1 and 2 pick the unit. Input only requests; every order
    /// is a NetRequest the server may refuse. A ground click first releases any held steering, because CommandableUnit
    /// drops an order that is accepted while it is steered.
    /// </summary>
    public sealed class NetTestControls : MonoBehaviour
    {
        [SerializeField] NetClient client;
        [SerializeField] Camera viewCamera;

        readonly NetIntentSender sender = new NetIntentSender();
        InputAction move;
        InputAction click;
        InputAction stop;
        InputAction pickFirst;
        InputAction pickSecond;
        int selected;

        internal void Initialize(NetClient netClient, Camera camera)
        {
            client = netClient;
            viewCamera = camera;
        }

        void OnEnable()
        {
            move = new InputAction("Move", InputActionType.Value);
            move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            click = new InputAction("Click", InputActionType.Button, "<Mouse>/leftButton");
            stop = new InputAction("Stop", InputActionType.Button, "<Keyboard>/x");
            pickFirst = new InputAction("PickFirst", InputActionType.Button, "<Keyboard>/1");
            pickSecond = new InputAction("PickSecond", InputActionType.Button, "<Keyboard>/2");
            move.Enable();
            click.Enable();
            stop.Enable();
            pickFirst.Enable();
            pickSecond.Enable();
        }

        void OnDisable()
        {
            foreach (var action in new[] { move, click, stop, pickFirst, pickSecond })
            {
                if (action == null)
                    continue;
                action.Disable();
                action.Dispose();
            }
        }

        void Update()
        {
            if (client == null || client.State != NetClientState.Ready || client.OwnedCount == 0)
                return;
            var now = Time.realtimeSinceStartupAsDouble;
            if (pickFirst.WasPressedThisFrame())
                selected = 0;
            if (pickSecond.WasPressedThisFrame() && client.OwnedCount > 1)
                selected = 1;
            var unitId = client.FirstOwnedId + Mathf.Min(selected, client.OwnedCount - 1);

            var axes = move.ReadValue<Vector2>();
            if (sender.Next(new Vector3(axes.x, 0f, axes.y), now, out var direction))
                client.Send(new NetMoveIntent(unitId, direction));
            if (stop.WasPressedThisFrame())
                client.Send(new NetStop(unitId));
            if (click.WasPressedThisFrame() && Mouse.current != null && viewCamera != null)
                OnClick(unitId);
        }

        void OnClick(int unitId)
        {
            var ray = viewCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out var hit, 200f))
            {
                var target = hit.collider.GetComponentInParent<NetUnitState>();
                if (target != null && target.Hostile)
                {
                    client.Send(new NetAttack(unitId, target.UnitId));
                    return;
                }
            }
            if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var distance))
            {
                if (sender.ReleaseBeforeOrder(out var release))
                    client.Send(new NetMoveIntent(unitId, release));
                var append = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
                client.Send(new NetMoveTo(unitId, ray.GetPoint(distance), append));
            }
        }
    }
}
```

- [ ] **Step 5: Implement the editor builders and the build script**

`Assets/_Project/Editor/Net/NetClientSceneBuilder.cs`:
```csharp
#if UNITY_EDITOR
using System;
using FishNet.Managing.Object;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the net-test client's scene (networking experiment): a camera, a light, a ground plane and one object holding the
    /// client components. Rebuilt from scratch every run, so it is idempotent; no game scene is opened or changed.
    /// </summary>
    public static class NetClientSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/NetTestClient.unity";
        const string Root = "Assets/_Project/";

        [MenuItem("Blackglass/Net/Build Net Test Client Scene")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
            camera.orthographic = true;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 40f, 0f), Quaternion.Euler(90f, 0f, 0f));

            var light = new GameObject("Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            UnityEngine.Object.DestroyImmediate(ground.GetComponent<Collider>());
            ground.GetComponent<Renderer>().sharedMaterial = Load<Material>("Materials/Ground.mat");

            var holder = new GameObject("NetClient");
            var client = holder.AddComponent<NetClient>();
            client.Initialize(AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset"));
            var harness = holder.AddComponent<NetHarness>();
            harness.Initialize(client);
            var view = holder.AddComponent<NetClientView>();
            view.Initialize(client, camera, Load<Material>("Materials/Obstacle.mat"));
            var controls = holder.AddComponent<NetTestControls>();
            controls.Initialize(client, camera);
            holder.AddComponent<NetClientBootstrap>().Initialize(client, harness, controls);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Could not save " + ScenePath + ".");
            Debug.Log("NetClientSceneBuilder: saved " + ScenePath + ".");
        }

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

`Assets/_Project/Editor/Net/NetClientBuild.cs`:
```csharp
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the Windows net-test client player from NetTestClient.unity into Builds/NetTestClient/ (git-ignored). The Editor's
    /// build target must already be Windows Standalone; the build never switches it.
    /// </summary>
    public static class NetClientBuild
    {
        public const string OutputPath = "Builds/NetTestClient/NetTestClient.exe";

        [MenuItem("Blackglass/Net/Build Net Test Client")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry: exits 0 on success, 1 on any failure.</summary>
        public static void BuildFromCommandLine() => EditorApplication.Exit(Build() ? 0 : 1);

        static bool Build()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                Debug.LogError("[NetClientBuild] the active build target is " + EditorUserBuildSettings.activeBuildTarget + ", not StandaloneWindows64.");
                return false;
            }
            var folder = Path.GetDirectoryName(OutputPath);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { NetClientSceneBuilder.ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            });
            Debug.Log("[NetClientBuild] " + report.summary.result + " " + (report.summary.totalSize / 1048576) + " MB " + OutputPath);
            return report.summary.result == BuildResult.Succeeded;
        }
    }
}
#endif
```

`Tools/build-netclient.sh` (follow the conventions of `Tools/build-server.sh`; LF line endings):
```bash
#!/usr/bin/env bash
# Builds the Windows net-test client player (networking experiment). The Unity Editor must be closed.
# Usage: Tools/build-netclient.sh      Output: Builds/NetTestClient/NetTestClient.exe
set -u
root_unix="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$root_unix" && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
log="$root/Logs/NetClientBuild.log"
mkdir -p "$root_unix/Logs" "$root_unix/Builds"
rm -rf "$root_unix/Builds/NetTestClient"

"$unity" -batchmode -quit -projectPath "$root" \
  -executeMethod Blackglass.EditorTools.NetClientBuild.BuildFromCommandLine -logFile "$log"
code=$?

if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
grep -h "\[NetClientBuild\]" "$log" 2>/dev/null
if [ "$code" -ne 0 ] || [ ! -f "$root_unix/Builds/NetTestClient/NetTestClient.exe" ]; then
  echo "BUILD FAILED (Unity exit $code). See $log"
  echo "EXIT=1"
  exit 1
fi
echo "client=$root_unix/Builds/NetTestClient/NetTestClient.exe"
echo "EXIT=0"
```
Run `bash -n Tools/build-netclient.sh` (Expected: no output).

- [ ] **Step 6: Build the scene and run the tests**

Run: `Tools/run-method.sh Blackglass.EditorTools.NetClientSceneBuilder.BuildFromCommandLine 2>&1 | tail -3` (Expected `EXIT=0`), then:
`Tools/run-tests.sh PlayMode NetClientScenePlayModeTests 2>&1 | sed 's/Unity requires.*behavior\.//' | tail -4`
Expected: `total="2" passed="2"`, `EXIT=0`.
Then build the player: `Tools/build-netclient.sh 2>&1 | tail -4` (Expected `client=.../Builds/NetTestClient/NetTestClient.exe` and `EXIT=0`), and smoke-run it with no token: `Builds/NetTestClient/NetTestClient.exe -batchmode -nographics -logFile Logs/NetClientSmoke.log` for a few seconds with `timeout 15` (Expected: the log contains `[NetClient] no -token was given; not connecting`, and no exception). Check `git status`: new files only plus the unchanged owner files; restore any unrelated `ProjectSettings.asset` `preloadedAssets` entry as in Task 6.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Net Assets/_Project/Editor/Net Assets/_Project/Scenes/NetTestClient.unity Assets/_Project/Scenes/NetTestClient.unity.meta Assets/_Project/Tests/EditMode/NetClientOptionsTests.cs Assets/_Project/Tests/EditMode/NetClientOptionsTests.cs.meta Assets/_Project/Tests/PlayMode/NetClientScenePlayModeTests.cs Assets/_Project/Tests/PlayMode/NetClientScenePlayModeTests.cs.meta Tools/build-netclient.sh
git commit -m "Net: the net-test client scene, controls, view, options and Windows build

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 11: VM and local scripts

**Files:**
- Create: `Tools/LinuxServer/run-net.sh`, `Tools/LinuxServer/make-dev-tokens.sh`, `Tools/run-net-harness.sh`
- Modify: `Tools/LinuxServer/smoke-run.sh`

**Interfaces:**
- Consumes: the server's `-net`, `-netQuiet`, `-netPort`, `-netTokens` options and its `[Net] listening` log line; the client's `-server`, `-port`, `-token`, `-netharness`; the existing `Tools/build-server.sh` (copies every `Tools/LinuxServer/*.sh` into the build, stripping CR) and `Tools/deploy-server.sh`.
- Produces:
  - `run-net.sh start|stop|status` (on the VM): starts `BlackglassServer.x86_64 -batchmode -nographics -net -netQuiet -netPort $NET_PORT -netTokens $BLACKGLASS_TOKENS` detached under `timeout` (default 3600 s), writes `net-server.pid` and `<UTC>-<rev>-net.log` under `/opt/blackglass/logs/`, waits up to 30 s for the `[Net] listening` line, and reports a server that exited. Exit 0 on success, 1 on failure, 2 on a missing token file.
  - `make-dev-tokens.sh [--force]` (on the VM): writes `/opt/blackglass/dev-tokens.txt` (mode 600) with three random tokens (`account-a ... squad`, `account-b ... none`, `account-ticket ... none other-attempt`) if it does not exist. It never prints a token.
  - `Tools/run-net-harness.sh [host]` (local, Git Bash): reads the tokens from the VM into shell variables, opens `ssh -f -N -L 7770:127.0.0.1:7770`, runs the client player for roles `a`, `b`, `ticket` and `badtoken`, prints one `role=… exit=… BLACKGLASS NET HARNESS …` line per role, closes the tunnel, and exits non-zero if any role failed. Logs go to `Builds/NetHarnessLogs/<UTC>/` (git-ignored).
  - `smoke-run.sh` appends `${SMOKE_ARGS:-}` to the server's command line.

These scripts have no unit tests; their checks are `bash -n` here and the real run in Task 12.

- [ ] **Step 1: Write the VM scripts**

`Tools/LinuxServer/make-dev-tokens.sh` (LF endings; `.gitattributes` already forces LF for `Tools/LinuxServer/*.sh`):
```bash
#!/usr/bin/env bash
# Creates the development token file for the networked server if it is missing (networking experiment, decision 046).
# Usage: make-dev-tokens.sh [--force]    File: $BLACKGLASS_TOKENS (default /opt/blackglass/dev-tokens.txt), mode 600.
# The tokens are random fixtures and are never printed. Read them with: ssh <host> cat /opt/blackglass/dev-tokens.txt
set -eu
tokens="${BLACKGLASS_TOKENS:-/opt/blackglass/dev-tokens.txt}"
if [ -f "$tokens" ] && [ "${1:-}" != "--force" ]; then
  echo "token file exists: $tokens (use --force to replace it)"
  exit 0
fi
mkdir -p "$(dirname "$tokens")"
newtoken() { head -c 24 /dev/urandom | base64 | tr -d '/+=\n'; }
umask 077
{
  echo "# Development tokens (networking experiment). Format: account token squad|none [attempt]"
  echo "account-a $(newtoken) squad"
  echo "account-b $(newtoken) none"
  echo "account-ticket $(newtoken) none other-attempt"
} > "$tokens"
chmod 600 "$tokens"
echo "wrote $tokens (3 accounts)"
```

`Tools/LinuxServer/run-net.sh`:
```bash
#!/usr/bin/env bash
# Starts, stops or reports the networked mission server on this machine (networking experiment, decision 046).
# Usage: run-net.sh start|stop|status
# Environment: NET_PORT (7770), NET_SECONDS (3600: `timeout` ends the process), BLACKGLASS_LOGS, BLACKGLASS_TOKENS.
# The server listens on 127.0.0.1 only; reach it with `ssh -L`. -netQuiet makes the harness repeatable (decision 046).
set -u
dir="$(cd "$(dirname "$0")" && pwd)"
logs="${BLACKGLASS_LOGS:-/opt/blackglass/logs}"
tokens="${BLACKGLASS_TOKENS:-/opt/blackglass/dev-tokens.txt}"
port="${NET_PORT:-7770}"
limit="${NET_SECONDS:-3600}"
pidfile="$logs/net-server.pid"

running() { [ -f "$pidfile" ] && kill -0 "$(cat "$pidfile")" 2>/dev/null; }

case "${1:-}" in
  start)
    [ -f "$tokens" ] || { echo "no token file at $tokens; run make-dev-tokens.sh first"; exit 2; }
    if running; then echo "already running (pid $(cat "$pidfile"))"; exit 0; fi
    mkdir -p "$logs"
    rev=$(awk -F= '$1=="revision"{print substr($2,1,7)}' "$dir/build-info.txt" 2>/dev/null)
    log="$logs/$(date -u +%Y%m%dT%H%M%SZ)-${rev:-unknown}-net.log"
    setsid nohup timeout --kill-after=10 "$limit" "$dir/BlackglassServer.x86_64" -batchmode -nographics -logFile "$log" \
      -net -netQuiet -netPort "$port" -netTokens "$tokens" > /dev/null 2>&1 < /dev/null &
    echo $! > "$pidfile"
    for _ in $(seq 1 30); do
      if grep -q "\[Net\] listening" "$log" 2>/dev/null; then
        echo "listening on 127.0.0.1:$port"
        echo "log=$log"
        exit 0
      fi
      if grep -q "exit code=" "$log" 2>/dev/null; then
        echo "the server exited:"
        grep -E "\[Server\] exit|\[Net\]" "$log" | tail -5
        exit 1
      fi
      sleep 1
    done
    echo "no 'listening' line within 30 s; see $log"
    exit 1
    ;;
  stop)
    if running; then
      kill "$(cat "$pidfile")"
      for _ in $(seq 1 30); do
        running || break
        sleep 1
      done
    fi
    if running; then echo "still running after 30 s"; exit 1; fi
    rm -f "$pidfile"
    echo "stopped"
    ;;
  status)
    if running; then
      echo "running (pid $(cat "$pidfile"))"
      ss -lnt 2>/dev/null | grep ":$port " || true
    else
      echo "not running"
    fi
    ;;
  *)
    echo "usage: run-net.sh start|stop|status"
    exit 2
    ;;
esac
```

In `Tools/LinuxServer/smoke-run.sh` change the launch line
```bash
"$dir/BlackglassServer.x86_64" -batchmode -nographics -logFile "$log" &
```
to
```bash
# SMOKE_ARGS adds server arguments (for example -net -netTokens <file>) to repeat the run with another mode.
"$dir/BlackglassServer.x86_64" -batchmode -nographics -logFile "$log" ${SMOKE_ARGS:-} &
```
and add `SMOKE_ARGS` to the usage comment at the top (`#   SMOKE_ARGS="-net ..." smoke-run.sh 600`).

- [ ] **Step 2: Write the local harness script**

`Tools/run-net-harness.sh` (LF endings; follow `Tools/build-server.sh`):
```bash
#!/usr/bin/env bash
# Runs the net-test client's harness roles against the networked server on the VM through an SSH tunnel (networking experiment).
# Usage: Tools/run-net-harness.sh [host]    (default host: bg-dev-mission01; port: $NET_PORT or 7770)
# Needs the server started by run-net.sh on the VM and Builds/NetTestClient/NetTestClient.exe (Tools/build-netclient.sh).
# Logs: Builds/NetHarnessLogs/<UTC>/ (git-ignored). The tokens are read from the VM into shell variables and never printed.
set -u
root="$(cd "$(dirname "$0")/.." && pwd)"
host="${1:-bg-dev-mission01}"
port="${NET_PORT:-7770}"
exe="$root/Builds/NetTestClient/NetTestClient.exe"
tokenfile="${BLACKGLASS_TOKENS:-/opt/blackglass/dev-tokens.txt}"
logs="$root/Builds/NetHarnessLogs/$(date -u +%Y%m%dT%H%M%SZ)"
[ -x "$exe" ] || { echo "build the client first: Tools/build-netclient.sh"; exit 2; }
mkdir -p "$logs"

text=$(ssh -o BatchMode=yes "$host" "cat $tokenfile") || { echo "cannot read the token file on $host"; exit 2; }
token_of() { printf '%s\n' "$text" | awk -v a="$1" '$1==a {print $2; exit}'; }
tok_a=$(token_of account-a)
tok_b=$(token_of account-b)
tok_t=$(token_of account-ticket)
if [ -z "$tok_a" ] || [ -z "$tok_b" ] || [ -z "$tok_t" ]; then
  echo "the token file lacks account-a, account-b or account-ticket"
  exit 2
fi

ssh -f -N -o ExitOnForwardFailure=yes -o BatchMode=yes -L "$port:127.0.0.1:$port" "$host" || { echo "the tunnel could not be opened (is port $port free here?)"; exit 2; }
tunnel_pid=$(pgrep -n -f "ssh -f -N .*-L $port:127.0.0.1:$port")
trap 'kill "$tunnel_pid" 2>/dev/null' EXIT

status=0
run_role() {
  local role="$1" token="$2" log="$logs/harness-$1.log"
  timeout 240 "$exe" -batchmode -nographics -logFile "$log" -server 127.0.0.1 -port "$port" -token "$token" -netharness "$role"
  local code=$?
  printf 'role=%s exit=%s  %s\n' "$role" "$code" "$(grep -h 'BLACKGLASS NET HARNESS' "$log" | tail -1)"
  if [ "$code" -ne 0 ]; then status=1; fi
}
run_role a "$tok_a"
run_role b "$tok_b"
run_role ticket "$tok_t"
run_role badtoken "not-a-real-token"
echo "logs=$logs"
exit $status
```

- [ ] **Step 3: Check the scripts**

```bash
bash -n Tools/LinuxServer/run-net.sh && bash -n Tools/LinuxServer/make-dev-tokens.sh && bash -n Tools/run-net-harness.sh && bash -n Tools/LinuxServer/smoke-run.sh && echo "syntax ok"
for f in Tools/LinuxServer/run-net.sh Tools/LinuxServer/make-dev-tokens.sh Tools/run-net-harness.sh; do printf '%s CR bytes: ' "$f"; tr -cd '\r' < "$f" | wc -c; done
git ls-files --stage Tools/LinuxServer | head -3
```
Expected: `syntax ok`; every script reports `CR bytes: 0` (if one does not, convert it with `tr -d '\r'` and check `.gitattributes`); after `git add` the three new files are mode 100755 (`git update-index --chmod=+x <file>` if not).
Also confirm `Tools/run-net-harness.sh` finds `pgrep`: `command -v pgrep` prints a path. If it does not, replace the `tunnel_pid=` line with `tunnel_pid=$(ps -W | awk '/ssh/ {print $1}' | tail -1)` after checking the output of `ps -W | grep ssh` by hand, and ledger the change.

- [ ] **Step 4: Commit**

```bash
git add Tools/LinuxServer/run-net.sh Tools/LinuxServer/make-dev-tokens.sh Tools/LinuxServer/smoke-run.sh Tools/run-net-harness.sh
git update-index --chmod=+x Tools/LinuxServer/run-net.sh Tools/LinuxServer/make-dev-tokens.sh Tools/run-net-harness.sh
git commit -m "Net: VM start/stop and token scripts, the local tunnel harness runner, SMOKE_ARGS

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 12: VM acceptance run, documents and final verification

**Files:**
- Modify: `Docs/Decisions.md` (append to decision 046), `Docs/OnlineReadinessAudit.md` (append §9.3), `Docs/DedicatedServer.md` (a networked-mode section)
- Scratch (not committed): `.claude/scratchpad/networking/part2-results.md`

**Interfaces:** consumes everything above. Produces the evidence and the documents; no new code.

This task is a run, not a TDD cycle. Each step states its expected output; a deviation is investigated, fixed in the owning task's code with a test that fails first, and ledgered.

- [ ] **Step 1: Full suites on the branch**

```bash
Tools/run-tests.sh EditMode > .claude/scratchpad/networking/part2-editmode.txt 2>&1; tail -3 .claude/scratchpad/networking/part2-editmode.txt | sed 's/Unity requires.*behavior\.//'
Tools/run-tests.sh PlayMode > .claude/scratchpad/networking/part2-playmode.txt 2>&1; tail -3 .claude/scratchpad/networking/part2-playmode.txt | sed 's/Unity requires.*behavior\.//'
```
Expected: EditMode `total="1366" passed="1366"`; PlayMode `total="1403 (1377 plus the known flaky test, so 1402 passing plus that one when it flakes)"` with no failure other than the known flaky particle test (re-run it alone to confirm). 1366 and 1403 (1377 plus the known flaky test, so 1402 passing plus that one when it flakes) are the totals in "Test counts" below. The PlayMode run takes well over ten minutes: run it in the background and wait for its notification.

**Test counts** (new tests per task; baselines EditMode 1306, PlayMode 1377 including the one flaky test):
| Task | New EditMode | New PlayMode |
|---|---|---|
| 1 token file, budget | 12 (7 + 5) | 0 |
| 2 admission | 11 | 0 |
| 3 session host | 9 | 0 |
| 4 order kinds, intent pacing | 6 (1 + 5) | 0 |
| 5 wire | 8 | 0 |
| 6 factory, avatar prefab | 2 | 3 |
| 7 server adapter | 1 (`With_ReplacesTheNetOptions`) | 8 |
| 8 client, loopback | 0 | 7 |
| 9 harness | 0 | 6 |
| 10 client scene | 11 | 2 |
| **Total** | **60** | **26** |

- [ ] **Step 2: Build the Linux server and the Windows client, deploy the server**

```bash
Tools/build-server.sh 2>&1 | tail -6
Tools/build-netclient.sh 2>&1 | tail -3
Tools/deploy-server.sh 2>&1 | tail -4
```
Expected: the server archive path and sha256 with `EXIT=0`; `client=.../Builds/NetTestClient/NetTestClient.exe` with `EXIT=0`; `deployed:` with the build info (the revision is the branch head, marked dirty while the owner's settings files are modified). The Editor's build target is restored to Windows after the server build (the client build refuses to run otherwise).

- [ ] **Step 3: Checkpoint A regression, tokens, start the networked server**

```bash
ssh bg-dev-mission01 '/opt/blackglass/server/run-diagnostic.sh' 2>&1 | grep -E "DIAGNOSTIC|exit="
ssh bg-dev-mission01 '/opt/blackglass/server/make-dev-tokens.sh'
ssh -f -n bg-dev-mission01 '/opt/blackglass/server/run-net.sh start > /opt/blackglass/logs/run-net.out 2>&1'
```
Wait about 20 seconds, then `ssh bg-dev-mission01 'cat /opt/blackglass/logs/run-net.out'`.
Expected: `BLACKGLASS DIAGNOSTIC PASS ...` and `exit=0` (Checkpoint A is unchanged without `-net`); `wrote /opt/blackglass/dev-tokens.txt (3 accounts)` (or `token file exists`); the start output `listening on 127.0.0.1:7770` and a `log=` path. (`ssh -f -n` is used because a plain `ssh` session stays open while the detached server runs.)

- [ ] **Step 4: Check that nothing is exposed**

```bash
ssh bg-dev-mission01 'ss -lntu | grep -E ":7770|:7777" || echo "nothing else"; stat -c "%a %U" /opt/blackglass/dev-tokens.txt'
ssh bg-dev-mission01 'grep -E "\[Net\]|\[Server\] (MISSION READY|heartbeat)" $(ls -t /opt/blackglass/logs/*-net.log | head -1) | head -8 | cut -c1-200'
```
Expected: exactly one listener, `tcp LISTEN ... 127.0.0.1:7770`, no UDP line and no `0.0.0.0` or public address; the token file is `600 root`; the log shows `[Net] server Starting`, `[Net] server Started`, `[Net] listening 127.0.0.1:7770 websockets attempt=... quiet=yes` after `MISSION READY`. Record the output in `part2-results.md`.

- [ ] **Step 5: Run the harness through the tunnel**

```bash
Tools/run-net-harness.sh 2>&1 | tail -8
```
Expected (a few minutes): four lines
```
role=a exit=0  BLACKGLASS NET HARNESS PASS role=a steps=... latencyMedianMs=... roundTripMs=...
role=b exit=0  BLACKGLASS NET HARNESS PASS role=b steps=...
role=ticket exit=0  BLACKGLASS NET HARNESS PASS role=ticket steps=...
role=badtoken exit=0  BLACKGLASS NET HARNESS PASS role=badtoken steps=...
```
and `logs=...`. These cover A2 (steering and the queued moves), A3's six cases, A4 (the attack lowers the replicated health; no message type changes health), A9 (account B refused for every payload; the ticket for another attempt refused). If a role fails, read its log (`Builds/NetHarnessLogs/<UTC>/harness-<role>.log`, the `[Harness]` lines name the step) and the server log's `[Net]` lines; fix the code in the owning task with a failing test first. Do not loosen a check to make it pass unless the real behaviour proves the check wrong, and ledger that as a ruling.
Record in `part2-results.md`: the four summary lines, `latencyMedianMs` and `roundTripMs` from role `a`, and the A2 verdict against the 150 ms target (the tunnel adds latency; report the measured number and whether it met 150 ms, without adjusting the target).

- [ ] **Step 6: What the server saw, and a clean stop**

```bash
ssh bg-dev-mission01 'grep -E "\[Net\]" $(ls -t /opt/blackglass/logs/*-net.log | head -1) | cut -c1-180 | tail -25'
ssh bg-dev-mission01 '/opt/blackglass/server/run-net.sh stop; /opt/blackglass/server/run-net.sh status'
ssh bg-dev-mission01 'tail -4 $(ls -t /opt/blackglass/logs/*-net.log | head -1) | cut -c1-160'
```
Expected: `welcomed account=account-a owns=2`, `welcomed account=account-b owns=0`, two `hello refused ... reason=WrongAttempt` and `reason=UnknownToken` lines, rate-limited `rejected client=... reason=...` lines (at most one per second per client), no exception text; then `stopped`, `not running`, and a final `[Server] quitting`. No token appears in the log (`grep -c "$(…)"` is not needed: the loopback test pins it; a manual check is `grep -ci "account-a [A-Za-z0-9]" <log>` printing 0).

- [ ] **Step 7: Ten-minute idle run with the final code (A1's server half)**

```bash
ssh -f -n bg-dev-mission01 'cd / && SMOKE_ARGS="-net -netQuiet -netTokens /opt/blackglass/dev-tokens.txt" setsid nohup /opt/blackglass/server/smoke-run.sh 600 > /opt/blackglass/logs/smoke-run-net.out 2>&1 < /dev/null &'
```
About 11 minutes later: `ssh bg-dev-mission01 'cat $(ls -t /opt/blackglass/logs/*-smoke-summary.txt | head -1)'`.
Expected: `stop: SIGTERM, exit status 0`, `launch to MISSION READY` about 5 s, heartbeat fps 60.0 on every line, `cpu after ready` mean at most 3.0 % and max below 5 %, `rss peak` at most 150 MB, `Exception` and `error` counts 0. Compare with decision 046's table (FishNet idle: CPU 1.9 mean / 2.2 max, RSS peak 115.2 MB). Record it.

- [ ] **Step 8: Documents**

Append to decision 046 in `Docs/Decisions.md` (match its layout; do not edit the spike's text). A new final subsection **"Part 2 results (with the run's date)"** holding: the files and options added (one line each); the measured harness results and the A2 figures with the tunnel caveat; the idle run next to the spike's table; the limits as built (60 requests per second with a burst of 60, 200 refused or dropped messages in 10 s end a connection, 5 s to say hello, one connection per token, the server on `127.0.0.1` only, the manager created only with `-net`); what is not covered (the list in "Rulings made while planning" item 13). Every number comes from `part2-results.md`; do not estimate any.

Append **§9.3** to `Docs/OnlineReadinessAudit.md` without altering earlier sections: title "9.3 Networking experiment, first slice (with the run's date)"; one paragraph saying what it is and that it is **neither Checkpoint A (no networking) nor Phase 13 (no accounts, backend, settlement)**; a table of acceptance checks A1 to A10 with columns "Covered", "Evidence" (A2, A3 six of nine, A4, A9, A10 covered; A1 server half; A5 to A8 and the three deferred A3 requests not covered); and the findings that matter for the next slice (hidden information needs per-connection visibility, the headless auto-start and frame-rate overrides, the one-component-per-file rule, log quietness, the 150 ms figure through the tunnel).

Add a **"Networked mode"** section to `Docs/DedicatedServer.md`: the options (`-net`, `-netPort`, `-netTokens`, `-netQuiet`), the token file format and how to create it (`make-dev-tokens.sh`), starting and stopping (`run-net.sh start|stop|status` through `ssh -f -n`), the tunnel command, building and running the client (`Tools/build-netclient.sh`, `Tools/run-net-harness.sh`, the four roles and their exit codes 0/1/2), the interactive controls, the exposure check (`ss -lntu`), and the log lines (`[Net] listening`, `welcomed`, `hello refused`, `rejected`, the heartbeat `net=` suffix). Keep it to the facts that were true in this run.

- [ ] **Step 9: Final verification and commit**

Re-run the two full suites if any code changed since Step 1 (otherwise the Step 1 results stand), check `git status` (only the three documents are modified; nothing under `Builds/` or the token file), then:
```bash
git add Docs/Decisions.md Docs/OnlineReadinessAudit.md Docs/DedicatedServer.md
git commit -m "Docs: networking experiment part 2 results, audit 9.3 and the networked-mode run guide

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```
Report: the build commands and artifact locations, the tests run, Editor validation, Linux build and Linux runtime as separate sections, the harness and idle-run evidence, the rulings and deferred minors, and the blockers and next action (hidden information and per-connection visibility, then Phase 13), as the part 1 report did.
