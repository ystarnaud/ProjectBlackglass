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
                case NetMoveTo move:
                    return Own(connection, move.UnitId, out _, out var moveFailure)
                        ? NotYetCarriedOut()
                        : Reject(moveFailure);
                case NetAttack attack:
                    return Attack(connection, attack);
                case NetStop stop:
                    return Own(connection, stop.UnitId, out _, out var stopFailure)
                        ? NotYetCarriedOut()
                        : Reject(stopFailure);
                default:
                    return Reject(NetRejectReason.Malformed);
            }
        }

        // The checks above have passed; the orders that go through CommandableUnit.Issue are added in Task 6.
        static NetResult NotYetCarriedOut() => Reject(NetRejectReason.OrderRefused);

        // Unknown attacker or target first, then ownership, then side: an attack must name a living hostile of the attacker.
        NetResult Attack(NetConnection connection, NetAttack attack)
        {
            if (!units.TryGet(attack.UnitId, out _) || !units.TryGet(attack.TargetId, out _)
                || !IsAlive(attack.UnitId) || !IsAlive(attack.TargetId))
                return Reject(NetRejectReason.UnknownUnit);
            if (!connection.Owns(attack.UnitId))
                return Reject(NetRejectReason.NotOwner);
            if (units.IsHostile(attack.UnitId) == units.IsHostile(attack.TargetId))
                return Reject(NetRejectReason.WrongSide);
            return NotYetCarriedOut();
        }

        bool IsAlive(int id) => units.TryGet(id, out var unit) && unit.IsAlive;

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
