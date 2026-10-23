using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;

namespace Blackglass
{
    public enum HoldState { Waiting, Redeeming, Starting, Started, CouldNotStart, Stopped }

    public enum TicketAdmissionState { Pending, Redeemed, Refused, Started, CouldNotStart }

    /// <summary>
    /// One ticket's admission (Stage 13, S13-D1): Pending → Redeemed (the backend redeemed it for this session) → Started (the backend
    /// committed the participant's start), or Refused with Stage 11's AdmissionRefusal, or CouldNotStart with a bounded reason.
    /// Stage 14's NetTicketAdmission keeps one per client and maps it onto AdmissionDecision. The same object throughout; read-only
    /// outside this assembly.
    /// </summary>
    public sealed class TicketAdmission
    {
        static readonly IReadOnlyList<string> None = new string[0];

        public TicketAdmissionState State { get; private set; } = TicketAdmissionState.Pending;
        /// <summary>Redeemed and Started only.</summary>
        public string AccountId { get; private set; }
        /// <summary>Redeemed and Started only.</summary>
        public IReadOnlyList<string> OperativeIds { get; private set; } = None;
        /// <summary>Refused only.</summary>
        public AdmissionRefusal Refusal { get; private set; }
        /// <summary>CouldNotStart only: a bounded code.</summary>
        public string Reason { get; private set; }

        internal static TicketAdmission Refused(AdmissionRefusal refusal) => new TicketAdmission { State = TicketAdmissionState.Refused, Refusal = refusal };

        internal void Refuse(AdmissionRefusal refusal) { State = TicketAdmissionState.Refused; Refusal = refusal; }

        internal void Redeem(Redeemed redeemed) { State = TicketAdmissionState.Redeemed; AccountId = redeemed.AccountId; OperativeIds = redeemed.OperativeIds; }

        internal void Start() => State = TicketAdmissionState.Started;

        internal void Fail(string reason) { State = TicketAdmissionState.CouldNotStart; Reason = reason; }
    }

    /// <summary>
    /// The participant's start hold (Stage 13, spec §7.5, plan §6): the mission stays paused until the backend commits this
    /// participant's start, and the start is built only from a ticket the backend redeemed for this process session, whose account
    /// and operatives are exactly the attempt's snapshot. AdmitTicket checks the ticket's shape locally, redeems it, then starts with
    /// idempotent retries (the same body, B8) until the start deadline. Every redeem refusal (the uniform 401 included) refuses that
    /// ticket only and returns to Waiting, so a bad ticket can never stop the mission; a 401 on the start is a real credential end
    /// (Stopped). Started is raised once, from a parsed 200 only. One admission at a time; reconnect is Stage 15's.
    /// </summary>
    public sealed class ParticipantStartHold
    {
        readonly ProcessApi api;
        readonly string session;
        readonly AttemptStart attempt;
        readonly BackendTimings timings;
        readonly double startDeadline;
        TicketAdmission current;
        BackendCall redeem, start;

        public ParticipantStartHold(ProcessApi api, string processSessionId, AttemptStart attempt, BackendTimings timings, double startDeadline)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            session = processSessionId ?? throw new ArgumentNullException(nameof(processSessionId));
            this.attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
            this.timings = timings ?? throw new ArgumentNullException(nameof(timings));
            this.startDeadline = startDeadline;
        }

        public HoldState State { get; private set; } = HoldState.Waiting;
        /// <summary>CouldNotStart or Stopped: a bounded code.</summary>
        public string Reason { get; private set; }
        /// <summary>Admitted (redeemed) and not released: the heartbeat's connected flag until Stage 14 reports real connections.</summary>
        public bool Connected { get; private set; }
        public event Action Started;

        /// <summary>
        /// One ticket. Refused(AlreadyConnected) unless Waiting; Refused(UnknownToken) for a malformed ticket, with no request sent.
        /// The ticket is never logged or kept after the redeem request is built.
        /// </summary>
        public TicketAdmission AdmitTicket(string ticket, double now)
        {
            if (State != HoldState.Waiting) return TicketAdmission.Refused(AdmissionRefusal.AlreadyConnected);
            if (!ProcessApi.IsTicket(ticket)) return TicketAdmission.Refused(AdmissionRefusal.UnknownToken);
            current = new TicketAdmission();
            redeem = api.Redeem(session, ticket, Math.Min(now + timings.RedeemBoundSeconds, startDeadline), now);
            State = HoldState.Redeeming;
            return current;
        }

        /// <summary>The client left (Stage 14 calls this): the connected flag only; a start in flight still completes.</summary>
        public void Release() => Connected = false;

        public void Tick(double now)
        {
            if (State == HoldState.Redeeming)
            {
                redeem.Tick(now);
                if (redeem.IsFinished) Redeemed(now);
            }
            else if (State == HoldState.Starting)
            {
                start.Tick(now);
                if (start.IsFinished) Committed();
            }
        }

        void Redeemed(double now)
        {
            var call = redeem;
            redeem = null;
            if (call.State == CallState.Succeeded && ProcessApi.TryRedeemed(call, out var redeemed, out _))
            {
                if (redeemed.AccountId != attempt.AccountId || !redeemed.OperativeIds.SequenceEqual(attempt.Snapshot.Select(o => o.OperativeId)))
                {
                    current.Fail("redeem_mismatch");
                    CouldNotStart("redeem_mismatch");
                    return;
                }
                current.Redeem(redeemed);
                Connected = true;
                start = api.Start(session, attempt.AccountId, startDeadline, now);
                State = HoldState.Starting;
                return;
            }
            current.Refuse(RefusalOf(call));
            State = HoldState.Waiting;
        }

        static AdmissionRefusal RefusalOf(BackendCall call)
        {
            if (call.State == CallState.Unauthorized) return AdmissionRefusal.UnknownToken;
            if (call.State == CallState.Refused && call.Status == 400) return AdmissionRefusal.UnknownToken;
            if (call.State == CallState.Refused && call.ProblemCode == "WrongAttempt") return AdmissionRefusal.WrongAttempt;
            return AdmissionRefusal.Unavailable; // WrongSession, InvalidState, a bad answer, or a redeem that gave up
        }

        void Committed()
        {
            var call = start;
            start = null;
            switch (call.State)
            {
                case CallState.Succeeded:
                    if (!ProcessApi.TryStarted(call, out _, out _)) { current.Fail("start_bad_answer"); CouldNotStart("start_bad_answer"); return; }
                    current.Start();
                    State = HoldState.Started;
                    Started?.Invoke();
                    return;
                case CallState.Unauthorized:
                    current.Fail("unauthorized");
                    Reason = "unauthorized";
                    State = HoldState.Stopped;
                    return;
                case CallState.Refused:
                    current.Fail("start_refused");
                    CouldNotStart("start_refused " + call.ProblemCode);
                    return;
                default:
                    current.Fail("start_gave_up");
                    CouldNotStart("start_gave_up");
                    return;
            }
        }

        void CouldNotStart(string reason)
        {
            Reason = reason;
            State = HoldState.CouldNotStart;
        }
    }
}
