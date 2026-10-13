using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>One line of the development token file: an account, its token, whether it owns the squad, and an optional attempt id.</summary>
    public sealed class NetTokenEntry
    {
        public NetTokenEntry(string account, string token, bool ownsSquad, string attempt, bool fixture = false)
        {
            Account = account;
            Token = token;
            OwnsSquad = ownsSquad;
            Attempt = attempt;
            Fixture = fixture;
        }

        public string Account { get; }
        public string Token { get; }
        /// <summary>True for a `squad` entry: the account owns the mission's friendlies.</summary>
        public bool OwnsSquad { get; }
        /// <summary>When set, the entry is only valid for a mission whose instance id equals this value (null: any).</summary>
        public string Attempt { get; }
        /// <summary>True for a `+fixture` entry: the account may drive the development fixture (Editor and fixture builds only).</summary>
        public bool Fixture { get; }
    }

    /// <summary>
    /// Parser for the development token file (networking experiment, spec section 3.4): one line per account,
    /// `account token squad|none[+fixture] [attempt]`, separated by spaces or tabs. Blank lines and `#` comments are ignored. The tokens
    /// are fixtures, not an identity scheme; a warning names the line and never contains a token.
    /// </summary>
    public static class NetTokenFile
    {
        public static List<NetTokenEntry> Parse(string text, out List<string> warnings)
        {
            var entries = new List<NetTokenEntry>();
            warnings = new List<string>();
            if (string.IsNullOrEmpty(text))
                return entries;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var number = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3 || fields.Length > 4)
                {
                    warnings.Add("line " + number + ": expected '<account> <token> <squad|none>[+fixture] [attempt]'; skipped");
                    continue;
                }
                if (!TryOwnership(fields[2], out var squad, out var fixture))
                {
                    warnings.Add("line " + number + ": the third field must be 'squad' or 'none', optionally with '+fixture'; skipped");
                    continue;
                }
                if (!seen.Add(fields[1]))
                {
                    warnings.Add("line " + number + ": this token is already used by an earlier line; skipped");
                    continue;
                }
                entries.Add(new NetTokenEntry(fields[0], fields[1], squad, fields.Length == 4 ? fields[3] : null, fixture));
            }
            return entries;
        }

        static bool TryOwnership(string field, out bool squad, out bool fixture)
        {
            fixture = false;
            var plus = field.IndexOf('+');
            if (plus >= 0)
            {
                if (field.Substring(plus + 1) != "fixture")
                {
                    squad = false;
                    return false;
                }
                fixture = true;
                field = field.Substring(0, plus);
            }
            squad = field == "squad";
            return squad || field == "none";
        }
    }
}
