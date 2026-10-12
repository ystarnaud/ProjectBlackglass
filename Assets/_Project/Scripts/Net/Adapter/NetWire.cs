namespace Blackglass
{
    /// <summary>Conversion between the request message on the wire and the library-free NetRequest the gate understands.</summary>
    public static class NetWire
    {
        public const byte MoveIntent = 1;
        public const byte MoveTo = 2;
        public const byte Attack = 3;
        public const byte Stop = 4;

        /// <summary>Null for an unknown kind, and for an append flag on anything but a move-to (spec section 3.3, check 1).</summary>
        public static NetRequest ToRequest(NetRequestMessage message)
        {
            switch (message.Kind)
            {
                case MoveIntent:
                    return message.Append ? null : new NetMoveIntent(message.UnitId, message.Vector);
                case MoveTo:
                    return new NetMoveTo(message.UnitId, message.Vector, message.Append);
                case Attack:
                    return message.Append ? null : new NetAttack(message.UnitId, message.TargetId);
                case Stop:
                    return message.Append ? null : new NetStop(message.UnitId);
                default:
                    return null;
            }
        }

        public static NetEnvelope ToEnvelope(NetRequestMessage message) =>
            new NetEnvelope(message.AttemptId, message.Sequence, message.CommandId, ToRequest(message));

        public static NetRequestMessage ToMessage(string attemptId, uint sequence, uint commandId, NetRequest request)
        {
            var message = new NetRequestMessage { AttemptId = attemptId, Sequence = sequence, CommandId = commandId };
            switch (request)
            {
                case NetMoveIntent intent:
                    message.Kind = MoveIntent;
                    message.UnitId = intent.UnitId;
                    message.Vector = intent.Direction;
                    break;
                case NetMoveTo move:
                    message.Kind = MoveTo;
                    message.UnitId = move.UnitId;
                    message.Vector = move.Destination;
                    message.Append = move.Append;
                    break;
                case NetAttack attack:
                    message.Kind = Attack;
                    message.UnitId = attack.UnitId;
                    message.TargetId = attack.TargetId;
                    break;
                case NetStop stop:
                    message.Kind = Stop;
                    message.UnitId = stop.UnitId;
                    break;
            }
            return message;
        }
    }
}
