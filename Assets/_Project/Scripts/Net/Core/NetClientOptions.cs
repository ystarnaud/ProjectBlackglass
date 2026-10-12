using System;
using System.Collections.Generic;
using System.Globalization;

namespace Blackglass
{
    /// <summary>
    /// The net-test client's command line (networking experiment, spec section 3.5): -server, -port, -token and -netharness
    /// with one of the four harness roles. Pure: parsed from the argument list. Unknown arguments (Unity's own) are ignored; a
    /// missing, out-of-range or unknown value keeps the default and adds a warning that never contains the token.
    /// </summary>
    public sealed class NetClientOptions
    {
        public const string DefaultServer = "127.0.0.1";
        public const int DefaultPort = 7770;
        static readonly string[] Roles = { "a", "b", "ticket", "badtoken" };

        public string Server { get; private set; } = DefaultServer;
        public int Port { get; private set; } = DefaultPort;
        public string Token { get; private set; }
        /// <summary>The harness role to run, or null for the interactive client.</summary>
        public string Harness { get; private set; }

        public static NetClientOptions Parse(IReadOnlyList<string> args, out List<string> warnings)
        {
            var options = new NetClientOptions();
            warnings = new List<string>();
            if (args == null)
                return options;
            for (var i = 0; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "-server":
                        if (TryText(args, ref i, warnings, out var server))
                            options.Server = server;
                        break;
                    case "-token":
                        if (TryText(args, ref i, warnings, out var token))
                            options.Token = token;
                        break;
                    case "-port":
                        if (TryText(args, ref i, warnings, out var portText))
                        {
                            if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port >= 1024 && port <= 65535)
                                options.Port = port;
                            else
                                warnings.Add("-port must be an integer from 1024 to 65535; keeping " + options.Port.ToString(CultureInfo.InvariantCulture));
                        }
                        break;
                    case "-netharness":
                        if (TryText(args, ref i, warnings, out var role))
                        {
                            if (Array.IndexOf(Roles, role) >= 0)
                                options.Harness = role;
                            else
                                warnings.Add("-netharness must be one of a, b, ticket, badtoken");
                        }
                        break;
                }
            }
            return options;
        }

        // Reads the text after args[i]. A missing value, or another flag, warns and leaves the flag for the loop.
        static bool TryText(IReadOnlyList<string> args, ref int i, List<string> warnings, out string value)
        {
            var name = args[i];
            value = null;
            if (i + 1 >= args.Count || args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                warnings.Add(name + " has no value; keeping the default");
                return false;
            }
            i++;
            value = args[i];
            return true;
        }
    }
}
