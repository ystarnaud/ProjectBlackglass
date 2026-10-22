using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Reads the command line, connects the client, and starts either the harness (-netharness) or the interactive controls.</summary>
    public sealed class NetClientBootstrap : MonoBehaviour
    {
        public const string Prefix = "[NetClient] ";

        [SerializeField] NetClient client;
        [SerializeField] NetHarness harness;
        [SerializeField] NetTestControls controls;

        internal Action<int> Exit = code => Application.Quit(code);

        internal void Initialize(NetClient netClient, NetHarness netHarness, NetTestControls netControls)
        {
            client = netClient;
            harness = netHarness;
            controls = netControls;
        }

        void Start()
        {
            var options = NetClientOptions.Parse(Environment.GetCommandLineArgs(), out var warnings);
            foreach (var warning in warnings)
                Debug.LogWarning(Prefix + "option ignored: " + warning);
            if (string.IsNullOrEmpty(options.Token))
            {
                Debug.LogWarning(Prefix + "no -token was given; not connecting");
                if (options.Harness != null)
                    Exit(NetHarness.ExitNoConnection);
                return;
            }
            Debug.Log(Prefix + FormattableString.Invariant($"connecting to {options.Server}:{options.Port}") +
                (options.Harness != null ? " as harness role " + options.Harness : string.Empty));
            if (!client.Connect(options.Server, (ushort)options.Port, options.Token))
            {
                Debug.LogWarning(Prefix + "the connection could not be started");
                if (options.Harness != null)
                    Exit(NetHarness.ExitNoConnection);
                return;
            }
            if (options.Harness != null)
            {
                controls.enabled = false;
                harness.Begin(options.Harness, options.AssistHostile, options.AssistExtraction);
            }
        }
    }
}
