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
            // Task 8 replaces this with the connection's NetVisibility.
            Gate = new ServerCommandGate(Units, director.InstanceId, NetSight.Everything) { Log = line => Debug.LogWarning(Prefix + line) };
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
