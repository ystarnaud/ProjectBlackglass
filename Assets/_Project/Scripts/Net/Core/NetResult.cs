namespace Blackglass
{
    /// <summary>Why the server gate refused a request (spec section 3.2).</summary>
    public enum NetRejectReason
    {
        /// <summary>Not a refusal.</summary>
        None,
        /// <summary>No request, no attempt id, an unknown request type, or a non-finite number.</summary>
        Malformed,
        WrongAttempt,
        /// <summary>The sequence is not greater than the connection's last accepted one.</summary>
        StaleSequence,
        DuplicateCommand,
        /// <summary>A net id that maps to nothing, or to a dead or destroyed unit.</summary>
        UnknownUnit,
        NotOwner,
        /// <summary>An attack on a unit of the attacker's own side.</summary>
        WrongSide,
        /// <summary>The request was valid but the existing command path would not carry it out (CommandableUnit.Issue returned false).</summary>
        OrderRefused,
        /// <summary>The participant's start is not committed by the backend yet (the start hold, Stage 13; appended, nothing renumbered).</summary>
        NotStarted,
    }

    public readonly struct NetResult
    {
        NetResult(NetRejectReason reason) => Reason = reason;

        public static NetResult Accepted => new NetResult(NetRejectReason.None);

        public static NetResult Rejected(NetRejectReason reason) => new NetResult(reason);

        public NetRejectReason Reason { get; }

        public bool IsAccepted => Reason == NetRejectReason.None;
    }
}
