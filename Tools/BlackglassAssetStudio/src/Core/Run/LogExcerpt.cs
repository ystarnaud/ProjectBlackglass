using System.Text.RegularExpressions;

namespace Blackglass.AssetStudio;

public static class LogExcerpt
{
    static readonly Regex ErrorLine = new(@"error CS\d+|Exception|\[AssetPipeline\] ERROR", RegexOptions.Compiled);

    public static IReadOnlyList<string> ErrorLines(string logPath, int max = 8) =>
        ReadLines(logPath).Where(l => ErrorLine.IsMatch(l)).Select(l => l.Trim()).Distinct().TakeLast(max).ToList();

    public static string LastLine(string logPath) =>
        ReadLines(logPath).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

    static string[] ReadLines(string path)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd().Split('\n');
        }
        catch (IOException) { return Array.Empty<string>(); }
    }
}
