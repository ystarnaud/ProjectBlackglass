using System;
using System.Collections.Generic;
using System.Globalization;

namespace Blackglass
{
    /// <summary>
    /// The dedicated server's command-line options (Checkpoint A, decision 045). Pure: parsed from the argument list, never
    /// from Unity. Unknown arguments (Unity's own -batchmode, -nographics, -logFile x) are ignored; a missing, non-numeric or
    /// out-of-range value keeps the default and adds a warning. Names are exact and case-sensitive; a repeated argument's
    /// last value wins.
    /// </summary>
    public sealed class ServerOptions
    {
        public const int DefaultFrameRate = 60;
        public const int DefaultDiagnosticTimeoutSeconds = 120;
        public const int DefaultStartupTimeoutSeconds = 60;
        public const int DefaultHeartbeatSeconds = 60;
        public const int DefaultNetPort = 7770;
        /// <summary>The self-test's state directory parent: never the real outbox's.</summary>
        public const string DefaultSelftestParent = "/var/lib/blackglass-selftest";

        /// <summary>The seed to generate; null uses the scene's fixture seed.</summary>
        public int? Seed { get; private set; }
        public int FrameRate { get; private set; } = DefaultFrameRate;
        public bool Diagnostic { get; private set; }
        public int DiagnosticTimeoutSeconds { get; private set; } = DefaultDiagnosticTimeoutSeconds;
        public int StartupTimeoutSeconds { get; private set; } = DefaultStartupTimeoutSeconds;
        /// <summary>Seconds between heartbeat lines; 0 turns the heartbeat off.</summary>
        public int HeartbeatSeconds { get; private set; } = DefaultHeartbeatSeconds;
        /// <summary>Start the networking adapter (the networking experiment, decision 046). Off by default.</summary>
        public bool Net { get; private set; }
        public int NetPort { get; private set; } = DefaultNetPort;
        /// <summary>Path of the development token file on the server; null when none was given.</summary>
        public string NetTokensPath { get; private set; }
        /// <summary>Apply the diagnostic's controls (hostile AI off, friendlies' autonomy off, forced hits) so a networked run is repeatable.</summary>
        public bool NetQuiet { get; private set; }
        /// <summary>-netFixture: the development fixture drives visibility (Editor and fixture builds only; needs -netQuiet).</summary>
        public bool NetFixture { get; private set; }
        /// <summary>-outboxSelftest publish|hold|resume: run only the Stage 11 outbox self-test (no mission, no network); null
        /// otherwise. Empty when the self-test was asked for without a phase (it then fails with exit 3).</summary>
        public string OutboxSelftest { get; private set; }
        public string SelftestParent { get; private set; } = DefaultSelftestParent;
        /// <summary>The canonical UUIDv7 naming the self-test's state directory ($STATE_DIRECTORY must be exactly parent/id).</summary>
        public string SelftestId { get; private set; }
        /// <summary>-attemptFixture &lt;path&gt;: start the authoritative attempt of this attempt-v1 fixture (Editor and fixture builds only; Stage 12).</summary>
        public string AttemptFixture { get; private set; }

        public static ServerOptions Default => new ServerOptions();

        public static ServerOptions Parse(IReadOnlyList<string> args, out List<string> warnings)
        {
            var options = new ServerOptions();
            var selftestOptionSeen = false;
            warnings = new List<string>();
            if (args == null)
                return options;
            for (var i = 0; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "-diagnostic":
                        options.Diagnostic = true;
                        break;
                    case "-seed":
                        if (TryValue(args, ref i, int.MinValue, int.MaxValue, warnings, out var seed))
                            options.Seed = seed;
                        break;
                    case "-fps":
                        if (TryValue(args, ref i, 1, 1000, warnings, out var fps))
                            options.FrameRate = fps;
                        break;
                    case "-diagnosticTimeout":
                        if (TryValue(args, ref i, 5, 3600, warnings, out var diagnostic))
                            options.DiagnosticTimeoutSeconds = diagnostic;
                        break;
                    case "-startupTimeout":
                        if (TryValue(args, ref i, 5, 3600, warnings, out var startup))
                            options.StartupTimeoutSeconds = startup;
                        break;
                    case "-net":
                        options.Net = true;
                        break;
                    case "-netQuiet":
                        options.NetQuiet = true;
                        break;
                    case "-netFixture":
                        options.NetFixture = true;
                        break;
                    case "-netPort":
                        if (TryValue(args, ref i, 1024, 65535, warnings, out var netPort))
                            options.NetPort = netPort;
                        break;
                    case "-netTokens":
                        if (TryText(args, ref i, warnings, out var tokens))
                            options.NetTokensPath = tokens;
                        break;
                    case "-outboxSelftest":
                        // A missing phase still selects the self-test, which then fails: never a mission by accident.
                        options.OutboxSelftest = TryText(args, ref i, warnings, out var phase) ? phase : string.Empty;
                        break;
                    case "-selftestParent":
                        selftestOptionSeen = true;
                        if (TryText(args, ref i, warnings, out var parent))
                            options.SelftestParent = parent;
                        break;
                    case "-selftestId":
                        selftestOptionSeen = true;
                        if (TryText(args, ref i, warnings, out var selftestId))
                            options.SelftestId = selftestId;
                        break;
                    case "-attemptFixture":
                        if (TryText(args, ref i, warnings, out var fixture))
                            options.AttemptFixture = fixture;
                        break;
                    case "-heartbeat":
                        if (TryValue(args, ref i, 0, 3600, warnings, out var heartbeat))
                        {
                            if (heartbeat != 0 && heartbeat < 5)
                                warnings.Add(FormattableString.Invariant($"-heartbeat {heartbeat} is out of range (0 or 5..3600); keeping {options.HeartbeatSeconds}"));
                            else
                                options.HeartbeatSeconds = heartbeat;
                        }
                        break;
                }
            }
            // A self-test option without -outboxSelftest is a mistyped self-test run: it fails, it never generates a mission.
            if (options.OutboxSelftest == null && selftestOptionSeen)
                options.OutboxSelftest = string.Empty;
            return options;
        }

