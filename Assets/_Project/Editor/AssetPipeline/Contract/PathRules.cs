#nullable disable
using System;

namespace Blackglass.AssetPipeline
{
    /// <summary>Project-relative destination and asset-name rules, shared by the app (before staging) and Unity (before copying).</summary>
    public static class PathRules
    {
        static readonly char[] InvalidSegmentChars = { '<', '>', ':', '"', '|', '?', '*' };

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
