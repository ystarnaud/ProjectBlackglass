using System;
using System.Collections.Generic;
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
        /// <summary>A held move intent not refreshed for this long is cleared, so a client that drops cannot leave a unit walking.</summary>
        public const double IntentExpirySeconds = 0.5;

        /// <summary>The most orders a client may queue behind a unit's current one (appended moves), so one client cannot grow server memory.</summary>
        public const int MaxPendingOrders = 16;

        readonly NetUnits units;
        readonly string attemptId;
        readonly Dictionary<int, double> intentAt = new Dictionary<int, double>();
        readonly List<int> expired = new List<int>();

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

        /// <summary>Clears the move intent of every unit whose client has not refreshed it within IntentExpirySeconds. Call once per frame.</summary>
        public void Tick(double now)
        {
            expired.Clear();
            foreach (var pair in intentAt)
            {
                if (now - pair.Value > IntentExpirySeconds)
                    expired.Add(pair.Key);
            }
            foreach (var id in expired)
            {
                intentAt.Remove(id);
                if (units.TryGet(id, out var unit))
                    unit.SetMoveIntent(Vector3.zero);
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
                        ? Steer(steered, intent, now)
                        : Reject(steerFailure);
                case NetMoveTo move:
                    return Own(connection, move.UnitId, out var mover, out var moveFailure)
                        ? Move(mover, move)
                        : Reject(moveFailure);
                case NetAttack attack:
                    return Attack(connection, attack);
                case NetStop stop:
                    return Own(connection, stop.UnitId, out var stopped, out var stopFailure)
                        ? Order(stopped, new StopCommand(), IssueMode.Replace)
                        : Reject(stopFailure);
                default:
                    return Reject(NetRejectReason.Malformed);
            }
        }

        // An appended move queues behind the unit's orders; a full queue is refused here, so the cap lives in the gate and
        // CommandableUnit stays unchanged for the single-player game.
        static NetResult Move(CommandableUnit unit, NetMoveTo move)
        {
            if (move.Append && unit.PendingCommands.Count >= MaxPendingOrders)
                return Reject(NetRejectReason.OrderRefused);
            return Order(unit, new MoveCommand(move.Destination), move.Append ? IssueMode.Append : IssueMode.Replace);
        }

        // The existing command path decides whether the order can be carried out; false means OrderRefused.
        static NetResult Order(CommandableUnit unit, UnitCommand command, IssueMode mode) =>
            unit.Issue(command, mode) ? NetResult.Accepted : Reject(NetRejectReason.OrderRefused);

        // Unknown attacker or target first, then ownership, then side: an attack must name a living hostile of the attacker.
        NetResult Attack(NetConnection connection, NetAttack attack)
        {
            if (!units.TryGet(attack.UnitId, out var attacker) || !attacker.IsAlive
                || !units.TryGet(attack.TargetId, out var target) || !target.IsAlive)
                return Reject(NetRejectReason.UnknownUnit);
            if (!connection.Owns(attack.UnitId))
                return Reject(NetRejectReason.NotOwner);
            if (units.IsHostile(attack.UnitId) == units.IsHostile(attack.TargetId))
                return Reject(NetRejectReason.WrongSide);
            if (!target.TryGetComponent<Health>(out var health))
                return Reject(NetRejectReason.UnknownUnit);
            return Order(attacker, new AttackCommand(health), IssueMode.Replace);
        }

        NetResult Steer(CommandableUnit unit, NetMoveIntent intent, double now)
        {
            unit.SetMoveIntent(intent.Direction);
            var id = intent.UnitId;
            if (unit.MoveIntent == Vector3.zero)
                intentAt.Remove(id);
            else
                intentAt[id] = now;
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
