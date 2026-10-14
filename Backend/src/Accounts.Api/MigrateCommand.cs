using System.Globalization;
using Npgsql;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.Api;

public static class MigrateCommand
{
    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string?> env, TextWriter output, TextWriter error, CancellationToken ct)
    {
        DbSettings settings;
        try { settings = DbSettings.FromEnvironment(env); }
        catch (DbSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return ExitCodes.Failure; }

        await using var dataSource = settings.CreateDataSource("blackglass-migrator", maxPoolSize: 2);
        try
        {
            var result = await new Migrator(dataSource, MigrationCatalog.Embedded).MigrateAsync(ct: ct);
            output.WriteLine(result.Applied.Count == 0
                ? $"Schema is current at {result.SchemaVersion:D4}; nothing applied."
                : $"Applied {string.Join(", ", result.Applied.Select(v => v.ToString("D4", CultureInfo.InvariantCulture)))}; schema is now at {result.SchemaVersion:D4}.");
            return ExitCodes.Ok;
        }
        catch (SchemaMismatchException e) { error.WriteLine($"Refusing to migrate: {e.Message}"); return ExitCodes.SchemaMismatch; }
        catch (MigrationFailedException e) { error.WriteLine(e.Message); return ExitCodes.Failure; }
        catch (MigratorBusyException e) { error.WriteLine(e.Message); return ExitCodes.Failure; }
        catch (NpgsqlException e) { error.WriteLine($"Database error ({settings}): {ServeCommand.Describe(e)}"); return ExitCodes.Failure; }
    }
}
