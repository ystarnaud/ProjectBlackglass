using System.Text.Json.Serialization;
using Blackglass.Accounts.Persistence.Attempts;

namespace Blackglass.Accounts.Api.Attempts;

/// <summary>
/// GET /v1/attempts/{attemptId}: <c>{attemptId, state, participantState, deadlines:{assign, launch?, join?}, energy:{amount,
/// state}, insurance:{covered, policyId, policyVersion}, join?:{hostId, port}}</c>. <c>join</c> appears once the attempt is
/// ready or running (where to connect); a ticket is minted only by POST .../tickets (B7). Never the seed or the retention salt.
/// </summary>
public sealed record AttemptResponse(Guid AttemptId, string State, string ParticipantState, AttemptDeadlinesDto Deadlines, AttemptEnergyDto Energy,
    AttemptInsuranceDto Insurance, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AttemptJoinDto? Join = null)
{
    public static AttemptResponse From(AttemptView view) => new(view.AttemptId, view.State, view.ParticipantState,
        new AttemptDeadlinesDto(view.AssignDeadline, view.LaunchDeadline, view.JoinDeadline),
        new AttemptEnergyDto(view.EnergyAmount, view.EnergyState),
        new AttemptInsuranceDto(view.InsurancePolicyId is not null, view.InsurancePolicyId, view.InsurancePolicyVersion),
        view.State is "ready" or "running" && view.HostId is { } host && view.MissionPort is { } port ? new AttemptJoinDto(host, port) : null);
}

/// <param name="Assign">When the deadline worker cancels the attempt if no host has assigned it (UTC, ISO-8601 with 'Z').</param>
/// <param name="Launch">Once claimed: when it is cancelled unless ready.</param>
/// <param name="Join">Once ready: when unstarted participants are released.</param>
public sealed record AttemptDeadlinesDto(DateTime Assign,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Launch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Join = null);

/// <summary>Where the participant connects: the mission host and port of the attempt's process.</summary>
public sealed record AttemptJoinDto(string HostId, int Port);

/// <summary>POST /v1/attempts/{attemptId}/tickets: the join ticket, shown once, and its expiry.</summary>
public sealed record TicketResponse(string Ticket, DateTime TicketExpiresAt);

/// <summary>The participant's energy hold: the amount reserved at admission and its state (reserved, released, later charged).</summary>
public sealed record AttemptEnergyDto(int Amount, string State);

/// <summary>Whether the participant's insurance snapshot was taken at admission, and of which policy version.</summary>
public sealed record AttemptInsuranceDto(bool Covered, string? PolicyId, int? PolicyVersion);

/// <summary>POST /v1/attempts/{attemptId}/cancel: <c>{participantState:"released", energy:{released, balanceAfter}}</c>, for the
/// release and for every repeat (the stored outcome).</summary>
public sealed record CancelResponse(string ParticipantState, CancelEnergyDto Energy);

/// <param name="Released">The energy given back: the hold's amount (0 for a zero-cost template).</param>
/// <param name="BalanceAfter">The wallet balance right after the release entry; null for a zero amount, which takes no energy
/// path (GET /v1/me reports the balance).</param>
public sealed record CancelEnergyDto(int Released, int? BalanceAfter);
