using System.Data;
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Settlement;

/// <summary>Stage 9's provisional monitoring values (S9-B18, spec §19).</summary>
/// <param name="AutoDrainAttempts">Distinct attempts with an exit-evidence void within the window (and since the host's last operator enable, so a re-enable sticks) that drain a host; 0 turns it off.</param>
public sealed record MonitoringOptions(int EscalationHours, int UnresponsiveAlertSeconds, int RevokedAlertHours, int AutoDrainAttempts, int AutoDrainWindowMinutes)
{
    public static readonly MonitoringOptions Default = new(24, 600, 24, 3, 60);
}

/// <summary>What one monitoring sweep raised.</summary>
public sealed record MonitoringReport(int Escalations, int Unresponsive, int Revoked, IReadOnlyList<string> Drained);

/// <summary>
/// The repeating alerts and the auto-drain (S9-B18). Alerts repeat and time never voids: no sweep changes a participant's
/// state, a hold, an inventory or a lock. A quarantined participant's escalation is raised once per period (deduplicated by its
/// due time) and its escalate_at moves one period forward; an unresponsive attempt and a revoked attempt with unsettled
/// participants get one alert per period. A host is drained (planned maintenance: running attempts and process credentials go
/// on) when the distinct attempts on it with an exit-evidence void within the window reach the threshold; it is never
/// disabled, revoked, voided or refunded, and an operator re-enables it. Lock order (§E, rev 3): candidates read unlocked; per
/// escalation, the participant's accounts row FOR KEY SHARE, the attempt FOR UPDATE SKIP LOCKED, the participant FOR UPDATE;
/// the attempt-level alerts and the drain's anomaly name no account; the drain takes only the host row FOR UPDATE.
/// </summary>
public sealed class MonitoringStore(NpgsqlDataSource db, MonitoringOptions options)
{
    public async Task<MonitoringReport> SweepAsync(CancellationToken ct)
    {
        var escalations = 0;
        List<(Guid Attempt, long Account)> due;
        await using (var conn = await DbFailure.OpenAsync(db, ct))
            due = (await conn.QueryAsync<(Guid, long)>(new CommandDefinition(
                "select attempt_id, account_id from attempt_participants where state = 'quarantined' and escalate_at <= clock_timestamp() order by escalate_at limit 100",
                cancellationToken: ct))).ToList();
        foreach (var (attempt, account) in due)
        {
            await using var conn = await DbFailure.OpenAsync(db, ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await SettlementStore.LockAccountAsync(conn, tx, account, ct);
            if (await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "select attempt_id from mission_attempts where attempt_id = @attempt for update skip locked", new { attempt }, tx, cancellationToken: ct)) is null)
                continue; // a settlement or recovery holds it: the next sweep
            var escalateAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                """
                select escalate_at from attempt_participants where attempt_id = @attempt and account_id = @account and state = 'quarantined'
                  and escalate_at <= clock_timestamp() for update
                """, new { attempt, account }, tx, cancellationToken: ct));
            if (escalateAt is not { } at) continue;
            var reason = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "select quarantine_reason from attempt_participants where attempt_id = @attempt and account_id = @account", new { attempt, account }, tx, cancellationToken: ct));
            await Anomalies.RaiseAsync(conn, tx, Anomalies.QuarantineEscalation, $"{attempt}:{account}:{SettlementStore.Utc(at).Ticks}", new { reason, dueAt = SettlementStore.Utc(at) }, ct,
                accountId: account, attemptId: attempt);
            await conn.ExecuteAsync(new CommandDefinition(
                "update attempt_participants set escalate_at = escalate_at + make_interval(hours => @hours) where attempt_id = @attempt and account_id = @account",
                new { attempt, account, hours = options.EscalationHours }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            escalations++;
        }

        int unresponsive, revoked;
        await using (var conn = await DbFailure.OpenAsync(db, ct))
        await using (var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            unresponsive = await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into anomaly_events (kind, host_id, build_revision, attempt_id, dedupe_key, detail)
                select 'unresponsive_attempt', a.host_id, a.build_revision, a.attempt_id,
                       a.attempt_id::text || ':' || floor(extract(epoch from clock_timestamp() - coalesce(a.last_process_heartbeat_at, a.started_at)) / @period)::bigint,
                       jsonb_build_object('silentSeconds', floor(extract(epoch from clock_timestamp() - coalesce(a.last_process_heartbeat_at, a.started_at)))::bigint)
                from mission_attempts a
                where a.state = 'unresponsive' and coalesce(a.last_process_heartbeat_at, a.started_at) <= clock_timestamp() - make_interval(secs => @period)
                on conflict (kind, dedupe_key) where reviewed_at is null do nothing
                """, new { period = (double)options.UnresponsiveAlertSeconds }, tx, cancellationToken: ct));
            revoked = await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into anomaly_events (kind, host_id, build_revision, attempt_id, dedupe_key, detail)
                select 'revoked_attempt_unsettled', a.host_id, a.build_revision, a.attempt_id,
                       a.attempt_id::text || ':' || floor(extract(epoch from clock_timestamp() - a.credential_revoked_at) / (@hours * 3600))::bigint,
                       jsonb_build_object('unsettled', (select count(*) from attempt_participants p where p.attempt_id = a.attempt_id
                                                        and p.started_at is not null and p.state not in ('settled', 'released')))
                from mission_attempts a
                where a.credential_revoked_at is not null
                  and exists (select 1 from attempt_participants p where p.attempt_id = a.attempt_id and p.started_at is not null and p.state not in ('settled', 'released'))
                on conflict (kind, dedupe_key) where reviewed_at is null do nothing
                """, new { hours = (double)options.RevokedAlertHours }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        var drained = new List<string>();
        if (options.AutoDrainAttempts > 0)
        {
            List<string> hosts;
            await using (var conn = await DbFailure.OpenAsync(db, ct))
                hosts = (await conn.QueryAsync<string>(new CommandDefinition(
                    """
                    select a.host_id from participant_settlements s join mission_attempts a using (attempt_id) join mission_hosts h on h.host_id = a.host_id
                    where s.void_basis = 'exit_evidence' and h.enabled and not h.drained
                      and s.settled_at > greatest(clock_timestamp() - make_interval(mins => @window),
                      coalesce((select max(o.created_at) from operator_actions o where o.action = 'host_enable' and o.host_id = a.host_id), '-infinity'))
                    group by a.host_id having count(distinct s.attempt_id) >= @threshold
                    """, new { window = options.AutoDrainWindowMinutes, threshold = options.AutoDrainAttempts }, cancellationToken: ct))).ToList();
            foreach (var host in hosts)
            {
                await using var conn = await DbFailure.OpenAsync(db, ct);
                await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    select count(distinct s.attempt_id) from participant_settlements s join mission_attempts a using (attempt_id)
                    where a.host_id = @host and s.void_basis = 'exit_evidence'
                      and s.settled_at > greatest(clock_timestamp() - make_interval(mins => @window),
                      coalesce((select max(o.created_at) from operator_actions o where o.action = 'host_enable' and o.host_id = a.host_id), '-infinity'))
                    """, new { host, window = options.AutoDrainWindowMinutes }, tx, cancellationToken: ct));
                var locked = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(
                    "select enabled and not drained from mission_hosts where host_id = @host for update", new { host }, tx, cancellationToken: ct));
                if (locked != true || count < options.AutoDrainAttempts) continue;
                await conn.ExecuteAsync(new CommandDefinition("update mission_hosts set drained = true where host_id = @host", new { host }, tx, cancellationToken: ct));
                await Anomalies.RaiseAsync(conn, tx, Anomalies.HostAutoDrained, $"{host}:{DateTime.UtcNow:yyyyMMddHHmm}",
                    new { voidedAttempts = count, windowMinutes = options.AutoDrainWindowMinutes, threshold = options.AutoDrainAttempts }, ct, hostId: host);
                await tx.CommitAsync(ct);
                drained.Add(host);
            }
        }
        return new MonitoringReport(escalations, unresponsive, revoked, drained);
    }
}
