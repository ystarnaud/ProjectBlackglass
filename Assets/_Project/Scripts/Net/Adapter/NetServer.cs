using System;
using System.Collections.Generic;
using System.IO;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Transporting;
using FishNet.Transporting.UTP;
using UnityEngine;
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
using System.Linq;
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
    /// With -netFixture (Editor and fixture builds only), the development fixture replaces the squad model as the source.
    /// Editor and fixture builds also brief the visual client (decision 052, owner ruling D1): the public map, the known
    /// objectives and the mission end, to welcomed connections that own units, for a Full/All mission only. A release build
    /// contains none of it. Tokens are never logged.
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
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
        NetFixture fixture;

        internal NetFixture Fixture => fixture;
        bool FixtureEnabled => fixture != null;
#else
        bool FixtureEnabled => false;
#endif
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
        // The visual client's briefing (decision 052). Editor and fixture builds only (owner ruling D1).
        readonly HashSet<NetConnection> participants = new HashSet<NetConnection>();
        readonly NetShotRelay shots = new NetShotRelay();
        readonly List<(UnitAttacker Attacker, Action<ShotRecord> Handler)> shotHandlers = new List<(UnitAttacker, Action<ShotRecord>)>();
        MissionDirector mission;
        NetMap briefingMap;
        bool briefingAllowed;
        bool hasTerminal;
        bool hasCameras;
        bool objectivesDirty;

        /// <summary>The map the briefing sends, or null when the mission is not projected. Tests replace it (the largest-map
        /// transport proof); there is no command-line option for it.</summary>
        internal NetMap BriefingMap
        {
            get => briefingMap;
            set => briefingMap = value;
        }
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
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
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
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
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
            Visibility = new NetVisibility(Units, source, line => Debug.LogError(Prefix + line));
            Guard = new NetAttackGuard(Units, Visibility);
            Gate = new ServerCommandGate(Units, director.InstanceId, Visibility)
            {
                Log = line => Debug.LogWarning(Prefix + line),
                AttackAccepted = Guard.Accepted,
            };
            Host = new NetSessionHost(Gate, new NetAdmission(entries, director.InstanceId, director.Friendlies.Count));
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
            BeginBriefing(director);
#endif
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

