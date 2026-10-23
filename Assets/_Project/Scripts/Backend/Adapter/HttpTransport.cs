namespace Blackglass.Backend.Adapter
{
    public enum HttpMethodKind { Post }

    public enum HttpOutcome { Pending, Completed, Timeout, ConnectionFailed, Aborted }

    /// <summary>
    /// One request in flight (Stage 13). Pending until the transport finishes it on a later Tick: Completed with an HTTP status (and
    /// the body, at most the transport's cap), Timeout at its own deadline, ConnectionFailed when nothing answered, Aborted when the
    /// owner gave up. Oversize and Redirect mark a Completed answer the caller must treat as a definitive refusal.
    /// </summary>
    public sealed class HttpExchange
    {
        public HttpOutcome Outcome { get; private set; } = HttpOutcome.Pending;
        /// <summary>The HTTP status when Completed (0 otherwise).</summary>
        public int Status { get; private set; }
        /// <summary>The body when Completed and within the cap; null otherwise.</summary>
        public byte[] Body { get; private set; }
        /// <summary>The raw Retry-After header, or null.</summary>
        public string RetryAfter { get; private set; }
        /// <summary>Completed, but the body passed the cap and was not kept.</summary>
        public bool Oversize { get; private set; }
        /// <summary>Completed with a redirect (never followed).</summary>
        public bool Redirect { get; private set; }

        public bool IsDone => Outcome != HttpOutcome.Pending;

        public void Complete(int status, byte[] body, string retryAfter = null, bool oversize = false, bool redirect = false)
        {
            if (IsDone) return;
            Status = status;
            Body = oversize ? null : body;
            RetryAfter = retryAfter;
            Oversize = oversize;
            Redirect = redirect;
            Outcome = HttpOutcome.Completed;
        }

        public void Fail(HttpOutcome outcome)
        {
            if (IsDone || outcome == HttpOutcome.Pending || outcome == HttpOutcome.Completed) return;
            Outcome = outcome;
        }
    }

    /// <summary>
    /// The HTTP seam (Stage 13): the production UnityWebRequestTransport and the tests' scripted fake. Single-threaded: Send and
    /// Tick run on the main thread only; nothing blocks. The transport attaches the credential itself, so callers never see it.
    /// </summary>
    public interface IHttpTransport
    {
        /// <summary>Starts one request to <paramref name="path"/> (under the endpoint) with these exact body bytes.</summary>
        HttpExchange Send(HttpMethodKind method, string path, byte[] jsonBody, double timeoutSeconds, double now);

        /// <summary>Advances every request in flight to <paramref name="now"/> (unscaled seconds).</summary>
        void Tick(double now);

        /// <summary>Ends every pending request as Aborted.</summary>
        void AbortAll();
    }
}
