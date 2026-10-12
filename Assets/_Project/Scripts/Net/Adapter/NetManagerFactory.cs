using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Server;
using FishNet.Managing.Transporting;
using FishNet.Transporting.UTP;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Builds FishNet's NetworkManager at runtime (decision 046), so nothing of FishNet exists until -net (the server) or a
    /// connection (the client) asks for it. The manager is created inactive and activated last: NetworkManager.Awake then builds
    /// its other managers and is ready in the same frame. The ServerManager is added first so headless auto-start can be turned
    /// off before NetworkManager.Start would use it, and so the frame cap is set before the server starts.
    /// </summary>
    internal static class NetManagerFactory
    {
        /// <summary>The existing manager (host mode, tests) or a new one. serverFrameRate 0 leaves the frame rate alone.</summary>
        internal static NetworkManager Acquire(PrefabObjects prefabs, ushort serverFrameRate, out UnityTransport transport)
        {
            if (NetworkManager.Instances.Count > 0)
            {
                var existing = NetworkManager.Instances[0];
                transport = existing.TransportManager.Transport as UnityTransport;
                return existing;
            }

            var host = new GameObject("NetworkManager");
            host.SetActive(false);
            transport = host.AddComponent<UnityTransport>();
            host.AddComponent<TransportManager>().Transport = transport;
            var server = host.AddComponent<ServerManager>();
            server.SetStartOnHeadless(false);
            if (serverFrameRate > 0)
                server.SetFrameRate(serverFrameRate);
#if UNITY_EDITOR
            // In the Editor, adding the component runs NetworkManager.OnValidate, which logs an error because the prefab collection
            // is not assigned yet (it cannot be, before the component exists). Player builds never call it. Only this call is silenced.
            var loggingWas = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            NetworkManager manager;
            try
            {
                manager = host.AddComponent<NetworkManager>();
            }
            finally
            {
                Debug.unityLogger.logEnabled = loggingWas;
            }
#else
            var manager = host.AddComponent<NetworkManager>();
#endif
            manager.SpawnablePrefabs = prefabs;
            host.SetActive(true);
            return manager;
        }

        /// <summary>Destroys every manager at once (tests, and NetServer.Stop for a manager it created).</summary>
        internal static void DestroyAll()
        {
            while (NetworkManager.Instances.Count > 0)
                Object.DestroyImmediate(NetworkManager.Instances[0].gameObject);
        }
    }
}
