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

        static string Value(IReadOnlyDictionary<string, string> values, string key) =>
            values != null && values.TryGetValue(key, out var value) && value.Length > 0 ? value : "unknown";
    }
}
