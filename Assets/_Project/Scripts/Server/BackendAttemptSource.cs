using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;

namespace Blackglass
{
    public enum RegistrationStatus { Pending, Loaded, Failed }

    /// <summary>
    /// The authenticated backend registration (Stage 13; owner ruling S13-D8): POST …/sessions with this build's identity, retried
    /// under its own bound, polled from the main thread (never a synchronous wait). On success the bootstrap hands the answer to the
    /// same validation the fixture uses (AttemptStart). It never falls back to a fixture, and never logs the answer (it holds the seed).
    /// Not an IAttemptSource: that synchronous seam stays the fixture's, unchanged.
    /// </summary>
    public sealed class BackendAttemptSource
    {
        readonly BackendCall call;
        RegistrationStatus status = RegistrationStatus.Pending;
        SessionRegisterResponse response;
        string problem;

        public BackendAttemptSource(ProcessApi api, string buildRevision, string catalogueVersion, double bound, double now) =>
            call = api.Register(new SessionRegisterRequest { buildRevision = buildRevision, catalogueVersion = catalogueVersion }, now + bound, now);

        /// <summary>Pending until the call ends; then Loaded with the answer, or Failed with one line (a status and a code, never a body).</summary>
        public RegistrationStatus Poll(double now, out SessionRegisterResponse registered, out string refusal)
        {
            if (status == RegistrationStatus.Pending)
            {
                call.Tick(now);
                if (call.IsFinished) Finish();
            }
            registered = response;
            refusal = problem;
            return status;
        }

        public void Abort() => call.Abort();

        void Finish()
        {
            switch (call.State)
            {
                case CallState.Succeeded:
                    if (ProcessApi.TryRegistered(call, out response, out var invalid)) { status = RegistrationStatus.Loaded; return; }
                    problem = "register_bad_answer " + invalid;
                    break;
                case CallState.Refused:
                    problem = "register_refused " + call.Status + " " + call.ProblemCode;
                    break;
                case CallState.Unauthorized:
                    problem = "register_unauthorized";
                    break;
                default:
                    problem = "register_gave_up after " + call.Attempts + " attempts";
                    break;
            }
            status = RegistrationStatus.Failed;
        }
    }
}
