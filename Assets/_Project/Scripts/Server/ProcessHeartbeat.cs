using System;
using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;

namespace Blackglass
{
    /// <summary>
    /// The process heartbeat (Stage 13, spec §11.2, B10): from registration until exit, every HeartbeatSeconds, never two in flight;
    /// the body names the session, the phase and the participant's connected flag. BackendReachable turns false after the lease (30 s)
    /// without a successful call and true again on the next success (Stage 15 counts windows with it). Only a 401 stops it (the
    /// credential ended: the attempt was cancelled, settled or revoked); any other answer is logged once per code and the beat goes on.
    /// </summary>
    public sealed class ProcessHeartbeat
    {
        readonly ProcessApi api;
        readonly string session;
        readonly string accountId;
        readonly Func<string> phase;
        readonly Func<bool> connected;
        readonly BackendTimings timings;
        readonly Action<string> log;
        readonly System.Collections.Generic.HashSet<string> logged = new System.Collections.Generic.HashSet<string>();
        BackendCall call;
        double next, lastSuccess, lastNow;
        bool stopped;

        public ProcessHeartbeat(ProcessApi api, string processSessionId, string accountId, Func<string> phase, Func<bool> connected, BackendTimings timings,
            Action<string> log, double now)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            session = processSessionId;
            this.accountId = accountId;
            this.phase = phase;
            this.connected = connected;
            this.timings = timings;
            this.log = log ?? (_ => { });
            next = now + timings.HeartbeatSeconds;
            lastSuccess = lastNow = now; // registration just succeeded
        }

        public bool BackendReachable => lastNow - lastSuccess <= timings.LeaseSeconds;
        public bool Unauthorized { get; private set; }
        public int Sent { get; private set; }

        public void Tick(double now)
        {
            lastNow = now;
            if (stopped || Unauthorized) return;
            if (call != null)
            {
                call.Tick(now);
                if (!call.IsFinished) return;
                Finished(call, now);
                call = null;
            }
            if (Unauthorized || now < next) return;
            next = now + timings.HeartbeatSeconds;
            Sent++;
            call = api.Heartbeat(new ProcessHeartbeatRequest
            {
                processSessionId = session,
                phase = phase(),
                participants = new[] { new ParticipantConnectionDto { accountId = accountId, connected = connected() } },
            }, now + timings.HeartbeatSeconds, now);
        }

        public void Stop()
        {
            stopped = true;
            call?.Abort();
            call = null;
        }

        void Finished(BackendCall done, double now)
        {
            switch (done.State)
            {
                case CallState.Succeeded:
                    lastSuccess = now;
                    break;
                case CallState.Unauthorized:
                    Unauthorized = true;
                    log("heartbeat unauthorized: the process credential ended");
                    break;
                case CallState.Refused:
                    lastSuccess = now; // the backend answered: it is reachable
                    if (logged.Add(done.ProblemCode)) log("heartbeat refused " + done.Status + " " + done.ProblemCode);
                    break;
            }
        }
    }
}
