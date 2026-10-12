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
