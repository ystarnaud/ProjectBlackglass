using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The play client's start: reads -server, -port and -token (NetClientOptions) and connects. It runs no harness. It logs once
    /// whether the map was accepted, with its counts only (never a coordinate); the two-process transport check reads that line.
    /// </summary>
    public sealed class NetPlayBootstrap : MonoBehaviour
    {
        public const string Prefix = "[NetPlay] ";

        [SerializeField] NetClient client;

        bool mapLogged;

        internal void Initialize(NetClient netClient) => client = netClient;

        void Start()
        {
            var options = NetClientOptions.Parse(Environment.GetCommandLineArgs(), out var warnings);
            foreach (var warning in warnings)
                Debug.LogWarning(Prefix + "option ignored: " + warning);
            if (options.Harness != null)
                Debug.LogWarning(Prefix + "-netharness belongs to the net-test client; the play client ignores it");
            if (string.IsNullOrEmpty(options.Token))
            {
                Debug.LogWarning(Prefix + "no -token was given; not connecting");
                return;
            }
            Debug.Log(Prefix + FormattableString.Invariant($"connecting to {options.Server}:{options.Port}"));
            if (!client.Connect(options.Server, (ushort)options.Port, options.Token))
                Debug.LogWarning(Prefix + "the connection could not be started");
        }

        void Update()
        {
            if (mapLogged || client == null)
                return;
            var record = client.Record;
            if (record.Map != null)
            {
                mapLogged = true;
                Debug.Log(Prefix + FormattableString.Invariant(
                    $"map accepted floors={record.Map.Floors.Count} boxes={record.Map.Boxes.Count} containers={record.Map.Containers.Count}"));
            }
            else if (record.MapProblem != null)
            {
                mapLogged = true;
                Debug.LogWarning(Prefix + "map refused: " + record.MapProblem);
            }
        }
    }
}
