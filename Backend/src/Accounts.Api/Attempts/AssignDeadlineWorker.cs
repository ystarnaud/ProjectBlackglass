using Npgsql;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Attempts;

namespace Blackglass.Accounts.Api.Attempts;

/// <summary>
/// The assignment deadline worker in the API process (owner ruling S7-D6): every BLACKGLASS_ATTEMPT_SWEEP_SECONDS it runs one
/// sweep (<see cref="AttemptStore.SweepAsync"/> in serve, with <see cref="BatchSize"/>), which cancels admitted attempts past
/// their deadline with <c>no_capacity</c> and releases their energy exactly once. serve always registers it: the release of an
/// expired attempt depends on it, so it cannot be switched off. Like the energy reconciliation it first waits one whole
/// interval, so startup never waits for (or runs) a sweep; sweeps never overlap within one process (a missed tick is
/// coalesced), and several processes are safe (the selection skips locked rows and each release re-locks its attempt). A
/// failed sweep, or a failed release within one, is logged and the next sweep still runs; nothing retries in between. Logs
/// (category <see cref="AttemptSettings.LogCategory"/>) carry counts and the SQLSTATE and constraint only: never a server
/// message, which can quote values. Stopping the host cancels a sweep in progress and ends the service cleanly.
/// </summary>
/// <param name="sweep">One sweep. A delegate rather than the store, so a test can count, hold or fail sweeps inside the real
/// host without an interface that has only one implementation.</param>
public sealed class AssignDeadlineWorker(Func<CancellationToken, Task<SweepReport>> sweep, TimeSpan interval, ILoggerFactory logs) : BackgroundService
{
    /// <summary>The most attempts one sweep releases; more wait for the next sweep (provisional).</summary>
    public const int BatchSize = 100;

    readonly Func<CancellationToken, Task<SweepReport>> sweep = sweep ?? throw new ArgumentNullException(nameof(sweep));
    readonly TimeSpan interval = interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
    readonly ILogger log = (logs ?? throw new ArgumentNullException(nameof(logs))).CreateLogger(AttemptSettings.LogCategory);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
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
            if (report.Released > 0 || report.AlreadyEnded > 0 || report.NotAdmitted > 0)
                log.LogInformation("Assignment deadline released {Released} attempts ({AlreadyEnded} already ended, {NotAdmitted} past admission) of {Due} due",
                    report.Released, report.AlreadyEnded, report.NotAdmitted, report.Due);
            if (report.Failures.Count > 0)
                log.LogError("Assignment deadline release failed for {Failed} of {Due} due attempts: {Failures}; they stay due for the next sweep in {IntervalSeconds} s",
                    report.Failures.Count, report.Due, string.Join("; ", report.Failures), interval.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (NpgsqlException e)
        {
            // Not the exception itself: its message and detail are the server's and can quote values.
            log.LogError("Assignment deadline sweep failed: {Failure}; nothing was released by it; the next sweep runs in {IntervalSeconds} s",
                DbFailure.SafeDescription(e), interval.TotalSeconds);
        }
        catch (Exception e)
        {
            // Anything else is a bug; its type is enough to find it, and the host must keep serving.
            log.LogError("Assignment deadline sweep failed: {Failure}; nothing was released by it; the next sweep runs in {IntervalSeconds} s",
                e.GetType().Name, interval.TotalSeconds);
        }
    }
}
