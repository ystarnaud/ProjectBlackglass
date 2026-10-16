using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Accounts;

namespace Blackglass.Accounts.Persistence.Energy;

/// <summary>One finding: a wallet that breaks <paramref name="Rule"/>, with its stored balance, the sum of its ledger deltas
/// and the balance_after of its latest entry (null when it has none).</summary>
public sealed record EnergyMismatch(long AccountId, string Rule, int Balance, long LedgerSum, int? LastBalanceAfter);

/// <param name="AnomaliesRecorded">The anomaly rows this run inserted: a finding that already has an outstanding (unreviewed)
/// anomaly is reported in <paramref name="Mismatches"/> but not recorded again.</param>
public sealed record ReconcileReport(int WalletsChecked, IReadOnlyList<EnergyMismatch> Mismatches, int AnomaliesRecorded);

/// <summary>
/// The energy reconciliation (owner ruling E4): compares every wallet with its ledger and records each finding as an anomaly.
/// It never corrects anything: no wallet, ledger entry or anomaly is ever updated or deleted, and nothing is retried.
/// The rules are those of <c>db/invariants/0006_energy_ledger.sql</c>, per account; EnergyReconcilerTests pins that both report
/// the same findings on every corruption fixture and that both name the same six rules.
/// </summary>
public sealed class EnergyReconciler(NpgsqlDataSource db)
{
    public const string AnomalyKind = "energy_ledger_mismatch";

    /// <summary>The rules checked, named as in the invariant file.</summary>
    public static IReadOnlyList<string> Rules { get; } =
        ["balance_vs_sum", "balance_vs_last_entry", "entry_chain", "open_missing", "open_not_first", "progress_not_below_interval"];

    /// <summary>
    /// One REPEATABLE READ, READ ONLY snapshot over every wallet and its ledger (a concurrent grant is seen whole or not at
    /// all), with the invariant file's rules. Then, in a separate short READ COMMITTED transaction, one anomaly_events row per
    /// mismatch: kind <see cref="AnomalyKind"/>, the account, dedupe key <c>'&lt;Base58 account id&gt;:&lt;rule&gt;'</c> (the numbers go
    /// in the detail, so a moving balance does not open a new anomaly each sweep; plan Checkpoint 1 F4) and detail
    /// <c>{rule, balance, ledgerSum, lastBalanceAfter}</c>, inserted with ON CONFLICT DO NOTHING against
    /// <c>anomaly_one_outstanding</c>, so concurrent sweeps and repeated runs record one outstanding row per finding. Either
    /// all of a run's new anomalies are recorded or none; a failure propagates to the caller, and no balance ever changes.
    /// </summary>
    public async Task<ReconcileReport> RunAsync(CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        int wallets;
        List<EnergyMismatch> mismatches;
        await using (var read = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
        {
            // Before any query, so the snapshot is read-only too: this transaction cannot write even by mistake.
            await conn.ExecuteAsync(new CommandDefinition("set transaction read only", transaction: read, cancellationToken: ct));
            wallets = await conn.ExecuteScalarAsync<int>(new CommandDefinition("select count(*)::int from energy_wallets", transaction: read, cancellationToken: ct));
            mismatches = (await conn.QueryAsync<EnergyMismatch>(new CommandDefinition(FindingsSql, transaction: read, cancellationToken: ct))).ToList();
            await read.CommitAsync(ct);
        }
        if (mismatches.Count == 0) return new ReconcileReport(wallets, mismatches, 0);

        var recorded = 0;
        await using (var write = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            foreach (var m in mismatches)
                recorded += await conn.ExecuteAsync(new CommandDefinition(
                    """
                    insert into anomaly_events (kind, account_id, host_id, build_revision, attempt_id, dedupe_key, detail)
                    values (@kind, @accountId, null, null, null, @dedupeKey,
                      jsonb_build_object('rule', @rule::text, 'balance', @balance::int, 'ledgerSum', @ledgerSum::bigint, 'lastBalanceAfter', @lastBalanceAfter::int))
                    on conflict (kind, dedupe_key) where reviewed_at is null do nothing
                    """,
                    new { kind = AnomalyKind, accountId = m.AccountId, dedupeKey = $"{AccountId.Format(m.AccountId)}:{m.Rule}", rule = m.Rule, balance = m.Balance,
                        ledgerSum = m.LedgerSum, lastBalanceAfter = m.LastBalanceAfter },
                    write, cancellationToken: ct));
            await write.CommitAsync(ct);
        }
        return new ReconcileReport(wallets, mismatches, recorded);
    }

    /// <summary>
    /// The invariant file's six rules, one row per (account, rule): the file reports entry_chain and open_not_first once per
    /// entry, this once per account. Sums are bigint, so a corrupt ledger can't overflow the check. Every ledger entry
    /// references its wallet (0006's foreign key), so every finding has a wallet's numbers.
    /// </summary>
    const string FindingsSql =
        """
        with sums as (
          select account_id, sum(delta::bigint)::bigint as total from energy_ledger group by account_id),
        latest as (
          select distinct on (account_id) account_id, balance_after from energy_ledger order by account_id, entry_id desc),
        chain as (
          select account_id, kind, delta, balance_after,
            lag(balance_after, 1, 0) over (partition by account_id order by entry_id) as previous,
            row_number() over (partition by account_id order by entry_id) as position
          from energy_ledger),
        findings as (
          select w.account_id, 'balance_vs_sum' as rule
          from energy_wallets w left join sums s on s.account_id = w.account_id
          where w.balance <> coalesce(s.total, 0)
          union all
          select w.account_id, 'balance_vs_last_entry'
          from energy_wallets w left join latest l on l.account_id = w.account_id
          where l.balance_after is distinct from w.balance
          union all
          select distinct c.account_id, 'entry_chain'
          from chain c
          where c.balance_after::bigint <> c.previous::bigint + c.delta
          union all
          select w.account_id, 'open_missing'
          from energy_wallets w
          where not exists (select 1 from energy_ledger e where e.account_id = w.account_id and e.kind = 'open')
          union all
          select distinct c.account_id, 'open_not_first'
          from chain c
          where c.kind = 'open' and c.position <> 1
          union all
          select w.account_id, 'progress_not_below_interval'
          from energy_wallets w join energy_policies p on p.version = w.policy_version
          where w.regen_progress_us >= (extract(epoch from p.regen_interval) * 1000000)::bigint)
        select f.account_id as AccountId, f.rule as Rule, w.balance as Balance, coalesce(s.total, 0)::bigint as LedgerSum, l.balance_after as LastBalanceAfter
        from findings f
          join energy_wallets w on w.account_id = f.account_id
          left join sums s on s.account_id = f.account_id
          left join latest l on l.account_id = f.account_id
        order by f.account_id, f.rule collate "C"
        """;
}
