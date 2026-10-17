using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Energy;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.Persistence.Energy;

namespace Blackglass.Accounts.Persistence.Settlement;

/// <summary>
/// The one protective-void transaction body (spec §11.3; S9-B7, S9-B8, S9-B9; revision 3), shared by the exit-evidence void,
/// the operator voids and the host-loss declaration. Nothing is consumed, granted or paid; equipment and bags stay as stored;
/// the energy hold is refunded (floor(amount × the wallet's current policy void_refund_pct / 100), spec §19: 100 % provisional)
/// with one <c>void_refund:&lt;attempt&gt;</c> entry when positive; the inventory version is bumped once; the settlement row
/// (outcome infra_void, its basis) and the void receipt are written; the participant becomes settled (its participation lock
/// released) and the attempt advances as a settlement would. Never commits; never retried.
/// </summary>
public static class VoidStore
{
    public static readonly IReadOnlyList<string> Bases = ["exit_evidence", "operator_quarantine", "operator_revoked_unrecoverable", "host_declared_lost"];

    /// <summary>
    /// Voids <paramref name="accountId"/> in <paramref name="attemptId"/>. The caller holds, in this order, the participants'
    /// accounts rows FOR KEY SHARE, the attempt FOR UPDATE and this participant FOR UPDATE, and has checked that the void is
    /// allowed; this takes the hold, the inventory, then (a positive hold) the wallet, the shared policy lock and the clock (§E).
    /// Returns the energy refunded.
    /// </summary>
    public static async Task<int> VoidParticipantLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attemptId, long accountId, string attemptState,
        string basis, CancellationToken ct)
    {
        if (!Bases.Contains(basis)) throw new ArgumentOutOfRangeException(nameof(basis));
        var link = new SettlementStore.Link(attemptId, accountId);
        var hold = await conn.QuerySingleAsync<(int Amount, string State)>(new CommandDefinition(
            "select amount, state from energy_holds where attempt_id = @Attempt and account_id = @Account for update", link, tx, cancellationToken: ct));
        if (hold.State != "charged") throw new InvalidOperationException("A voided participant's hold is not charged.");
        await conn.ExecuteAsync(new CommandDefinition("select 1 from inventories where account_id = @Account for update", link, tx, cancellationToken: ct));

        DateTime now;
        var refunded = 0;
        if (hold.Amount > 0)
        {
            var materialized = await EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, accountId, ct)
                ?? throw new InvalidOperationException("A positive energy hold has no wallet to refund into.");
            now = materialized.Now;
            refunded = (int)((long)hold.Amount * materialized.Current.VoidRefundPct / 100);
            if (refunded > 0)
            {
                var after = EnergyMath.Credit(materialized.Wallet.Balance, refunded);
                await SettlementStore.ExactlyOneAsync(conn, tx, ct, "update energy_wallets set balance = @after where account_id = @Account", new { after, link.Account });
                await SettlementStore.ExactlyOneAsync(conn, tx, ct,
                    """
                    insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key)
                    values (@Account, @Attempt, 'void_refund', @refunded, @after, @key)
                    """, new { link.Account, link.Attempt, refunded, after, key = RefundKey(attemptId) });
            }
        }
        else now = SettlementStore.Utc(await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("select clock_timestamp()", transaction: tx, cancellationToken: ct)));
        await SettlementStore.ExactlyOneAsync(conn, tx, ct,
            "update energy_holds set state = 'refunded' where attempt_id = @Attempt and account_id = @Account and state = 'charged'", link);
        var version = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "update inventories set version = version + 1 where account_id = @Account returning version", link, tx, cancellationToken: ct));

        var facts = await conn.QuerySingleAsync<(string Catalogue, string Snapshot)>(new CommandDefinition(
            """
            select a.catalogue_version, p.loadout_snapshot::text from mission_attempts a join attempt_participants p using (attempt_id)
            where a.attempt_id = @Attempt and p.account_id = @Account
            """, link, tx, cancellationToken: ct));
        var insurance = await conn.QuerySingleOrDefaultAsync<(string PolicyId, int PolicyVersion, string Terms)?>(new CommandDefinition(
            "select policy_id, policy_version, terms::text from attempt_insurance where attempt_id = @Attempt and account_id = @Account", link, tx, cancellationToken: ct));
        var snapshot = insurance is { } i ? new InsuranceSnapshot(i.PolicyId, i.PolicyVersion, InsuranceTerms.Parse(i.Terms)) : null;
        var receipt = ReceiptWriter.ForVoid(attemptId, facts.Catalogue, LoadoutSnapshot.Parse(facts.Snapshot), snapshot, hold.Amount, refunded, version, now);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into participant_settlements (attempt_id, account_id, outcome, void_basis, catalogue_version, receipt, settled_at)
            values (@Attempt, @Account, 'infra_void', @basis, @Catalogue, @receipt, @now)
            """, new { link.Attempt, link.Account, basis, facts.Catalogue, receipt = System.Text.Encoding.UTF8.GetString(receipt), now }, tx, cancellationToken: ct));
        await SettlementStore.SettleStatesAsync(conn, tx, link, attemptState, ct);
        return refunded;
    }

    /// <summary>The ledger key of a participant's void refund (0009: exactly 'void_refund:' and the attempt).</summary>
    public static string RefundKey(Guid attemptId) => "void_refund:" + attemptId.ToString("D");
}
