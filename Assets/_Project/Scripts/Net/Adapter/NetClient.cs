using System;
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
    /// token, and then sends numbered requests and collects the server's answers. Holds no game rule; everything it decodes is kept in Record, which the harness reads; the avatars the server
    /// replicates are read by NetClientView and the harness (NetUnitState).
    /// </summary>
    public sealed class NetClient : MonoBehaviour
    {
        const int ResultsKept = 256;

        [SerializeField] PrefabObjects spawnablePrefabs;

        readonly Dictionary<uint, byte> results = new Dictionary<uint, byte>();
        readonly Queue<uint> resultOrder = new Queue<uint>();
        readonly NetClientRecord record = new NetClientRecord();
        NetworkManager manager;
        UnityTransport transport;
        string token;
        bool createdManager;
        uint sequence;
        uint commandId;

        internal void Initialize(PrefabObjects prefabs) => spawnablePrefabs = prefabs;

        public NetClientRecord Record => record;
        static double Now => Time.realtimeSinceStartupAsDouble;

        public NetClientState State { get; private set; }
        public NetRefusal Refusal { get; private set; }
        public string AttemptId { get; private set; }
        public int FirstOwnedId { get; private set; }
        public int OwnedCount { get; private set; }
        public long RoundTripMs => manager != null && manager.TimeManager != null ? manager.TimeManager.RoundTripTime : 0L;
        /// <summary>The reason of the last refused request (None before any) and the command it answered; the overlay shows them.</summary>
        public NetRejectReason LastReject { get; private set; }
        public uint LastRejectCommand { get; private set; }

        /// <summary>A relayed shot (decision 052): both units are avatars this client was sent. Presentation reads it; nothing else.</summary>
        public event Action<NetShot> ShotReceived;

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
            record.Clear();
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
            client.RegisterBroadcast<NetKnowledgeMessage>(OnKnowledge);
            client.RegisterBroadcast<NetContainerMessage>(OnContainer);
            client.RegisterBroadcast<NetFixtureStepMessage>(OnStep);
            client.RegisterBroadcast<NetMapMessage>(OnMap);
            client.RegisterBroadcast<NetObjectivesMessage>(OnObjectives);
            client.RegisterBroadcast<NetMissionEndMessage>(OnMissionEnd);
            client.RegisterBroadcast<NetShotMessage>(OnShot);
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
                    client.UnregisterBroadcast<NetKnowledgeMessage>(OnKnowledge);
                    client.UnregisterBroadcast<NetContainerMessage>(OnContainer);
                    client.UnregisterBroadcast<NetFixtureStepMessage>(OnStep);
                    client.UnregisterBroadcast<NetMapMessage>(OnMap);
                    client.UnregisterBroadcast<NetObjectivesMessage>(OnObjectives);
                    client.UnregisterBroadcast<NetMissionEndMessage>(OnMissionEnd);
                    client.UnregisterBroadcast<NetShotMessage>(OnShot);
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
            record.Note(Now, nameof(NetWelcomeMessage));
            AttemptId = message.AttemptId;
            FirstOwnedId = message.FirstOwnedId;
            OwnedCount = message.OwnedCount;
            State = NetClientState.Ready;
        }

        void OnRefused(NetRefusedMessage message, Channel channel)
        {
            record.Note(Now, nameof(NetRefusedMessage));
            Refusal = (NetRefusal)message.Reason;
            State = NetClientState.Refused;
        }

        void OnResult(NetResultMessage message, Channel channel)
        {
            record.Note(Now, nameof(NetResultMessage));
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

        // The briefing (decision 052): each payload is decoded and validated; an invalid one is recorded with its reason and
        // nothing of it is kept or drawn.
        void OnMap(NetMapMessage message, Channel channel)
        {
            var map = NetMapWire.FromMessage(message, out var problem);
            record.ReceiveMap(Now, map, problem);
            if (map == null)
                Debug.LogWarning("[NetClient] map refused: " + problem);
        }

        void OnObjectives(NetObjectivesMessage message, Channel channel)
        {
            var list = NetMapWire.FromMessage(message, out var problem);
            record.ReceiveObjectives(Now, list, problem);
            if (list == null)
                Debug.LogWarning("[NetClient] objectives refused: " + problem);
        }

        void OnMissionEnd(NetMissionEndMessage message, Channel channel) => record.ReceiveEnd(Now, (byte)NetMapWire.FromMessage(message));

        void OnShot(NetShotMessage message, Channel channel)
        {
            record.ReceiveShot(Now);
            if (NetMapWire.FromMessage(message, out var shot))
                ShotReceived?.Invoke(shot);
        }
    }
}
