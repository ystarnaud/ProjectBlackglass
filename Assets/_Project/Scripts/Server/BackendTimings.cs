using Blackglass.Backend.Adapter;

namespace Blackglass
{
    /// <summary>
    /// The process's provisional backend timings (Stage 13, plan §3.4; owner ruling S13-D6): configuration with bounded ranges, each
    /// overridable on the command line (ServerOptions), and tested with controlled clocks. They bound how long the process waits for
    /// an answer; none of them is evidence of anything: a timeout ends a wait, it never invents a failure or discards a result.
    /// </summary>
    public sealed class BackendTimings
    {
        public double RequestTimeoutSeconds = 10;
        public double RegisterBoundSeconds = 60;
        public double ReadyBoundSeconds = 30;
        public double StartGraceSeconds = 30;
        public double HeartbeatSeconds = 10;
        /// <summary>The backend's process lease (spec §7.6): unreachable after this long without a successful call.</summary>
        public double LeaseSeconds = 30;
        /// <summary>§H step 3: the recorded notice ends after at most this long, whatever happens.</summary>
        public double RecordedBoundSeconds = 30;
        public double RedeemBoundSeconds = 30;
        /// <summary>The best-effort failed report before an exit.</summary>
        public double FailedReportBoundSeconds = 5;
        public int OutboxResubmissions = 3;
        public double OutboxResubmitDelaySeconds = 2;
        public double DrainSeconds = 10;
        /// <summary>The longest join window the process believes from the backend (a far-future stamp is clamped).</summary>
        public double MaxJoinWindowSeconds = 600;

        public RetryPolicy Retry => new RetryPolicy { AttemptTimeoutSeconds = RequestTimeoutSeconds };
    }
}
