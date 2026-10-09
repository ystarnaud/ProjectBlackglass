#nullable disable
using System;

namespace Blackglass.AssetPipeline
{
    /// <summary>Project-relative destination and asset-name rules, shared by the app (before staging) and Unity (before copying).</summary>
    public static class PathRules
    {
        static readonly char[] InvalidSegmentChars = { '<', '>', ':', '"', '|', '?', '*' };

        static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static string Normalize(string folder)
        {
            if (folder == null) return "";
            return folder.Trim().Replace('\\', '/').TrimEnd('/').Trim();
        }

        /// <summary>Null when the destination is acceptable, otherwise a message for the user.</summary>
        public static string ValidateDestination(string folder)
        {
            var path = Normalize(folder);
            if (path.Length == 0) return "The destination folder is empty.";
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                return "The destination must be a folder inside the project's Assets folder (for example Assets/Art/Props).";
            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0) return "The destination contains an empty folder name.";
                if (segment == "." || segment == "..") return "The destination must not contain '.' or '..'.";
                if (segment.IndexOfAny(InvalidSegmentChars) >= 0 || HasControl(segment))
                    return "The destination folder name '" + segment + "' contains an invalid character.";
                if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
                    return "The destination folder name '" + segment + "' must not end with a dot or a space.";
                var segmentProblem = IgnoredOrReserved(segment);
                if (segmentProblem != null) return "The destination folder name '" + segment + "' " + segmentProblem;
            }
            return null;
        }

        /// <summary>Null when the name is usable as an asset file name.</summary>
        public static string ValidateAssetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "The asset name is empty.";
            if (name.IndexOfAny(InvalidSegmentChars) >= 0 || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || HasControl(name))
                return "The asset name contains an invalid character.";
            if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal))
                return "The asset name must not start or end with a dot.";
            if (name != name.Trim()) return "The asset name must not start or end with a space.";
            var nameProblem = IgnoredOrReserved(name);
            if (nameProblem != null) return "The asset name '" + name + "' " + nameProblem;
            return null;
        }

        /// <summary>Null when fine. Unity ignores names starting with a dot or ending with a tilde; Windows reserves device names (CON.txt included).</summary>
        static string IgnoredOrReserved(string segment)
        {
            if (segment.StartsWith(".", StringComparison.Ordinal)) return "must not start with a dot (Unity ignores it).";
            if (segment.EndsWith("~", StringComparison.Ordinal)) return "must not end with a tilde (Unity ignores it).";
            var dot = segment.IndexOf('.');
            var stem = (dot < 0 ? segment : segment.Substring(0, dot)).TrimEnd(' ');
            foreach (var reserved in ReservedDeviceNames)
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                    return "is a reserved Windows device name.";
            return null;
        }

        static bool HasControl(string s)
        {
            foreach (var c in s)
                if (char.IsControl(c)) return true;
            return false;
        }
    }
}
