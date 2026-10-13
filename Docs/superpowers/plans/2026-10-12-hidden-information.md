# Networking experiment, hidden information and per-connection visibility (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the dedicated server send each connection only the hostile state its intelligence view allows (nothing live while hidden, one frozen last-known record), and prove it on decoded client state through the real Windows-client-to-Linux-VM harness.

**Architecture:** A library-free projection (`NetVisibility`, `Scripts/Net/Core`) keeps, per connection and hostile, `Unknown`/`Observed`/`LastKnown` with a frozen position, fed by one source: the real squad model (`NetSquadIntel`, which fails closed) or a development-only fixture (`NetFixture`). FishNet only carries the result: a `NetVisibilityCondition` on the avatar prefab asks the projection, the server rebuilds observers on transitions, and a `NetKnowledgeMessage` carries the last-known record to that connection only. The gate answers `UnknownUnit` for hidden hostiles, and a small guard ends gate-accepted attacks whose target the issuing connection stops observing.

**Tech Stack:** Unity 6000.3.25f1, C#, NUnit, FishNet 4.7.3 with FishyUnityTransport 2.0.0-pre.2 (WebSocket) on Unity Transport 2.7.4 (decision 046), Git Bash scripts, OpenSSH.

**Spec:** `Docs/superpowers/specs/2026-10-12-hidden-information-design.md` (approved by the owner on 2026-10-12, including its two rulings). Read it before starting a task.

## Global Constraints

- Unity **6000.3.25f1**; the server target is Linux Dedicated Server with **Mono**.
- `Scripts/Net/Core/` references **no networking library**: `FishNet`, `Unity.Netcode`, `Unity.Networking`, `Unity.Transport`, `Unity.Collections`, `Mirror`, `LiteNetLib`, `System.Net.Sockets` (`NetSourceAuditTests`). FishNet types live only in `Scripts/Net/Adapter/` and the editor builders.
- **Fail closed everywhere:** a missing visibility reference, a missing or disabled `IntelligenceService`, a null model or fog off must hide every hostile, never reveal them.
- **Fixture code** (`NetFixture`, `NetFixtureContainer`, and every use of them) sits inside `#if UNITY_EDITOR || DEVELOPMENT_BUILD`. The release server build contains none of it and refuses `-netFixture` with exit code 2.
- The server listens on **`127.0.0.1` only**; clients reach it with `ssh -L` through the `bg-dev-mission01` alias. Never write the VM's address or key names into the repo.
- **One MonoBehaviour, NetworkBehaviour or ScriptableObject per file, named after its class** (a mismatch kills the Linux server at start, decision 046). `NetHarness` may be split into a second `partial` file because `NetHarness.cs` still holds and names the class.
- **No client scene, HUD, input or gameplay change**, except the new `CommandableUnit.DropCurrent` (spec §3.5). AI, companion assist, AutoRetaliate and abilities are untouched.
- Never send the seed, spawn plans, mission plans or container contents before a search.
- Never committed: `Builds/`, build output, tokens, logs, keys, the owner's two modified files (`Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset`) and `.claude/`. Use `git add <paths>` with explicit paths, never `git add -A`.
- Work stays on branch **`networking-hidden-info`** (off `main` `3dce9ac`; the spec is commit `235673b`). Do not push or merge.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. `git add` includes the `.meta` files Unity creates beside new files and folders.
- Baselines on `main` `3dce9ac`: **EditMode 1366/1366, PlayMode 1403/1403**. `AreaPresentationPlayModeTests.TheGeneratedExplosion_ScalesItsExtentWithTheRadius` is known to flake (unseeded particles): when it is the only failure, re-run it alone; if it then passes, record it as flaky, not failed.
- Unity runs one instance per project: tests, builds and method runs happen one at a time with the Editor closed. `Tools/run-tests.sh EditMode|PlayMode [filter]` prints a result line and `EXIT=` (0 passed, 1 compile error, 2 failures); filters join test or class names with `;`. Strip the licence noise with `| sed 's/Unity requires.*behavior\.//'`. `Tools/run-method.sh Namespace.Class.Method` runs one editor method.
- Never use `?.` or `??` on UnityEngine objects (use `== null`). Format numbers in log text with `FormattableString.Invariant`, one call per interpolated string.
- Do not wrap commands in `bash -c '...'` inside Bash tool calls.
- Scratch output (test logs, VM results) goes to `.claude/scratchpad/hidden-info/` (never committed).

## FishNet facts this plan relies on (read from the installed package, 2026-10-12)

| Need | Member |
|---|---|
| A condition | `FishNet.Observing.ObserverCondition` (ScriptableObject): `public NetworkObject NetworkObject` (set by `Initialize`), `abstract bool ConditionMet(NetworkConnection connection, bool currentlyAdded, out bool notProcessed)`, `abstract ObserverConditionType GetConditionType()` (`FishNet.Observing.ObserverConditionType.Normal` is checked only on rebuilds). |
| The observer component | `FishNet.Observing.NetworkObserver`: serialized `_overrideType` (`ConditionOverrideType.IgnoreManager = 3`) and `_observerConditions` (list). `Initialize` instantiates a copy of each condition per object. The owner of an object always observes it; avatars have no owner. |
| Rebuilds | `ServerManager.Objects.RebuildObservers(NetworkObject nob, NetworkConnection conn, bool timedOnly = false)` spawns or despawns that object for that connection (the spawn carries the current SyncVars and transform); `RebuildObservers(NetworkConnection conn, bool timedOnly = false)` re-evaluates every object for one connection. |
| Observers | `NetworkObject.Observers` (`HashSet<NetworkConnection>`); `NetworkObject.Owner.IsValid` is false for an object with no owner. |
| Client side | `NetworkBehaviour.IsClientInitialized`: true while the object is spawned for this client. In host mode an object the host client does not observe still exists in the scene, so client code must filter on this flag (verified in Task 11, Step 3). |
| Broadcasts | an unregistered client-to-server broadcast is skipped silently, so the server registers a handler for every client message it must count. |
| Transform authority | `NetworkTransform` serialized `_clientAuthoritative` (default true). |

## Review Focus

The spec implies these conditions but no task's main tests exercise them. Each is pinned by a named test in the owning task.

1. **Every friendly is dead.** A connection that owns nothing must still receive no squad knowledge (the "owns every living friendly" rule must not pass vacuously). Pinned: `NetSquadIntelTests.WhenEveryFriendlyIsDead_AConnectionOwningNothing_StillGetsNothing` (Task 2).
2. **Fixture messages before hello, or as a flood.** Before hello they are ignored; a flood is dropped and then disconnected; the test identity gets no budget exemption. Pinned: `NetFixtureIdentityTests.AFixtureMessageBeforeHello_IsIgnored`, `AFixtureFlood_IsDropped_ThenDisconnects` (Task 5).
3. **A client leaves while it observes a hostile or has a guarded attack.** Its view, grant and records go; the other connection is unchanged; nothing throws. Pinned: `NetVisibilityTests.ARemovedConnection_IsForgotten_AndTheOtherIsUnchanged` (Task 1), `NetAttackGuardPlayModeTests.Forget_DropsOnlyThatConnectionsRecords` (Task 4), `NetFixtureTests.Forget_DropsTheGrantAndTheSearch` (Task 6).
4. **A hostile dies while hidden, or while observed.** Hidden: the record stays frozen and no alive state leaks. Observed: the death is witnessed and the avatar stays. Pinned: `NetVisibilityTests.AHostileThatDiesWhileHidden_KeepsItsRecord_AndNothingNew`, `AHostileThatDiesWhileObserved_StaysObserved` (Task 1).
5. **A hostile's own orders** (set by the server, AI, or a test) must never reach any client through the avatar. Pinned: `NetVisibilityServerPlayModeTests.AHostileAvatar_NeverCarriesOrders` (Task 8).

## Rulings made while planning

Each is open to the owner; the cost if wrong is stated.

1. **The projection runs every server frame**, not on the intelligence pass. Changes are sent only on transitions, so the cost is one source query per connection and hostile per frame (two connections, one hostile here). Cost if wrong: a timer in `NetServer.Update`.
2. **The last-known record is the position last sent while observed**, not the intelligence model's own last pass position. It is what the client last saw. Cost if wrong: one line in `NetVisibility.Update`.
3. **`NetKnowledgeMessage` is sent on every transition**, including Unknown to Observed (with a zero position), so the client's record logic has one rule. Cost if wrong: one condition in `NetServer.Publish`.
4. **The fixture's hostile is the first hostile** (`friendlyCount + 1`), its walk points are sampled from the NavMesh at 4 m and 7 m around its spawn, at least 3 m apart, and startup fails if fewer than two are found. Cost if wrong: constants in `NetFixture`.
5. **The fixture container's contents** are two fixed entries: `fixture-medkit` ×2 (`fixture-item-0001`) and `fixture-rifle-ammo` ×30 (`fixture-item-0002`). They are labelled as fixtures and name no real catalogue item. Cost if wrong: the array in `NetFixtureContainer`.
6. **A fixture verb that changes nothing** (an unknown verb, a search with no living owned unit) consumes budget, is logged at most once a second, and is not counted as abuse. Cost if wrong: one call to the budget's refusal counter.
7. **The client keeps the first 4096 decoded messages and avatar sightings** and marks the record as truncated beyond that; the harness fails on a truncated record. Cost if wrong: one constant.
8. **The harness's B role finishes on fixture step 6**, A's last verb (Observe, MoveHostile, Unobserve, MoveHostile, Observe, Search). Cost if wrong: one constant shared by both roles.
9. **The development server build** is the release build plus `BuildOptions.Development`, archived as `blackglass-server-linux-<rev>[-dirty]-fixture.tar.gz`, with `variant=fixture` in `build-info.txt` (`variant=release` otherwise). Cost if wrong: the build script.
10. **The VM token file is regenerated** (`make-dev-tokens.sh --force`) so account A carries `squad+fixture`. A release server ignores the suffix's meaning, because it has no fixture code. Cost if wrong: none; the file is a development fixture.

## File structure

Create (under `Assets/_Project/` unless stated):
- Core (`Scripts/Net/Core/`):
  - `NetSight.cs` (`INetSight`, `NetSight.Everything`);
  - `NetVisibility.cs` (`NetKnowledge`, `INetIntelSource`, `NetKnowledgeChange`, `NetVisibility`);
  - `NetSquadIntel.cs`;
  - `NetAttackGuard.cs`;
  - `NetFixtureVerb.cs`;
  - `NetItem.cs`;
  - `NetFixture.cs` and `NetFixtureContainer.cs` (development only);
  - `NetClientRecord.cs`.
- Adapter (`Scripts/Net/Adapter/`): `NetVisibilityCondition.cs`, `NetHarnessVisibility.cs` (the second `partial` file of `NetHarness`).
- Asset: `Prefabs/Net/NetVisibilityCondition.asset` (generated).
- Tests (EditMode): `NetVisibilityTests.cs`, `NetSquadIntelTests.cs`, `NetGateVisibilityTests.cs`, `NetFixtureIdentityTests.cs`, `NetFixtureTests.cs`, `NetMessageShapeTests.cs`, `NetVisibilityConditionTests.cs`, `NetClientRecordTests.cs`.
- Tests (PlayMode): `NetSquadIntelPlayModeTests.cs`, `NetAttackGuardPlayModeTests.cs`, `NetVisibilityServerPlayModeTests.cs`, `NetFixturePlayModeTests.cs`.

Modify:
- Core: `ServerCommandGate.cs`, `NetConnection.cs`, `NetTokenFile.cs`, `NetAdmission.cs`, `NetSessionHost.cs`.
- Gameplay: `Scripts/Units/CommandableUnit.cs` (`DropCurrent` only).
- Adapter: `NetMessages.cs`, `NetUnitState.cs`, `NetAvatars.cs`, `NetServer.cs`, `NetClient.cs`, `NetClientView.cs`, `NetHarness.cs`.
- Server and editor: `Scripts/Server/ServerOptions.cs`, `Editor/Net/NetAssetBuilder.cs`, `Editor/Server/ServerSceneBuilder.cs`, `Editor/Server/ServerBuild.cs`.
- Generated assets: `Prefabs/Net/NetUnitAvatar.prefab` and `Scenes/ServerMission.unity`, both regenerated.
- Tests: `NetGateIntentTests.cs`, `NetGateRejectionTests.cs`, `NetSessionHostTests.cs`, `NetAvatarPrefabTests.cs`, `NetSourceAuditTests.cs`, `ServerOptionsTests.cs`, `PlayMode/NetGatePlayModeTests.cs`, `PlayMode/NetLoopbackPlayModeTests.cs`, `PlayMode/NetHarnessPlayModeTests.cs`, `PlayMode/ServerMissionPlayModeTests.cs`, `PlayMode/TestSupport/NetLoopbackRig.cs`.
- Scripts: `Tools/build-server.sh`, `Tools/run-net-harness.sh`, `Tools/LinuxServer/run-net.sh`, `Tools/LinuxServer/make-dev-tokens.sh`.
- Docs: `Docs/Decisions.md`, `Docs/OnlineReadinessAudit.md`, `Docs/DedicatedServer.md`, `CLAUDE.md`.

---

### Task 1: The visibility projection (Core)

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetSight.cs`, `Assets/_Project/Scripts/Net/Core/NetVisibility.cs`
- Test: `Assets/_Project/Tests/EditMode/NetVisibilityTests.cs`

**Interfaces:**
- Consumes: `NetUnits` (`Count`, `TryGet`, `IsHostile`), `NetConnection` (`Owns`), `CommandableUnit.IsAlive`.
- Produces:
  - `interface INetSight { bool CanSee(NetConnection connection, int unitId); }`
  - `static class NetSight { static readonly INetSight Everything; }`
  - `enum NetKnowledge : byte { Unknown = 0, Observed = 1, LastKnown = 2 }`
  - `interface INetIntelSource { bool Observes(NetConnection connection, int hostileId); }`
  - `readonly struct NetKnowledgeChange(NetConnection connection, int unitId, NetKnowledge knowledge, Vector3 position)` with get-only `Connection`, `UnitId`, `Knowledge`, `Position`
  - `sealed class NetVisibility : INetSight`, with:
    - `NetVisibility(NetUnits units, INetIntelSource source)`;
    - `int ConnectionCount`;
    - `void Add(NetConnection)` and `void Remove(NetConnection)`;
    - `NetKnowledge KnowledgeOf(NetConnection, int unitId)`;
    - `bool TryLastKnown(NetConnection, int unitId, out Vector3 position)`;
    - `bool CanSee(NetConnection, int unitId)`;
    - `bool Replicates(NetConnection, int unitId)`;
    - `void Update(List<NetKnowledgeChange> changes)`, which appends and never clears.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetVisibilityTests.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetVisibilityTests
    {
        const int FriendA = 1;
        const int FriendB = 2;
        const int Hostile = 3;

        sealed class FakeIntel : INetIntelSource
        {
            public readonly HashSet<(NetConnection, int)> Seen = new HashSet<(NetConnection, int)>();
            public readonly List<int> Asked = new List<int>();

            public bool Observes(NetConnection connection, int hostileId)
            {
                Asked.Add(hostileId);
                return Seen.Contains((connection, hostileId));
            }
        }

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<NetKnowledgeChange> changes = new List<NetKnowledgeChange>();
        CommandableUnit hostile;
        FakeIntel intel;
        NetVisibility visibility;
        NetConnection a;
        NetConnection b;

        [SetUp]
        public void SetUp()
        {
            var units = new NetUnits();
            units.Register(NetTestKit.MakeUnit(hosts, "FriendlyUnit_1"), false);
            units.Register(NetTestKit.MakeUnit(hosts, "FriendlyUnit_2"), false);
            hostile = NetTestKit.MakeUnit(hosts, "HostileUnit_1");
            units.Register(hostile, true);
            hostile.transform.position = new Vector3(10f, 0f, 0f);
            intel = new FakeIntel();
            visibility = new NetVisibility(units, intel);
            a = new NetConnection("account-a", new[] { FriendA, FriendB });
            b = new NetConnection("account-b", new int[0]);
            visibility.Add(a);
            visibility.Add(b);
            changes.Clear();
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        void Tick()
        {
            changes.Clear();
            visibility.Update(changes);
        }

        void See(NetConnection connection) => intel.Seen.Add((connection, Hostile));

        void Lose(NetConnection connection) => intel.Seen.Remove((connection, Hostile));

        void Kill() => hostile.GetComponent<Health>().TakeDamage(100000);

        [Test]
        public void AHostile_StartsUnknown_ForEveryConnection()
        {
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.KnowledgeOf(b, Hostile), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.TryLastKnown(a, Hostile, out _), Is.False);
        }

        [Test]
        public void Observing_GivesOneObservedChange_ToThatConnectionOnly()
        {
            See(a);
            Tick();
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Connection, Is.SameAs(a));
            Assert.That(changes[0].UnitId, Is.EqualTo(Hostile));
            Assert.That(changes[0].Knowledge, Is.EqualTo(NetKnowledge.Observed));
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Observed));
            Assert.That(visibility.KnowledgeOf(b, Hostile), Is.EqualTo(NetKnowledge.Unknown), "A's observation never widens B's view");
        }

        [Test]
        public void UpdatingAnUnchangedWorld_ReportsNothing()
        {
            See(a);
            Tick();
            Tick();
            Assert.That(changes, Is.Empty, "a transition is reported once");
            Lose(a);
            Tick();
            Tick();
            Assert.That(changes, Is.Empty);
        }

        [Test]
        public void LosingSight_GivesLastKnown_AtTheLastObservedPosition()
        {
            See(a);
            Tick();
            hostile.transform.position = new Vector3(12f, 0f, 3f);
            Tick();
            Assert.That(changes, Is.Empty, "movement while observed is not a transition");
            Lose(a);
            Tick();
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Knowledge, Is.EqualTo(NetKnowledge.LastKnown));
            Assert.That(changes[0].Position, Is.EqualTo(new Vector3(12f, 0f, 3f)));
            Assert.That(visibility.TryLastKnown(a, Hostile, out var at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(12f, 0f, 3f)));
        }

        [Test]
        public void HiddenMovement_NeverMovesTheLastKnownRecord()
        {
            See(a);
            Tick();
            Lose(a);
            Tick();
            hostile.transform.position = new Vector3(-20f, 0f, 5f);
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.TryLastKnown(a, Hostile, out var at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(10f, 0f, 0f)));
        }

        [Test]
        public void ReObserving_ClearsTheRecord_AndReportsTheCurrentPosition()
        {
            See(a);
            Tick();
            Lose(a);
            Tick();
            hostile.transform.position = new Vector3(-20f, 0f, 5f);
            See(a);
            Tick();
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Knowledge, Is.EqualTo(NetKnowledge.Observed));
            Assert.That(changes[0].Position, Is.EqualTo(new Vector3(-20f, 0f, 5f)));
            Assert.That(visibility.TryLastKnown(a, Hostile, out _), Is.False, "the stale record is gone");
        }

        [Test]
        public void AHostileThatDiesWhileObserved_StaysObserved()
        {
            See(a);
            Tick();
            Kill();
            Lose(a);
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Observed), "the death was witnessed");
            Assert.That(visibility.Replicates(a, Hostile), Is.True);
        }

        [Test]
        public void AHostileThatDiesWhileHidden_KeepsItsRecord_AndNothingNew()
        {
            See(a);
            Tick();
            Lose(a);
            Tick();
            Kill();
            See(a);
            See(b);
            Tick();
            Assert.That(changes, Is.Empty, "a dead hostile is never revealed");
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.LastKnown));
            Assert.That(visibility.KnowledgeOf(b, Hostile), Is.EqualTo(NetKnowledge.Unknown));
        }

        [Test]
        public void Replicates_FriendliesOnlyToTheirOwner_AndHostilesOnlyWhileObserved()
        {
            Tick();
            Assert.That(visibility.Replicates(a, FriendA), Is.True);
            Assert.That(visibility.Replicates(a, FriendB), Is.True);
            Assert.That(visibility.Replicates(b, FriendA), Is.False, "B owns nothing and sees nothing");
            Assert.That(visibility.Replicates(a, Hostile), Is.False);
            See(a);
            Tick();
            Assert.That(visibility.Replicates(a, Hostile), Is.True);
            Assert.That(visibility.Replicates(b, Hostile), Is.False);
            Lose(a);
            Tick();
            Assert.That(visibility.Replicates(a, Hostile), Is.False, "a last-known hostile sends nothing live");
            Assert.That(visibility.Replicates(a, 99), Is.False);
            Assert.That(visibility.Replicates(a, 0), Is.False);
        }

        [Test]
        public void CanSee_FriendliesAlways_HostilesOnlyWhileObserved()
        {
            Tick();
            Assert.That(visibility.CanSee(b, FriendA), Is.True, "friendlies are not hidden information");
            Assert.That(visibility.CanSee(a, Hostile), Is.False);
            See(a);
            Tick();
            Assert.That(visibility.CanSee(a, Hostile), Is.True);
            Assert.That(visibility.CanSee(b, Hostile), Is.False);
        }

        [Test]
        public void AConnectionNeverAdded_ReplicatesNothing_AndKnowsNothing()
        {
            var stranger = new NetConnection("account-x", new[] { FriendA });
            intel.Seen.Add((stranger, Hostile));
            Tick();
            Assert.That(changes.Any(c => c.Connection == stranger), Is.False);
            Assert.That(visibility.Replicates(stranger, FriendA), Is.False, "nothing before hello");
            Assert.That(visibility.KnowledgeOf(stranger, Hostile), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.Replicates(null, FriendA), Is.False);
        }

        [Test]
        public void ARemovedConnection_IsForgotten_AndTheOtherIsUnchanged()
        {
            See(a);
            See(b);
            Tick();
            visibility.Remove(a);
            Lose(a);
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.ConnectionCount, Is.EqualTo(1));
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.KnowledgeOf(b, Hostile), Is.EqualTo(NetKnowledge.Observed));
            visibility.Add(a);
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Unknown), "a returning connection starts from nothing");
        }

        [Test]
        public void AFlappingSource_GivesAtMostOneChangePerConnectionAndHostilePerUpdate()
        {
            for (var i = 0; i < 20; i++)
            {
                if (i % 2 == 0)
                {
                    See(a);
                    See(b);
                }
                else
                {
                    Lose(a);
                    Lose(b);
                }
                Tick();
                Assert.That(changes.Count, Is.LessThanOrEqualTo(2));
            }
        }

        [Test]
        public void TheSourceIsAskedOnlyAboutLivingHostiles()
        {
            Tick();
            Assert.That(intel.Asked.Distinct(), Is.EquivalentTo(new[] { Hostile }));
            intel.Asked.Clear();
            Kill();
            Tick();
            Assert.That(intel.Asked, Is.Empty);
        }

        [Test]
        public void TheEverythingSight_SeesEveryUnit()
        {
            Assert.That(NetSight.Everything.CanSee(b, Hostile), Is.True);
            Assert.That(NetSight.Everything.CanSee(null, 99), Is.True);
        }
    }
}
```

- [ ] **Step 2: Run the tests to make sure they fail**

Run: `Tools/run-tests.sh EditMode NetVisibilityTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `EXIT=1` with `error CS0246` naming `INetIntelSource` / `NetVisibility`.

- [ ] **Step 3: Write the implementation**

`Assets/_Project/Scripts/Net/Core/NetSight.cs`:
```csharp
namespace Blackglass
{
    /// <summary>
    /// What the gate may admit exists for a connection (hidden information slice, spec section 3.4). A hostile the connection
    /// cannot see is answered UnknownUnit, exactly like an id that does not exist, so no answer reveals it.
    /// </summary>
    public interface INetSight
    {
        bool CanSee(NetConnection connection, int unitId);
    }

    public static class NetSight
    {
        sealed class All : INetSight
        {
            public bool CanSee(NetConnection connection, int unitId) => true;
        }

