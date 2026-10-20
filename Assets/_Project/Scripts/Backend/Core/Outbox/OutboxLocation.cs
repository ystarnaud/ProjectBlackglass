namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The only production path rule (owner ruling D6): the mission writes into the directory systemd created for its unit
    /// (StateDirectory=blackglass-outbox/%i) and passed as $STATE_DIRECTORY, and nothing else. The value must be exactly
    /// "&lt;requiredParent&gt;/&lt;attemptId&gt;": absolute, a single path (systemd joins several with ':'), normalized (no "//",
    /// ".", "..", trailing '/', or NUL), at most 4096 bytes, directly under the parent, its last segment the attempt's canonical
    /// UUIDv7.
    /// </summary>
    public static class OutboxLocation
    {
        public const string StateDirectoryVariable = "STATE_DIRECTORY";
        public const int MaxPathBytes = 4096;

        public static bool TryResolve(string stateDirectory, string requiredParent, string attemptId, out string path, out string refusal)
        {
            path = null;
            refusal = Refusal(stateDirectory, requiredParent, attemptId);
            if (refusal != null) return false;
            path = stateDirectory;
            return true;
        }

        static string Refusal(string value, string parent, string attempt)
        {
            if (string.IsNullOrEmpty(value)) return "missing";
            if (StrictJsonReader.Utf8ByteCount(value) > MaxPathBytes) return "too_long";
            if (value[0] != '/') return "not_absolute";
            if (value.IndexOf(':') >= 0) return "multiple_paths";
            if (value.IndexOf('\0') >= 0 || value.EndsWith("/", System.StringComparison.Ordinal)) return "not_normalized";
            foreach (var segment in value.Substring(1).Split('/'))
                if (segment.Length == 0 || segment == "." || segment == "..") return "not_normalized";
            if (string.IsNullOrEmpty(parent) || parent[0] != '/' || parent.EndsWith("/", System.StringComparison.Ordinal)) return "outside_parent";
            if (!value.StartsWith(parent + "/", System.StringComparison.Ordinal)) return "outside_parent";
            var last = value.Substring(parent.Length + 1);
            if (last.IndexOf('/') >= 0 || !CanonicalIds.IsAttemptId(attempt) || last != attempt) return "wrong_attempt";
            return null;
        }
    }
}
