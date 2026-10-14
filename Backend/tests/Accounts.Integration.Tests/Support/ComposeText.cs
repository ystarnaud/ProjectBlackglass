namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>Reads our own Compose files by indentation (two spaces per level, as the files' headers require). This is a
/// static guard; the runtime check on the VM is deploy/services/check-exposure.sh. List items may sit at the key's indent.</summary>
public static class ComposeText
{
    public static IReadOnlyList<string> Lines(string file) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "compose", file));

    public static int Indent(string line) => line.Length - line.TrimStart(' ').Length;
    public static bool IsBlankOrComment(string line) => line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    /// <summary>The child lines of the block at the key path, e.g. ("services", "postgres"), or null if absent.</summary>
    public static (int Start, int End)? Find(IReadOnlyList<string> lines, params string[] path)
    {
        int start = 0, end = lines.Count;
        for (var depth = 0; depth < path.Length; depth++)
        {
            var indent = depth * 2;
            var at = -1;
            for (var i = start; i < end; i++)
                if (!IsBlankOrComment(lines[i]) && Indent(lines[i]) == indent && lines[i].TrimStart().StartsWith(path[depth] + ":", StringComparison.Ordinal)) { at = i; break; }
            if (at < 0) return null;
            var stop = at + 1;
            while (stop < end && (IsBlankOrComment(lines[stop]) || Indent(lines[stop]) > indent
                                  || (Indent(lines[stop]) == indent && lines[stop].TrimStart().StartsWith("- ", StringComparison.Ordinal)))) stop++;
            (start, end) = (at + 1, stop);
        }
        return (start, end);
    }

    public static IEnumerable<string> Block(IReadOnlyList<string> lines, params string[] path) =>
        Find(lines, path) is { } r ? lines.Skip(r.Start).Take(r.End - r.Start).Where(l => !IsBlankOrComment(l)) : [];

    /// <summary>The keys directly under a block (e.g. the network names under "networks").</summary>
    public static IEnumerable<string> ChildKeys(IReadOnlyList<string> lines, params string[] path) =>
        Block(lines, path).Where(l => Indent(l) == path.Length * 2).Select(l => l.Trim().TrimEnd(':').Split(':')[0]);
}
