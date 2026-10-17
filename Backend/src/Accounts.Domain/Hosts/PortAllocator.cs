namespace Blackglass.Accounts.Domain.Hosts;

/// <summary>A host's mission ports are [portBase, portBase + capacity); each live slot holds one.</summary>
public static class PortAllocator
{
    /// <summary>The lowest port in the host's range that no live slot uses; null when none is free.</summary>
    public static int? Lowest(int portBase, int capacity, IReadOnlySet<int> used)
    {
        for (var port = portBase; port < portBase + capacity; port++)
            if (!used.Contains(port)) return port;
        return null;
    }
}
