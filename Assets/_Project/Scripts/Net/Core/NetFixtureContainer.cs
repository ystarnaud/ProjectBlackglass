#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Blackglass
{
    /// <summary>
    /// The development fixture's one container (spec section 3.9). Its fixed contents exist only on the server until a connection
    /// searches it, and then only for that connection. The real loot containers and inventory are not on the server; this narrow
    /// stand-in proves the replication rule only. The items are labelled as fixtures and name no catalogue item.
    /// </summary>
    public sealed class NetFixtureContainer
    {
        public const int Id = 1;

        static readonly IReadOnlyList<NetItem> Contents = new ReadOnlyCollection<NetItem>(new[]
        {
            new NetItem("fixture-medkit", 2, "fixture-item-0001"),
            new NetItem("fixture-rifle-ammo", 30, "fixture-item-0002"),
        });

        readonly HashSet<NetConnection> searched = new HashSet<NetConnection>();

        public bool IsSearchedBy(NetConnection connection) => connection != null && searched.Contains(connection);

        /// <summary>The contents for a connection that searched; null for any other.</summary>
        public IReadOnlyList<NetItem> ContentsFor(NetConnection connection) => IsSearchedBy(connection) ? Contents : null;

        public void Search(NetConnection connection)
        {
            if (connection != null)
                searched.Add(connection);
        }

        public void Forget(NetConnection connection)
        {
            if (connection != null)
                searched.Remove(connection);
        }
    }
}
#endif
