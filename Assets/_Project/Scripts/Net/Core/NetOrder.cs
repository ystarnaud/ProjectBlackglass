namespace Blackglass
{
    /// <summary>The kind of order a unit is carrying out, as replicated to clients (a byte on the wire).</summary>
    public enum NetOrderKind : byte
    {
        None = 0,
        Move = 1,
        Attack = 2,
        Other = 3,
    }

    public static class NetOrder
    {
        public static NetOrderKind KindOf(UnitCommand command)
        {
            if (command == null)
                return NetOrderKind.None;
            if (command is MoveCommand)
                return NetOrderKind.Move;
            if (command is AttackCommand)
                return NetOrderKind.Attack;
            return NetOrderKind.Other;
        }
    }
}
