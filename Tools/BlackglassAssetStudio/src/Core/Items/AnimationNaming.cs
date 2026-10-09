using System.Text;
using System.Text.RegularExpressions;

namespace Blackglass.AssetStudio;

/// <summary>Turns an animation file name into the words of its action ("EnemyUnit-Crouch-Idle-Rifle" for EnemyUnit gives Crouch Idle Rifle).</summary>
public static class AnimationNaming
{
    const string Fallback = "Clip";
    static readonly Regex CopySuffix = new(@"\s*\(\d+\)\s*$", RegexOptions.Compiled);
    static readonly Regex CamelBoundary = new("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
    static readonly Regex NonWord = new("[^\\p{L}\\p{N}]+", RegexOptions.Compiled);
    static readonly HashSet<string> ExportNoise = new(StringComparer.OrdinalIgnoreCase) { "mixamo", "com" };

    /// <summary>The action's words, Title-cased, without the leading character name, Mixamo export noise or a "(1)" copy suffix. Never empty.</summary>
    public static IReadOnlyList<string> ActionWords(string fileStem, string characterName)
    {
        var stem = CopySuffix.Replace(fileStem ?? "", "");
        var tokens = NonWord.Split(CamelBoundary.Replace(stem, " ")).Where(t => t.Length > 0).ToList();

        tokens = RemoveSkinPhrases(tokens.Where(t => !ExportNoise.Contains(t)).ToList());
        DropLeadingCharacter(tokens, characterName);

        var words = tokens.Select(t => char.ToUpperInvariant(t[0]) + t.Substring(1)).ToList();
        return words.Count == 0 ? new[] { Fallback } : words;
    }

    /// <summary>"EnemyUnit Aim Pistol": the character, then the action's words separated by spaces (the Darius file naming).</summary>
    public static string AssetName(string characterName, IReadOnlyList<string> words) =>
        $"{characterName} {string.Join(' ', words)}".Trim();

    /// <summary>"AimPistol": the clip name shown in an Animator, without spaces.</summary>
    public static string ClipName(IReadOnlyList<string> words)
    {
        var sb = new StringBuilder();
        foreach (var w in words) sb.Append(w);
        return sb.Length == 0 ? Fallback : sb.ToString();
    }

    // "Without Skin" / "With Skin" is a Mixamo download option, not part of the action's name.
    static List<string> RemoveSkinPhrases(List<string> tokens)
    {
        var result = new List<string>();
        foreach (var t in tokens)
        {
            if (t.Equals("skin", StringComparison.OrdinalIgnoreCase))
            {
                if (result.Count > 0 && (result[^1].Equals("with", StringComparison.OrdinalIgnoreCase) || result[^1].Equals("without", StringComparison.OrdinalIgnoreCase)))
                    result.RemoveAt(result.Count - 1);
                continue;
            }
            result.Add(t);
        }
        return result;
    }

    static void DropLeadingCharacter(List<string> tokens, string characterName)
    {
        var target = new string((characterName ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (target.Length == 0) return;
        var joined = "";
        for (var i = 0; i < tokens.Count; i++)
        {
            joined += tokens[i].ToLowerInvariant();
            if (joined == target) { tokens.RemoveRange(0, i + 1); return; }
            if (joined.Length >= target.Length || !target.StartsWith(joined, StringComparison.Ordinal)) return;
        }
    }
}
