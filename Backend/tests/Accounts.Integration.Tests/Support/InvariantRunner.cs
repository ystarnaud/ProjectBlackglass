using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>Runs invariant files (spec §18.3): each is a query returning violating rows; zero rows means it holds.
/// The same files are run by deploy/services/restore-drill.sh.</summary>
public static class InvariantRunner
{
    public static string RealDirectory => Path.Combine(AppContext.BaseDirectory, "invariants");

    public static async Task<IReadOnlyList<string>> RunAsync(NpgsqlDataSource superuser, params string[] directories)
    {
        var failures = new List<string>();
        await using var conn = await superuser.OpenConnectionAsync();
        foreach (var file in directories.SelectMany(d => Directory.GetFiles(d, "*.sql")).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var rows = (await conn.QueryAsync(await File.ReadAllTextAsync(file))).ToList();
            if (rows.Count > 0) failures.Add($"{Path.GetFileName(file)}: {rows.Count} violating row(s)");
        }
        return failures;
    }
}
