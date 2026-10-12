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