        /// <summary>Sees every unit: for tests of the gate that are not about hidden information. The server never uses it.</summary>
        public static readonly INetSight Everything = new All();
    }
}
```

`Assets/_Project/Scripts/Net/Core/NetVisibility.cs`:
```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What one connection knows about one hostile. The wire carries this byte in NetKnowledgeMessage.</summary>
    public enum NetKnowledge : byte
    {
        Unknown = 0,
        /// <summary>Live: the connection receives the hostile's avatar.</summary>
        Observed = 1,
        /// <summary>Hidden again: the connection keeps only a frozen record of where it last saw the hostile.</summary>
        LastKnown = 2,
    }

    /// <summary>Whether a connection observes a hostile right now: the squad's intelligence, or the development fixture.</summary>
    public interface INetIntelSource
    {
        /// <summary>Asked only about registered, living hostiles.</summary>
        bool Observes(NetConnection connection, int hostileId);
    }

    /// <summary>
    /// One transition for one connection and one hostile. Position is the hostile's position for Observed and the frozen record
    /// for LastKnown.
    /// </summary>
    public readonly struct NetKnowledgeChange
    {
        public NetKnowledgeChange(NetConnection connection, int unitId, NetKnowledge knowledge, Vector3 position)
        {
            Connection = connection;
            UnitId = unitId;
            Knowledge = knowledge;
            Position = position;
        }

        public NetConnection Connection { get; }
        public int UnitId { get; }
        public NetKnowledge Knowledge { get; }
        public Vector3 Position { get; }
    }

    /// <summary>
    /// The server's per-connection view of the hostiles (hidden information slice, decision 047). Each frame it asks one source
    /// whether each welcomed connection observes each living hostile and records a transition only when the answer changes:
    /// Unknown or LastKnown to Observed, and Observed to LastKnown at the last position recorded while observed. A last-known
    /// record never moves while the hostile is hidden. A hostile that dies keeps whatever each connection knew at that moment,
    /// so a death is witnessed only by a connection that was observing. Friendlies replicate only to their owner. Holds no
    /// networking type: the adapter turns the transitions into observer rebuilds and messages.
    /// </summary>
    public sealed class NetVisibility : INetSight
    {
        struct Entry
        {
            public NetKnowledge Knowledge;
            public Vector3 Position;
        }

        readonly NetUnits units;
        readonly INetIntelSource source;
        readonly Dictionary<NetConnection, Dictionary<int, Entry>> views = new Dictionary<NetConnection, Dictionary<int, Entry>>();

        public NetVisibility(NetUnits units, INetIntelSource source)
        {
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public int ConnectionCount => views.Count;

        /// <summary>A connection whose hello was accepted. It starts knowing nothing.</summary>
        public void Add(NetConnection connection)
        {
            if (connection != null && !views.ContainsKey(connection))
                views.Add(connection, new Dictionary<int, Entry>());
        }

        public void Remove(NetConnection connection)
        {
            if (connection != null)
                views.Remove(connection);
        }

        public NetKnowledge KnowledgeOf(NetConnection connection, int unitId) =>
            connection != null && views.TryGetValue(connection, out var view) && view.TryGetValue(unitId, out var entry)
                ? entry.Knowledge
                : NetKnowledge.Unknown;

        public bool TryLastKnown(NetConnection connection, int unitId, out Vector3 position)
        {
            position = default;
            if (connection == null || !views.TryGetValue(connection, out var view) || !view.TryGetValue(unitId, out var entry)
                || entry.Knowledge != NetKnowledge.LastKnown)
                return false;
            position = entry.Position;
            return true;
        }

        /// <summary>For the gate: friendlies always (they are not hidden information); a hostile only while observed.</summary>
        public bool CanSee(NetConnection connection, int unitId) =>
            !units.IsHostile(unitId) || KnowledgeOf(connection, unitId) == NetKnowledge.Observed;

        /// <summary>For the observer condition: whether this connection may receive the unit's live avatar.</summary>
        public bool Replicates(NetConnection connection, int unitId)
        {
            if (connection == null || !views.ContainsKey(connection) || unitId < 1 || unitId > units.Count)
                return false;
            return units.IsHostile(unitId)
                ? KnowledgeOf(connection, unitId) == NetKnowledge.Observed
                : connection.Owns(unitId);
        }

        /// <summary>Re-evaluates every connection and living hostile and appends one change per transition.</summary>
        public void Update(List<NetKnowledgeChange> changes)
        {
            foreach (var pair in views)
            {
                var connection = pair.Key;
                var view = pair.Value;
                for (var id = 1; id <= units.Count; id++)
                {
                    if (!units.IsHostile(id) || !units.TryGet(id, out var unit) || !unit.IsAlive)
                        continue;
                    view.TryGetValue(id, out var entry);
                    var position = unit.transform.position;
                    if (source.Observes(connection, id))
                    {
                        if (entry.Knowledge != NetKnowledge.Observed)
                            changes.Add(new NetKnowledgeChange(connection, id, NetKnowledge.Observed, position));
                        entry.Knowledge = NetKnowledge.Observed;
                        entry.Position = position;
                    }
                    else if (entry.Knowledge == NetKnowledge.Observed)
                    {
                        entry.Knowledge = NetKnowledge.LastKnown;
                        changes.Add(new NetKnowledgeChange(connection, id, NetKnowledge.LastKnown, entry.Position));
                    }
                    view[id] = entry;
                }
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to make sure they pass**

Run: `Tools/run-tests.sh EditMode "NetVisibilityTests;NetSourceAuditTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `result="Passed" total="16" passed="16"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net/Core/NetSight.cs Assets/_Project/Scripts/Net/Core/NetSight.cs.meta Assets/_Project/Scripts/Net/Core/NetVisibility.cs Assets/_Project/Scripts/Net/Core/NetVisibility.cs.meta Assets/_Project/Tests/EditMode/NetVisibilityTests.cs Assets/_Project/Tests/EditMode/NetVisibilityTests.cs.meta
git commit -m "Net: per-connection visibility projection with a frozen last-known record

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: The squad intelligence source, failing closed (Core)

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetSquadIntel.cs`
- Test: `Assets/_Project/Tests/EditMode/NetSquadIntelTests.cs`, `Assets/_Project/Tests/PlayMode/NetSquadIntelPlayModeTests.cs`

**Interfaces:**
- Consumes: `INetIntelSource`, `NetVisibility` (Task 1); `IntelligenceService.Model`, `MissionIntelligence.IsOpen`, `MissionIntelligence.StateOfEnemy(Health)`, `KnowledgeState.Observed`.
- Produces:
  - `sealed class NetSquadIntel : INetIntelSource`:
    - `NetSquadIntel(IntelligenceService service, NetUnits units, int friendlyCount, Action<string> log)`;
    - `static string Problem(IntelligenceService service)`, which returns null when usable, otherwise the reason;
    - `static bool OwnsSquad(NetConnection connection, NetUnits units, int friendlyCount)`.
  - The log callback receives one line per failure episode.

- [ ] **Step 1: Write the failing EditMode tests**

`Assets/_Project/Tests/EditMode/NetSquadIntelTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetSquadIntelTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();
        NetUnits units;
        CommandableUnit friend1;
        CommandableUnit friend2;

        [SetUp]
        public void SetUp()
        {
            friend1 = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            friend2 = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            units = NetUnits.FromMission(new List<CommandableUnit> { friend1, friend2 },
                new List<CommandableUnit> { NetTestKit.MakeUnit(hosts, "HostileUnit_1") });
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        static void Kill(CommandableUnit unit) => unit.GetComponent<Health>().TakeDamage(100000);

        [Test]
        public void OwnsSquad_OnlyWhenEveryLivingFriendlyIsOwned()
        {
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("a", new[] { 1, 2 }), units, 2), Is.True);
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("half", new[] { 1 }), units, 2), Is.False, "split ownership fails closed");
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("b", new int[0]), units, 2), Is.False);
            Assert.That(NetSquadIntel.OwnsSquad(null, units, 2), Is.False);
        }

        [Test]
        public void OwnsSquad_IgnoresDeadFriendlies_ButNeedsOneOwned()
        {
            Kill(friend2);
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("half", new[] { 1 }), units, 2), Is.True, "the dead do not feed the model");
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("b", new int[0]), units, 2), Is.False);
        }

        [Test]
        public void WhenEveryFriendlyIsDead_AConnectionOwningNothing_StillGetsNothing()
        {
            Kill(friend1);
            Kill(friend2);
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("b", new int[0]), units, 2), Is.False);
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("a", new[] { 1, 2 }), units, 2), Is.True);
        }

        [Test]
        public void AMissingService_IsAProblem_AndNothingIsObserved_WithOneLogLine()
        {
            Assert.That(NetSquadIntel.Problem(null), Does.Contain("no IntelligenceService"));
            var logs = new List<string>();
            var source = new NetSquadIntel(null, units, 2, logs.Add);
            var owner = new NetConnection("a", new[] { 1, 2 });
            Assert.That(source.Observes(owner, 3), Is.False);
            Assert.That(source.Observes(owner, 3), Is.False);
            Assert.That(logs, Has.Count.EqualTo(1), "a failure is logged once, not every frame");
            StringAssert.Contains("fails closed", logs[0]);
        }
    }
}
```

- [ ] **Step 2: Write the failing PlayMode tests (real IntelligenceService, no fixture)**

`Assets/_Project/Tests/PlayMode/NetSquadIntelPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The real intelligence model feeding per-connection visibility (spec section 3.2), separate from the fixture.</summary>
    public class NetSquadIntelPlayModeTests
    {
        const int Shooter = 2;

        IntelRig rig;
        Health shooter;
        UnitAttacker attacker;
        NetVisibility visibility;
        NetConnection a;
        NetConnection b;
        readonly List<string> logs = new List<string>();
        readonly List<NetKnowledgeChange> changes = new List<NetKnowledgeChange>();

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        void Build(IntelligenceSettings settings)
        {
            rig = new IntelRig(corridor: true);
            (shooter, attacker) = rig.AddShooter(IntelRig.InLineGround);
            rig.Begin(settings);
            var units = NetUnits.FromMission(new List<CommandableUnit> { rig.Friendly.GetComponent<CommandableUnit>() },
                new List<CommandableUnit> { shooter.GetComponent<CommandableUnit>() });
            logs.Clear();
            visibility = new NetVisibility(units, new NetSquadIntel(rig.Service, units, 1, logs.Add));
            a = new NetConnection("account-a", new[] { 1 });
            b = new NetConnection("account-b", new int[0]);
            visibility.Add(a);
            visibility.Add(b);
        }

        static IntelligenceSettings Fog() => IntelRig.Fog(s =>
        {
            s.observationRange = 8f;   // the shooter is 13 m away: only exposure can reveal it
            s.exposureSeconds = 1f;
        });

        void Tick()
        {
            changes.Clear();
            visibility.Update(changes);
        }

        [UnityTest]
        public IEnumerator TheRealModel_HidesTheHostileAtStart_FromEveryone()
        {
            Build(Fog());
            yield return null;
            Assert.That(NetSquadIntel.Problem(rig.Service), Is.Null);
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.KnowledgeOf(b, Shooter), Is.EqualTo(NetKnowledge.Unknown));
        }

        [UnityTest]
        public IEnumerator AShot_ExposesTheShooter_ToTheSquadsOwnerOnly_ThenLeavesAFrozenRecord()
        {
            Build(Fog());
            yield return null;
            Tick();

            Assert.That(attacker.TryAttack(rig.FriendlyHealth), Is.True, "the shot is fired (decision 037: firing exposes)");
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.Observed), "exposure reaches the owner");
            Assert.That(visibility.KnowledgeOf(b, Shooter), Is.EqualTo(NetKnowledge.Unknown), "and nobody else");

            yield return new WaitForSeconds(1.4f);
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.LastKnown));
            Assert.That(visibility.TryLastKnown(a, Shooter, out var record), Is.True);
            Assert.That(Vector3.Distance(record, shooter.transform.position), Is.LessThan(0.01f));

            Assert.That(shooter.GetComponent<NavMeshAgent>().Warp(IntelRig.OffAxisGround), Is.True);
            yield return null;
            Tick();
            Assert.That(changes, Is.Empty, "hidden movement is not reported");
            Assert.That(visibility.TryLastKnown(a, Shooter, out var after), Is.True);
            Assert.That(after, Is.EqualTo(record), "the record does not follow the hidden hostile");
            Assert.That(visibility.KnowledgeOf(b, Shooter), Is.EqualTo(NetKnowledge.Unknown));
        }

        [UnityTest]
        public IEnumerator FogOff_FailsClosed_AndLogsOnce()
        {
            Build(new IntelligenceSettings { fogEnabled = false });
            yield return null;
            StringAssert.Contains("fog is off", NetSquadIntel.Problem(rig.Service));
            Tick();
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.Unknown), "an open model must not reveal everything");
            Assert.That(logs, Has.Count.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DisablingTheServiceMidMission_HidesEverything_AndLogsOnce()
        {
            Build(Fog());
            yield return null;
            Assert.That(attacker.TryAttack(rig.FriendlyHealth), Is.True);
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.Observed));

            rig.Service.enabled = false;
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Shooter), Is.EqualTo(NetKnowledge.LastKnown), "no live state without a model");
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(logs, Has.Count.EqualTo(1));
            StringAssert.Contains("disabled", logs[0]);
        }
    }
}
#endif
```

- [ ] **Step 3: Run both to make sure they fail**

Run: `Tools/run-tests.sh EditMode NetSquadIntelTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `EXIT=1`, `error CS0103`/`CS0246` naming `NetSquadIntel`.

- [ ] **Step 4: Write the implementation**

`Assets/_Project/Scripts/Net/Core/NetSquadIntel.cs`:
```csharp
using System;

namespace Blackglass
{
    /// <summary>
    /// The real visibility source in normal -net mode (hidden information slice, spec section 3.2): the squad's intelligence
    /// model (decision 037: sight, cameras, scans, exposure by firing, briefing). A connection gets squad knowledge only if it owns
    /// every living friendly that feeds the model, and at least one; anyone else observes nothing. It fails closed: with no
    /// service, a disabled one, no model or an open model (fog off) nothing is observed, and the reason is logged once.
    /// MissionIntelligence.IsObserved is deliberately not used: it answers true for untracked units and for an open model.
    /// </summary>
    public sealed class NetSquadIntel : INetIntelSource
    {
        readonly IntelligenceService service;
        readonly NetUnits units;
        readonly int friendlyCount;
        readonly Action<string> log;
        bool reported;

        public NetSquadIntel(IntelligenceService service, NetUnits units, int friendlyCount, Action<string> log)
        {
            this.service = service;
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.friendlyCount = friendlyCount;
            this.log = log;
        }

        /// <summary>Null when the service can feed visibility; otherwise why not.</summary>
        public static string Problem(IntelligenceService service)
        {
            if (service == null)
                return "the scene has no IntelligenceService";
            if (!service.isActiveAndEnabled)
                return "the IntelligenceService is disabled";
            if (service.Model == null)
                return "the IntelligenceService has no mission model";
            if (service.Model.IsOpen)
                return "fog is off, so the intelligence model is open";
            return null;
        }

        /// <summary>True when the connection owns every living friendly of 1..friendlyCount and at least one of them.</summary>
        public static bool OwnsSquad(NetConnection connection, NetUnits units, int friendlyCount)
        {
            if (connection == null || units == null || friendlyCount <= 0)
                return false;
            var ownsAny = false;
            for (var id = 1; id <= friendlyCount; id++)
            {
                var owns = connection.Owns(id);
                ownsAny |= owns;
                if (!owns && units.TryGet(id, out var unit) && unit.IsAlive)
                    return false;
            }
            return ownsAny;
        }

        public bool Observes(NetConnection connection, int hostileId)
        {
            var problem = Problem(service);
            if (problem != null)
            {
                if (!reported)
                {
                    reported = true;
                    log?.Invoke("visibility fails closed: " + problem + "; every hostile is hidden");
                }
                return false;
            }
            reported = false;
            if (!OwnsSquad(connection, units, friendlyCount))
                return false;
            if (!units.TryGet(hostileId, out var unit) || !unit.TryGetComponent<Health>(out var health))
                return false;
            return service.Model.StateOfEnemy(health) == KnowledgeState.Observed;
        }
    }
}
```

- [ ] **Step 5: Run the tests to make sure they pass**

Run: `Tools/run-tests.sh EditMode "NetSquadIntelTests;NetSourceAuditTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `total="5" passed="5"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode NetSquadIntelPlayModeTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `total="4" passed="4"`, `EXIT=0`.
If `OffAxisGround` cannot be warped to (`Warp` returns false), use `IntelRig.InLineGround + new Vector3(0f, 0f, 1.5f)`, which is still in room B, and note it in the task report.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Net/Core/NetSquadIntel.cs Assets/_Project/Scripts/Net/Core/NetSquadIntel.cs.meta Assets/_Project/Tests/EditMode/NetSquadIntelTests.cs Assets/_Project/Tests/EditMode/NetSquadIntelTests.cs.meta Assets/_Project/Tests/PlayMode/NetSquadIntelPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetSquadIntelPlayModeTests.cs.meta
git commit -m "Net: squad intelligence as the visibility source, failing closed

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The gate answers UnknownUnit for hidden hostiles and reports accepted attacks

**Files:**
- Modify: `Assets/_Project/Scripts/Net/Core/ServerCommandGate.cs`, `Assets/_Project/Scripts/Net/Adapter/NetServer.cs:82`, `Assets/_Project/Tests/EditMode/NetGateIntentTests.cs:25`, `Assets/_Project/Tests/EditMode/NetGateRejectionTests.cs:37`, `Assets/_Project/Tests/EditMode/NetSessionHostTests.cs:24`, `Assets/_Project/Tests/PlayMode/NetGatePlayModeTests.cs:36`
- Test: `Assets/_Project/Tests/EditMode/NetGateVisibilityTests.cs`, one new test in `Assets/_Project/Tests/PlayMode/NetGatePlayModeTests.cs`

