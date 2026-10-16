using Npgsql;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Energy;

namespace Blackglass.Accounts.Api.Energy;

/// <summary>
/// The periodic energy reconciliation in the API process (owner ruling E4), every BLACKGLASS_ENERGY_RECONCILE_SECONDS; serve
/// registers it only when that is not 0. It first waits one whole interval, so startup never waits for (or runs) a sweep. A
/// failed sweep is logged and the next tick still runs; nothing is retried in between. Logs (category
/// <see cref="LogCategory"/>) carry the counts, or the SQLSTATE and constraint only: never a server message, which can quote
/// values. Stopping the host cancels a sweep in progress and ends the service cleanly.
/// </summary>
/// <param name="sweep">One sweep: <see cref="EnergyReconciler.RunAsync"/> in serve. A delegate rather than the class, so a test
/// can hold or fail a sweep inside the real host without an interface that has only one implementation.</param>
public sealed class EnergyReconcileService(Func<CancellationToken, Task<ReconcileReport>> sweep, TimeSpan interval, ILoggerFactory logs) : BackgroundService
{
    public const string LogCategory = "Blackglass.Energy";

    readonly TimeSpan interval = interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
    readonly ILogger log = logs.CreateLogger(LogCategory);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            // A tick missed while a sweep runs is coalesced: sweeps never overlap within one process.
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            var report = await sweep(ct);
            log.Log(report.Mismatches.Count == 0 ? LogLevel.Information : LogLevel.Warning,
                "Energy reconciliation checked {WalletsChecked} wallets: {Mismatches} mismatches, {AnomaliesRecorded} anomalies recorded",
                report.WalletsChecked, report.Mismatches.Count, report.AnomaliesRecorded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (NpgsqlException e)
        {
            // Not the exception itself: its message and detail are the server's and can quote values.
            log.LogError("Energy reconciliation failed: {Failure}; no balance was changed; the next sweep runs in {IntervalSeconds} s",
                DbFailure.SafeDescription(e), interval.TotalSeconds);
        }
        catch (Exception e)
        {
            // Anything else is a bug; its type is enough to find it, and the host must keep serving (an escaping exception
            // would stop it).
            log.LogError("Energy reconciliation failed: {Failure}; no balance was changed; the next sweep runs in {IntervalSeconds} s",
                e.GetType().Name, interval.TotalSeconds);
        }
    }
}
