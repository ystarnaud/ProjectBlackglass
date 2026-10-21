using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The `build-info.txt` the server build writes beside the executable (key=value lines: revision, dirty, builtUtc,
    /// buildGuid, ...). The server reads it to log which revision is running; in the Editor there is no file and every value
    /// is "unknown".
    /// </summary>
    public static class ServerBuildInfo
    {
        public const string FileName = "build-info.txt";

        public static Dictionary<string, string> Parse(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text))
                return values;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
            }
            return values;
        }

        /// <summary>The file beside the player executable (the folder above `*_Data`); empty when missing or unreadable.</summary>
        public static Dictionary<string, string> ReadBesideExecutable()
        {
            try
            {
                var folder = Path.GetDirectoryName(Application.dataPath);
                var path = Path.Combine(folder ?? ".", FileName);
                return File.Exists(path) ? Parse(File.ReadAllText(path)) : Parse(null);
            }
            catch (Exception)
            {
                return Parse(null);
            }
        }

        public static string Describe(IReadOnlyDictionary<string, string> values)
        {
            var revision = Value(values, "revision");
            if (Value(values, "dirty") == "true")
                revision += "+dirty";
            return $"revision={revision} built={Value(values, "builtUtc")} buildGuid={Value(values, "buildGuid")}";
        }

        /// <summary>
        /// The provenance lines of build-info.txt: exactly the source state Tools/build-server.sh captured before Unity started
        /// (BLACKGLASS_SOURCE_REVISION, _TREE, _DIRTY). Unity rewrites settings it switches and restores during a build, so the
        /// build never reads git itself. Anything but a full lowercase revision and tree and dirty true or false stamps every
        /// value unknown, never clean (an Editor-menu build has no capture).
        /// </summary>
        public static string[] SourceLines(string revision, string tree, string dirty)
        {
            if (!IsObjectId(revision) || !IsObjectId(tree) || (dirty != "true" && dirty != "false"))
                return new[] { "revision=unknown", "sourceTree=unknown", "dirty=unknown" };
            return new[] { "revision=" + revision, "sourceTree=" + tree, "dirty=" + dirty };
        }

        static bool IsObjectId(string value)
        {
            if (value == null || value.Length != 40)
                return false;
            foreach (var c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))
                    return false;
            return true;
        }

        static string Value(IReadOnlyDictionary<string, string> values, string key) =>
            values != null && values.TryGetValue(key, out var value) && value.Length > 0 ? value : "unknown";
    }
}
