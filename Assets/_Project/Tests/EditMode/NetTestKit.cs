using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>Builds plain units for the networking tests: a GameObject with Health and CommandableUnit, no NavMesh.</summary>
    internal static class NetTestKit
    {
        public static CommandableUnit MakeUnit(List<GameObject> hosts, string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            host.AddComponent<Health>();
            return host.AddComponent<CommandableUnit>();
        }

        public static void DestroyAll(List<GameObject> hosts)
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }
    }
}
