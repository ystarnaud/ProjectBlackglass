namespace Blackglass.Backend.Adapter
{
    /// <summary>
    /// Where the mission process reaches the backend (Stage 13): plain HTTP to a loopback address only, 127.0.0.1 or [::1], with an
    /// explicit port and nothing else (no path, query, fragment or user info). On the mission VM that is the SSH tunnel's local end
    /// (127.0.0.1:5081), which is the transport security; a name such as "localhost" is refused because resolving it proves nothing.
    /// </summary>
    public sealed class BackendEndpoint
    {
        const string Scheme = "http://";

        BackendEndpoint(string baseUrl) => BaseUrl = baseUrl;

        /// <summary>"http://127.0.0.1:5081": no trailing slash; routes are appended to it.</summary>
        public string BaseUrl { get; }

        /// <summary>False with one code: missing, not_http, has_userinfo, has_query, has_path, not_loopback or bad_port.</summary>
        public static bool TryParse(string text, out BackendEndpoint endpoint, out string refusal)
        {
            endpoint = null;
            refusal = Refusal(text);
            if (refusal != null) return false;
            endpoint = new BackendEndpoint(text);
            return true;
        }

        static string Refusal(string text)
        {
            if (string.IsNullOrEmpty(text)) return "missing";
            if (!text.StartsWith(Scheme, System.StringComparison.Ordinal)) return "not_http";
            var authority = text.Substring(Scheme.Length);
            if (authority.IndexOf('@') >= 0) return "has_userinfo";
            if (authority.IndexOf('?') >= 0 || authority.IndexOf('#') >= 0) return "has_query";
            if (authority.IndexOf('/') >= 0) return "has_path";
            string host, port;
            if (authority.StartsWith("[", System.StringComparison.Ordinal))
            {
                var close = authority.IndexOf(']');
                if (close < 0) return "not_loopback";
                host = authority.Substring(0, close + 1);
                var rest = authority.Substring(close + 1);
                if (host != "[::1]") return "not_loopback";
                if (!rest.StartsWith(":", System.StringComparison.Ordinal)) return "bad_port";
                port = rest.Substring(1);
            }
            else
            {
                var colon = authority.LastIndexOf(':');
                host = colon < 0 ? authority : authority.Substring(0, colon);
                if (host != "127.0.0.1") return "not_loopback";
                if (colon < 0) return "bad_port";
                port = authority.Substring(colon + 1);
            }
            return IsPort(port) ? null : "bad_port";
        }

        // 1..65535 in plain decimal: digits only, no sign, no leading zero.
        static bool IsPort(string text)
        {
            if (text.Length < 1 || text.Length > 5 || text[0] == '0') return false;
            var value = 0;
            foreach (var c in text)
            {
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return value >= 1 && value <= 65535;
        }
    }
}