#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
        // The public map and known objectives: built once from the ready mission, or refused with a reason (counts only).
        void BeginBriefing(MissionDirector director)
        {
            mission = director;
            var current = director.Current;
            hasTerminal = current.Terminal != null;
            hasCameras = current.Security.HasTerminal || current.Security.Cameras.Count > 0;
            // The approved profile (owner ruling D1): nothing of the briefing reaches anyone otherwise, not even the end or a shot.
            briefingAllowed = NetMapProjection.Problem(director.Settings.intelligence, hasTerminal, hasCameras) == null;
            briefingMap = NetMapProjection.Map(current.Layout, current.Loot.Containers.Select(c => c.Tile).ToList(), director.Settings.intelligence,
                hasTerminal, hasCameras, out var refusal);
            Debug.Log(Prefix + (briefingMap != null
                ? FormattableString.Invariant($"map projection on floors={briefingMap.Floors.Count} boxes={briefingMap.Boxes.Count} containers={briefingMap.Containers.Count}")
                : "map projection off: " + refusal));
            if (director.Runtime != null)
                director.Runtime.ObjectiveChanged += OnObjectiveChanged;
            director.MissionFinished += OnMissionFinished;
            for (var id = 1; id <= Units.Count; id++)
            {
                if (!Units.TryGet(id, out var unit) || !unit.TryGetComponent<UnitAttacker>(out var attacker))
                    continue;
                var attackerId = id;   // the loop variable is shared by every iteration's closure
                Action<ShotRecord> handler = record => OnFired(attackerId, record);
                attacker.Fired += handler;
                shotHandlers.Add((attacker, handler));
            }
        }

        void StopBriefing()
        {
            if (mission != null)
            {
                if (mission.Runtime != null)
                    mission.Runtime.ObjectiveChanged -= OnObjectiveChanged;
                mission.MissionFinished -= OnMissionFinished;
            }
            foreach (var (attacker, handler) in shotHandlers)
                if (attacker != null)
                    attacker.Fired -= handler;
            shotHandlers.Clear();
            shots.Clear();
            mission = null;
            briefingMap = null;
            briefingAllowed = false;
            participants.Clear();
            objectivesDirty = false;
        }

        // A resolved basic attack between two registered units; ability shots stay off the wire (Stage 14).
        void OnFired(int attackerId, ShotRecord record)
        {
            if (record.Ability != null || record.Target == null || !record.Target.TryGetComponent<CommandableUnit>(out var target))
                return;
            var targetId = Units.IdOf(target);
            if (targetId != 0)
                shots.Add(new NetShot(attackerId, targetId, record.Outcome == ShotOutcome.Hit));
        }

        // After the visibility pass, so this frame's exposure is in force: each shot to the participants that receive both avatars.
        void FlushShots() =>
            shots.Flush(participants, Visibility.Replicates, (participant, shot) =>
            {
                if (welcomed.TryGetValue(participant, out var target))
                    manager.ServerManager.Broadcast(target, NetMapWire.ToMessage(shot), false);
            });

        List<NetObjective> CurrentObjectives() =>
            mission != null && mission.Runtime != null
                ? NetMapProjection.Objectives(mission.Runtime.Objectives, mission.Settings.intelligence, hasTerminal, hasCameras)
                : new List<NetObjective>();

        static NetMissionResult ResultOf(MissionPhase phase) =>
            phase == MissionPhase.Success ? NetMissionResult.Success : phase == MissionPhase.Failure ? NetMissionResult.Failure : NetMissionResult.None;

        // Only after the welcome and only to a connection that owns units: the map, the known objectives, and the result if over.
        void SendBriefing(NetworkConnection connection)
        {
            if (briefingMap != null)
                manager.ServerManager.Broadcast(connection, NetMapWire.ToMessage(briefingMap), false);
            var objectives = CurrentObjectives();
            if (objectives.Count > 0)
                manager.ServerManager.Broadcast(connection, NetMapWire.ToMessage(objectives), false);
            var result = ResultOf(mission != null ? mission.Phase : MissionPhase.Inactive);
            if (result != NetMissionResult.None)
                manager.ServerManager.Broadcast(connection, NetMapWire.ToMessage(result), false);
        }

        // At most one objectives message per frame, however many objectives changed.
        void FlushObjectives()
        {
            if (!objectivesDirty)
                return;
            objectivesDirty = false;
            var objectives = CurrentObjectives();
            if (objectives.Count == 0)
                return;
            var message = NetMapWire.ToMessage(objectives);
            foreach (var participant in participants)
                if (welcomed.TryGetValue(participant, out var target))
                    manager.ServerManager.Broadcast(target, message, false);
        }

        void OnObjectiveChanged(MissionObjective objective) => objectivesDirty = true;

        void OnMissionFinished(MissionPhase phase)
        {
            objectivesDirty = true;
            var result = ResultOf(phase);
            if (result == NetMissionResult.None)
                return;
            var message = NetMapWire.ToMessage(result);
            foreach (var participant in participants)
                if (welcomed.TryGetValue(participant, out var target))
                    manager.ServerManager.Broadcast(target, message, false);
            Debug.Log(Prefix + FormattableString.Invariant($"mission end {phase} sent to {participants.Count} client(s)"));
        }

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
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
            fixture = null;
            StopBriefing();
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
            try
            {
                Visibility.Update(changes);
            }
            finally
            {
                // A change already written into the view must reach the observers even if the pass threw (fail closed).
                foreach (var change in changes)
                    Publish(change);
            }
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
            FlushShots();
#endif
            Guard.Tick();
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
            FlushObjectives();
#endif
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
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
                if (fixture != null)
                    fixture.Forget(leaving);
                participants.Remove(leaving);
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
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
            // The briefing goes only to a connection that owns at least one operative.
            if (ownedCount > 0 && briefingAllowed && participants.Add(joined))
                SendBriefing(connection);
#endif
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
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
            ApplyFixture(connection, caller, (NetFixtureVerb)message.Verb);
#endif
        }

#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
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
