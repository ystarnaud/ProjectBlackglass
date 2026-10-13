namespace Blackglass
{
    /// <summary>
    /// What the gate may admit exists for a connection (hidden information slice, spec section 3.4). A hostile the connection
    /// cannot see is answered UnknownUnit, exactly like an id that does not exist, so no answer reveals it.
    /// </summary>
    public interface INetSight
    {
        bool CanSee(NetConnection connection, int unitId);
    }

    public static class NetSight
    {
        sealed class All : INetSight
        {
            public bool CanSee(NetConnection connection, int unitId) => true;
        }

        /// <summary>Sees every unit: for tests of the gate that are not about hidden information. The server never uses it.</summary>
        public static readonly INetSight Everything = new All();
    }
}
