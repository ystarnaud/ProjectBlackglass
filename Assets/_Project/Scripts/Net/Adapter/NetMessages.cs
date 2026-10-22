using FishNet.Broadcast;
using UnityEngine;

namespace Blackglass
{
    // The messages of the networking experiment (decision 046). Plain public fields so FishNet can serialise them;
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

    // The visual network client's briefing (decision 052). Sent only by Editor and fixture servers, only for a Full/All
    // mission, only to a welcomed connection that owns units. Each message opens with its format version (NetMap.Format).

    /// <summary>One floor rectangle of the public map, in tiles.</summary>
    public struct NetRectWire
    {
        public short X;
        public short Y;
        public short Width;
        public short Height;
    }

    /// <summary>One wall or obstacle box of the public map: Kind is MissionBoxKind + 1 (0 is never valid); Rise in metres.</summary>
    public struct NetBoxWire
    {
        public byte Kind;
        public short X;
        public short Y;
        public short Width;
        public short Height;
        public float Rise;
    }

    /// <summary>One tile of the public map (a closed container: its place only).</summary>
    public struct NetTileWire
    {
        public short X;
        public short Y;
    }

    /// <summary>Server to a connection that owns units, once after its welcome: the public map. Nothing else of the generation travels.</summary>
    public struct NetMapMessage : IBroadcast
    {
        public byte Format;
        public short Width;
        public short Height;
        public NetRectWire[] Floors;
        public NetBoxWire[] Boxes;
        public NetTileWire[] Containers;
    }

    /// <summary>One known objective: Kind is a NetObjectiveKind, State an ObjectiveState; Point and Radius for extraction only.</summary>
    public struct NetObjectiveWire
    {
        public byte Kind;
        public byte State;
        public string Title;
        public bool HasPoint;
        public Vector3 Point;
        public float Radius;
    }

    /// <summary>Server to a connection that owns units: the whole known-objective list, at welcome and after every change.</summary>
    public struct NetObjectivesMessage : IBroadcast
    {
        public byte Format;
        public NetObjectiveWire[] Objectives;
    }

    /// <summary>Server to a connection that owns units: the mission ended (a NetMissionResult value, 1 or 2).</summary>
    public struct NetMissionEndMessage : IBroadcast
    {
        public byte Format;
        public byte Result;
    }

    /// <summary>Server to a connection that receives both units' avatars: one resolved basic attack (Hit false: cover turned it away).</summary>
    public struct NetShotMessage : IBroadcast
    {
        public byte Format;
        public int AttackerId;
        public int TargetId;
        public bool Hit;
    }
}
