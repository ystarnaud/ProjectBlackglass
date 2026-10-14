using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>Runs invariant files (spec §18.3): each is a query returning violating rows; zero rows means it holds.
/// The same files are run by deploy/services/restore-drill.sh.
/// A real invariant named NNNN_lower_snake.sql holds from schema version NNNN onward; a file without that prefix is not version-gated.</summary>
public static class InvariantRunner
{
    public static string RealDirectory => Path.Combine(AppContext.BaseDirectory, "invariants");

    public static Task<IReadOnlyList<string>> RunAsync(NpgsqlDataSource superuser, params string[] directories) =>
        RunAsync(superuser, int.MaxValue, directories);

    /// <summary>Runs the invariants that apply at <paramref name="maxVersion"/>: files whose NNNN prefix is greater are skipped.</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(NpgsqlDataSource superuser, int maxVersion, params string[] directories)
    {
        var failures = new List<string>();
        await using var conn = await superuser.OpenConnectionAsync();
        foreach (var file in directories.SelectMany(d => Directory.GetFiles(d, "*.sql")).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            if (VersionPrefix(Path.GetFileName(file)) > maxVersion) continue;
            var rows = (await conn.QueryAsync(await File.ReadAllTextAsync(file))).ToList();
            if (rows.Count > 0) failures.Add($"{Path.GetFileName(file)}: {rows.Count} violating row(s)");
        }
        return failures;
    }

    static int VersionPrefix(string fileName) =>
        fileName.Length > 5 && fileName[4] == '_' && fileName.Take(4).All(char.IsAsciiDigit)
            ? int.Parse(fileName.AsSpan(0, 4), System.Globalization.CultureInfo.InvariantCulture)
            : -1;
}
