using System;
using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;

namespace Blackglass
{
    public enum PublisherState { Idle, Writing, Notifying, Publishing, Published, NotPublished }

    /// <summary>
    /// One participant's result publication (Stage 13; Stage 9 §H steps 1–5) over Stage 11's OutboxWorker: Submit (durable .written),
    /// then the recorded notice with the handle's hash, sent only once the handle is Written and bounded by RecordedBoundSeconds (any
    /// answer, a 401, a refusal or the bound ends it), then AllowPublication (.ready), then ResultSaved. An unreachable backend never
    /// blocks publication. A different result already in the outbox is never overwritten (NotPublished outbox_conflict); a failed
    /// write is resubmitted a bounded number of times; a durable .written always stays for the agent (A10-D1). Never a settlement call.
    /// </summary>
    public sealed class ResultPublisher
    {
        readonly OutboxWorker worker;
        readonly ProcessApi api;
        readonly BackendTimings timings;
        PublicationHandle handle;
        BackendCall recorded;
        string accountId, hash;
        byte[] bytes;
        int resubmissions;
        double resubmitAt = double.NaN;

        public ResultPublisher(OutboxWorker worker, ProcessApi api, BackendTimings timings)
        {
            this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.timings = timings ?? throw new ArgumentNullException(nameof(timings));
        }

        public PublisherState State { get; private set; } = PublisherState.Idle;
        /// <summary>NotPublished: outbox_failed or outbox_conflict.</summary>
        public string Reason { get; private set; }
        /// <summary>How the recorded notice ended: ok, conflicting_report, invalid_state, not_found, refused, unauthorized, gave_up, aborted; null before.</summary>
        public string RecordedAnswer { get; private set; }
        /// <summary>A durable .written or .ready exists for the account (whatever happened after).</summary>
        public bool Durable => handle != null && handle.Status.Durable;
        public event Action<string> ResultSaved;

        public void Begin(string account, byte[] canonicalBytes, string manifestHash, double now)
        {
            if (State != PublisherState.Idle) throw new InvalidOperationException("One publication per participant.");
            accountId = account;
            bytes = (byte[])canonicalBytes.Clone();
            hash = manifestHash;
            handle = worker.Submit(accountId, bytes, hash);
            State = PublisherState.Writing;
        }

        public void Tick(double now)
        {
            switch (State)
            {
                case PublisherState.Writing: Writing(now); break;
                case PublisherState.Notifying:
                    recorded.Tick(now);
                    if (recorded.IsFinished) Notified(Answer(recorded));
                    break;
                case PublisherState.Publishing: Publishing(); break;
            }
        }

        /// <summary>The process is quitting: the notice attempt ends now and a durable result is allowed to publish as the worker drains.</summary>
        public void Shutdown()
        {
            if (State == PublisherState.Notifying)
            {
                recorded.Abort();
                Notified("aborted");
            }
            else if (State == PublisherState.Writing && handle != null)
            {
                handle.AllowPublication(); // publication follows the write if it completes during the drain
            }
        }

        void Writing(double now)
        {
            if (!double.IsNaN(resubmitAt))
            {
                if (now < resubmitAt) return;
                resubmitAt = double.NaN;
                handle = worker.Submit(accountId, bytes, hash);
                return;
            }
            var status = handle.Status;
            switch (status.Stage)
            {
                case PublicationStage.Written:
                    recorded = api.Recorded(accountId, hash, now + timings.RecordedBoundSeconds, now);
                    State = PublisherState.Notifying;
                    break;
                case PublicationStage.Published: // an identical .ready already existed
                    Saved();
                    break;
                case PublicationStage.Conflict:
                    NotPublished("outbox_conflict");
                    break;
                case PublicationStage.Failed:
                case PublicationStage.Cancelled:
                    if (status.Durable) { recorded = api.Recorded(accountId, hash, now + timings.RecordedBoundSeconds, now); State = PublisherState.Notifying; break; }
                    if (resubmissions >= timings.OutboxResubmissions) { NotPublished("outbox_failed"); break; }
                    resubmissions++;
                    resubmitAt = now + timings.OutboxResubmitDelaySeconds;
                    break;
            }
        }

        void Notified(string answer)
        {
            RecordedAnswer = answer;
            recorded = null;
            handle.AllowPublication();
            State = PublisherState.Publishing;
        }

        void Publishing()
        {
            var stage = handle.Status.Stage;
            if (stage == PublicationStage.Published) Saved();
            else if (stage == PublicationStage.Conflict) NotPublished("outbox_conflict");
            else if (stage == PublicationStage.Failed || stage == PublicationStage.Cancelled) NotPublished("outbox_failed");
        }

        void Saved()
        {
            State = PublisherState.Published;
            ResultSaved?.Invoke(accountId);
        }

        void NotPublished(string reason)
        {
            Reason = reason;
            State = PublisherState.NotPublished;
        }

        static string Answer(BackendCall call)
        {
            switch (call.State)
            {
                case CallState.Succeeded: return "ok";
                case CallState.Unauthorized: return "unauthorized";
                case CallState.GaveUp: return "gave_up";
                default:
                    if (call.ProblemCode == "ConflictingReport") return "conflicting_report";
                    if (call.ProblemCode == "InvalidState") return "invalid_state";
                    if (call.Status == 404) return "not_found";
                    return "refused";
            }
        }
    }
}
