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
