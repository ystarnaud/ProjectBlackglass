using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The participant start hold (Stage 13, Task 7; spec §7.5): no start without a ticket the backend redeemed for this session with
    /// exactly the snapshot's account and operatives; the start retried idempotently to its deadline; Started raised once, from a parsed
    /// 200 only; every redeem refusal refuses that ticket only, so a bad ticket is never a denial of service.
    /// </summary>
    public sealed class ParticipantStartHoldTests
    {
        FakeHttpTransport transport;
        StepClock clock;
        AttemptStart attempt;
        ParticipantStartHold hold;
        int started;

        [SetUp]
        public void SetUp()
        {
            transport = new FakeHttpTransport(BackendTestKit.Prefix);
            clock = new StepClock();
            attempt = BackendTestKit.Start();
            hold = new ParticipantStartHold(BackendTestKit.Api(transport), BackendTestKit.Session, attempt, new BackendTimings(), clock.Now + 120);
            started = 0;
            hold.Started += () => started++;
        }

        void Run(double seconds) => clock.Run(transport, hold.Tick, seconds);

        [Test]
        public void A_redeemed_ticket_then_a_committed_start_releases_the_hold_once()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 200, BackendTestKit.Started);
            var admission = hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Assert.That(admission.State, Is.EqualTo(TicketAdmissionState.Pending));
            Run(1);
            Assert.That(hold.State, Is.EqualTo(HoldState.Started));
            Assert.That(started, Is.EqualTo(1));
            Assert.That(admission.State, Is.EqualTo(TicketAdmissionState.Started));
            Assert.That(admission.AccountId, Is.EqualTo(attempt.AccountId));
            Assert.That(admission.OperativeIds, Is.EqualTo(attempt.Snapshot.Select(o => o.OperativeId)));
            Assert.That(transport.Routes, Is.EqualTo(new[] { "tickets/redeem", "start" }));
            Assert.That(transport.To("start").Single().Path, Does.EndWith("/participants/" + attempt.AccountId + "/start"));
        }

        [Test]
        public void The_admission_passes_through_redeemed_before_started()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).SilentAlways("start");
            var admission = hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(0.5);
            Assert.That(admission.State, Is.EqualTo(TicketAdmissionState.Redeemed));
            Assert.That(hold.State, Is.EqualTo(HoldState.Starting));
            Assert.That(hold.Connected, Is.True);
            Assert.That(started, Is.Zero);
        }

        [Test]
        public void Lost_start_answers_are_retried_with_the_same_body_and_start_once()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Drop("start").Drop("start").Answer("start", 200, BackendTestKit.Started);
            hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(60);
            Assert.That(hold.State, Is.EqualTo(HoldState.Started));
            Assert.That(started, Is.EqualTo(1));
            var bodies = transport.To("start").Select(r => r.BodyText).ToArray();
            Assert.That(bodies.Length, Is.EqualTo(3));
            Assert.That(bodies.Distinct().Count(), Is.EqualTo(1));
        }

        [Test]
        public void A_redeem_for_another_account_or_other_operatives_never_starts()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt, account: "BukQL"));
            var admission = hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(1);
            Assert.That(hold.State, Is.EqualTo(HoldState.CouldNotStart));
            Assert.That(hold.Reason, Is.EqualTo("redeem_mismatch"));
            Assert.That(admission.State, Is.EqualTo(TicketAdmissionState.CouldNotStart));
            Assert.That(transport.To("start"), Is.Empty);
        }

        [Test]
        public void Operatives_other_than_the_snapshots_never_start()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt, attempt.Snapshot.Select(o => o.OperativeId).Concat(new[] { "Zz" })));
            hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(1);
            Assert.That(hold.State, Is.EqualTo(HoldState.CouldNotStart));
            Assert.That(transport.To("start"), Is.Empty);
        }

        [TestCase(401, "", AdmissionRefusal.UnknownToken)]
        [TestCase(400, "InvalidBody", AdmissionRefusal.UnknownToken)]
        [TestCase(409, "WrongAttempt", AdmissionRefusal.WrongAttempt)]
        [TestCase(409, "WrongSession", AdmissionRefusal.Unavailable)]
        [TestCase(409, "InvalidState", AdmissionRefusal.Unavailable)]
        public void A_refused_ticket_refuses_that_ticket_only_and_never_stops(int status, string code, AdmissionRefusal refusal)
        {
            transport.Answer("tickets/redeem", status, code.Length == 0 ? "" : BackendTestKit.Problem(code));
            var admission = hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(1);
            Assert.That(admission.State, Is.EqualTo(TicketAdmissionState.Refused));
            Assert.That(admission.Refusal, Is.EqualTo(refusal));
            Assert.That(hold.State, Is.EqualTo(HoldState.Waiting), "back to waiting: no stop");
            Assert.That(transport.To("start"), Is.Empty);
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 200, BackendTestKit.Started);
            hold.AdmitTicket(BackendTestKit.OtherTicket, clock.Now);
            Run(1);
            Assert.That(hold.State, Is.EqualTo(HoldState.Started), "a good ticket after a bad one still starts");
        }

        [Test]
        public void A_redeem_that_gives_up_is_unavailable()
        {
            transport.RefuseAlways("tickets/redeem");
            var admission = hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(40);
            Assert.That(admission.State, Is.EqualTo(TicketAdmissionState.Refused));
            Assert.That(admission.Refusal, Is.EqualTo(AdmissionRefusal.Unavailable));
            Assert.That(hold.State, Is.EqualTo(HoldState.Waiting));
        }

        [TestCase("jt1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [TestCase("pc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [TestCase("")]
        [TestCase(null)]
        public void A_malformed_ticket_is_refused_with_no_request(string ticket)
        {
            var admission = hold.AdmitTicket(ticket, clock.Now);
            Assert.That(admission.State, Is.EqualTo(TicketAdmissionState.Refused));
            Assert.That(admission.Refusal, Is.EqualTo(AdmissionRefusal.UnknownToken));
            Assert.That(transport.Requests, Is.Empty);
            Assert.That(hold.State, Is.EqualTo(HoldState.Waiting));
        }

        [TestCase("Cancelled")]
        [TestCase("DeadlinePassed")]
        [TestCase("NotRedeemed")]
        [TestCase("WrongSession")]
        [TestCase("InvalidState")]
        public void A_refused_start_could_not_start(string code)
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 409, BackendTestKit.Problem(code));
            var admission = hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(1);
            Assert.That(hold.State, Is.EqualTo(HoldState.CouldNotStart));
            Assert.That(hold.Reason, Does.Contain(code));
            Assert.That(admission.State, Is.EqualTo(TicketAdmissionState.CouldNotStart));
            Assert.That(started, Is.Zero);
        }

        [Test]
        public void A_404_start_could_not_start()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 404, BackendTestKit.Problem("ParticipantNotFound"));
            hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(1);
            Assert.That(hold.State, Is.EqualTo(HoldState.CouldNotStart));
        }

        [Test]
        public void A_start_that_never_answers_gives_up_at_the_start_deadline()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 503).RefuseAlways("start");
            hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(130);
            Assert.That(hold.State, Is.EqualTo(HoldState.CouldNotStart));
            Assert.That(hold.Reason, Is.EqualTo("start_gave_up"));
            Assert.That(transport.To("start").Last().At, Is.LessThan(100 + 120));
            Assert.That(started, Is.Zero);
        }

        [Test]
        public void A_start_answer_that_does_not_say_started_never_releases_the_hold()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 200, "{\"started\":false}");
            hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(1);
            Assert.That(hold.State, Is.EqualTo(HoldState.CouldNotStart));
            Assert.That(started, Is.Zero);
        }

        [Test]
        public void A_start_401_is_a_real_credential_end()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 401, "");
            var admission = hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            Run(1);
            Assert.That(hold.State, Is.EqualTo(HoldState.Stopped));
            Assert.That(admission.State, Is.EqualTo(TicketAdmissionState.CouldNotStart));
            Assert.That(admission.Reason, Is.EqualTo("unauthorized"));
        }

        [Test]
        public void Only_one_admission_at_a_time_and_none_after_the_start()
        {
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).SilentAlways("start");
            hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
            var second = hold.AdmitTicket(BackendTestKit.OtherTicket, clock.Now);
            Assert.That(second.State, Is.EqualTo(TicketAdmissionState.Refused));
            Assert.That(second.Refusal, Is.EqualTo(AdmissionRefusal.AlreadyConnected));
            Run(0.5);
            Assert.That(hold.AdmitTicket(BackendTestKit.OtherTicket, clock.Now).Refusal, Is.EqualTo(AdmissionRefusal.AlreadyConnected));
            Assert.That(transport.To("tickets/redeem").Count(), Is.EqualTo(1));
        }

        [Test]
        public void The_ticket_appears_only_in_the_redeem_body_and_never_in_a_log()
        {
            var lines = new System.Collections.Generic.List<string>();
            Application.LogCallback capture = (text, trace, type) => lines.Add(text + trace);
            Application.logMessageReceived += capture;
            try
            {
                transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 409, BackendTestKit.Problem("Cancelled"));
                hold.AdmitTicket(BackendTestKit.Ticket, clock.Now);
                Run(1);
            }
            finally { Application.logMessageReceived -= capture; }
            Assert.That(lines.Any(l => l.Contains(BackendTestKit.Ticket)), Is.False);
            Assert.That(transport.Requests.Where(r => r.BodyText.Contains(BackendTestKit.Ticket)).Select(r => FakeHttpTransport.Route(r.Path)), Is.EqualTo(new[] { "tickets/redeem" }));
            Assert.That(hold.Reason, Does.Not.Contain(BackendTestKit.Ticket));
        }
    }
}