**Interfaces:**
- Consumes: `INetSight`, `NetSight.Everything` (Task 1).
- Produces:
  - `ServerCommandGate(NetUnits units, string attemptId, INetSight sight)`; a null sight throws `ArgumentNullException`.
  - `Action<NetConnection, int, int, UnitCommand> AttackAccepted { get; set; }`, which receives `(connection, unitId, targetId, command)` after `Issue` accepted an attack. The command is the `AttackCommand` instance that `Issue` received.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetGateVisibilityTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetGateVisibilityTests
    {
        const string Attempt = "attempt-1";

        sealed class HiddenSight : INetSight
        {
            public readonly HashSet<int> Hidden = new HashSet<int>();
            public bool CanSee(NetConnection connection, int unitId) => !Hidden.Contains(unitId);
        }

        readonly List<GameObject> hosts = new List<GameObject>();
        CommandableUnit friendA;
        CommandableUnit hostile;
        HiddenSight sight;
        ServerCommandGate gate;
        NetConnection owner;
        NetConnection observer;
        uint counter;
        const int IdA = 1;
        const int IdB = 2;
        const int IdHostile = 3;

        [SetUp]
        public void SetUp()
        {
            friendA = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            var friendB = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            hostile = NetTestKit.MakeUnit(hosts, "HostileUnit_1");
            var units = NetUnits.FromMission(new List<CommandableUnit> { friendA, friendB }, new List<CommandableUnit> { hostile });
            sight = new HiddenSight();
            sight.Hidden.Add(IdHostile);
            gate = new ServerCommandGate(units, Attempt, sight);
            owner = new NetConnection("account-a", new[] { IdA, IdB });
            observer = new NetConnection("account-b", new int[0]);
            counter = 0;
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        NetResult Send(NetConnection connection, NetRequest request)
        {
            counter++;
            return gate.Handle(connection, new NetEnvelope(Attempt, counter, counter, request), 0d);
        }

        [Test]
        public void AGateWithoutASight_CannotBeBuilt()
        {
            Assert.Throws<ArgumentNullException>(() => new ServerCommandGate(new NetUnits(), Attempt, null));
        }

        [Test]
        public void AnAttackOnAHiddenHostile_IsUnknownUnit_LikeAMadeUpId()
        {
            Assert.That(Send(owner, new NetAttack(IdA, IdHostile)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(owner, new NetAttack(IdA, 999)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(friendA.CurrentCommand, Is.Null, "nothing was ordered");
        }

        [Test]
        public void AMoveNamingAHiddenHostile_IsUnknownUnit_LikeAMadeUpId()
        {
            Assert.That(Send(owner, new NetMoveTo(IdHostile, Vector3.one)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(owner, new NetMoveTo(999, Vector3.one)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(owner, new NetStop(IdHostile)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(owner, new NetMoveIntent(IdHostile, Vector3.right)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(hostile.CurrentCommand, Is.Null);
        }

        [Test]
        public void AVisibleHostile_StillAnswersNotOwner()
        {
            sight.Hidden.Clear();
            Assert.That(Send(owner, new NetMoveTo(IdHostile, Vector3.one)).Reason, Is.EqualTo(NetRejectReason.NotOwner));
        }

        [Test]
        public void ANonOwner_NamingAHiddenHostile_GetsUnknownUnit_AndAFriendlyTarget_GetsNotOwner()
        {
            Assert.That(Send(observer, new NetAttack(IdA, IdHostile)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(observer, new NetAttack(IdA, IdB)).Reason, Is.EqualTo(NetRejectReason.NotOwner),
                "friendlies are not hidden information");
        }

        [Test]
        public void ARejectedAttack_IsNeverReportedAsAccepted()
        {
            var reported = 0;
            gate.AttackAccepted = (connection, unit, target, command) => reported++;
            Send(owner, new NetAttack(IdA, IdHostile));
            Send(owner, new NetAttack(IdA, IdB));
            Assert.That(reported, Is.EqualTo(0));
        }
    }
}
```

In `Assets/_Project/Tests/PlayMode/NetGatePlayModeTests.cs`, add this test after `AnAttackOnAHostile_IsAccepted_ThroughIssue`:
```csharp
        [UnityTest]
        public IEnumerator AnAcceptedAttack_IsReported_WithTheCommandIssueReceived()
        {
            yield return Ready();
            NetConnection reportedConnection = null;
            var reportedUnit = 0;
            var reportedTarget = 0;
            UnitCommand reportedCommand = null;
            gate.AttackAccepted = (connection, unit, target, command) =>
            {
                reportedConnection = connection;
                reportedUnit = unit;
                reportedTarget = target;
                reportedCommand = command;
            };
            Assert.That(Send(new NetAttack(friendId, enemyId)).IsAccepted, Is.True);
            Assert.That(reportedConnection, Is.SameAs(owner));
            Assert.That(reportedUnit, Is.EqualTo(friendId));
            Assert.That(reportedTarget, Is.EqualTo(enemyId));
            Assert.That(reportedCommand, Is.InstanceOf<AttackCommand>());
            Assert.That(friend.CurrentCommand, Is.SameAs(reportedCommand), "the guard can recognise the order later");
        }
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `Tools/run-tests.sh EditMode NetGateVisibilityTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `EXIT=1`, `error CS1729` (no constructor that takes 3 arguments) or `CS1061` (`AttackAccepted`).

- [ ] **Step 3: Change the gate**

In `ServerCommandGate.cs`:
- add the field `readonly INetSight sight;`;
- replace the constructor with:
```csharp
        public ServerCommandGate(NetUnits units, string attemptId, INetSight sight)
        {
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.attemptId = attemptId ?? throw new ArgumentNullException(nameof(attemptId));
            this.sight = sight ?? throw new ArgumentNullException(nameof(sight));
        }

        /// <summary>
        /// Receives (connection, unit id, target id, the AttackCommand given to Issue) after an attack was accepted, so the server
        /// can end it if the connection stops observing the target (NetAttackGuard, spec section 3.5).
        /// </summary>
        public Action<NetConnection, int, int, UnitCommand> AttackAccepted { get; set; }
```
- in `Attack(...)`, after the first `UnknownUnit` check and before `NotOwner`, insert:
```csharp
            // A hostile this connection cannot see answers exactly like an id that does not exist (spec section 3.4).
            if (!sight.CanSee(connection, attack.UnitId) || !sight.CanSee(connection, attack.TargetId))
                return Reject(NetRejectReason.UnknownUnit);
```
  and replace its last line `return Order(attacker, new AttackCommand(health), IssueMode.Replace);` with:
```csharp
            var command = new AttackCommand(health);
            var result = Order(attacker, command, IssueMode.Replace);
            if (result.IsAccepted)
                AttackAccepted?.Invoke(connection, attack.UnitId, attack.TargetId, command);
            return result;
```
- in `Own(...)`, after the `UnknownUnit` block and before the `NotOwner` block, insert:
```csharp
            if (!sight.CanSee(connection, unitId))
            {
                failure = NetRejectReason.UnknownUnit;
                return false;
            }
```
- update the comments above `Attack` and `Own` to say the hidden case answers UnknownUnit;
- in the class summary, after "a living unit,", add "visible to this connection,".

Update the four test construction sites to `new ServerCommandGate(<same arguments>, NetSight.Everything)`. In `NetServer.cs:82`, pass `NetSight.Everything` for now and add the comment `// Task 8 replaces this with the connection's NetVisibility.` (Task 8 removes both the argument and the comment).

- [ ] **Step 4: Run the tests to make sure they pass**

Run: `Tools/run-tests.sh EditMode "NetGateVisibilityTests;NetGateIntentTests;NetGateRejectionTests;NetSessionHostTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: all passed, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode NetGatePlayModeTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: all passed (one more than before), `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net/Core/ServerCommandGate.cs Assets/_Project/Scripts/Net/Adapter/NetServer.cs Assets/_Project/Tests/EditMode/NetGateVisibilityTests.cs Assets/_Project/Tests/EditMode/NetGateVisibilityTests.cs.meta Assets/_Project/Tests/EditMode/NetGateIntentTests.cs Assets/_Project/Tests/EditMode/NetGateRejectionTests.cs Assets/_Project/Tests/EditMode/NetSessionHostTests.cs Assets/_Project/Tests/PlayMode/NetGatePlayModeTests.cs
git commit -m "Net: the gate answers UnknownUnit for hidden hostiles and reports accepted attacks

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `CommandableUnit.DropCurrent` and the attack guard

**Files:**
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs` (one new public method, nothing else)
- Create: `Assets/_Project/Scripts/Net/Core/NetAttackGuard.cs`
- Test: `Assets/_Project/Tests/PlayMode/NetAttackGuardPlayModeTests.cs`

**Interfaces:**
- Consumes: `NetVisibility.KnowledgeOf`, `INetIntelSource` (Task 1); `NetUnits`; the gate's `AttackAccepted` callback shape (Task 3).
- Produces:
  - `bool CommandableUnit.DropCurrent(UnitCommand command)`: ends `command` the way an unreachable attack ends, but only while it is still the current order, and then starts the next queued order. Returns false for null or for a command that is not current.
  - `sealed class NetAttackGuard`, with:
    - `NetAttackGuard(NetUnits units, NetVisibility visibility)`;
    - `void Accepted(NetConnection connection, int unitId, int targetId, UnitCommand command)`, which matches the gate's `AttackAccepted`;
    - `void Forget(NetConnection connection)`;
    - `int Tick()`, which returns how many attacks it ended;
    - `int Count`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/NetAttackGuardPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The attack-chase mitigation (spec section 3.5): only gate-accepted attacks end when their connection loses sight.</summary>
    public class NetAttackGuardPlayModeTests
    {
        sealed class Switch : INetIntelSource
        {
            public bool On = true;
            public bool Observes(NetConnection connection, int hostileId) => On;
        }

        readonly List<NetKnowledgeChange> changes = new List<NetKnowledgeChange>();
        TestWorld world;
        CommandableUnit attacker;
        CommandableUnit bystander;
        CommandableUnit target;
        Switch intel;
        NetVisibility visibility;
        NetAttackGuard guard;
        NetConnection owner;

        [TearDown]
        public void TearDown()
        {
            world?.Dispose();
            world = null;
        }

        IEnumerator Ready()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            attacker = world.CreateFighter(new Vector3(-6f, 0f, 0f));
            bystander = world.CreateFighter(new Vector3(-6f, 0f, 3f));
            target = world.CreateFighter(new Vector3(6f, 0f, 0f));
            target.GetComponent<AutoRetaliate>().enabled = false;
            yield return null;
            var units = NetUnits.FromMission(new List<CommandableUnit> { attacker, bystander }, new List<CommandableUnit> { target });
            intel = new Switch();
            visibility = new NetVisibility(units, intel);
            guard = new NetAttackGuard(units, visibility);
            owner = new NetConnection("account-a", new[] { 1, 2 });
            visibility.Add(owner);
            Tick();
        }

        void Tick()
        {
            changes.Clear();
            visibility.Update(changes);
        }

        AttackCommand Attack(CommandableUnit unit)
        {
            var command = new AttackCommand(target.GetComponent<Health>());
            Assert.That(unit.Issue(command, IssueMode.Replace), Is.True, "Precondition: the attack is accepted");
            return command;
        }

        [UnityTest]
        public IEnumerator DropCurrent_EndsTheGivenAttack_AndTheQueuedOrderStarts()
        {
            yield return Ready();
            var attack = Attack(attacker);
            var move = new MoveCommand(new Vector3(-6f, 0f, -3f));
            Assert.That(attacker.Issue(move, IssueMode.Append), Is.True);

            Assert.That(attacker.DropCurrent(attack), Is.True);
            Assert.That(attacker.CurrentCommand, Is.SameAs(move), "orders queued behind the attack carry on");
        }

        [UnityTest]
        public IEnumerator DropCurrent_OfAnotherCommand_OrNull_ChangesNothing()
        {
            yield return Ready();
            var attack = Attack(attacker);
            Assert.That(attacker.DropCurrent(new AttackCommand(target.GetComponent<Health>())), Is.False);
            Assert.That(attacker.DropCurrent(null), Is.False);
            Assert.That(attacker.CurrentCommand, Is.SameAs(attack));
        }

        [UnityTest]
        public IEnumerator LosingSight_EndsAGuardedAttack()
        {
            yield return Ready();
            var attack = Attack(attacker);
            guard.Accepted(owner, 1, 3, attack);
            Tick();
            Assert.That(guard.Tick(), Is.EqualTo(0), "still observed: the attack goes on");
            Assert.That(attacker.CurrentCommand, Is.SameAs(attack));

            intel.On = false;
            Tick();
            Assert.That(guard.Tick(), Is.EqualTo(1));
            Assert.That(attacker.CurrentCommand, Is.Null);
            Assert.That(guard.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator AnAttackTheGuardNeverSaw_KeepsChasing()
        {
            yield return Ready();
            var guarded = Attack(attacker);
            guard.Accepted(owner, 1, 3, guarded);
            var unguarded = Attack(bystander);   // as enemy AI, companion assist, retaliation or an ability would issue it

            intel.On = false;
            Tick();
            guard.Tick();
            Assert.That(attacker.CurrentCommand, Is.Null);
            Assert.That(bystander.CurrentCommand, Is.SameAs(unguarded), "only gate-accepted attacks are guarded");
        }

        [UnityTest]
        public IEnumerator AReplacedOrder_IsForgotten_AndNeverDropped()
        {
            yield return Ready();
            var attack = Attack(attacker);
            guard.Accepted(owner, 1, 3, attack);
            var move = new MoveCommand(new Vector3(-6f, 0f, -3f));
            Assert.That(attacker.Issue(move, IssueMode.Replace), Is.True);

            intel.On = false;
            Tick();
            Assert.That(guard.Tick(), Is.EqualTo(0));
            Assert.That(guard.Count, Is.EqualTo(0));
            Assert.That(attacker.CurrentCommand, Is.SameAs(move));
        }

        [UnityTest]
        public IEnumerator Forget_DropsOnlyThatConnectionsRecords()
        {
            yield return Ready();
            var other = new NetConnection("account-c", new[] { 2 });
            visibility.Add(other);
            var first = Attack(attacker);
            guard.Accepted(owner, 1, 3, first);
            var second = Attack(bystander);
            guard.Accepted(other, 2, 3, second);

            guard.Forget(owner);
            Assert.That(guard.Count, Is.EqualTo(1));
            intel.On = false;
            Tick();
            guard.Tick();
            Assert.That(attacker.CurrentCommand, Is.SameAs(first), "a departed connection's order runs as before");
            Assert.That(bystander.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator AUnitHasAtMostOneRecord()
        {
            yield return Ready();
            guard.Accepted(owner, 1, 3, Attack(attacker));
            guard.Accepted(owner, 1, 3, Attack(attacker));
            Assert.That(guard.Count, Is.EqualTo(1));
        }
    }
}
#endif
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `Tools/run-tests.sh PlayMode NetAttackGuardPlayModeTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `EXIT=1`, `error CS1061` (`DropCurrent`) and `CS0246` (`NetAttackGuard`).

- [ ] **Step 3: Add `DropCurrent` to `CommandableUnit`**

In `Assets/_Project/Scripts/Units/CommandableUnit.cs`, add this public method directly after the public `Issue(UnitCommand, IssueMode)` method. Change nothing else in the file:
```csharp
        /// <summary>
        /// Ends `command` the way an unreachable attack ends, but only while it is still the current order; the next queued order
        /// then starts. Returns false (and changes nothing) for null or for a command that is not current. The networked server
        /// uses it to end a client's attack on a target that client no longer observes (decision 047); no game system calls it.
        /// </summary>
        public bool DropCurrent(UnitCommand command)
        {
            if (command == null || !ReferenceEquals(queue.Current, command))
                return false;
            Finish();
            return true;
        }
```

- [ ] **Step 4: Write the guard**

`Assets/_Project/Scripts/Net/Core/NetAttackGuard.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// The attack-chase mitigation (hidden information slice, spec section 3.5). An accepted attack would otherwise keep following
    /// its target after the issuing client lost sight of it, so the friendly's movement would reveal where the hidden hostile
    /// goes. The guard remembers each attack the gate accepted (one per unit) and ends it, through CommandableUnit.DropCurrent,
    /// as soon as its connection no longer observes the target. Attacks issued by AI, companion assist, retaliation or abilities
    /// never pass the gate and are never touched.
    /// </summary>
    public sealed class NetAttackGuard
    {
        struct Record
        {
            public NetConnection Connection;
            public int UnitId;
            public int TargetId;
            public UnitCommand Command;
        }

        readonly NetUnits units;
        readonly NetVisibility visibility;
        readonly List<Record> records = new List<Record>();

        public NetAttackGuard(NetUnits units, NetVisibility visibility)
        {
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        }

        public int Count => records.Count;

        public void Accepted(NetConnection connection, int unitId, int targetId, UnitCommand command)
        {
            if (connection == null || command == null)
                return;
            records.RemoveAll(r => r.UnitId == unitId);
            records.Add(new Record { Connection = connection, UnitId = unitId, TargetId = targetId, Command = command });
        }

        public void Forget(NetConnection connection) => records.RemoveAll(r => r.Connection == connection);

        /// <summary>Call after NetVisibility.Update each frame. Returns how many attacks it ended.</summary>
        public int Tick()
        {
            var ended = 0;
            for (var i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                if (!units.TryGet(record.UnitId, out var unit) || !ReferenceEquals(unit.CurrentCommand, record.Command))
                {
                    records.RemoveAt(i);
                    continue;
                }
                if (visibility.KnowledgeOf(record.Connection, record.TargetId) == NetKnowledge.Observed)
                    continue;
                unit.DropCurrent(record.Command);
                records.RemoveAt(i);
                ended++;
            }
            return ended;
        }
    }
}
```

- [ ] **Step 5: Run the tests to make sure they pass, and nothing else moved**

Run: `Tools/run-tests.sh PlayMode NetAttackGuardPlayModeTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `total="7" passed="7"`, `EXIT=0`.
If `Issue(move, IssueMode.Append)` behind an attack returns false in the first test, read `CommandableUnit.Issue` to see why. Issue the move in a way that makes it a queued order, without changing `CommandableUnit`, and record the change as a ruling.
Run the existing command and combat tests to show no caller changed: `Tools/run-tests.sh PlayMode "CommandableUnitPlayModeTests;CommandableUnitQueuePlayModeTests;CommandableUnitAbilityPlayModeTests;CombatPlayModeTests;RangedCombatPlayModeTests;CompanionAIPlayModeTests;AutoRetaliatePlayModeTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`. Expected: all passed. If a class is not found, check the name in `ls Assets/_Project/Tests/PlayMode` and correct it here.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Units/CommandableUnit.cs Assets/_Project/Scripts/Net/Core/NetAttackGuard.cs Assets/_Project/Scripts/Net/Core/NetAttackGuard.cs.meta Assets/_Project/Tests/PlayMode/NetAttackGuardPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetAttackGuardPlayModeTests.cs.meta
git commit -m "Net: end a client's attack when it loses sight of the target (CommandableUnit.DropCurrent)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The fixture identity: token suffix, connection flag, and the host's fixture admission

**Files:**
- Modify: `Assets/_Project/Scripts/Net/Core/NetTokenFile.cs`, `NetConnection.cs`, `NetAdmission.cs`, `NetSessionHost.cs`
- Test: `Assets/_Project/Tests/EditMode/NetFixtureIdentityTests.cs`

**Interfaces:**
- Consumes: `NetBudget` (unchanged), `NetSight.Everything` (Task 1).
- Produces:
  - `NetTokenEntry(string account, string token, bool ownsSquad, string attempt, bool fixture = false)` and `bool Fixture`; the token file's third field accepts `squad`, `none`, `squad+fixture` and `none+fixture`.
  - `NetConnection(string account, IEnumerable<int> ownedUnitIds, bool drivesFixture = false)` and `bool DrivesFixture`.
  - `enum NetFixtureAdmission { Ignored, Refused, Allowed, Disconnect }`.
  - In `NetSessionHost`:
    - `NetConnection ConnectionOf(int clientId)`, null before hello;
    - `NetFixtureAdmission FixtureRequest(int clientId, bool fixtureEnabled, double now, out NetConnection connection)`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetFixtureIdentityTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Who may drive the development fixture (spec section 3.9): only a connection admitted with a `+fixture` token.</summary>
    public class NetFixtureIdentityTests
    {
        const string Attempt = "attempt-1";

        readonly List<GameObject> hosts = new List<GameObject>();
        NetSessionHost host;

        [SetUp]
        public void SetUp()
        {
            var units = NetUnits.FromMission(
                new List<CommandableUnit> { NetTestKit.MakeUnit(hosts, "FriendlyUnit_1"), NetTestKit.MakeUnit(hosts, "FriendlyUnit_2") },
                new List<CommandableUnit> { NetTestKit.MakeUnit(hosts, "HostileUnit_1") });
            var admission = new NetAdmission(new List<NetTokenEntry>
            {
                new NetTokenEntry("account-a", "tok-a", true, null, fixture: true),
                new NetTokenEntry("account-b", "tok-b", false, null),
            }, Attempt, friendlyCount: 2);
            host = new NetSessionHost(new ServerCommandGate(units, Attempt, NetSight.Everything), admission);
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        void Join(int clientId, string token)
        {
            host.Connected(clientId, 0d);
            Assert.That(host.Hello(clientId, token, 0d, out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void AFixtureSuffix_MarksTheEntry()
        {
            var entries = NetTokenFile.Parse("account-a tok-a squad+fixture\naccount-b tok-b none+fixture\naccount-c tok-c squad\n", out var warnings);
            Assert.That(warnings, Is.Empty);
            Assert.That(entries, Has.Count.EqualTo(3));
            Assert.That(entries[0].OwnsSquad && entries[0].Fixture, Is.True);
            Assert.That(!entries[1].OwnsSquad && entries[1].Fixture, Is.True);
            Assert.That(entries[2].OwnsSquad && !entries[2].Fixture, Is.True);
        }

        [Test]
        public void AnUnknownSuffix_IsSkipped_WithoutEchoingTheToken()
        {
            var entries = NetTokenFile.Parse("account-a tok-secret squad+admin\n", out var warnings);
            Assert.That(entries, Is.Empty);
            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.DoesNotContain("tok-secret", warnings[0]);
            StringAssert.StartsWith("line 1", warnings[0]);
        }

        [Test]
        public void AFixtureEntry_GivesAConnectionThatDrivesTheFixture()
        {
            Join(1, "tok-a");
            Join(2, "tok-b");
            Assert.That(host.ConnectionOf(1).DrivesFixture, Is.True);
            Assert.That(host.ConnectionOf(2).DrivesFixture, Is.False);
            Assert.That(host.ConnectionOf(9), Is.Null);
        }

        [Test]
        public void AFixtureMessageBeforeHello_IsIgnored()
        {
            host.Connected(1, 0d);
            Assert.That(host.FixtureRequest(1, true, 0d, out var connection), Is.EqualTo(NetFixtureAdmission.Ignored));
            Assert.That(connection, Is.Null);
            Assert.That(host.FixtureRequest(7, true, 0d, out _), Is.EqualTo(NetFixtureAdmission.Ignored), "an unknown client too");
            Assert.That(host.Rejected + host.Dropped, Is.EqualTo(0));
        }

        [Test]
        public void TheTestIdentity_IsAllowed_AndBoundToItsOwnConnection()
        {
            Join(1, "tok-a");
            Assert.That(host.FixtureRequest(1, true, 0d, out var connection), Is.EqualTo(NetFixtureAdmission.Allowed));
            Assert.That(connection, Is.SameAs(host.ConnectionOf(1)));
        }

        [Test]
        public void WithTheFixtureOff_EvenTheTestIdentityIsRefused()
        {
            Join(1, "tok-a");
            Assert.That(host.FixtureRequest(1, false, 0d, out var connection), Is.EqualTo(NetFixtureAdmission.Refused));
            Assert.That(connection, Is.Null);
        }

        [Test]
        public void AnOrdinaryClient_IsRefused_AndCountsTowardsAbuse()
        {
            Join(2, "tok-b");
            for (var i = 0; i < NetSessionHost.AbuseLimit; i++)
                Assert.That(host.FixtureRequest(2, true, i * 0.02d, out _), Is.EqualTo(NetFixtureAdmission.Refused), "message " + i);
            Assert.That(host.FixtureRequest(2, true, NetSessionHost.AbuseLimit * 0.02d, out _), Is.EqualTo(NetFixtureAdmission.Disconnect));
            Assert.That(host.Rejected, Is.EqualTo(NetSessionHost.AbuseLimit + 1));
        }

        [Test]
        public void AFixtureFlood_IsDropped_ThenDisconnects()
        {
            Join(1, "tok-a");
            for (var i = 0; i < NetSessionHost.Burst; i++)
                Assert.That(host.FixtureRequest(1, true, 0d, out _), Is.EqualTo(NetFixtureAdmission.Allowed));
            for (var i = 0; i < NetSessionHost.AbuseLimit; i++)
                Assert.That(host.FixtureRequest(1, true, 0d, out _), Is.EqualTo(NetFixtureAdmission.Ignored), "over budget " + i);
            Assert.That(host.FixtureRequest(1, true, 0d, out _), Is.EqualTo(NetFixtureAdmission.Disconnect));
            Assert.That(host.Dropped, Is.EqualTo(NetSessionHost.AbuseLimit + 1));
        }
    }
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `Tools/run-tests.sh EditMode NetFixtureIdentityTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `EXIT=1`, errors naming `fixture` (named argument), `ConnectionOf`, `FixtureRequest`, `NetFixtureAdmission`.

- [ ] **Step 3: Implement**

`NetTokenFile.cs`:
- `NetTokenEntry`: add the parameter `bool fixture = false` (last) to the constructor, assign it, and add:
```csharp
        /// <summary>True for a `+fixture` entry: the account may drive the development fixture (Editor and development builds only).</summary>
        public bool Fixture { get; }
```
- Change the format in the summaries to `account token squad|none[+fixture] [attempt]`. Replace the `bool squad; if (fields[2] == "squad") ... else { warnings.Add(...); continue; }` block with:
```csharp
                if (!TryOwnership(fields[2], out var squad, out var fixture))
                {
                    warnings.Add("line " + number + ": the third field must be 'squad' or 'none', optionally with '+fixture'; skipped");
                    continue;
                }
```
- Change the `entries.Add(...)` call to pass `fixture` as the fifth argument.
- Change the field-count warning to `"... expected '<account> <token> <squad|none>[+fixture] [attempt]'; skipped"`.
- Add:
```csharp
        static bool TryOwnership(string field, out bool squad, out bool fixture)
        {
            fixture = false;
            var plus = field.IndexOf('+');
            if (plus >= 0)
            {
                if (field.Substring(plus + 1) != "fixture")
                {
                    squad = false;
                    return false;
                }
                fixture = true;
                field = field.Substring(0, plus);
            }
            squad = field == "squad";
            return squad || field == "none";
        }
```

`NetConnection.cs`: change the constructor to `public NetConnection(string account, IEnumerable<int> ownedUnitIds, bool drivesFixture = false)`, assign it, and add:
```csharp
        /// <summary>Admitted with a `+fixture` token: may send fixture verbs when the server runs -netFixture.</summary>
        public bool DrivesFixture { get; }
```

`NetAdmission.cs`: `connection = new NetConnection(entry.Account, Enumerable.Range(firstOwnedId, ownedCount), entry.Fixture);`

`NetSessionHost.cs`: add the enum above the class:
```csharp
    /// <summary>What the host did with one fixture message (spec section 3.9).</summary>
    public enum NetFixtureAdmission
    {
        /// <summary>Before hello, or over budget: dropped and not answered.</summary>
        Ignored,
        /// <summary>The fixture is off, or the client has no fixture identity: changes nothing and counts toward abuse.</summary>
        Refused,
        /// <summary>The adapter may carry out the verb for the returned connection.</summary>
        Allowed,
        /// <summary>The client has become abusive: the adapter must disconnect it.</summary>
        Disconnect,
    }
```
and these members in the class:
```csharp
        /// <summary>The welcomed client's connection; null before hello and for unknown clients.</summary>
        public NetConnection ConnectionOf(int clientId) =>
            sessions.TryGetValue(clientId, out var session) ? session.Connection : null;

        /// <summary>
        /// Decides one fixture message. It shares the request budget and the abuse count; a refusal counts like a rejected request.
        /// The returned connection is the server's own object for this client, so a grant can never name another account.
        /// </summary>
        public NetFixtureAdmission FixtureRequest(int clientId, bool fixtureEnabled, double now, out NetConnection connection)
        {
            connection = null;
            if (!sessions.TryGetValue(clientId, out var session) || session.Connection == null)
                return NetFixtureAdmission.Ignored;
            if (!session.Budget.TryTake(now))
            {
                Dropped++;
                return session.Budget.NoteRefusal(now) ? NetFixtureAdmission.Disconnect : NetFixtureAdmission.Ignored;
            }
            if (!fixtureEnabled || !session.Connection.DrivesFixture)
            {
                Rejected++;
                return session.Budget.NoteRefusal(now) ? NetFixtureAdmission.Disconnect : NetFixtureAdmission.Refused;
            }
            connection = session.Connection;
            return NetFixtureAdmission.Allowed;
        }
```

- [ ] **Step 4: Run the tests to make sure they pass**

Run: `Tools/run-tests.sh EditMode "NetFixtureIdentityTests;NetTokenFileTests;NetAdmissionTests;NetSessionHostTests;NetSourceAuditTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: all passed, `EXIT=0`. If `AFixtureFlood` or `AnOrdinaryClient` disagree on a count by one, read `NetBudget.NoteRefusal` (the limit counts "more than AbuseLimit"). Correct the test's expectation to the documented rule, without changing the budget.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net/Core/NetTokenFile.cs Assets/_Project/Scripts/Net/Core/NetConnection.cs Assets/_Project/Scripts/Net/Core/NetAdmission.cs Assets/_Project/Scripts/Net/Core/NetSessionHost.cs Assets/_Project/Tests/EditMode/NetFixtureIdentityTests.cs Assets/_Project/Tests/EditMode/NetFixtureIdentityTests.cs.meta
git commit -m "Net: the fixture test identity (+fixture tokens) and its budgeted admission

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The development fixture (Core, development builds only)

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetFixtureVerb.cs`, `NetItem.cs`, `NetFixtureContainer.cs`, `NetFixture.cs`
- Test: `Assets/_Project/Tests/EditMode/NetFixtureTests.cs`

**Interfaces:**
- Consumes: `INetIntelSource` (Task 1), `NetUnits`, `NetConnection.Owns`.
- Produces (always compiled):
  - `enum NetFixtureVerb : byte { None = 0, Observe = 1, Unobserve = 2, MoveHostile = 3, Search = 4 }`;
  - `readonly struct NetItem(string definitionId, int quantity, string instanceId)` with `DefinitionId`, `Quantity`, `InstanceId`.
- Produces (only inside `#if UNITY_EDITOR || DEVELOPMENT_BUILD`):
  - `sealed class NetFixtureContainer`, with:
    - `const int Id = 1`;
    - `bool IsSearchedBy(NetConnection)`;
    - `IReadOnlyList<NetItem> ContentsFor(NetConnection)`, which is null unless that connection searched;
    - `void Search(NetConnection)` and `void Forget(NetConnection)`.
  - `sealed class NetFixture : INetIntelSource`, with:
    - `NetFixture(NetUnits units, int hostileId, IReadOnlyList<Vector3> points, Action<Vector3> moveHostile)`;
    - `const float PointSpacing = 3f`;
    - `int HostileId`, `IReadOnlyList<Vector3> Points`, `int Step` and `NetFixtureContainer Container`;
    - `bool Apply(NetConnection, NetFixtureVerb)` and `void Forget(NetConnection)`;
    - `static List<Vector3> SamplePoints(Vector3 origin, int count)`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetFixtureTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetFixtureTests
    {
        const int Hostile = 3;
        static readonly Vector3 P1 = new Vector3(4f, 0f, 0f);
        static readonly Vector3 P2 = new Vector3(0f, 0f, 4f);

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<Vector3> moves = new List<Vector3>();
        CommandableUnit friend1;
        CommandableUnit friend2;
        NetUnits units;
        NetFixture fixture;
        NetConnection a;
        NetConnection b;

        [SetUp]
        public void SetUp()
        {
            friend1 = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            friend2 = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            units = NetUnits.FromMission(new List<CommandableUnit> { friend1, friend2 },
                new List<CommandableUnit> { NetTestKit.MakeUnit(hosts, "HostileUnit_1") });
            moves.Clear();
            fixture = new NetFixture(units, Hostile, new[] { P1, P2 }, moves.Add);
            a = new NetConnection("account-a", new[] { 1, 2 }, drivesFixture: true);
            b = new NetConnection("account-b", new int[0]);
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        [Test]
        public void Observe_GrantsOnlyTheSendingConnection()
        {
            Assert.That(fixture.Apply(a, NetFixtureVerb.Observe), Is.True);
            Assert.That(fixture.Observes(a, Hostile), Is.True);
            Assert.That(fixture.Observes(b, Hostile), Is.False, "A's grant never reveals the hostile to B");
            Assert.That(fixture.Step, Is.EqualTo(1));
        }

        [Test]
        public void Unobserve_RevokesOnlyThatConnection()
        {
            fixture.Apply(a, NetFixtureVerb.Observe);
            fixture.Apply(b, NetFixtureVerb.Observe);
            fixture.Apply(a, NetFixtureVerb.Unobserve);
            Assert.That(fixture.Observes(a, Hostile), Is.False);
            Assert.That(fixture.Observes(b, Hostile), Is.True);
        }

        [Test]
        public void TheFixture_SeesOnlyItsHostile()
        {
            fixture.Apply(a, NetFixtureVerb.Observe);
            Assert.That(fixture.Observes(a, 1), Is.False);
            Assert.That(fixture.Observes(a, 2), Is.False);
            Assert.That(fixture.Observes(null, Hostile), Is.False);
            Assert.That(fixture.HostileId, Is.EqualTo(Hostile));
        }

        [Test]
        public void MoveHostile_WalksThePointsInOrder()
        {
            for (var i = 0; i < 3; i++)
                Assert.That(fixture.Apply(a, NetFixtureVerb.MoveHostile), Is.True);
            Assert.That(moves, Is.EqualTo(new[] { P1, P2, P1 }));
            Assert.That(fixture.Step, Is.EqualTo(3));
        }

        [Test]
        public void MoveHostile_WithoutPoints_ChangesNothing()
        {
            var empty = new NetFixture(units, Hostile, new Vector3[0], moves.Add);
            Assert.That(empty.Apply(a, NetFixtureVerb.MoveHostile), Is.False);
            Assert.That(empty.Step, Is.EqualTo(0));
            Assert.That(moves, Is.Empty);
        }

        [Test]
        public void Search_NeedsALivingOwnedUnit()
        {
            Assert.That(fixture.Apply(b, NetFixtureVerb.Search), Is.False, "B owns no operative");
            friend1.GetComponent<Health>().TakeDamage(100000);
            friend2.GetComponent<Health>().TakeDamage(100000);
            Assert.That(fixture.Apply(a, NetFixtureVerb.Search), Is.False, "a squad with nobody alive cannot search");
            Assert.That(fixture.Step, Is.EqualTo(0));
        }

        [Test]
        public void TheContainer_ShowsItsContents_OnlyToTheSearcher()
        {
            Assert.That(fixture.Container.ContentsFor(a), Is.Null, "nothing before the search");
            Assert.That(fixture.Apply(a, NetFixtureVerb.Search), Is.True);
            Assert.That(fixture.Container.ContentsFor(a), Has.Count.EqualTo(2));
            Assert.That(fixture.Container.ContentsFor(b), Is.Null, "B never receives A's search");
            Assert.That(fixture.Container.IsSearchedBy(b), Is.False);
        }

        [Test]
        public void TheContainersContents_AreFixed()
        {
            fixture.Apply(a, NetFixtureVerb.Search);
            var contents = fixture.Container.ContentsFor(a);
            Assert.That(contents[0].DefinitionId, Is.EqualTo("fixture-medkit"));
            Assert.That(contents[0].Quantity, Is.EqualTo(2));
            Assert.That(contents[0].InstanceId, Is.EqualTo("fixture-item-0001"));
            Assert.That(contents[1].DefinitionId, Is.EqualTo("fixture-rifle-ammo"));
            Assert.That(contents[1].Quantity, Is.EqualTo(30));
            Assert.That(contents[1].InstanceId, Is.EqualTo("fixture-item-0002"));
            Assert.That(NetFixtureContainer.Id, Is.EqualTo(1));
        }

        [Test]
        public void AnUnknownVerb_OrNoConnection_ChangesNothing()
        {
            Assert.That(fixture.Apply(a, NetFixtureVerb.None), Is.False);
            Assert.That(fixture.Apply(a, (NetFixtureVerb)99), Is.False);
            Assert.That(fixture.Apply(null, NetFixtureVerb.Observe), Is.False);
            Assert.That(fixture.Step, Is.EqualTo(0));
        }

        [Test]
        public void Forget_DropsTheGrantAndTheSearch()
        {
            fixture.Apply(a, NetFixtureVerb.Observe);
            fixture.Apply(a, NetFixtureVerb.Search);
            fixture.Forget(a);
            Assert.That(fixture.Observes(a, Hostile), Is.False);
            Assert.That(fixture.Container.ContentsFor(a), Is.Null);
        }
    }
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `Tools/run-tests.sh EditMode NetFixtureTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `EXIT=1`, `CS0246` naming `NetFixture`, `NetFixtureVerb`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Net/Core/NetFixtureVerb.cs`:
```csharp
namespace Blackglass
{
    /// <summary>
    /// The development fixture's verbs (hidden information slice, spec section 3.9). The wire carries this byte in
    /// NetFixtureMessage; a verb names no unit and no account. Compiled into every build so the server can always recognise,
    /// count and refuse the message; only Editor and development builds can carry one out.
    /// </summary>
    public enum NetFixtureVerb : byte
    {
        None = 0,
        Observe = 1,
        Unobserve = 2,
        MoveHostile = 3,
        Search = 4,
    }
}
```

`Assets/_Project/Scripts/Net/Core/NetItem.cs`:
```csharp
namespace Blackglass
{
    /// <summary>One stack in a container: what it is, how many, and its instance id. Sent only to a connection that searched.</summary>
    public readonly struct NetItem
    {
        public NetItem(string definitionId, int quantity, string instanceId)
        {
            DefinitionId = definitionId;
            Quantity = quantity;
            InstanceId = instanceId;
        }

        public string DefinitionId { get; }
        public int Quantity { get; }
        public string InstanceId { get; }
    }
}
```

`Assets/_Project/Scripts/Net/Core/NetFixtureContainer.cs`:
```csharp
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// The development fixture's one container (spec section 3.9). Its fixed contents exist only on the server until a connection
    /// searches it, and then only for that connection. The real loot containers and inventory are not on the server; this narrow
    /// stand-in proves the replication rule only. The items are labelled as fixtures and name no catalogue item.
    /// </summary>
    public sealed class NetFixtureContainer
    {
        public const int Id = 1;

        static readonly NetItem[] Contents =
        {
            new NetItem("fixture-medkit", 2, "fixture-item-0001"),
            new NetItem("fixture-rifle-ammo", 30, "fixture-item-0002"),
        };

        readonly HashSet<NetConnection> searched = new HashSet<NetConnection>();

        public bool IsSearchedBy(NetConnection connection) => connection != null && searched.Contains(connection);

        /// <summary>The contents for a connection that searched; null for any other.</summary>
        public IReadOnlyList<NetItem> ContentsFor(NetConnection connection) => IsSearchedBy(connection) ? Contents : null;

        public void Search(NetConnection connection)
        {
            if (connection != null)
                searched.Add(connection);
        }

        public void Forget(NetConnection connection)
        {
            if (connection != null)
                searched.Remove(connection);
        }
    }
}
#endif
```

`Assets/_Project/Scripts/Net/Core/NetFixture.cs`:
```csharp
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>
    /// The deterministic development fixture (hidden information slice, spec section 3.9), compiled only into the Editor and
    /// development builds. In -netFixture mode it is the visibility source: a connection observes the fixture's hostile only after
    /// Observe, and grants are keyed by the server's own connection object, never by an account string. Real sight and exposure
    /// are not consulted, so what the harness sees does not depend on the map. MoveHostile walks the hostile through points the
    /// server sampled and never sends. The adapter checks the caller's fixture identity before Apply.
    /// </summary>
    public sealed class NetFixture : INetIntelSource
    {
        /// <summary>The smallest distance between two walk points, and between a point and the spawn.</summary>
        public const float PointSpacing = 3f;

        readonly NetUnits units;
        readonly int hostileId;
        readonly IReadOnlyList<Vector3> points;
        readonly Action<Vector3> moveHostile;
        readonly HashSet<NetConnection> observers = new HashSet<NetConnection>();
        int nextPoint;

        public NetFixture(NetUnits units, int hostileId, IReadOnlyList<Vector3> points, Action<Vector3> moveHostile)
        {
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.hostileId = hostileId;
            this.points = points ?? new Vector3[0];
            this.moveHostile = moveHostile ?? throw new ArgumentNullException(nameof(moveHostile));
        }

        public int HostileId => hostileId;
        public IReadOnlyList<Vector3> Points => points;
        /// <summary>How many verbs have changed something.</summary>
        public int Step { get; private set; }
        public NetFixtureContainer Container { get; } = new NetFixtureContainer();

        public bool Observes(NetConnection connection, int id) =>
            id == hostileId && connection != null && observers.Contains(connection);

        /// <summary>Carries out one verb for an authorized connection; false, with no step, when it changes nothing.</summary>
        public bool Apply(NetConnection connection, NetFixtureVerb verb)
        {
            if (connection == null)
                return false;
            switch (verb)
            {
                case NetFixtureVerb.Observe:
                    observers.Add(connection);
                    break;
                case NetFixtureVerb.Unobserve:
                    observers.Remove(connection);
                    break;
                case NetFixtureVerb.MoveHostile:
                    if (points.Count == 0)
                        return false;
                    moveHostile(points[nextPoint % points.Count]);
                    nextPoint++;
                    break;
                case NetFixtureVerb.Search:
                    if (!OwnsALivingUnit(connection))
                        return false;
                    Container.Search(connection);
                    break;
                default:
                    return false;
            }
            Step++;
            return true;
        }

        public void Forget(NetConnection connection)
        {
            observers.Remove(connection);
            Container.Forget(connection);
        }

        bool OwnsALivingUnit(NetConnection connection)
        {
            for (var id = 1; id <= units.Count; id++)
            {
                if (connection.Owns(id) && units.TryGet(id, out var unit) && unit.IsAlive)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Up to `count` walkable points around `origin`, on rings of 4 m and 7 m, each at least PointSpacing (on the ground plane)
        /// from the origin and from each other.
        /// </summary>
        public static List<Vector3> SamplePoints(Vector3 origin, int count)
        {
            var found = new List<Vector3>();
            foreach (var radius in new[] { 4f, 7f })
            {
                for (var step = 0; step < 8 && found.Count < count; step++)
                {
                    var angle = step * Mathf.PI / 4f;
                    var candidate = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                    if (!NavMesh.SamplePosition(candidate, out var hit, 1.5f, NavMesh.AllAreas))
                        continue;
                    if (Flat(hit.position - origin) < PointSpacing)
                        continue;
                    var clear = true;
                    foreach (var point in found)
                        clear &= Flat(hit.position - point) >= PointSpacing;
                    if (clear)
                        found.Add(hit.position);
                }
            }
            return found;
        }

        static float Flat(Vector3 offset)
        {
            offset.y = 0f;
            return offset.magnitude;
        }
    }
}
#endif
```

- [ ] **Step 4: Run the tests to make sure they pass**

Run: `Tools/run-tests.sh EditMode "NetFixtureTests;NetSourceAuditTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `total="11" passed="11"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net/Core/NetFixtureVerb.cs Assets/_Project/Scripts/Net/Core/NetFixtureVerb.cs.meta Assets/_Project/Scripts/Net/Core/NetItem.cs Assets/_Project/Scripts/Net/Core/NetItem.cs.meta Assets/_Project/Scripts/Net/Core/NetFixtureContainer.cs Assets/_Project/Scripts/Net/Core/NetFixtureContainer.cs.meta Assets/_Project/Scripts/Net/Core/NetFixture.cs Assets/_Project/Scripts/Net/Core/NetFixture.cs.meta Assets/_Project/Tests/EditMode/NetFixtureTests.cs Assets/_Project/Tests/EditMode/NetFixtureTests.cs.meta
git commit -m "Net: the development fixture (grants per connection, scripted hostile moves, one container)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Messages, the observer condition, a server-authoritative avatar, and the audits

**Files:**
- Modify:
  - `Assets/_Project/Scripts/Net/Adapter/NetMessages.cs`, `NetUnitState.cs`;
  - `NetAvatars.cs` (rename `Spawn` to `SpawnAll`), and its one call in `NetServer.cs`;
  - `Assets/_Project/Editor/Net/NetAssetBuilder.cs`;
  - `Assets/_Project/Tests/EditMode/NetAvatarPrefabTests.cs` and `NetSourceAuditTests.cs`.
- Create: `Assets/_Project/Scripts/Net/Adapter/NetVisibilityCondition.cs`
- Regenerate: `Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab`; create `Assets/_Project/Prefabs/Net/NetVisibilityCondition.asset`
- Test: `Assets/_Project/Tests/EditMode/NetMessageShapeTests.cs`, `Assets/_Project/Tests/EditMode/NetVisibilityConditionTests.cs`

**Interfaces:**
- Consumes: `NetKnowledge` (Task 1), `NetFixtureVerb` (Task 6).
- Produces:
  - Broadcasts:
    - `NetKnowledgeMessage { int UnitId; byte Knowledge; Vector3 Position; }`;
    - `NetFixtureMessage { byte Verb; }`;
    - `NetFixtureStepMessage { int Step; }`;
    - `NetContainerMessage { int ContainerId; NetItemWire[] Entries; }`.
  - `struct NetItemWire { string DefinitionId; int Quantity; string InstanceId; }`.
  - On `NetUnitState`:
    - `internal Func<NetworkConnection, bool> VisibleTo`, server only and never replicated;
    - `internal bool IsVisibleTo(NetworkConnection)`, false when `VisibleTo` is null.
  - `sealed class NetVisibilityCondition : ObserverCondition` (Normal), which asks the avatar's `NetUnitState`.
  - `NetAvatars.SpawnAll(ServerManager server, GameObject prefab, NetUnits units)`, renamed; Task 8 adds a parameter.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetMessageShapeTests.cs`:
```csharp
using System;
using System.Linq;
using System.Reflection;
using FishNet.Broadcast;
using FishNet.Object.Synchronizing;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// No normal client message can carry the seed, a spawn plan, a mission plan or a layout (spec section 3.8): every field of
    /// every broadcast and every replicated avatar field is a plain value, and none is named after one of those.
    /// </summary>
    public class NetMessageShapeTests
    {
        static readonly string[] ForbiddenNames = { "seed", "plan", "spawn", "layout" };

        static Type[] Broadcasts() => typeof(NetHelloMessage).Assembly.GetTypes()
            .Where(t => t.IsValueType && typeof(IBroadcast).IsAssignableFrom(t)).ToArray();

        static bool IsPlain(Type type) =>
            type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Vector3) || type == typeof(Quaternion);

        static void CheckName(string name, string where)
        {
            foreach (var bad in ForbiddenNames)
                Assert.That(name.IndexOf(bad, StringComparison.OrdinalIgnoreCase), Is.LessThan(0), where + " is named after '" + bad + "'");
        }

        static void CheckFields(Type type, string path)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var where = path + "." + field.Name;
                CheckName(field.Name, where);
                var fieldType = field.FieldType;
                if (fieldType.IsArray)
                {
                    var element = fieldType.GetElementType();
                    Assert.That(element.IsValueType && !element.IsPrimitive && !element.IsEnum && element.Namespace == "Blackglass", Is.True,
                        where + " is an array of " + element.Name + "; only arrays of the networking experiment's own structs are allowed");
                    CheckFields(element, where + "[]");
                    continue;
                }
                Assert.That(IsPlain(fieldType), Is.True, where + " has type " + fieldType.Name);
            }
        }

        [Test]
        public void TheBroadcasts_AreExactlyTheKnownMessages()
        {
            Assert.That(Broadcasts().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal), Is.EqualTo(new[]
            {
                "NetContainerMessage", "NetFixtureMessage", "NetFixtureStepMessage", "NetHelloMessage", "NetKnowledgeMessage",
                "NetRefusedMessage", "NetRequestMessage", "NetResultMessage", "NetWelcomeMessage",
            }));
        }

        [Test]
        public void EveryBroadcastField_IsAPlainValue_NamedAfterNoSeedPlanSpawnOrLayout()
        {
            foreach (var type in Broadcasts())
                CheckFields(type, type.Name);
        }

        [Test]
        public void EveryReplicatedAvatarField_IsAPlainValue_NamedAfterNoSeedPlanSpawnOrLayout()
        {
            var fields = typeof(NetUnitState).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(SyncVar<>)).ToList();
            Assert.That(fields.Select(f => f.Name), Is.SupersetOf(new[] { "unitId", "hostile", "health", "maxHealth", "alive", "order", "queue", "steering" }));
            foreach (var field in fields)
            {
                CheckName(field.Name, "NetUnitState." + field.Name);
                var valueType = field.FieldType.GetGenericArguments()[0];
                Assert.That(IsPlain(valueType), Is.True, "NetUnitState." + field.Name + " replicates a " + valueType.Name);
            }
        }
    }
}
```

`Assets/_Project/Tests/EditMode/NetVisibilityConditionTests.cs`:
```csharp
using FishNet.Observing;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetVisibilityConditionTests
    {
        [Test]
        public void WithoutAnAvatar_TheConditionHidesEverything()
        {
            var condition = ScriptableObject.CreateInstance<NetVisibilityCondition>();
            try
            {
                Assert.That(condition.ConditionMet(null, false, out var notProcessed), Is.False, "fails closed");
                Assert.That(notProcessed, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(condition);
            }
        }

        [Test]
        public void TheCondition_IsCheckedOnlyOnRebuilds()
        {
            var condition = ScriptableObject.CreateInstance<NetVisibilityCondition>();
            try
            {
                Assert.That(condition.GetConditionType(), Is.EqualTo(ObserverConditionType.Normal));
            }
            finally
            {
                Object.DestroyImmediate(condition);
            }
        }
    }
}
```

Add to `NetAvatarPrefabTests.cs` (add `using FishNet.Observing;`):
```csharp
        [Test]
        public void TheAvatarTransform_IsServerAuthoritative()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            var settings = new SerializedObject(prefab.GetComponent<NetworkTransform>());
            var property = settings.FindProperty("_clientAuthoritative");
            Assert.That(property, Is.Not.Null, "FishNet renamed the field; re-check the authority setting");
            Assert.That(property.boolValue, Is.False, "no client may write an avatar's transform, owner or not");
        }

        [Test]
        public void TheAvatar_IsObservedOnlyThroughTheVisibilityCondition()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            var observer = prefab.GetComponent<NetworkObserver>();
            Assert.That(observer, Is.Not.Null, "without a NetworkObserver every client observes every avatar");
            Assert.That(observer.OverrideType, Is.EqualTo(NetworkObserver.ConditionOverrideType.IgnoreManager));
            Assert.That(observer.ObserverConditions, Has.Count.EqualTo(1));
            Assert.That(observer.ObserverConditions[0], Is.InstanceOf<NetVisibilityCondition>());
        }
```

In `NetSourceAuditTests.cs`, add `"Unity.Transport", "Unity.Collections"` to the `forbidden` array, add `using System.Text.RegularExpressions;`, and add:
```csharp
        [Test]
        public void TheAdapter_NeverGivesOwnership_NorSpawnsWithAnOwner()
        {
            var files = Directory.GetFiles("Assets/_Project/Scripts/Net/Adapter", "*.cs");
            Assert.That(files, Is.Not.Empty, "the audit must not pass vacuously");
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var text = File.ReadAllText(file);
                Assert.That(text.Contains("GiveOwnership"), Is.False, name + " gives ownership of an object");
                Assert.That(text.Contains("_clientAuthoritative"), Is.False, name + " touches transform authority at runtime");
                foreach (Match call in Regex.Matches(text, @"\.Spawn\(([^()]*)\)"))
                    Assert.That(call.Groups[1].Value.Contains(","), Is.False, name + " spawns with an owner: " + call.Value);
            }
        }
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `Tools/run-tests.sh EditMode "NetMessageShapeTests;NetVisibilityConditionTests;NetAvatarPrefabTests;NetSourceAuditTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `EXIT=1` (`NetVisibilityCondition` does not exist).

- [ ] **Step 3: Messages, the condition and the avatar's server-side hook**

Append to `NetMessages.cs`, and change its header comment from "The five messages" to "The messages":
```csharp
    /// <summary>
    /// Server to one client: its knowledge of one hostile changed (a NetKnowledge value). Position is the frozen last-known record
    /// for LastKnown and zero otherwise; the live position travels only on the avatar while the hostile is observed.
    /// </summary>
    public struct NetKnowledgeMessage : IBroadcast
    {
        public int UnitId;
        public byte Knowledge;
        public Vector3 Position;
    }

    /// <summary>
    /// Client to server, development fixture only: one NetFixtureVerb. It names no unit and no account; the server binds it to
    /// the sending connection and refuses it from anyone without the fixture identity.
    /// </summary>
    public struct NetFixtureMessage : IBroadcast
    {
        public byte Verb;
    }

    /// <summary>Server to every welcomed client, development fixture only: how many fixture verbs have taken effect.</summary>
    public struct NetFixtureStepMessage : IBroadcast
    {
        public int Step;
    }

    /// <summary>One stack of a searched container, on the wire.</summary>
    public struct NetItemWire
    {
        public string DefinitionId;
        public int Quantity;
        public string InstanceId;
    }

    /// <summary>Server to the one client that searched: the container's contents. Never sent before the search.</summary>
    public struct NetContainerMessage : IBroadcast
    {
        public int ContainerId;
        public NetItemWire[] Entries;
    }
```

`Assets/_Project/Scripts/Net/Adapter/NetVisibilityCondition.cs`:
```csharp
using FishNet.Connection;
using FishNet.Observing;

namespace Blackglass
{
    /// <summary>
    /// The avatar's only observer condition (hidden information slice, spec section 3.3). FishNet asks it, on each observer
    /// rebuild, whether a connection may receive this avatar; it answers what the server's NetVisibility decided, through the
    /// avatar's NetUnitState, and "no" whenever anything is missing. A Normal condition: it is checked only when the server
    /// rebuilds observers, which it does on every transition and when a client's hello is accepted.
    /// </summary>
    public sealed class NetVisibilityCondition : ObserverCondition
    {
        NetUnitState state;

        public override bool ConditionMet(NetworkConnection connection, bool currentlyAdded, out bool notProcessed)
        {
            notProcessed = false;
            if (state == null && NetworkObject != null)
                state = NetworkObject.GetComponent<NetUnitState>();
            return state != null && state.IsVisibleTo(connection);
        }

        public override ObserverConditionType GetConditionType() => ObserverConditionType.Normal;
    }
}
```

In `NetUnitState.cs`, add `using System;` and `using FishNet.Connection;`. Change the summary's last sentence to: "A client reads these properties; no message can change them, and the server decides per connection whether the avatar is sent at all (NetVisibilityCondition)." Add:
```csharp
        /// <summary>
        /// Server only: whether a connection may receive this avatar. NetAvatars sets it before spawning; it is never replicated.
        /// Unset means nobody, so a misconfigured avatar fails closed.
        /// </summary>
        internal Func<NetworkConnection, bool> VisibleTo;

        internal bool IsVisibleTo(NetworkConnection connection) => connection != null && VisibleTo != null && VisibleTo(connection);
```

In `NetAvatars.cs`, rename `internal void Spawn(ServerManager server, GameObject prefab, NetUnits units)` to `SpawnAll`. In `NetServer.cs`, change the call `avatars.Spawn(manager.ServerManager, avatarPrefab, Units);` to `avatars.SpawnAll(manager.ServerManager, avatarPrefab, Units);`. The audit only allows one-argument `.Spawn(` calls, so `NetAvatars` must not share the name.

Until Task 8 sets `VisibleTo`, every avatar is hidden from every client. Server-side state is unchanged.

- [ ] **Step 4: The builder: a server-authoritative transform and the condition**

Replace `NetAssetBuilder.Build()` and its summary with the version below, and add `using FishNet.Observing;`:
```csharp
    /// <summary>
    /// Builds the networked avatar prefab (networking experiment, decisions 046 and 047): a NetworkObject, a server-authoritative
    /// NetworkTransform, a NetworkObserver whose only condition is NetVisibilityCondition (ignoring the manager's defaults), and a
    /// NetUnitState, with no renderer or collider. Importing the prefab makes FishNet's asset postprocessor add it to
    /// Assets/DefaultPrefabObjects.asset. Idempotent.
    /// </summary>
    public static class NetAssetBuilder
    {
        public const string AvatarPath = "Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab";
        public const string ConditionPath = "Assets/_Project/Prefabs/Net/NetVisibilityCondition.asset";
        public const string PrefabCollectionPath = "Assets/DefaultPrefabObjects.asset";

        [MenuItem("Blackglass/Net/Build Net Assets")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        public static void Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AvatarPath));
            var condition = AssetDatabase.LoadAssetAtPath<NetVisibilityCondition>(ConditionPath);
            if (condition == null)
            {
                condition = ScriptableObject.CreateInstance<NetVisibilityCondition>();
                AssetDatabase.CreateAsset(condition, ConditionPath);
            }

            var avatar = new GameObject("NetUnitAvatar");
            avatar.AddComponent<NetworkObject>();
            var networkTransform = avatar.AddComponent<NetworkTransform>();
            var observer = avatar.AddComponent<NetworkObserver>();
            avatar.AddComponent<NetUnitState>();

            // FishNet defaults a NetworkTransform to client-authoritative; ownership must never grant a client the transform.
            var transformSettings = new SerializedObject(networkTransform);
            transformSettings.FindProperty("_clientAuthoritative").boolValue = false;
            transformSettings.ApplyModifiedPropertiesWithoutUndo();

            var observerSettings = new SerializedObject(observer);
            observerSettings.FindProperty("_overrideType").intValue = (int)NetworkObserver.ConditionOverrideType.IgnoreManager;
            var conditions = observerSettings.FindProperty("_observerConditions");
            conditions.arraySize = 1;
            conditions.GetArrayElementAtIndex(0).objectReferenceValue = condition;
            observerSettings.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(avatar, AvatarPath);
            Object.DestroyImmediate(avatar);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("NetAssetBuilder: saved " + AvatarPath + " and " + ConditionPath + ".");
        }
    }
```

Then regenerate the assets:
```bash
Tools/run-method.sh Blackglass.EditorTools.NetAssetBuilder.BuildFromCommandLine 2>&1 | tail -3
grep -h "NetAssetBuilder" Logs/Method-Blackglass_EditorTools_NetAssetBuilder_BuildFromCommandLine.log | tail -2
git status --short Assets/_Project/Prefabs Assets/DefaultPrefabObjects.asset
grep -n "_clientAuthoritative\|_overrideType\|_observerConditions" -A2 Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab | head -12
```
Expected:
- `EXIT=0` and `NetAssetBuilder: saved ...`;
- the prefab is modified and the condition asset (and its `.meta`) is new; `DefaultPrefabObjects.asset` is unchanged, or changes only in a reordered entry. Restore any other change with `git checkout -- <file>`, then report it;
- the prefab shows `_clientAuthoritative: 0`, `_overrideType: 3`, and one `_observerConditions` entry that references the condition asset's GUID.

- [ ] **Step 5: Run the tests to make sure they pass**

Run: `Tools/run-tests.sh EditMode "NetMessageShapeTests;NetVisibilityConditionTests;NetAvatarPrefabTests;NetSourceAuditTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: all passed (3 + 2 + 4 + 2 = 11), `EXIT=0`.
Then run the networking PlayMode tests. Each must still pass: in host mode the avatars exist on the server whether or not the host client observes them.
`Tools/run-tests.sh PlayMode "NetServerPlayModeTests;NetLoopbackPlayModeTests;NetManagerFactoryPlayModeTests;NetGatePlayModeTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: all passed. If FishNet logs an error about the observer (for example "is not initialized"), read it. The usual cause is a missing condition reference in the prefab: rebuild it, and do not loosen the test.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Net/Adapter/NetMessages.cs Assets/_Project/Scripts/Net/Adapter/NetUnitState.cs Assets/_Project/Scripts/Net/Adapter/NetAvatars.cs Assets/_Project/Scripts/Net/Adapter/NetServer.cs Assets/_Project/Scripts/Net/Adapter/NetVisibilityCondition.cs Assets/_Project/Scripts/Net/Adapter/NetVisibilityCondition.cs.meta Assets/_Project/Editor/Net/NetAssetBuilder.cs Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab Assets/_Project/Prefabs/Net/NetVisibilityCondition.asset Assets/_Project/Prefabs/Net/NetVisibilityCondition.asset.meta Assets/_Project/Tests/EditMode/NetMessageShapeTests.cs Assets/_Project/Tests/EditMode/NetMessageShapeTests.cs.meta Assets/_Project/Tests/EditMode/NetVisibilityConditionTests.cs Assets/_Project/Tests/EditMode/NetVisibilityConditionTests.cs.meta Assets/_Project/Tests/EditMode/NetAvatarPrefabTests.cs Assets/_Project/Tests/EditMode/NetSourceAuditTests.cs
git commit -m "Net: visibility messages, the avatar observer condition, server-authoritative avatars, message and source audits

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: The server runs the real intelligence and replicates per connection

**Files:**
- Modify:
  - `Assets/_Project/Scripts/Server/ServerOptions.cs`;
  - `Assets/_Project/Editor/Server/ServerSceneBuilder.cs`;
  - `Assets/_Project/Scripts/Net/Adapter/NetServer.cs` (replaced in full), `NetAvatars.cs` (replaced in full);
  - `Assets/_Project/Tests/EditMode/ServerOptionsTests.cs`;
  - in `Assets/_Project/Tests/PlayMode/`: `TestSupport/NetLoopbackRig.cs`, `NetLoopbackPlayModeTests.cs`, `NetServerPlayModeTests.cs`, `NetHarnessPlayModeTests.cs` and `ServerMissionPlayModeTests.cs`.
- Regenerate: `Assets/_Project/Scenes/ServerMission.unity`
- Test: `Assets/_Project/Tests/PlayMode/NetVisibilityServerPlayModeTests.cs`

**Interfaces:**
- Consumes:
  - Tasks 1–7: `NetVisibility`, `NetSquadIntel`, `NetAttackGuard`, `NetFixture`, `NetFixtureContainer`, `NetSessionHost.ConnectionOf/FixtureRequest`, the new messages and `NetUnitState.VisibleTo`;
  - `ServerExitCodes.StartupFailed` (= 2).
- Produces:
  - `ServerOptions.NetFixture` (`-netFixture`), and `With(..., bool? netFixture = null)`. `Describe` ends with `fixture=yes` only when it is set.
  - `NetServer.Initialize(GameObject avatar, PrefabObjects prefabs, IntelligenceService intelligence)`.
  - `NetServer` internals for tests:
    - `Visibility` and `Guard`;
    - `IReadOnlyCollection<int> ClientIds`;
    - `bool IsObserver(int clientId, int unitId)`;
    - `NetFixture Fixture`, in Editor and development builds only.
  - `NetAvatars.SpawnAll(ServerManager, GameObject, NetUnits, Func<NetworkConnection, int, bool> visible)` and `NetworkObject ObjectOf(int unitId)`.
  - `NetLoopbackRig.BeginNet(bool quiet = true, bool fixture = false)`; the rig's account A token is `squad+fixture`.

- [ ] **Step 1: Options and their test**

In `ServerOptions.cs`:
- add `public bool NetFixture { get; private set; }` beside `NetQuiet`, documented as "-netFixture: the development fixture drives visibility (Editor and development builds only; needs -netQuiet)";
- add `case "-netFixture": options.NetFixture = true; break;`;
- add `bool? netFixture = null` as the last parameter of `With`, with `if (netFixture.HasValue) copy.NetFixture = netFixture.Value;`;
- in `Describe`, change the net part to:
```csharp
            (Net ? FormattableString.Invariant($"net=yes port={NetPort} quiet={(NetQuiet ? "yes" : "no")}") + (NetFixture ? " fixture=yes" : string.Empty) : "net=no");
```
Add to `ServerOptionsTests.cs`:
```csharp
        [Test]
        public void NetFixture_IsParsed_Described_AndCopied()
        {
            var options = Parse("-net", "-netQuiet", "-netFixture");
            Assert.That(options.NetFixture, Is.True);
            Assert.That(options.Describe(7), Does.EndWith("net=yes port=7770 quiet=yes fixture=yes"));
            Assert.That(Parse("-net").NetFixture, Is.False);
            Assert.That(Parse().With(netFixture: true).NetFixture, Is.True);
            Assert.That(Parse("-netFixture").With(diagnostic: true).NetFixture, Is.True, "untouched values stay as they were");
        }
```

- [ ] **Step 2: The server scene gets the intelligence service, fog on**

In `ServerSceneBuilder.cs`:
- in `FixtureSettings()`, replace `intelligence = new IntelligenceSettings(),` with:
```csharp
            // Fog on: the networked server runs the real knowledge model and visibility fails closed without it (decision 047).
            // Map and objectives stay known; no briefing markers and no cameras, so only sight and exposure reveal a hostile.
            intelligence = new IntelligenceSettings
            {
                fogEnabled = true,
                map = MapKnowledge.Full,
                objectives = ObjectiveKnowledge.All,
                enemyMarkersAtStart = 0,
                cameraCount = 0,
            },
```
- in `Build()`, after `var interactables = systems.AddComponent<InteractableRegistry>();`, add `var intelligence = systems.AddComponent<IntelligenceService>();`;
- add `intelligence = intelligence,` to the `MissionSystems` initializer;
- pass `intelligence` as the third argument of `server.AddComponent<NetServer>().Initialize(...)`.

In `ServerMissionPlayModeTests.cs`, add `typeof(IntelligenceService)` to the `Allowed` array.

- [ ] **Step 3: Write the failing server tests**

`NetLoopbackRig.cs`:
- the token line becomes `"account-a " + TokenA + " squad+fixture\naccount-b " + ...` (the rest unchanged);
- `BeginNet` becomes:
```csharp
        public void BeginNet(bool quiet = true, bool fixture = false) =>
            Bootstrap.BeginNet(Bootstrap.Options.With(net: true, netPort: Port, netTokensPath: TokenPath, netQuiet: quiet, netFixture: fixture));
```

`Assets/_Project/Tests/PlayMode/NetVisibilityServerPlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The networked server with the real intelligence model (normal -net mode). Host mode: these tests read the server's observer
    /// sets; that the client process holds nothing is proven by the VM harness.
    /// </summary>
    public class NetVisibilityServerPlayModeTests
    {
        const int Hostile = 3;

        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        int Client => rig.Server.ClientIds.Single();

        [UnityTest]
        public IEnumerator ADisabledIntelligenceService_KeepsTheNetServerFromStarting()
        {
            Object.FindAnyObjectByType<IntelligenceService>().enabled = false;
            rig.BeginNet();
            yield return null;
            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            Assert.That(rig.Server.Running, Is.False);
            Assert.That(rig.Logs.Any(l => l.Contains("visibility: the IntelligenceService is disabled")), Is.True, string.Join("\n", rig.Logs));
        }

        [UnityTest]
        public IEnumerator TheFixtureFlag_WithoutNetQuiet_KeepsTheNetServerFromStarting()
        {
            rig.BeginNet(quiet: false, fixture: true);
            yield return null;
            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            Assert.That(rig.Logs.Any(l => l.Contains("-netFixture needs -netQuiet")), Is.True);
        }

        [UnityTest]
        public IEnumerator TheOwner_ReceivesItsSquad_ButNotTheHiddenHostile()
        {
            rig.BeginNet();
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return new WaitForSecondsRealtime(1f);
            Assert.That(rig.Server.IsObserver(Client, 1), Is.True);
            Assert.That(rig.Server.IsObserver(Client, 2), Is.True);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False, "16 m apart and out of sight: the real model hides it");
            Assert.That(rig.Server.Visibility.KnowledgeOf(rig.Server.Host.ConnectionOf(Client), Hostile), Is.EqualTo(NetKnowledge.Unknown));
        }

        [UnityTest]
        public IEnumerator AnAccountWithoutUnits_ReceivesNoUnitAtAll()
        {
            rig.BeginNet();
            yield return rig.Connect(NetLoopbackRig.TokenB);
            yield return new WaitForSecondsRealtime(1f);
            for (var id = 1; id <= 3; id++)
                Assert.That(rig.Server.IsObserver(Client, id), Is.False, "unit " + id);
        }

        [UnityTest]
        public IEnumerator EveryAvatar_IsServerOwned()
        {
            rig.BeginNet();
            yield return rig.Connect(NetLoopbackRig.TokenA);
            Assert.That(rig.Server.AvatarStates, Is.Not.Empty);
            foreach (var state in rig.Server.AvatarStates)
                Assert.That(state.Owner.IsValid, Is.False, state.name + " has an owner");
        }

        [UnityTest]
        public IEnumerator AHostileAvatar_NeverCarriesOrders()
        {
            rig.BeginNet();
            yield return null;
            var hostile = rig.Director.Hostiles[0];
            Assert.That(hostile.Issue(new MoveCommand(rig.Director.Friendlies[0].transform.position), IssueMode.Replace), Is.True);
            hostile.SetMoveIntent(Vector3.right);
            yield return null;
            yield return null;
            Assert.That(hostile.CurrentCommand, Is.Not.Null, "Precondition: the hostile has an order");
            var avatar = rig.Server.AvatarStates.Single(s => s.Hostile);
            Assert.That(avatar.Order, Is.EqualTo(NetOrderKind.None));
            Assert.That(avatar.Queue, Is.EqualTo(0));
            Assert.That(avatar.Steering, Is.False);
        }

        [UnityTest]
        public IEnumerator TheListeningLine_NamesTheVisibilitySource()
        {
            rig.BeginNet();
            yield return null;
            Assert.That(rig.Logs.Any(l => l.StartsWith(NetServer.Prefix + "listening") && l.Contains("visibility=squad")), Is.True);
        }
    }
}
#endif
```

In `NetLoopbackPlayModeTests.cs`:
- rename `ARequestForAHostile_IsAnsweredNotOwner` to `ARequestForTheHiddenHostile_IsAnsweredUnknownUnit`, expecting `NetRejectReason.UnknownUnit`;
- in `NoTokenIsEverLogged`, change the expected reason for `NetMoveTo(3, ...)` to `NetRejectReason.UnknownUnit`.

In `NetServerPlayModeTests.TheAvatars_MirrorTheirUnits`, a hostile's order is never replicated. Replace its two order lines with:
```csharp
                var hostile = rig.Director.Hostiles.Contains(units[i]);
                Assert.That(state.Order, Is.EqualTo(hostile ? NetOrderKind.None : NetOrder.KindOf(units[i].CurrentCommand)));
                Assert.That(state.Queue, Is.EqualTo(hostile ? 0 : units[i].PendingCommands.Count));
```

In `NetHarnessPlayModeTests.cs`, add `[Ignore("Task 11 adapts the harness roles to hidden information")]` above `TheObserverRole_PassesWhenEveryOrderForTheSquadIsRefused` and `TheOwnerRole_PassesTheWholeScript`. Task 11 removes both.

- [ ] **Step 4: Run them to make sure they fail**

Run: `Tools/run-tests.sh PlayMode NetVisibilityServerPlayModeTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `EXIT=1` (`NetServer.ClientIds`, `IsObserver`, `Visibility` and the three-argument `Initialize` do not exist).

- [ ] **Step 5: Replace `NetAvatars.cs`**

```csharp
using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Server;
using FishNet.Object;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass
{
    /// <summary>
    /// The server's avatar objects (networking experiment, decisions 046 and 047): one networked object per simulation unit,
    /// spawned with no owner when the server starts. Before spawning, each avatar is told whom it may be sent to (`visible`,
    /// asked by NetVisibilityCondition). Each frame the avatar copies its unit's transform and pushes health and orders into
    /// NetUnitState; a hostile's orders, queue and steering are never pushed, because they would reveal what it does and whom
    /// it targets. The simulation units themselves are never networked.
    /// </summary>
    internal sealed class NetAvatars
    {
        struct Entry
        {
            public CommandableUnit Unit;
            public Health Health;
            public Transform Avatar;
            public NetUnitState State;
            public bool Hostile;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly List<NetUnitState> states = new List<NetUnitState>();
        readonly Dictionary<int, NetworkObject> objects = new Dictionary<int, NetworkObject>();

        internal int Count => entries.Count;
        internal IReadOnlyList<NetUnitState> States => states;

        internal void SpawnAll(ServerManager server, GameObject prefab, NetUnits units, Func<NetworkConnection, int, bool> visible)
        {
            Forget();
            for (var id = 1; id <= units.Count; id++)
            {
                if (!units.TryGet(id, out var unit))
                    continue;
                var avatar = Object.Instantiate(prefab, unit.transform.position, unit.transform.rotation);
                avatar.name = "Avatar_" + id;
                var state = avatar.GetComponent<NetUnitState>();
                var unitId = id;   // the loop variable is shared by every iteration's closure
                state.VisibleTo = connection => visible(connection, unitId);
                server.Spawn(avatar);
                var hostile = units.IsHostile(id);
                state.Assign(id, hostile);
                var entry = new Entry { Unit = unit, Health = unit.GetComponent<Health>(), Avatar = avatar.transform, State = state, Hostile = hostile };
                entries.Add(entry);
                states.Add(state);
                objects[id] = avatar.GetComponent<NetworkObject>();
                Push(entry);
            }
        }

        internal NetworkObject ObjectOf(int unitId) => objects.TryGetValue(unitId, out var found) && found != null ? found : null;

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
            objects.Clear();
        }

        static void Push(Entry entry)
        {
            if (entry.Unit == null || entry.Avatar == null)
                return;
            entry.Avatar.SetPositionAndRotation(entry.Unit.transform.position, entry.Unit.transform.rotation);
            var health = entry.Health;
            var current = health != null ? health.Current : 0;
            var maximum = health != null ? health.Max : 0;
            var alive = health != null && health.IsAlive;
            if (entry.Hostile)
                entry.State.Push(current, maximum, alive, NetOrderKind.None, 0, false);
            else
                entry.State.Push(current, maximum, alive, NetOrder.KindOf(entry.Unit.CurrentCommand), entry.Unit.PendingCommands.Count,
                    entry.Unit.MoveIntent != Vector3.zero);
        }
    }
}
```

- [ ] **Step 6: Replace `NetServer.cs`**

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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine.AI;
#endif

namespace Blackglass
{
    /// <summary>
    /// The dedicated server's networking adapter (networking experiment, decisions 046 and 047). It starts only when -net is
    /// given. Startup:
    /// - It refuses to start unless the real intelligence model is running with fog on, so visibility fails closed.
    /// - It reads the development token file and builds the FishNet manager.
    /// - It listens on 127.0.0.1 with the WebSocket transport and spawns one ownerless avatar per unit.
    ///
    /// Every hello, request and fixture message goes to NetSessionHost, which holds the rules. Each frame, NetVisibility decides
    /// what each welcomed connection may see. This class turns those transitions into observer rebuilds and NetKnowledgeMessages,
    /// and ends guarded attacks on targets the issuing connection no longer observes.
    ///
    /// With -netFixture (Editor and development builds only), the development fixture replaces the squad model as the source.
    /// Tokens are never logged.
    /// </summary>
    public sealed class NetServer : MonoBehaviour
    {
        public const string Prefix = "[Net] ";
        const string LoopbackAddress = "127.0.0.1";

        [SerializeField] GameObject avatarPrefab;
        [SerializeField] PrefabObjects spawnablePrefabs;
        [SerializeField] IntelligenceService intelligence;

        readonly Dictionary<int, NetworkConnection> connections = new Dictionary<int, NetworkConnection>();
        readonly Dictionary<NetConnection, NetworkConnection> welcomed = new Dictionary<NetConnection, NetworkConnection>();
        readonly Dictionary<int, double> nextLimitedLog = new Dictionary<int, double>();
        readonly Dictionary<int, int> suppressed = new Dictionary<int, int>();
        readonly List<int> expired = new List<int>();
        readonly List<NetKnowledgeChange> changes = new List<NetKnowledgeChange>();
        readonly NetAvatars avatars = new NetAvatars();
        NetworkManager manager;
        UnityTransport transport;
        bool createdManager;
        bool registered;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        NetFixture fixture;

        internal NetFixture Fixture => fixture;
        bool FixtureEnabled => fixture != null;
#else
        bool FixtureEnabled => false;
#endif

        internal void Initialize(GameObject avatar, PrefabObjects prefabs, IntelligenceService service)
        {
            avatarPrefab = avatar;
            spawnablePrefabs = prefabs;
            intelligence = service;
        }

        internal bool Running { get; private set; }
        internal NetworkManager Manager => manager;
        internal NetSessionHost Host { get; private set; }
        internal ServerCommandGate Gate { get; private set; }
        internal NetUnits Units { get; private set; }
        internal NetVisibility Visibility { get; private set; }
        internal NetAttackGuard Guard { get; private set; }
        internal int AvatarCount => avatars.Count;
        internal IReadOnlyList<NetUnitState> AvatarStates => avatars.States;
        internal string BindAddress => transport != null ? transport.GetServerBindAddress(IPAddressType.IPv4) : null;
        internal int ConnectionCount => connections.Count;
        internal IReadOnlyCollection<int> ClientIds => connections.Keys;

        /// <summary>Whether FishNet currently sends this unit's avatar to this client (tests read the server's observer sets).</summary>
        internal bool IsObserver(int clientId, int unitId)
        {
            var avatar = avatars.ObjectOf(unitId);
            return avatar != null && connections.TryGetValue(clientId, out var connection) && avatar.Observers.Contains(connection);
        }

        static double Now => Time.realtimeSinceStartupAsDouble;

        /// <summary>Null when the server is listening; otherwise the reason startup must fail (the bootstrap exits with code 2).</summary>
        internal string Begin(ServerOptions options, MissionDirector director)
        {
            if (Running)
                return "the net server is already running";
            if (avatarPrefab == null || spawnablePrefabs == null)
                return "the scene's NetServer is not wired to the avatar prefab and the prefab collection";
            // Fail closed: without the real model running with fog on, no client may connect at all (decision 047).
            var intelligenceProblem = NetSquadIntel.Problem(intelligence);
            if (intelligenceProblem != null)
                return "visibility: " + intelligenceProblem;
            if (options.NetFixture)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (!options.NetQuiet)
                    return "-netFixture needs -netQuiet, so the hostile neither moves nor fires on its own";
#else
                return "-netFixture is not available in a release build";
#endif
            }
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
            INetIntelSource source = null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (options.NetFixture)
            {
                var fixtureProblem = CreateFixture(director);
                if (fixtureProblem != null)
                    return fixtureProblem;
                source = fixture;
            }
#endif
            if (source == null)
                source = new NetSquadIntel(intelligence, Units, director.Friendlies.Count, line => Debug.LogError(Prefix + line));
            Visibility = new NetVisibility(Units, source);
            Guard = new NetAttackGuard(Units, Visibility);
            Gate = new ServerCommandGate(Units, director.InstanceId, Visibility)
            {
                Log = line => Debug.LogWarning(Prefix + line),
                AttackAccepted = Guard.Accepted,
            };
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
            // Always registered: FishNet skips an unregistered message silently, and a refused fixture message must be counted.
            server.RegisterBroadcast<NetFixtureMessage>(OnFixture, false);
            registered = true;
            Running = true;
            if (!server.StartConnection())
                return Abort("the transport would not start on " + LoopbackAddress + ":" + options.NetPort + " (is the port in use?)");
            if (FixtureEnabled && BindAddress != LoopbackAddress)
                return Abort("the fixture runs only on a loopback bind, not " + BindAddress);

            Debug.Log(Prefix + FormattableString.Invariant(
                $"listening {LoopbackAddress}:{options.NetPort} websockets attempt={director.InstanceId} accounts={entries.Count} quiet={(options.NetQuiet ? "yes" : "no")} visibility={(FixtureEnabled ? "fixture" : "squad")}"));
            return null;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // The fixture's hostile is the first one; its walk points are found once, here, and never sent (plan ruling 4).
        string CreateFixture(MissionDirector director)
        {
            var hostileId = director.Friendlies.Count + 1;
            if (!Units.IsHostile(hostileId) || !Units.TryGet(hostileId, out var hostile))
                return "the fixture needs a hostile";
            var agent = hostile.GetComponent<NavMeshAgent>();
            if (agent == null)
                return "the fixture's hostile has no NavMeshAgent";
            var points = NetFixture.SamplePoints(hostile.transform.position, 2);
            if (points.Count < 2)
                return FormattableString.Invariant($"the fixture found {points.Count} of 2 walkable points near its hostile");
            fixture = new NetFixture(Units, hostileId, points, point => agent.Warp(point));
            return null;
        }
#endif

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
                        server.UnregisterBroadcast<NetFixtureMessage>(OnFixture);
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
            welcomed.Clear();
            changes.Clear();
            nextLimitedLog.Clear();
            suppressed.Clear();
            avatars.Forget();
            Visibility = null;
            Guard = null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            fixture = null;
#endif
        }

        void OnDestroy() => Stop();

        void Update()
        {
            if (!Running || Host == null)
                return;
            var now = Now;
            Gate.Tick(now);
            avatars.Sync();
            changes.Clear();
            Visibility.Update(changes);
            foreach (var change in changes)
                Publish(change);
            Guard.Tick();
            Host.CollectExpired(now, expired);
            foreach (var clientId in expired)
            {
                Debug.Log(Prefix + FormattableString.Invariant($"client {clientId} said no hello in time; disconnected"));
                if (connections.TryGetValue(clientId, out var connection))
                    connection.Disconnect(true);
            }
            expired.Clear();
        }

        // NetVisibilityCondition asks this for every avatar and connection; anything unknown answers no.
        bool Replicates(NetworkConnection connection, int unitId) =>
            Visibility != null && Host != null && connection != null && Visibility.Replicates(Host.ConnectionOf(connection.ClientId), unitId);

        // One transition: FishNet spawns or despawns the avatar for this connection only, and the connection learns its knowledge.
        void Publish(NetKnowledgeChange change)
        {
            if (!welcomed.TryGetValue(change.Connection, out var connection))
                return;
            var avatar = avatars.ObjectOf(change.UnitId);
            if (avatar != null)
                manager.ServerManager.Objects.RebuildObservers(avatar, connection);
            manager.ServerManager.Broadcast(connection, new NetKnowledgeMessage
            {
                UnitId = change.UnitId,
                Knowledge = (byte)change.Knowledge,
                Position = change.Knowledge == NetKnowledge.LastKnown ? change.Position : Vector3.zero,
            }, false);
            if (FixtureEnabled)
                Debug.Log(Prefix + FormattableString.Invariant($"visibility client={connection.ClientId} unit={change.UnitId} {change.Knowledge}"));
        }

        void OnServerState(ServerConnectionStateArgs args)
        {
            Debug.Log(Prefix + "server " + args.ConnectionState);
            if (args.ConnectionState == LocalConnectionState.Started)
                avatars.SpawnAll(manager.ServerManager, avatarPrefab, Units, Replicates);
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
            var leaving = Host.ConnectionOf(clientId);
            if (leaving != null)
            {
                Visibility.Remove(leaving);
                Guard.Forget(leaving);
                welcomed.Remove(leaving);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (fixture != null)
                    fixture.Forget(leaving);
#endif
            }
            var account = Host.AccountOf(clientId);
            Host.Disconnected(clientId);
            connections.Remove(clientId);
            nextLimitedLog.Remove(clientId);
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
            var joined = Host.ConnectionOf(clientId);
            welcomed[joined] = connection;
            Visibility.Add(joined);
            // Only now may this client receive anything: its own units at once, hostiles as the projection allows.
            manager.ServerManager.Objects.RebuildObservers(connection);
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
                LogLimited(clientId, FormattableString.Invariant(
                    $"rejected client={clientId} account={Host.AccountOf(clientId)} reason={outcome.Result.Reason} command={message.CommandId}"));
        }

        void OnFixture(NetworkConnection connection, NetFixtureMessage message, Channel channel)
        {
            var clientId = connection.ClientId;
            switch (Host.FixtureRequest(clientId, FixtureEnabled, Now, out var caller))
            {
                case NetFixtureAdmission.Ignored:
                    return;
                case NetFixtureAdmission.Disconnect:
                    Debug.LogWarning(Prefix + FormattableString.Invariant($"client {clientId} is abusive (too many refused or dropped messages); disconnected"));
                    connection.Disconnect(true);
                    return;
                case NetFixtureAdmission.Refused:
                    LogLimited(clientId, FormattableString.Invariant($"fixture refused client={clientId} account={Host.AccountOf(clientId)}"));
                    return;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ApplyFixture(connection, caller, (NetFixtureVerb)message.Verb);
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void ApplyFixture(NetworkConnection connection, NetConnection caller, NetFixtureVerb verb)
        {
            if (!fixture.Apply(caller, verb))
            {
                LogLimited(connection.ClientId, FormattableString.Invariant($"fixture verb {verb} changed nothing client={connection.ClientId}"));
                return;
            }
            Debug.Log(Prefix + FormattableString.Invariant($"fixture step={fixture.Step} verb={verb} account={caller.Account}"));
            if (verb == NetFixtureVerb.Search)
                SendContents(connection, caller);
            var step = new NetFixtureStepMessage { Step = fixture.Step };
            foreach (var welcomedConnection in welcomed.Values)
                manager.ServerManager.Broadcast(welcomedConnection, step, false);
        }

        // Only to the connection that searched; nothing about the container exists on the wire before this.
        void SendContents(NetworkConnection connection, NetConnection searcher)
        {
            var items = fixture.Container.ContentsFor(searcher);
            if (items == null)
                return;
            var entries = new NetItemWire[items.Count];
            for (var i = 0; i < items.Count; i++)
                entries[i] = new NetItemWire { DefinitionId = items[i].DefinitionId, Quantity = items[i].Quantity, InstanceId = items[i].InstanceId };
            manager.ServerManager.Broadcast(connection, new NetContainerMessage { ContainerId = NetFixtureContainer.Id, Entries = entries }, false);
        }
#endif

        // At most one line per client per second, so a misbehaving client cannot fill the log.
        void LogLimited(int clientId, string line)
        {
            var now = Now;
            suppressed.TryGetValue(clientId, out var skipped);
            if (nextLimitedLog.TryGetValue(clientId, out var next) && now < next)
            {
                suppressed[clientId] = skipped + 1;
                return;
            }
            nextLimitedLog[clientId] = now + 1d;
            suppressed[clientId] = 0;
            var more = skipped > 0 ? FormattableString.Invariant($" (+{skipped} more in the last second)") : string.Empty;
            Debug.Log(Prefix + line + more);
        }
    }
}
```

Regenerate the scene:
```bash
Tools/run-method.sh Blackglass.EditorTools.ServerSceneBuilder.BuildFromCommandLine 2>&1 | tail -3
git status --short Assets/_Project/Scenes
grep -c "IntelligenceService\|fogEnabled: 1" Assets/_Project/Scenes/ServerMission.unity
```
Expected:
- `EXIT=0`;
- only `ServerMission.unity` is modified;
- the grep finds the component and `fogEnabled: 1`. If the serialized field name differs, read the scene and confirm both by eye.

- [ ] **Step 7: Run the server, scene and networking tests**

```bash
Tools/run-tests.sh EditMode "ServerOptionsTests;NetSourceAuditTests;NetMessageShapeTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'
Tools/run-tests.sh PlayMode "NetVisibilityServerPlayModeTests;NetLoopbackPlayModeTests;NetServerPlayModeTests;ServerMissionPlayModeTests;NetGatePlayModeTests;NetHarnessPlayModeTests;NetManagerFactoryPlayModeTests;NetClientScenePlayModeTests" 2>&1 | tail -5 | sed 's/Unity requires.*behavior\.//'
```
Expected: all passed except the two `[Ignore]`d harness tests, which show as skipped, and `EXIT=0`. `ServerMissionPlayModeTests.Diagnostic_OnTheFixture_ExitsZero` must pass: the Checkpoint A diagnostic is unchanged with fog on.
If it fails because of fog, read `ServerDiagnostic` for anything gated by knowledge, and report before changing anything: the diagnostic must not change behaviour silently.

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Scripts/Server/ServerOptions.cs Assets/_Project/Editor/Server/ServerSceneBuilder.cs Assets/_Project/Scenes/ServerMission.unity Assets/_Project/Scripts/Net/Adapter/NetServer.cs Assets/_Project/Scripts/Net/Adapter/NetAvatars.cs Assets/_Project/Tests/EditMode/ServerOptionsTests.cs Assets/_Project/Tests/PlayMode/TestSupport/NetLoopbackRig.cs Assets/_Project/Tests/PlayMode/NetLoopbackPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetServerPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetHarnessPlayModeTests.cs Assets/_Project/Tests/PlayMode/ServerMissionPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetVisibilityServerPlayModeTests.cs Assets/_Project/Tests/PlayMode/NetVisibilityServerPlayModeTests.cs.meta
git commit -m "Net: the server runs the real intelligence, fails closed, and replicates per connection

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: The client records what it decoded

**Files:**
- Create: `Assets/_Project/Scripts/Net/Core/NetClientRecord.cs`
- Modify: `Assets/_Project/Scripts/Net/Adapter/NetClient.cs`, `Assets/_Project/Scripts/Net/Adapter/NetClientView.cs`
- Test: `Assets/_Project/Tests/EditMode/NetClientRecordTests.cs`

**Interfaces:**
- Consumes: `NetKnowledge` (Task 1), `NetItem` and `NetFixtureVerb` (Task 6), the messages (Task 7).
- Produces:
  - `sealed class NetClientRecord`:
    - `const int Limit = 4096`;
    - `IReadOnlyDictionary<int, Vector3> LastKnown`;
    - `IReadOnlyList<NetClientRecord.Decoded> DecodedMessages`, where each entry has `Time`, `Type`, `HasPosition` and `Position`;
    - `IReadOnlyList<NetClientRecord.Sighting> Sightings`, where each entry has `Time`, `UnitId`, `Hostile` and `Position`;
    - `IReadOnlyList<NetItem> Contents`, which stays null until a container message arrives;
    - the counters `int KnowledgeMessages`, `int ContainerMessages` and `int FixtureStep`, and the flag `bool Truncated`;
    - the methods `Clear()`, `Note(double, string)`, `Knowledge(double, int, NetKnowledge, Vector3)`, `Container(double, IReadOnlyList<NetItem>)`, `Step(double, int)` and `bool Sight(double, int instanceKey, int unitId, bool hostile, Vector3 position)`.
  - `NetClient`:
    - `NetClientRecord Record`;
    - `void SendFixture(NetFixtureVerb verb)`;
    - `static List<NetUnitState> VisibleStates()`, which returns only client-initialized avatars with an id.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/NetClientRecordTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetClientRecordTests
    {
        NetClientRecord record;

        [SetUp]
        public void SetUp() => record = new NetClientRecord();

        [Test]
        public void ALastKnownMessage_AddsARecord_AndObservedClearsIt()
        {
            record.Knowledge(1d, 3, NetKnowledge.LastKnown, new Vector3(4f, 0f, 2f));
            Assert.That(record.LastKnown[3], Is.EqualTo(new Vector3(4f, 0f, 2f)));
            record.Knowledge(2d, 3, NetKnowledge.Observed, Vector3.zero);
            Assert.That(record.LastKnown.ContainsKey(3), Is.False);
            Assert.That(record.KnowledgeMessages, Is.EqualTo(2));
        }

        [Test]
        public void ALaterLastKnown_ReplacesTheRecord_SoThereIsOnePerUnit()
        {
            record.Knowledge(1d, 3, NetKnowledge.LastKnown, Vector3.one);
            record.Knowledge(2d, 3, NetKnowledge.LastKnown, Vector3.right);
            Assert.That(record.LastKnown, Has.Count.EqualTo(1));
            Assert.That(record.LastKnown[3], Is.EqualTo(Vector3.right));
        }

        [Test]
        public void TheDecodeLog_KeepsTimesAndPositions()
        {
            record.Note(0.5d, "NetWelcomeMessage");
            record.Knowledge(1d, 3, NetKnowledge.LastKnown, Vector3.one);
            Assert.That(record.DecodedMessages, Has.Count.EqualTo(2));
            Assert.That(record.DecodedMessages[0].Type, Is.EqualTo("NetWelcomeMessage"));
            Assert.That(record.DecodedMessages[0].HasPosition, Is.False);
            Assert.That(record.DecodedMessages[1].Type, Is.EqualTo("NetKnowledgeMessage"));
            Assert.That(record.DecodedMessages[1].HasPosition, Is.True);
            Assert.That(record.DecodedMessages[1].Position, Is.EqualTo(Vector3.one));
            Assert.That(record.DecodedMessages[1].Time, Is.EqualTo(1d));
        }

        [Test]
        public void Contents_AreKeptAsReceived()
        {
            Assert.That(record.Contents, Is.Null);
            record.Container(1d, new List<NetItem> { new NetItem("fixture-medkit", 2, "fixture-item-0001") });
            Assert.That(record.Contents, Has.Count.EqualTo(1));
            Assert.That(record.Contents[0].Quantity, Is.EqualTo(2));
            Assert.That(record.ContainerMessages, Is.EqualTo(1));
        }

        [Test]
        public void TheFixtureStep_NeverGoesBack()
        {
            record.Step(1d, 4);
            record.Step(2d, 2);
            Assert.That(record.FixtureStep, Is.EqualTo(4));
        }

        [Test]
        public void Sight_RecordsEachAvatarInstanceOnce()
        {
            Assert.That(record.Sight(1d, 100, 3, true, Vector3.one), Is.True);
            Assert.That(record.Sight(2d, 100, 3, true, Vector3.right), Is.False);
            Assert.That(record.Sight(3d, 101, 3, true, Vector3.right), Is.True, "a respawned avatar is a new instance");
            Assert.That(record.Sightings, Has.Count.EqualTo(2));
            Assert.That(record.Sightings[0].Hostile, Is.True);
        }

        [Test]
        public void TheLogs_AreBounded_AndSayWhenTheyAreFull()
        {
            for (var i = 0; i < NetClientRecord.Limit + 5; i++)
                record.Note(i, "NetResultMessage");
            Assert.That(record.DecodedMessages, Has.Count.EqualTo(NetClientRecord.Limit));
            Assert.That(record.Truncated, Is.True);
        }

        [Test]
        public void Clear_ForgetsEverything()
        {
            record.Knowledge(1d, 3, NetKnowledge.LastKnown, Vector3.one);
            record.Container(1d, new List<NetItem>());
            record.Step(1d, 2);
            record.Sight(1d, 100, 3, true, Vector3.one);
            record.Clear();
            Assert.That(record.LastKnown, Is.Empty);
            Assert.That(record.Contents, Is.Null);
            Assert.That(record.FixtureStep, Is.EqualTo(0));
            Assert.That(record.Sightings, Is.Empty);
            Assert.That(record.DecodedMessages, Is.Empty);
            Assert.That(record.KnowledgeMessages + record.ContainerMessages, Is.EqualTo(0));
            Assert.That(record.Sight(2d, 100, 3, true, Vector3.one), Is.True, "the instance can be seen again after a clear");
        }
    }
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `Tools/run-tests.sh EditMode NetClientRecordTests 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'`
Expected: `EXIT=1` (`NetClientRecord` does not exist).

- [ ] **Step 3: Write `NetClientRecord`**

`Assets/_Project/Scripts/Net/Core/NetClientRecord.cs`:
```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What the net-test client decoded, kept so the harness can judge what reached it (hidden information slice, spec section
    /// 3.11): the last-known records (one per unit), the searched contents, the fixture step, and bounded logs of decoded
    /// messages and avatar sightings with their times and positions. The logs keep the first Limit entries and say when more
    /// arrived. Holds no networking type.
    /// </summary>
    public sealed class NetClientRecord
    {
        public const int Limit = 4096;

        public readonly struct Decoded
        {
            public Decoded(double time, string type, bool hasPosition, Vector3 position)
            {
                Time = time;
                Type = type;
                HasPosition = hasPosition;
                Position = position;
            }

            public double Time { get; }
            public string Type { get; }
            public bool HasPosition { get; }
            public Vector3 Position { get; }
        }

        public readonly struct Sighting
        {
            public Sighting(double time, int unitId, bool hostile, Vector3 position)
            {
                Time = time;
                UnitId = unitId;
                Hostile = hostile;
                Position = position;
            }

            public double Time { get; }
            public int UnitId { get; }
            public bool Hostile { get; }
            public Vector3 Position { get; }
        }

        readonly Dictionary<int, Vector3> lastKnown = new Dictionary<int, Vector3>();
        readonly List<Decoded> decoded = new List<Decoded>();
        readonly List<Sighting> sightings = new List<Sighting>();
        readonly HashSet<int> sighted = new HashSet<int>();
        NetItem[] contents;

        public IReadOnlyDictionary<int, Vector3> LastKnown => lastKnown;
        public IReadOnlyList<Decoded> DecodedMessages => decoded;
        public IReadOnlyList<Sighting> Sightings => sightings;
        /// <summary>Null until a container message arrives.</summary>
        public IReadOnlyList<NetItem> Contents => contents;
        public int KnowledgeMessages { get; private set; }
        public int ContainerMessages { get; private set; }
        public int FixtureStep { get; private set; }
        /// <summary>More than Limit messages or sightings arrived; the logs hold only the first Limit.</summary>
        public bool Truncated { get; private set; }

        public void Clear()
        {
            lastKnown.Clear();
            decoded.Clear();
            sightings.Clear();
            sighted.Clear();
            contents = null;
            KnowledgeMessages = 0;
            ContainerMessages = 0;
            FixtureStep = 0;
            Truncated = false;
        }

        public void Note(double time, string type) => Add(new Decoded(time, type, false, Vector3.zero));

        public void Knowledge(double time, int unitId, NetKnowledge knowledge, Vector3 position)
        {
            KnowledgeMessages++;
            var lastKnownRecord = knowledge == NetKnowledge.LastKnown;
            Add(new Decoded(time, "NetKnowledgeMessage", lastKnownRecord, position));
            if (lastKnownRecord)
                lastKnown[unitId] = position;
            else
                lastKnown.Remove(unitId);
        }

        public void Container(double time, IReadOnlyList<NetItem> items)
        {
            ContainerMessages++;
            Add(new Decoded(time, "NetContainerMessage", false, Vector3.zero));
            var copy = new NetItem[items != null ? items.Count : 0];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = items[i];
            contents = copy;
        }

        public void Step(double time, int step)
        {
            Add(new Decoded(time, "NetFixtureStepMessage", false, Vector3.zero));
            if (step > FixtureStep)
                FixtureStep = step;
        }

        /// <summary>Records the first sighting of an avatar instance; true when this instance had not been seen.</summary>
        public bool Sight(double time, int instanceKey, int unitId, bool hostile, Vector3 position)
        {
            if (!sighted.Add(instanceKey))
                return false;
            if (sightings.Count >= Limit)
                Truncated = true;
            else
                sightings.Add(new Sighting(time, unitId, hostile, position));
            return true;
        }

        void Add(Decoded entry)
        {
            if (decoded.Count >= Limit)
                Truncated = true;
            else
                decoded.Add(entry);
        }
    }
}
```

- [ ] **Step 4: Wire it into `NetClient` and the view**

In `NetClient.cs`:
- add the field `readonly NetClientRecord record = new NetClientRecord();` and `public NetClientRecord Record => record;`, plus `static double Now => Time.realtimeSinceStartupAsDouble;`;
- in `Connect`, call `record.Clear();` next to `results.Clear();`, and register the three new broadcasts after `OnResult`:
```csharp
            client.RegisterBroadcast<NetKnowledgeMessage>(OnKnowledge);
            client.RegisterBroadcast<NetContainerMessage>(OnContainer);
            client.RegisterBroadcast<NetFixtureStepMessage>(OnStep);
```
- in `Disconnect`, unregister the same three;
- in `OnWelcome`, `OnRefused` and `OnResult`, add a first line `record.Note(Now, nameof(NetWelcomeMessage));` (respectively `NetRefusedMessage`, `NetResultMessage`);
- add:
```csharp
        /// <summary>Development fixture only: asks the server to carry out one verb. A server without the fixture refuses it.</summary>
        public void SendFixture(NetFixtureVerb verb) => manager.ClientManager.Broadcast(new NetFixtureMessage { Verb = (byte)verb });

        /// <summary>
        /// The avatars this client has been sent. In a separate process that is every NetUnitState; in host mode (tests) the
        /// server's avatars exist in the scene whether or not this client observes them, so only client-initialized ones count.
        /// </summary>
        public static List<NetUnitState> VisibleStates()
        {
            var result = new List<NetUnitState>();
            foreach (var state in FindObjectsByType<NetUnitState>(FindObjectsSortMode.None))
            {
                if (state.IsClientInitialized && state.UnitId > 0)
                    result.Add(state);
            }
            return result;
        }

        // Every avatar instance this client is sent, the first frame it is seen (the harness checks nothing arrived early).
        void Update()
        {
            if (State != NetClientState.Ready && State != NetClientState.Authenticating)
                return;
            foreach (var state in VisibleStates())
                record.Sight(Now, state.GetInstanceID(), state.UnitId, state.Hostile, state.transform.position);
        }

        void OnKnowledge(NetKnowledgeMessage message, Channel channel) =>
            record.Knowledge(Now, message.UnitId, (NetKnowledge)message.Knowledge, message.Position);

        void OnContainer(NetContainerMessage message, Channel channel)
        {
            var items = new List<NetItem>();
            if (message.Entries != null)
            {
                foreach (var entry in message.Entries)
                    items.Add(new NetItem(entry.DefinitionId, entry.Quantity, entry.InstanceId));
            }
            record.Container(Now, items);
        }

        void OnStep(NetFixtureStepMessage message, Channel channel) => record.Step(Now, message.Step);
```
- extend the class summary with: "Everything it decodes is kept in Record, which the harness reads."

In `NetClientView.cs`:
- in `LateUpdate` and `OnGUI`, replace `FindObjectsByType<NetUnitState>(FindObjectsSortMode.None)` with `NetClient.VisibleStates()`;
- in `LateUpdate`, before the camera framing, hide any capsule whose avatar is no longer visible, which only happens in host mode:
```csharp
            foreach (var pair in visuals)
            {
                if (pair.Value != null && pair.Key != null && !pair.Key.IsClientInitialized)
                    pair.Value.SetActive(false);
            }
```
- draw last-known records as flat grey discs. Add the fields `readonly Dictionary<int, GameObject> markers = new Dictionary<int, GameObject>();` and `readonly List<int> staleMarkers = new List<int>();`, call `if (client != null) DrawLastKnown();` at the start of `LateUpdate`, and add:
```csharp
        // A last-known record is a flat grey disc where the hostile was last seen; it never moves until the record changes.
        void DrawLastKnown()
        {
            foreach (var pair in client.Record.LastKnown)
            {
                if (!markers.TryGetValue(pair.Key, out var marker) || marker == null)
                    markers[pair.Key] = marker = CreateMarker(pair.Key);
                marker.transform.position = pair.Value + new Vector3(0f, 0.05f, 0f);
            }
            staleMarkers.Clear();
            foreach (var pair in markers)
            {
                if (!client.Record.LastKnown.ContainsKey(pair.Key))
                    staleMarkers.Add(pair.Key);
            }
            foreach (var id in staleMarkers)
            {
                if (markers[id] != null)
                    Destroy(markers[id]);
                markers.Remove(id);
            }
        }

        GameObject CreateMarker(int unitId)
        {
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "LastKnown_" + unitId;
            Destroy(disc.GetComponent<Collider>());
            disc.transform.localScale = new Vector3(1.2f, 0.02f, 1.2f);
            var renderer = disc.GetComponent<Renderer>();
            renderer.sharedMaterial = unitMaterial;
            if (block == null)
                block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, new Color(0.55f, 0.55f, 0.55f));
            renderer.SetPropertyBlock(block);
            return disc;
        }
```
- in `OnGUI`, after the last-refused line, add `text.Append("\nlast known: ").Append(client.Record.LastKnown.Count);`;
- update the class summary: "...hostiles; a grey disc where a hostile was last seen...".

- [ ] **Step 5: Run the tests**

```bash
Tools/run-tests.sh EditMode "NetClientRecordTests;NetSourceAuditTests;NetMessageShapeTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'
Tools/run-tests.sh PlayMode "NetClientScenePlayModeTests;NetLoopbackPlayModeTests;NetVisibilityServerPlayModeTests" 2>&1 | tail -3 | sed 's/Unity requires.*behavior\.//'
```
Expected: all passed, `EXIT=0` both times.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Net/Core/NetClientRecord.cs Assets/_Project/Scripts/Net/Core/NetClientRecord.cs.meta Assets/_Project/Scripts/Net/Adapter/NetClient.cs Assets/_Project/Scripts/Net/Adapter/NetClientView.cs Assets/_Project/Tests/EditMode/NetClientRecordTests.cs Assets/_Project/Tests/EditMode/NetClientRecordTests.cs.meta
git commit -m "Net: the client records decoded knowledge, contents, fixture steps and avatar sightings

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: The fifteen transitions, end to end through the server (fixture mode)

**Files:**
- Test: `Assets/_Project/Tests/PlayMode/NetFixturePlayModeTests.cs`
- Fix, only if a test exposes a defect: the file that owns it, with the fix recorded in the task report.

**Interfaces:**
- Consumes:
  - `NetLoopbackRig.BeginNet(quiet, fixture)` (Task 8);
  - `NetServer.IsObserver`, `ClientIds`, `Visibility`, `Host` and `Fixture` (Task 8);
  - `NetClient.SendFixture` and `Record` (Task 9);
  - `NetFixture.Points` and `HostileId` (Task 6).
- Produces: nothing new; this task proves the server path in host mode.

- [ ] **Step 1: Write the tests**

`Assets/_Project/Tests/PlayMode/NetFixturePlayModeTests.cs`:
```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The development fixture through the real server (spec section 3.9). Host mode: these tests read the server's observer sets
    /// and the messages the one client decoded; that a separate client process holds nothing is proven by the VM harness.
    /// </summary>
    public class NetFixturePlayModeTests
    {
        const int Hostile = 3;
        const int Melee = 2;

        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true, fixture: true);
            Assert.That(rig.Server.Running, Is.True, string.Join("\n", rig.Logs));
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        int Client => rig.Server.ClientIds.Single();
        Vector3 HostilePosition => rig.Director.Hostiles[0].transform.position;

        static float Flat(Vector3 offset)
        {
            offset.y = 0f;
            return offset.magnitude;
        }

        IEnumerator Verb(NetFixtureVerb verb)
        {
            var before = rig.Client.Record.FixtureStep;
            rig.Client.SendFixture(verb);
            yield return TestWorld.WaitUntil(() => rig.Client.Record.FixtureStep > before, 5f);
            Assert.That(rig.Client.Record.FixtureStep, Is.EqualTo(before + 1), verb + " took effect");
            yield return null;   // the projection runs in the server's next frame
            yield return null;
        }

        IEnumerator Await(uint command, NetRejectReason expected)
        {
            yield return TestWorld.WaitUntil(() => rig.Client.TryGetResult(command, out _), 5f);
            Assert.That(rig.Client.TryGetResult(command, out var reason), Is.True, "no answer to command " + command);
            Assert.That(reason, Is.EqualTo(expected));
        }

        [UnityTest]
        public IEnumerator TheFixture_HasTwoWalkPoints_AtLeastThreeMetresApart()
        {
            yield return null;
            var points = rig.Server.Fixture.Points;
            Assert.That(points, Has.Count.EqualTo(2));
            Assert.That(Flat(points[0] - points[1]), Is.GreaterThanOrEqualTo(NetFixture.PointSpacing));
            foreach (var point in points)
                Assert.That(Flat(point - HostilePosition), Is.GreaterThanOrEqualTo(NetFixture.PointSpacing));
            Assert.That(rig.Server.Fixture.HostileId, Is.EqualTo(Hostile));
        }

        [UnityTest]
        public IEnumerator TheFifteenTransitions_OnTheServersObserverSets_AndTheClientsRecord()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var record = rig.Client.Record;

            // 1-2: hidden at start.
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False);
            Assert.That(record.KnowledgeMessages, Is.Zero);

            // 4-5: a grant reveals the hostile.
            yield return Verb(NetFixtureVerb.Observe);
            yield return TestWorld.WaitUntil(() => rig.Server.IsObserver(Client, Hostile), 2f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.True);

            // 7-8: it moves while observed and stays observed.
            var spawn = HostilePosition;
            yield return Verb(NetFixtureVerb.MoveHostile);
            Assert.That(Flat(HostilePosition - spawn), Is.GreaterThanOrEqualTo(NetFixture.PointSpacing - 0.01f));
            yield return null;
            var finalObserved = HostilePosition;
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.True);

            // 9-10: observation ends; one record at the final observed position.
            yield return Verb(NetFixtureVerb.Unobserve);
            yield return TestWorld.WaitUntil(() => !rig.Server.IsObserver(Client, Hostile) && record.LastKnown.ContainsKey(Hostile), 2f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False);
            Assert.That(record.LastKnown, Has.Count.EqualTo(1));
            Assert.That(Vector3.Distance(record.LastKnown[Hostile], finalObserved), Is.LessThan(0.01f));

            // 11-12: it moves while hidden; the record stays.
            var frozen = record.LastKnown[Hostile];
            var messages = record.KnowledgeMessages;
            yield return Verb(NetFixtureVerb.MoveHostile);
            Assert.That(Flat(HostilePosition - finalObserved), Is.GreaterThanOrEqualTo(NetFixture.PointSpacing - 0.01f));
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(record.LastKnown[Hostile], Is.EqualTo(frozen));
            Assert.That(record.KnowledgeMessages, Is.EqualTo(messages));
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False);

            // 14-15: re-observation gives the current position and clears the record.
            yield return Verb(NetFixtureVerb.Observe);
            yield return TestWorld.WaitUntil(() => rig.Server.IsObserver(Client, Hostile) && !record.LastKnown.ContainsKey(Hostile), 2f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.True);
            Assert.That(record.LastKnown.ContainsKey(Hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator AnOrdinaryClient_CannotDriveTheFixture_AndReceivesNothing()
        {
            yield return rig.Connect(NetLoopbackRig.TokenB);
            var rejected = rig.Server.Host.Rejected;
            rig.Client.SendFixture(NetFixtureVerb.Observe);
            rig.Client.SendFixture(NetFixtureVerb.Search);
            yield return new WaitForSecondsRealtime(1f);
            Assert.That(rig.Server.Host.Rejected, Is.EqualTo(rejected + 2), "both refused and counted");
            Assert.That(rig.Server.Fixture.Step, Is.EqualTo(0));
            Assert.That(rig.Client.Record.FixtureStep, Is.EqualTo(0));
            for (var id = 1; id <= 3; id++)
                Assert.That(rig.Server.IsObserver(Client, id), Is.False, "unit " + id);
            Assert.That(rig.Client.Record.Contents, Is.Null);
            Assert.That(rig.Client.Record.KnowledgeMessages, Is.Zero);
        }

        [UnityTest]
        public IEnumerator Search_SendsTheContents_OnlyAfterTheSearch()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(rig.Client.Record.Contents, Is.Null);
            Assert.That(rig.Client.Record.ContainerMessages, Is.Zero);
            yield return Verb(NetFixtureVerb.Search);
            yield return TestWorld.WaitUntil(() => rig.Client.Record.Contents != null, 2f);
            var contents = rig.Client.Record.Contents;
            Assert.That(contents, Has.Count.EqualTo(2));
            Assert.That(contents[0].DefinitionId, Is.EqualTo("fixture-medkit"));
            Assert.That(contents[0].Quantity, Is.EqualTo(2));
            Assert.That(contents[0].InstanceId, Is.EqualTo("fixture-item-0001"));
        }

        [UnityTest]
        public IEnumerator AGuessedAttackOnTheHiddenHostile_IsUnknownUnit_AndChangesNothing()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return Await(rig.Client.Send(new NetAttack(1, Hostile)), NetRejectReason.UnknownUnit);
            yield return Await(rig.Client.Send(new NetAttack(1, 999)), NetRejectReason.UnknownUnit);
            Assert.That(rig.Director.Friendlies[0].CurrentCommand, Is.Null);
            Assert.That(rig.Director.Hostiles[0].GetComponent<Health>().Current, Is.EqualTo(rig.Director.Hostiles[0].GetComponent<Health>().Max));
        }

        [UnityTest]
        public IEnumerator LosingSight_EndsTheClientsAttack_AndTheIdIsUnknownAgain()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return Verb(NetFixtureVerb.Observe);
            yield return TestWorld.WaitUntil(() => rig.Server.IsObserver(Client, Hostile), 2f);
            yield return Await(rig.Client.Send(new NetAttack(Melee, Hostile)), NetRejectReason.None);
            var melee = rig.Director.Friendlies[1];
            Assert.That(melee.CurrentCommand, Is.InstanceOf<AttackCommand>());

            yield return Verb(NetFixtureVerb.Unobserve);
            yield return TestWorld.WaitUntil(() => melee.CurrentCommand == null, 2f);
            Assert.That(melee.CurrentCommand, Is.Null, "the server ended the chase");
            var hostileHealth = rig.Director.Hostiles[0].GetComponent<Health>();
            Assert.That(hostileHealth.Current, Is.EqualTo(hostileHealth.Max), "the melee operative never reached it");
            yield return Await(rig.Client.Send(new NetAttack(Melee, Hostile)), NetRejectReason.UnknownUnit);
        }
    }
}
#endif
```

- [ ] **Step 2: Run them**

Run: `Tools/run-tests.sh PlayMode NetFixturePlayModeTests 2>&1 | tail -5 | sed 's/Unity requires.*behavior\.//'`
Expected: `total="6" passed="6"`, `EXIT=0`. These tests exercise Tasks 1 to 9 together, so a failure points to one of them:
- **The fixture fails to start because it finds fewer than two points.** Read the logs and the hostile's spawn. Widen the rings in `NetFixture.SamplePoints` (for example add a 10 m ring) and record ruling 4's new values. Do not drop the spacing.
- **The hostile never becomes observed.** Check that `OnHello` added the connection to `Visibility`, that `Publish` runs, and that the prefab has the condition (Task 7). Fix with a failing test in the owning task first.
- **No contents arrive.** If the log shows a FishNet serializer error for `NetItemWire[]`, FishNet's code generator did not cover the array. Change the message to use three parallel arrays (`string[] DefinitionIds`, `int[] Quantities`, `string[] InstanceIds`), update `NetMessageShapeTests` and `NetClient.OnContainer`, and record it as a ruling.

- [ ] **Step 3: Commit**

```bash
git add Assets/_Project/Tests/PlayMode/NetFixturePlayModeTests.cs Assets/_Project/Tests/PlayMode/NetFixturePlayModeTests.cs.meta
git commit -m "Net: fixture-mode PlayMode tests for the fifteen transitions, loot, guessed ids and the attack chase

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
Add any fixed source file to the same commit, and name the fix in the message.

---

### Task 11: Harness roles for hidden information

**Files:**
- Modify: `Assets/_Project/Scripts/Net/Adapter/NetHarness.cs`, `Assets/_Project/Tests/PlayMode/NetHarnessPlayModeTests.cs`
- Create: `Assets/_Project/Scripts/Net/Adapter/NetHarnessVisibility.cs` (the second `partial` file of `NetHarness`)

**Interfaces:**
- Consumes: `NetClient.Record`, `SendFixture`, `VisibleStates` (Task 9); `NetFixtureVerb`.
- Produces:
  - harness roles `visibility-a`, `visibility-b` and `fixture-denied`;
  - `NetHarness.VisibilityVerbs = 6`;
  - `NetHarness.ReadyLine = "BLACKGLASS NET HARNESS READY role="`, which B prints once it is connected and has checked its first view.
  - The PASS and FAIL lines and the exit codes are unchanged.

- [ ] **Step 1: Make `NetHarness` partial, read only visible avatars, and adapt roles a and b**

In `NetHarness.cs`:
- change `public sealed class NetHarness` to `public sealed partial class NetHarness`;
- change the summary's last sentence to: "It reads only what the client decoded: the NetClient's answers and record, and the avatars this client was actually sent (NetClient.VisibleStates)."
- `static NetUnitState[] States() => NetClient.VisibleStates().ToArray();`
- in `Run`, add before `default`:
```csharp
                case "visibility-a":
                    yield return VisibilityA();
                    break;
                case "visibility-b":
                    yield return VisibilityB();
                    break;
                case "fixture-denied":
                    yield return FixtureDenied();
                    break;
```
- in `Owner()`, directly after `var second = own + 1;`, add:
```csharp
            // Hidden information: the hostile is sent only while observed, so the owner role needs the fixture's grant
            // (the VM runs the part 2 roles against the fixture build; see Docs/DedicatedServer.md).
            client.SendFixture(NetFixtureVerb.Observe);
```
- replace `Observer()` with:
```csharp
        IEnumerator Observer()
        {
            yield return Connected();
            if (Failed)
                yield break;
            if (!Check("no ownership", client.OwnedCount == 0, "owns " + client.OwnedCount))
                yield break;
            yield return Wait(2d);
            if (!Check("no units", States().Length == 0, States().Length + " avatars arrived; a client that owns nothing receives none"))
                yield break;

            // The fixture mission's friendlies are ids 1 and 2 (NetUnits numbers friendlies first) and its hostile is 3.
            var cases = new List<KeyValuePair<string, KeyValuePair<NetRequest, NetRejectReason>>>();
            foreach (var id in new[] { 1, 2 })
            {
                var other = id == 1 ? 2 : 1;
                cases.Add(Order("not owner: move intent for unit " + id, new NetMoveIntent(id, Vector3.right), NetRejectReason.NotOwner));
                cases.Add(Order("not owner: move for unit " + id, new NetMoveTo(id, Vector3.zero), NetRejectReason.NotOwner));
                cases.Add(Order("not owner: attack by unit " + id, new NetAttack(id, other), NetRejectReason.NotOwner));
                cases.Add(Order("not owner: stop for unit " + id, new NetStop(id), NetRejectReason.NotOwner));
            }
            cases.Add(Order("unknown: an attack on the hidden hostile", new NetAttack(1, 3), NetRejectReason.UnknownUnit));
            cases.Add(Order("unknown: an attack on a made-up id", new NetAttack(1, 999), NetRejectReason.UnknownUnit));
            foreach (var pair in cases)
            {
                var command = client.Send(pair.Value.Key);
                yield return AwaitResult(command, 3d);
                client.TryGetResult(command, out var reason);
                if (!Check(pair.Key, waitOk && reason == pair.Value.Value, "answered " + reason + ", expected " + pair.Value.Value))
                    yield break;
            }
        }

        static KeyValuePair<string, KeyValuePair<NetRequest, NetRejectReason>> Order(string name, NetRequest request, NetRejectReason expected) =>
            new KeyValuePair<string, KeyValuePair<NetRequest, NetRejectReason>>(name, new KeyValuePair<NetRequest, NetRejectReason>(request, expected));
```

- [ ] **Step 2: Write the new roles**

`Assets/_Project/Scripts/Net/Adapter/NetHarnessVisibility.cs`:
```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Blackglass
{
    // The hidden-information roles of the harness (spec section 3.11). Numbers in comments are the brief's fifteen transitions.
    // Every check reads decoded client state: the avatars this client was sent and NetClient.Record.
    public sealed partial class NetHarness
    {
        /// <summary>How many fixture verbs visibility-a sends (Observe, MoveHostile, Unobserve, MoveHostile, Observe, Search).</summary>
        public const int VisibilityVerbs = 6;
        public const string ReadyLine = "BLACKGLASS NET HARNESS READY role=";

        static readonly HashSet<string> AllowedMessages = new HashSet<string>
        {
            nameof(NetWelcomeMessage), nameof(NetResultMessage), nameof(NetKnowledgeMessage), nameof(NetFixtureStepMessage),
            nameof(NetContainerMessage),
        };

        IEnumerator VisibilityA()
        {
            yield return Connected();
            if (Failed)
                yield break;
            var record = client.Record;
            var own = client.FirstOwnedId;
            var melee = own + 1;
            var guessed = client.FirstOwnedId + client.OwnedCount;   // the first hostile's id, guessed from the numbering

            // 1, 2: nothing hostile before observation.
            yield return Wait(2d);
            if (!Check("hidden at start", FindHostile() == null && record.KnowledgeMessages == 0 && record.ContainerMessages == 0,
                    "no hostile avatar, no knowledge and no container message"))
                yield break;

            // A guessed attack on the hidden hostile is answered exactly like a made-up id, and changes nothing.
            var before = OwnSnapshot();
            var guess = client.Send(new NetAttack(own, guessed));
            var madeUp = client.Send(new NetAttack(own, 999));
            yield return AwaitResult(guess, 3d);
            yield return AwaitResult(madeUp, 3d);
            client.TryGetResult(guess, out var guessReason);
            client.TryGetResult(madeUp, out var madeUpReason);
            if (!Check("guessed attack refused", guessReason == NetRejectReason.UnknownUnit && madeUpReason == NetRejectReason.UnknownUnit,
                    "guessed id answered " + guessReason + ", made-up id answered " + madeUpReason))
                yield break;
            yield return Wait(0.5d);
            var changed = Differences(before, OwnSnapshot());
            if (!Check("guessed attack changed nothing", changed == null, changed ?? "the squad is as it was"))
                yield break;

            // 4, 5: observation reveals the live hostile.
            var observeSentAt = Now;
            yield return Verb(NetFixtureVerb.Observe);
            if (Failed)
                yield break;
            yield return Until(() => FindHostile() != null, 5d);
            if (!Check("revealed", waitOk && FindHostile().Alive && FindHostile().Health > 0, "the hostile arrived with its health"))
                yield break;
            var hostileId = FindHostile().UnitId;
            yield return Wait(0.5d);
            var revealed = Position(hostileId);
            if (!CheckNothingBefore(hostileId, revealed, observeSentAt))
                yield break;

            // 7, 8: live movement reaches A.
            yield return Verb(NetFixtureVerb.MoveHostile);
            if (Failed)
                yield break;
            yield return Until(() => Flat(Position(hostileId) - revealed) >= 2f, 5d);
            if (!Check("live movement", waitOk, FormattableString.Invariant($"the hostile moved {Flat(Position(hostileId) - revealed):0.0} m")))
                yield break;
            yield return Wait(1d);
            var finalSeen = Position(hostileId);

            // The attack-chase mitigation: an accepted attack ends when observation ends (the melee operative is still walking).
            var attack = client.Send(new NetAttack(melee, hostileId));
            yield return AwaitResult(attack, 3d);
            client.TryGetResult(attack, out var attackReason);
            if (!Check("attack accepted", waitOk && attackReason == NetRejectReason.None, "answered " + attackReason))
                yield break;
            yield return Until(() => State(melee) != null && State(melee).Order == NetOrderKind.Attack, 3d);
            if (!Check("attacking", waitOk, "the melee operative's order is Attack"))
                yield break;

            // 9, 10: observation ends; one frozen record at the final observed position.
            yield return Verb(NetFixtureVerb.Unobserve);
            if (Failed)
                yield break;
            yield return Until(() => FindHostile() == null && record.LastKnown.ContainsKey(hostileId), 5d);
            if (!Check("hidden again", waitOk, "the avatar left and a last-known record arrived"))
                yield break;
            var stale = record.LastKnown[hostileId];
            if (!Check("one record at the last position", record.LastKnown.Count == 1 && Flat(stale - finalSeen) <= 0.25f,
                    FormattableString.Invariant($"{record.LastKnown.Count} record(s), {Flat(stale - finalSeen):0.00} m from the last seen position")))
                yield break;
            yield return Until(() => State(melee) != null && State(melee).Order != NetOrderKind.Attack, 5d);
            if (!Check("attack ended", waitOk, "the server ended the attack on a target this client no longer observes"))
                yield break;

            // 11, 12: hidden movement does not move the record.
            var knowledgeBefore = record.KnowledgeMessages;
            yield return Verb(NetFixtureVerb.MoveHostile);
            if (Failed)
                yield break;
            yield return Wait(1d);
            if (!Check("record frozen", FindHostile() == null && record.LastKnown[hostileId] == stale && record.KnowledgeMessages == knowledgeBefore,
                    "no avatar, no message, the record unchanged"))
                yield break;

            // A reused id is refused like a made-up one.
            var reuse = client.Send(new NetAttack(own, hostileId));
            yield return AwaitResult(reuse, 3d);
            client.TryGetResult(reuse, out var reuseReason);
            if (!Check("reused id refused", waitOk && reuseReason == NetRejectReason.UnknownUnit, "answered " + reuseReason))
                yield break;

            // 14, 15: re-observation shows the current position and clears the stale record.
            yield return Verb(NetFixtureVerb.Observe);
            if (Failed)
                yield break;
            yield return Until(() => FindHostile() != null && !record.LastKnown.ContainsKey(hostileId), 5d);
            if (!Check("re-observed", waitOk, "the avatar is back and the record cleared"))
                yield break;
            yield return Wait(0.5d);
            if (!Check("current position", Flat(Position(hostileId) - stale) >= 2f,
                    FormattableString.Invariant($"{Flat(Position(hostileId) - stale):0.0} m from the stale record")))
                yield break;

            // Loot: nothing before the search, the contents after it.
            if (!Check("no contents before search", record.ContainerMessages == 0 && record.Contents == null, "no container message"))
                yield break;
            yield return Verb(NetFixtureVerb.Search);
            if (Failed)
                yield break;
            yield return Until(() => record.Contents != null, 5d);
            if (!Check("contents after search", waitOk && record.Contents.Count > 0 && record.Contents.All(i =>
                    !string.IsNullOrEmpty(i.DefinitionId) && i.Quantity > 0 && !string.IsNullOrEmpty(i.InstanceId)),
                    (record.Contents != null ? record.Contents.Count : 0) + " stacks"))
                yield break;

            // Only the expected message types were decoded, and nothing was lost from the record.
            var unexpected = record.DecodedMessages.Select(d => d.Type).Where(t => !AllowedMessages.Contains(t)).Distinct().ToList();
            Check("message types", unexpected.Count == 0 && !record.Truncated, unexpected.Count > 0
                ? "unexpected: " + string.Join(", ", unexpected)
                : record.Truncated ? "the record was truncated" : record.DecodedMessages.Count + " messages, all of expected types");
        }

        IEnumerator VisibilityB()
        {
            yield return Connected();
            if (Failed)
                yield break;
            var record = client.Record;
            // B has no fixture identity: its own attempt to drive the fixture changes nothing.
            client.SendFixture(NetFixtureVerb.Observe);
            client.SendFixture(NetFixtureVerb.Search);
            yield return Wait(1d);
            if (!CheckBlind("refused fixture"))
                yield break;
            Debug.Log(ReadyLine + "visibility-b");

            var first = -1;
            var last = record.FixtureStep;
            var end = Now + 180d;
            while (Now < end)
            {
                if (!IsBlind(out _))
                {
                    CheckBlind("blind at step " + record.FixtureStep);
                    yield break;
                }
                if (record.FixtureStep != last)
                {
                    last = record.FixtureStep;
                    if (first < 0)
                        first = last;
                    Debug.Log(Prefix + "step " + last + " blind");
                    if (last >= first + VisibilityVerbs - 1)
                        break;
                }
                yield return null;
            }
            if (!Check("saw A's steps", first >= 0 && last >= first + VisibilityVerbs - 1, "steps " + first + " to " + last))
                yield break;
            yield return Wait(1d);   // time for A's last verb (the search) to arrive here, if it were going to leak
            CheckBlind("blind after the last step");
        }

        IEnumerator FixtureDenied()
        {
            yield return Connected();
            if (Failed)
                yield break;
            client.SendFixture(NetFixtureVerb.Observe);
            client.SendFixture(NetFixtureVerb.Search);
            yield return Wait(3d);
            var record = client.Record;
            Check("fixture refused", FindHostile() == null && record.FixtureStep == 0 && record.KnowledgeMessages == 0 && record.Contents == null,
                "no hostile, no step, no knowledge, no contents");
        }

        IEnumerator Verb(NetFixtureVerb verb)
        {
            var before = client.Record.FixtureStep;
            client.SendFixture(verb);
            yield return Until(() => client.Record.FixtureStep > before, 5d);
            Check("fixture " + verb, waitOk, "step " + client.Record.FixtureStep);
        }

        // Before observation nothing may have carried the hostile: no avatar sighting of it, and no sighting or decoded message
        // holding a position within 5 cm of where it turned out to be (its spawn, since it had not moved).
        bool CheckNothingBefore(int hostileId, Vector3 revealed, double observeSentAt)
        {
            var record = client.Record;
            var hostileSightings = record.Sightings.Count(s => s.Time < observeSentAt && (s.Hostile || s.UnitId == hostileId));
            var positions = record.Sightings.Where(s => s.Time < observeSentAt).Select(s => s.Position)
                .Concat(record.DecodedMessages.Where(d => d.Time < observeSentAt && d.HasPosition).Select(d => d.Position))
                .Count(p => (p - revealed).magnitude <= 0.05f);
            return Check("nothing before the reveal", hostileSightings == 0 && positions == 0 && !record.Truncated,
                hostileSightings + " hostile sightings and " + positions + " positions at the hostile before it was observed");
        }

        bool IsBlind(out string detail)
        {
            var record = client.Record;
            var avatars = States().Length;
            detail = avatars + " avatars, " + record.Sightings.Count + " sightings, " + record.LastKnown.Count + " last-known records, " +
                record.KnowledgeMessages + " knowledge and " + record.ContainerMessages + " container messages";
            return avatars == 0 && record.Sightings.Count == 0 && record.LastKnown.Count == 0 && record.KnowledgeMessages == 0
                && record.ContainerMessages == 0 && record.Contents == null && !record.Truncated;
        }

        bool CheckBlind(string step) => Check(step, IsBlind(out var detail), detail);

        List<Snap> OwnSnapshot() =>
            Snapshot().Where(s => s.Id >= client.FirstOwnedId && s.Id < client.FirstOwnedId + client.OwnedCount).ToList();

        static float Flat(Vector3 offset)
        {
            offset.y = 0f;
            return offset.magnitude;
        }
    }
}
```

- [ ] **Step 3: Adapt and add the harness PlayMode tests**

In `NetHarnessPlayModeTests.cs`:
- remove both `[Ignore(...)]` attributes from Task 8;
- give `Run` a `bool fixture = false` parameter, used as `rig.BeginNet(fixture: fixture);`;
- run `TheOwnerRole_PassesTheWholeScript` with `fixture: true`;
- add:
```csharp
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator TheVisibilityRoleA_PassesTheWholeScript()
        {
            yield return Run("visibility-a", NetLoopbackRig.TokenA, seconds: 120f, fixture: true);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheFixtureDeniedRole_PassesAgainstANormalServer()
        {
            yield return Run("fixture-denied", NetLoopbackRig.TokenA);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheFixtureDeniedRole_Fails_WhenTheFixtureAnswers()
        {
            yield return Run("fixture-denied", NetLoopbackRig.TokenA, fixture: true);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitFailed), harness.Summary);
            StringAssert.Contains("step=fixture refused", harness.Summary);
        }
```

- [ ] **Step 4: Run them**

Run: `Tools/run-tests.sh PlayMode NetHarnessPlayModeTests 2>&1 | tail -5 | sed 's/Unity requires.*behavior\.//'`
Expected: `total="9" passed="9"`, `EXIT=0`.

**Host-mode check.** These roles rely on `NetUnitState.IsClientInitialized` being false for an avatar the host client does not observe. This covers both an avatar it never observed and one it stopped observing. Confirm it with this run:
- **The run passes:** nothing to do.
- **A role fails only at a check that counts visible avatars, and its detail shows an avatar is present:** the checks concerned are "hidden at start", "hidden again", "record frozen", "no units" and "fixture refused". This means host mode keeps unobserved avatars client-initialized. In that case:
  1. Leave the roles unchanged; the VM run in Task 13 is the real proof of client-side absence.
  2. Mark only the affected tests with `[Ignore("host mode keeps unobserved avatars client-initialized; proven on the VM (Task 13)")]`. Keep `TheFixtureDeniedRole_Fails_WhenTheFixtureAnswers`, which must still fail for the right reason.
  3. Record this as a ruling, and say so in the task report.
- **Any other failure** is a defect: fix it in the owning code.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Net/Adapter/NetHarness.cs Assets/_Project/Scripts/Net/Adapter/NetHarnessVisibility.cs Assets/_Project/Scripts/Net/Adapter/NetHarnessVisibility.cs.meta Assets/_Project/Tests/PlayMode/NetHarnessPlayModeTests.cs
git commit -m "Net: harness roles visibility-a, visibility-b and fixture-denied on decoded client state

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Builds and scripts: a development fixture build, `NET_FIXTURE`, `+fixture` tokens, a two-client runner

**Files:**
- Modify: `Assets/_Project/Editor/Server/ServerBuild.cs`, `Tools/build-server.sh`, `Tools/LinuxServer/run-net.sh`, `Tools/LinuxServer/make-dev-tokens.sh`, `Tools/run-net-harness.sh`

**Interfaces:**
- Consumes: the harness roles (Task 11), `-netFixture` (Task 8).
- Produces:
  - `Blackglass.EditorTools.ServerBuild.BuildFixtureFromCommandLine`.
  - `build-info.txt` gains `variant=release|fixture`.
  - `Tools/build-server.sh [--fixture]`. The fixture archive is `Builds/blackglass-server-linux-<rev>[-dirty]-fixture.tar.gz`.
  - `NET_FIXTURE=1 run-net.sh start` adds `-netFixture`.
  - `make-dev-tokens.sh` gives account A `squad+fixture`.
  - `Tools/run-net-harness.sh [--denied] [host]` prints one line per role and a final `leftover clients=<n> tunnel=<pid|none>` line.

- [ ] **Step 1: The development server build**

In `ServerBuild.cs`:
- replace the body of `BuildFromCommandLine()` with a call to a new `static void BuildForCommandLine(bool development)` that holds the old body, with `Build(out message)` changed to `Build(out message, development)`;
- add:
```csharp
        /// <summary>
        /// Unity -batchmode -executeMethod entry for the development build the harness runs against: the release build plus
        /// BuildOptions.Development, so the development fixture (-netFixture) is compiled in (decision 047). Exits 0 or 1.
        /// </summary>
        public static void BuildFixtureFromCommandLine() => BuildForCommandLine(development: true);
```
- keep `public static bool Build(out string message)` for the menu and existing callers, as `=> Build(out message, development: false);`;
- add `public static bool Build(out string message, bool development)`, holding the old body, with:
  - `options = BuildOptions.StrictMode | (development ? BuildOptions.Development : BuildOptions.None),`;
  - `WriteBuildInfo(root, output, summary, development);`;
  - `variant=fixture|release` prepended to the success message.
- `WriteBuildInfo` gains the parameter `bool development` and the line `"variant=" + (development ? "fixture" : "release"),` after `"subtarget=Server",`.

In `Tools/build-server.sh`:
- the usage line becomes `# Usage: Tools/build-server.sh [--fixture]   (--fixture: a development build with the -netFixture test fixture)`;
- after `set -u`, add:
```bash
method=Blackglass.EditorTools.ServerBuild.BuildFromCommandLine
variant=release
if [ "${1:-}" = "--fixture" ]; then
  method=Blackglass.EditorTools.ServerBuild.BuildFixtureFromCommandLine
  variant=fixture
fi
```
- replace the hard-coded `-executeMethod Blackglass.EditorTools.ServerBuild.BuildFromCommandLine` with `-executeMethod "$method"`;
- after `if [ "$dirty" = "true" ]; then suffix="-dirty"; fi`, add `if [ "$variant" = "fixture" ]; then suffix="${suffix}-fixture"; fi`.

- [ ] **Step 2: The VM scripts**

`Tools/LinuxServer/run-net.sh`:
- add `NET_FIXTURE (1: start the development fixture; needs the fixture build)` to the environment comment;
- in `start`, before `setsid`, add:
```bash
    fixture_args=""
    if [ "${NET_FIXTURE:-0}" = "1" ]; then fixture_args="-netFixture"; fi
    variant=$(awk -F= '$1=="variant"{print $2}' "$dir/build-info.txt" 2>/dev/null)
    echo "build variant=${variant:-unknown} fixture=${NET_FIXTURE:-0}"
```
- change the server's arguments to `-net -netQuiet $fixture_args -netPort "$port" -netTokens "$tokens"`.

`Tools/LinuxServer/make-dev-tokens.sh`:
- the comment line becomes `echo "# Development tokens (networking experiment). Format: account token squad|none[+fixture] [attempt]"`;
- the account-a line becomes `echo "account-a $(newtoken) squad+fixture"`;
- the header comment gets: `account-a may drive the development fixture (decision 047); only a fixture build started with NET_FIXTURE=1 accepts it.`

- [ ] **Step 3: The runner**

Replace `Tools/run-net-harness.sh` with:
```bash
#!/usr/bin/env bash
# Runs the net-test client's harness roles against the networked server on the VM through an SSH tunnel (networking experiment).
# Usage: Tools/run-net-harness.sh [--denied] [host]    (default host: bg-dev-mission01; port: $NET_PORT or 7770)
#   default:  against the fixture build started with `NET_FIXTURE=1 run-net.sh start`: visibility-b and visibility-a at the
#             same time (B must see nothing of what A is shown), then the part 2 roles a, b, ticket and badtoken.
#   --denied: against the release build started with `run-net.sh start`: the fixture-denied role only.
# Needs Builds/NetTestClient/NetTestClient.exe (Tools/build-netclient.sh). Logs: Builds/NetHarnessLogs/<UTC>/ (git-ignored).
# The tokens are read from the VM into shell variables and never printed. Every client this script started and the tunnel are
# stopped on any exit, and the last line reports what is left: "leftover clients=<n> tunnel=<pid|none>".
set -u
mode=full
if [ "${1:-}" = "--denied" ]; then
  mode=denied
  shift
fi
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

# Git Bash has no pgrep: find the tunnel by its command line (the shell running this script must not match, so anchor on "ssh").
find_tunnel() {
  local f
  for f in /proc/[0-9]*/cmdline; do
    if tr '\0' ' ' < "$f" 2>/dev/null | grep -q "^ssh -f -N .*-L $port:127.0.0.1:$port"; then
      basename "$(dirname "$f")"
      return
    fi
  done
}
count_clients() { tasklist //NH //FI "IMAGENAME eq NetTestClient.exe" 2>/dev/null | grep -c "NetTestClient.exe"; }

tunnel_pid=""
b_pid=""
cleanup() {
  if [ -n "$b_pid" ]; then
    kill "$b_pid" 2>/dev/null
    wait "$b_pid" 2>/dev/null
  fi
  if [ -n "$tunnel_pid" ]; then kill "$tunnel_pid" 2>/dev/null; fi
  sleep 1
  local left
  left=$(find_tunnel)
  echo "leftover clients=$(count_clients) tunnel=${left:-none}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

ssh -f -N -o ExitOnForwardFailure=yes -o BatchMode=yes -L "$port:127.0.0.1:$port" "$host" || { echo "the tunnel could not be opened (is port $port free here?)"; exit 2; }
tunnel_pid=$(find_tunnel)

status=0
summary() { grep -h 'BLACKGLASS NET HARNESS' "$1" 2>/dev/null | grep -v 'HARNESS READY' | tail -1; }
run_role() {
  local role="$1" token="$2" log="$logs/harness-$1.log"
  timeout 240 "$exe" -batchmode -nographics -logFile "$log" -server 127.0.0.1 -port "$port" -token "$token" -netharness "$role" > /dev/null 2>&1
  local code=$?
  printf 'role=%s exit=%s  %s\n' "$role" "$code" "$(summary "$log")"
  if [ "$code" -ne 0 ]; then status=1; fi
}

if [ "$mode" = "denied" ]; then
  run_role fixture-denied "$tok_a"
else
  blog="$logs/harness-visibility-b.log"
  timeout 240 "$exe" -batchmode -nographics -logFile "$blog" -server 127.0.0.1 -port "$port" -token "$tok_b" -netharness visibility-b > /dev/null 2>&1 &
  b_pid=$!
  ready=0
  for _ in $(seq 1 60); do
    if grep -q "BLACKGLASS NET HARNESS READY role=visibility-b" "$blog" 2>/dev/null; then
      ready=1
      break
    fi
    kill -0 "$b_pid" 2>/dev/null || break
    sleep 1
  done
  if [ "$ready" -eq 1 ]; then
    run_role visibility-a "$tok_a"
  else
    echo "visibility-b never became ready; visibility-a was not run"
    kill "$b_pid" 2>/dev/null
    status=1
  fi
  wait "$b_pid"
  bcode=$?
  b_pid=""
  printf 'role=%s exit=%s  %s\n' visibility-b "$bcode" "$(summary "$blog")"
  if [ "$bcode" -ne 0 ]; then status=1; fi
  run_role a "$tok_a"
  run_role b "$tok_b"
  run_role ticket "$tok_t"
  run_role badtoken "not-a-real-token"
fi
echo "logs=$logs"
exit $status
```

- [ ] **Step 4: Check the scripts and the build method**

```bash
bash -n Tools/run-net-harness.sh && bash -n Tools/build-server.sh && bash -n Tools/LinuxServer/run-net.sh && bash -n Tools/LinuxServer/make-dev-tokens.sh && echo "syntax ok"
Tools/run-tests.sh EditMode "ServerOptionsTests;NetSourceAuditTests" 2>&1 | tail -2 | sed 's/Unity requires.*behavior\.//'
```
Expected: `syntax ok`; the tests pass (this compiles `ServerBuild.cs` with the Editor assembly).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Editor/Server/ServerBuild.cs Tools/build-server.sh Tools/LinuxServer/run-net.sh Tools/LinuxServer/make-dev-tokens.sh Tools/run-net-harness.sh
git commit -m "Tools: a development fixture server build, NET_FIXTURE, +fixture tokens, and a two-client harness runner with cleanup

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Full suites, builds, and the acceptance run on `bg-dev-mission01`

**Files:** none committed. Results go to `.claude/scratchpad/hidden-info/results.md` (create the folder).

Each step's output is evidence for the final report and the docs. Copy the exact lines into `results.md` as you go. Never estimate a number.

- [ ] **Step 1: Full suites**

```bash
mkdir -p .claude/scratchpad/hidden-info
Tools/run-tests.sh EditMode > .claude/scratchpad/hidden-info/editmode.txt 2>&1; tail -3 .claude/scratchpad/hidden-info/editmode.txt | sed 's/Unity requires.*behavior\.//'
Tools/run-tests.sh PlayMode > .claude/scratchpad/hidden-info/playmode.txt 2>&1; tail -3 .claude/scratchpad/hidden-info/playmode.txt | sed 's/Unity requires.*behavior\.//'
```
Run PlayMode in the background (it takes well over ten minutes) and wait for its notification.

Expected: EditMode `total="1426" passed="1426"`; PlayMode `total="1431"`, with every test passed except any `[Ignore]` that Task 11 recorded.
- If `AreaPresentationPlayModeTests.TheGeneratedExplosion_ScalesItsExtentWithTheRadius` fails alone, re-run it alone. If it then passes, report it as flaky.
- Report any other flaky test separately, with its re-run result.

**Test counts:**

| Task | New EditMode | New PlayMode |
|---|---|---|
| 1 projection | 15 | 0 |
| 2 squad source | 4 | 4 |
| 3 gate | 6 | 1 |
| 4 guard | 0 | 7 |
| 5 identity | 8 | 0 |
| 6 fixture | 10 | 0 |
| 7 messages, prefab, audits | 8 | 0 |
| 8 server | 1 | 7 |
| 9 client record | 8 | 0 |
| 10 fixture end to end | 0 | 6 |
| 11 harness | 0 | 3 |
| **Total** | **60** (1366 → 1426) | **28** (1403 → 1431) |

If a count differs, reconcile it against this table and explain the difference in the report.

- [ ] **Step 2: Builds**

```bash
Tools/build-server.sh 2>&1 | tail -8
Tools/build-server.sh --fixture 2>&1 | tail -8
Tools/build-netclient.sh 2>&1 | tail -3
ls -la Builds/*.tar.gz | tail -4
```
Expected:
- each server build prints `variant=release` or `variant=fixture`, the archive path, `sizeMB=` and a sha256, then `EXIT=0`;
- the client build prints `client=.../NetTestClient.exe` and `EXIT=0`.

Record the revision, the dirty flag, the two archive names, their sizes and sha256s, and the client path. Also record the package versions from `Packages/packages-lock.json`: FishNet, FishyUnityTransport and Unity Transport.

- [ ] **Step 3: Release build: Checkpoint A, a single loopback listener, the fixture refused**

```bash
Tools/deploy-server.sh bg-dev-mission01 "$(ls -t Builds/blackglass-server-linux-*.tar.gz | grep -v fixture | head -1)" 2>&1 | tail -4
ssh bg-dev-mission01 '/opt/blackglass/server/run-diagnostic.sh' 2>&1 | grep -E "DIAGNOSTIC|exit="
ssh bg-dev-mission01 '/opt/blackglass/server/make-dev-tokens.sh --force; grep -c "squad+fixture" /opt/blackglass/dev-tokens.txt; stat -c "%a %U" /opt/blackglass/dev-tokens.txt'
ssh -f -n bg-dev-mission01 'NET_FIXTURE=1 /opt/blackglass/server/run-net.sh start > /opt/blackglass/logs/run-net-refused.out 2>&1'
```
About 20 seconds later (do other work, do not sleep in the foreground):
`ssh bg-dev-mission01 'cat /opt/blackglass/logs/run-net-refused.out; /opt/blackglass/server/run-net.sh status'`

Expected:
- `deployed:` with `variant=release`;
- `BLACKGLASS DIAGNOSTIC PASS` and `exit=0` (acceptance criterion 2);
- `wrote ... (3 accounts)`, `1` and `600 root`;
- `build variant=release fixture=1`, then `the server exited:` with `-netFixture is not available in a release build`;
- `not running`.

```bash
ssh -f -n bg-dev-mission01 '/opt/blackglass/server/run-net.sh start > /opt/blackglass/logs/run-net.out 2>&1'
```
About 20 seconds later:
```bash
ssh bg-dev-mission01 'cat /opt/blackglass/logs/run-net.out; ss -lntu | grep -E ":7770|:7777" || echo "nothing else"'
Tools/run-net-harness.sh --denied 2>&1 | tail -4
ssh bg-dev-mission01 'grep -E "\[Net\]" $(ls -t /opt/blackglass/logs/*-net.log | head -1) | cut -c1-200 | tail -12; /opt/blackglass/server/run-net.sh stop'
```
Expected:
- `listening on 127.0.0.1:7770`, and exactly one listener, `tcp LISTEN ... 127.0.0.1:7770`, with no UDP, `0.0.0.0` or public address (acceptance criterion 1);
- `role=fixture-denied exit=0  BLACKGLASS NET HARNESS PASS role=fixture-denied ...`;
- `leftover clients=0 tunnel=none`;
- the server log shows `listening ... visibility=squad`, `welcomed account=account-a owns=2`, `fixture refused client=... account=account-a` (rate-limited), no exception, then `stopped`.

- [ ] **Step 4: Release build: ten-minute idle run with `-net`**

```bash
ssh -f -n bg-dev-mission01 'cd / && SMOKE_ARGS="-net -netQuiet -netTokens /opt/blackglass/dev-tokens.txt" setsid nohup /opt/blackglass/server/smoke-run.sh 600 > /opt/blackglass/logs/smoke-run-net.out 2>&1 < /dev/null &'
```
Carry on with Step 5 only after this run has finished: one server at a time, because both use port 7770. About 11 minutes later:
`ssh bg-dev-mission01 'cat $(ls -t /opt/blackglass/logs/*-smoke-summary.txt | head -1)'`

Expected:
- `stop: SIGTERM, exit status 0`;
- 59 fps or more on every heartbeat;
- `cpu after ready` mean at most 2.9 % (part 2: 1.9 % mean, 2.2 % max);
- `rss peak` at most 136 MB (part 2: 116.3 MB);
- `Exception` and `error` counts of 0.

Record it next to the part 2 figures (acceptance criterion 18). A breach is a finding to report, not something to tune away.

- [ ] **Step 5: Fixture build: the full harness through the tunnel**

```bash
Tools/deploy-server.sh bg-dev-mission01 "$(ls -t Builds/blackglass-server-linux-*-fixture.tar.gz | head -1)" 2>&1 | tail -4
ssh -f -n bg-dev-mission01 'NET_FIXTURE=1 /opt/blackglass/server/run-net.sh start > /opt/blackglass/logs/run-net.out 2>&1'
```
About 20 seconds later:
```bash
ssh bg-dev-mission01 'cat /opt/blackglass/logs/run-net.out; ss -lntu | grep -E ":7770|:7777" || echo "nothing else"'
Tools/run-net-harness.sh 2>&1 | tail -10
```
Expected:
- `deployed:` with `variant=fixture`;
- `build variant=fixture fixture=1` and `listening on 127.0.0.1:7770`, with the same single listener;
- then, from the harness:
```
role=visibility-a exit=0  BLACKGLASS NET HARNESS PASS role=visibility-a steps=...
role=visibility-b exit=0  BLACKGLASS NET HARNESS PASS role=visibility-b steps=...
role=a exit=0  BLACKGLASS NET HARNESS PASS role=a steps=... latencyMedianMs=... roundTripMs=...
role=b exit=0  BLACKGLASS NET HARNESS PASS role=b steps=...
role=ticket exit=0  BLACKGLASS NET HARNESS PASS role=ticket steps=...
role=badtoken exit=0  BLACKGLASS NET HARNESS PASS role=badtoken steps=...
logs=...
leftover clients=0 tunnel=none
```
If a role fails, read its log (`[Harness]` lines name the step) and the server log's `[Net]` lines. Fix the owning code with a failing test first, rebuild, redeploy and re-run. Never loosen a check unless the real behaviour proves the check wrong, and record that as a ruling.

Record:
- the six summary lines;
- B's `step N blind` lines (`grep "step .* blind" <logs>/harness-visibility-b.log`);
- the latency figures from role `a`, reported as transport evidence next to the 150 ms LAN target (client prediction stays deferred).

- [ ] **Step 6: What the server saw, and a forced failure leaves nothing behind**

```bash
ssh bg-dev-mission01 'L=$(ls -t /opt/blackglass/logs/*-net.log | head -1); grep -E "\[Net\] (fixture|visibility|welcomed|rejected|hello)" $L | cut -c1-180 | tail -40; grep -ciE "exception" $L; grep -cE "account-[abt]+ [A-Za-z0-9]{20,}" $L'
ssh bg-dev-mission01 '/opt/blackglass/server/run-net.sh stop; /opt/blackglass/server/run-net.sh status'
Tools/run-net-harness.sh 2>&1 | tail -4
```
Expected:
- `fixture step=1 verb=Observe account=account-a` through `step=6 verb=Search`, then step 7 from role `a`;
- `visibility client=... unit=3 Observed/LastKnown` lines;
- `fixture refused ... account=account-b` (B's own attempt);
- no exception (`0`) and no token in the log (`0`);
- then `stopped` and `not running`;
- the last command, with the server down: every role exits 2 (no connection), then `leftover clients=0 tunnel=none` (acceptance criterion 17).

- [ ] **Step 7: Leave the VM on the release build**

```bash
Tools/deploy-server.sh bg-dev-mission01 "$(ls -t Builds/blackglass-server-linux-*.tar.gz | grep -v fixture | head -1)" 2>&1 | tail -3
```
Expected: `deployed:` with `variant=release`. Nothing is left running on the VM.

- [ ] **Step 8: Write up the acceptance criteria**

In `results.md`, list the brief's 18 acceptance criteria one by one. For each, give the evidence (test names, harness lines, log lines) and PASS, FAIL or PARTIAL, plus the ruling or limitation when it is not a plain PASS.

---

### Task 14: Documents

**Files:**
- Modify: `Docs/Decisions.md`, `Docs/OnlineReadinessAudit.md`, `Docs/DedicatedServer.md`, `CLAUDE.md`

Every number comes from `.claude/scratchpad/hidden-info/results.md`. Do not edit earlier decisions' text or earlier audit findings.

- [ ] **Step 1: Decision 047 in `Docs/Decisions.md`**

Add **"## 047. Hidden information: server-authoritative per-connection visibility (2026-10-12)"** after decision 046. Use the layout of 046: what was decided, why, the alternatives rejected, and the implications. Include:
- **What is filtered:**
  - a hidden hostile sends nothing live (avatar despawned per connection);
  - a last-known record is frozen and sent only to its connection;
  - hostile orders, queue and steering are never sent;
  - friendlies go only to their owner;
  - the gate answers `UnknownUnit` for hidden hostiles;
  - container contents go only to the searcher, after the search;
  - no seed, plan or spawn field exists in any message (reflection test).
- **What remains visible:**
  - a connection's own units, including their health falling under fire;
  - a hostile while observed (position, rotation, health, alive state);
  - the fixture step counter, on fixture builds only;
  - the net-test client receives no geometry, but layout secrecy is not claimed (spec §3.12).
- **Per-connection versus shared intelligence:**
  - the squad model is shared; a connection gets it only if it owns every living friendly, and at least one;
  - split ownership fails closed;
  - co-op sharing is a later decision.
- **Fail closed:** startup refuses without the service, its model, or fog; at runtime everything is hidden and one line is logged.
- **The fixture:**
  - development builds only;
  - needs `-netFixture`, `-netQuiet`, a loopback bind and a `+fixture` token;
  - grants are bound to the server's connection object;
  - grants only, real sight not consulted;
  - one fixture container.
- **The attack-chase mitigation** (`CommandableUnit.DropCurrent`, gate-accepted attacks only), and what it does not touch.
- **Avatar authority:** `_clientAuthoritative: 0`, no owners, and the source audit.
- **Measured results:**
  - test totals;
  - archive sizes and hashes;
  - harness lines;
  - the idle run next to part 2;
  - latency as transport evidence.
- **Limitations:**
  - host-mode tests prove server observer sets only;
  - the real loot and inventory are not on the server;
  - Recon Scan, abilities on cooldown and taking items are not exercised;
  - geometry secrecy is deferred;
  - the pre-hello and never-hello items stay deferred.
- **Rulings:** the spec's two rulings, the plan's ten, and any made during implementation.
- Revisions (branch head) and package versions.

- [ ] **Step 2: Audit §9.4 in `Docs/OnlineReadinessAudit.md`**

- Append **"### 9.4 Networking experiment, hidden-information slice (2026-10-12)"**.
- One paragraph says what it is, and that it is not Phase 13: no accounts, backend or settlement.
- Add a table of checks A3 (seven of nine), A5, A6 (search only), A9 and A10, plus the brief's 18 acceptance criteria. Columns: "Covered" and "Evidence".
- Add the findings that matter next:
  - per-owner models for co-op;
  - geometry secrecy;
  - the real loot replication;
  - host-mode test limits;
  - the latency figure.
- In §9.1's status table, add one row for this slice.
- Do not change any earlier text.

- [ ] **Step 3: `Docs/DedicatedServer.md`**

In "Networked mode":
- add `-netFixture` to the option list (development builds only, needs `-netQuiet`);
- the token format `squad|none[+fixture]`, and `make-dev-tokens.sh --force` to regenerate;
- `Tools/build-server.sh --fixture` and the archive name;
- `NET_FIXTURE=1 run-net.sh start`;
- `Tools/run-net-harness.sh` (six roles, two at once) and `--denied`, with the `leftover` line;
- the new log lines (`visibility=`, `fixture step=`, `fixture refused`, `visibility client=`);
- that the VM is left on the release build.

Keep to the facts that were true in this run.

- [ ] **Step 4: `CLAUDE.md`**

Apply the spec's Appendix A replacement text exactly (two replacements: the authorized step and its out-of-scope list; and "Current Priority").

- [ ] **Step 5: Verify and commit**

```bash
git status --short
git diff --stat
```
Expected: only the four documents are modified. The owner's two settings files show as modified, as they did before. Nothing under `Builds/` is tracked.
```bash
git add Docs/Decisions.md Docs/OnlineReadinessAudit.md Docs/DedicatedServer.md CLAUDE.md
git commit -m "Docs: decision 047, audit 9.4, the fixture run guide, and CLAUDE.md for the hidden-information slice

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

Then finish with superpowers:finishing-a-development-branch. **Do not push or merge.** Report to the owner:
- the branch and head commit, the commits over `main`, and the files and systems changed;
- the focused and full test results, with flaky tests listed separately;
- the Linux build and runtime results, the real harness results and the performance observations;
- the rulings made, the deferred minors, and anything that needs an owner decision.

Stop there; Phase 13 is not started.