        // Reads the value after args[i]. A next token that is an integer is the value (so "-seed -5" is a seed); another
        // flag is left alone for the loop to read, and a missing value warns.
        static bool TryValue(IReadOnlyList<string> args, ref int i, int min, int max, List<string> warnings, out int value)
        {
            var name = args[i];
            value = 0;
            if (i + 1 >= args.Count)
            {
                warnings.Add($"{name} has no value; keeping the default");
                return false;
            }
            var text = args[i + 1];
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                if (text.StartsWith("-", StringComparison.Ordinal))
                {
                    warnings.Add($"{name} has no value (next is {text}); keeping the default");
                    return false;
                }
                i++;
                warnings.Add($"{name} {text} is not an integer; keeping the default");
                return false;
            }
            i++;
            if (value < min || value > max)
            {
                warnings.Add(FormattableString.Invariant($"{name} {value} is out of range ({min}..{max}); keeping the default"));
                return false;
            }
            return true;
        }

        // Reads the text after args[i]. A missing value, or another flag, warns and leaves the flag for the loop.
        static bool TryText(IReadOnlyList<string> args, ref int i, List<string> warnings, out string value)
        {
            var name = args[i];
            value = null;
            if (i + 1 >= args.Count)
            {
                warnings.Add($"{name} has no value; keeping the default");
                return false;
            }
            var text = args[i + 1];
            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                warnings.Add($"{name} has no value (next is {text}); keeping the default");
                return false;
            }
            i++;
            value = text;
            return true;
        }

        /// <summary>A copy with the given values replaced (tests, and the bootstrap's diagnostic hand-off).</summary>
        internal ServerOptions With(int? seed = null, bool? diagnostic = null, int? diagnosticTimeoutSeconds = null,
            bool? net = null, int? netPort = null, string netTokensPath = null, bool? netQuiet = null, bool? netFixture = null)
        {
            var copy = (ServerOptions)MemberwiseClone();
            if (seed.HasValue)
                copy.Seed = seed;
            if (diagnostic.HasValue)
                copy.Diagnostic = diagnostic.Value;
            if (diagnosticTimeoutSeconds.HasValue)
                copy.DiagnosticTimeoutSeconds = diagnosticTimeoutSeconds.Value;
            if (net.HasValue)
                copy.Net = net.Value;
            if (netPort.HasValue)
                copy.NetPort = netPort.Value;
            if (netTokensPath != null)
                copy.NetTokensPath = netTokensPath;
            if (netQuiet.HasValue)
                copy.NetQuiet = netQuiet.Value;
            if (netFixture.HasValue)
                copy.NetFixture = netFixture.Value;
            return copy;
        }

        /// <summary>One log line; `fixtureSeed` is the scene's seed, shown when there is no override.</summary>
        public string Describe(int fixtureSeed) =>
            FormattableString.Invariant($"seed={Seed ?? fixtureSeed}({(Seed.HasValue ? "override" : "fixture")}) fps={FrameRate} diagnostic={(Diagnostic ? "yes" : "no")} ") +
            FormattableString.Invariant($"diagnosticTimeout={DiagnosticTimeoutSeconds}s startupTimeout={StartupTimeoutSeconds}s heartbeat={(HeartbeatSeconds > 0 ? HeartbeatSeconds + "s" : "off")} ") +
            (Net ? FormattableString.Invariant($"net=yes port={NetPort} quiet={(NetQuiet ? "yes" : "no")}") + (NetFixture ? " fixture=yes" : string.Empty) : "net=no") +
            (AttemptFixture != null ? " attempt=fixture" : string.Empty);
    }
}
