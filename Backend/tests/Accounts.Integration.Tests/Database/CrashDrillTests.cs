using Dapper;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The crash drill's driver (S9-B17; run only by tools/vm-crash-drill.sh, against the disposable blackglass-crashdb cluster, with
/// BLACKGLASS_CRASH_DRILL=1; in an ordinary run it does nothing). It prepares started participants, signals the script
/// (/out/drill-loaded), drives result relays and exit-evidence voids while the script kills and restarts PostgreSQL, waits for the
/// database, then checks the invariants and resubmits every result and report: each participant ends with exactly one outcome.
/// </summary>
[Trait("Requires", "Postgres")]
public class CrashDrillTests
{
    const int Participants = 60; // the drill host's 64 slots

    [Fact]
    public async Task The_crash_drill_keeps_every_settlement_exactly_once()
    {
        if (Environment.GetEnvironmentVariable("BLACKGLASS_CRASH_DRILL") != "1") return; // only tools/vm-crash-drill.sh runs it
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Blackglass.Accounts.Persistence.Migrations.Migrator(db.AsMigrator(), Blackglass.Accounts.Persistence.Migrations.MigrationCatalog.Embedded).MigrateAsync();
        var kit = await MissionKit.CreateAsync(db, capacity: 64);
        var running = new List<ManifestKit.Running>();
        for (var i = 0; i < Participants; i++) running.Add(await ManifestKit.StartAsync(kit, db));
        var secret = await AttemptFixtures.HostSecretHashAsync(db, "h1");
        var startedBefore = await ScalarAsSuperuserAsync<DateTime>(db, "select pg_postmaster_start_time()");
        await File.WriteAllTextAsync("/out/drill-loaded", "loaded\n");

        // Load: two of three participants relay a result, the third gets exit evidence of absence; failures (the kill) are counted.
        var failures = 0;
        var load = running.Select((r, i) => Task.Run(async () =>
        {
            await Task.Delay(i * 40);
            try
            {
                if (i % 3 == 2)
                    await kit.Hosts.ExitedAsync("h1", secret, r.Attempt, new ExitedRequest(1, null, [new ExitResult(r.Account, false)], true), default);
                else
                    await ManifestKit.Store(db).SubmitAsync("h1", secret, ManifestKit.Body(ManifestKit.Manifest(r)), default);
            }
            catch (Exception) { Interlocked.Increment(ref failures); }
        })).ToList();
        await Task.WhenAll(load);

        // Wait for the restarted cluster (the script kills it mid-load and starts it again).
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            try { await ScalarAsSuperuserAsync<int>(db, "select 1"); break; }
            catch (Exception) when (DateTime.UtcNow < deadline) { await Task.Delay(500); }
        }
        Npgsql.NpgsqlConnection.ClearAllPools(); // connections opened before the kill are dead
        // The drill proves nothing unless PostgreSQL really restarted during the load (the negative test skips the kill).
        Assert.NotEqual(startedBefore, await ScalarAsSuperuserAsync<DateTime>(db, "select pg_postmaster_start_time()"));
        Assert.Empty(await InvariantFailuresAsync(db));

        // Resubmission pass: every result and report again; each participant ends with exactly one settlement.
        var answers = new List<string>();
        for (var i = 0; i < running.Count; i++)
        {
            var r = running[i];
            if (i % 3 == 2)
                answers.Add((await kit.Hosts.ExitedAsync("h1", secret, r.Attempt, new ExitedRequest(1, null, [new ExitResult(r.Account, false)], true), default)).ToString());
            else
            {
                var outcome = await ManifestKit.Store(db).SubmitAsync("h1", secret, ManifestKit.Body(ManifestKit.Manifest(r)), default);
                Assert.True(outcome is ResultOutcome.Settled or ResultOutcome.Duplicate, $"participant {i}: {outcome}");
                answers.Add(outcome.ToString());
            }
        }
        Assert.Equal((long)Participants, await ScalarAsSuperuserAsync<long>(db, "select count(*) from participant_settlements"));
        Assert.Equal((long)(Participants / 3), await ScalarAsSuperuserAsync<long>(db, "select count(*) from participant_settlements where outcome = 'infra_void'"));
        Assert.Empty(await InvariantFailuresAsync(db));
        await File.WriteAllTextAsync("/out/drill-result.txt",
            $"restarted=true participants={Participants} loadFailures={failures} settlements={Participants} voids={Participants / 3} resubmission={string.Join(',', answers.GroupBy(a => a).Select(g => $"{g.Key}x{g.Count()}"))}\n");
    }
}
