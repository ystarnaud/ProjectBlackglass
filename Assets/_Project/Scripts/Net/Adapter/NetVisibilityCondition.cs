using FishNet.Connection;
using FishNet.Observing;

namespace Blackglass
{
    /// <summary>
    /// The avatar's only observer condition (hidden information slice, spec section 3.3). FishNet asks it, on each observer
    /// rebuild, whether a connection may receive this avatar; it answers what the server's NetVisibility decided, through the
    /// avatar's NetUnitState, and "no" whenever anything is missing. A Normal condition: it is checked only when the server
    /// rebuilds observers, which it does on every transition and when a client's hello is accepted.
    /// </summary>
    public sealed class NetVisibilityCondition : ObserverCondition
    {
        NetUnitState state;

        public override bool ConditionMet(NetworkConnection connection, bool currentlyAdded, out bool notProcessed)
        {
            notProcessed = false;
            if (state == null && NetworkObject != null)
                state = NetworkObject.GetComponent<NetUnitState>();
            return state != null && state.IsVisibleTo(connection);
        }

        public override ObserverConditionType GetConditionType() => ObserverConditionType.Normal;
    }
}
