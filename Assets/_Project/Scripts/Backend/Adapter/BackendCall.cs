using System;
using System.Globalization;
using UnityEngine;

namespace Blackglass.Backend.Adapter
{
    /// <summary>Provisional retry values (spec §19 style: configuration, never hard-coded elsewhere).</summary>
    public sealed class RetryPolicy
    {
        public double AttemptTimeoutSeconds = 10;
        public double InitialBackoffSeconds = 0.5;
        public double MaxBackoffSeconds = 8;
        public double JitterFraction = 0.2;
    }

    /// <summary>The jitter source: a private System.Random, never UnityEngine.Random (the mission's hit rolls use that one).</summary>
    public static class BackendRandom
    {
        public static Func<double> Create()
        {
            var random = new System.Random();
            return random.NextDouble;
        }
    }

    public enum CallState { Running, Succeeded, Refused, Unauthorized, GaveUp }

    /// <summary>
    /// One logical backend call (Stage 13, plan §3.3): the same body bytes on every attempt, so a retry is the same request and the
    /// backend's idempotency answers a lost response with the stored result. 2xx succeeds; 401 is Unauthorized; 408, 429, 5xx, a
    /// timeout or no connection is retried with backoff (0.5 s doubling to 8 s, ±20 % jitter; Retry-After honoured up to the cap)
    /// until the deadline, then GaveUp; every other answer (400, 404, 409, 413, 415, 422, 3xx, 403, …, an oversize body, a redirect)
    /// is a definitive Refused with the ProblemDetails code, as is a request the transport could not send ("transport"). Never
    /// retries a definitive answer, never sends once the deadline has passed (even on a late tick), and never lets an attempt's
    /// timeout run past it. The owner ticks the transport first, then the call; nothing blocks and nothing throws.
    /// </summary>
    public sealed class BackendCall
    {
        [Serializable]
        sealed class Problem
        {
            public string code;
        }

        readonly IHttpTransport transport;
        readonly string path;
        readonly byte[] body;
        readonly RetryPolicy policy;
        readonly double deadline;
        readonly Func<double> random01;
        HttpExchange exchange;
        double nextSend;
        int retries;

        public BackendCall(IHttpTransport transport, string path, byte[] body, RetryPolicy policy, double deadline, Func<double> random01, double now)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.path = path ?? throw new ArgumentNullException(nameof(path));
            this.body = (byte[])(body ?? throw new ArgumentNullException(nameof(body))).Clone();
            this.policy = policy ?? new RetryPolicy();
            this.deadline = deadline;
            this.random01 = random01 ?? throw new ArgumentNullException(nameof(random01));
            if (now >= deadline) { State = CallState.GaveUp; return; }
            SendNow(now);
        }

        public CallState State { get; private set; } = CallState.Running;
        /// <summary>The last completed status (0 when none completed).</summary>
        public int Status { get; private set; }
        /// <summary>The ProblemDetails code of a Refused answer: a word of letters, at most 64; "none", "oversize" or "redirect" otherwise.</summary>
        public string ProblemCode { get; private set; } = "none";
        /// <summary>The body of a Succeeded answer.</summary>
        public byte[] Body { get; private set; }
        public int Attempts { get; private set; }
        public bool IsFinished => State != CallState.Running;

        public void Tick(double now)
        {
            if (State != CallState.Running) return;
            if (exchange == null)
            {
                if (now >= deadline) { State = CallState.GaveUp; return; } // a late tick: the bound has passed, never send again
                if (now >= nextSend) SendNow(now);
                return;
            }
            if (!exchange.IsDone) return;
            var done = exchange;
            exchange = null;
            if (done.Outcome == HttpOutcome.Aborted) { State = CallState.GaveUp; return; }
            if (done.Outcome != HttpOutcome.Completed) { Retry(now, null); return; }
            Status = done.Status;
            if (done.TransportError) { Refuse("transport"); return; }
            if (done.Oversize) { Refuse("oversize"); return; }
            if (done.Redirect) { Refuse("redirect"); return; }
            if (done.Status >= 200 && done.Status < 300) { Body = done.Body ?? new byte[0]; State = CallState.Succeeded; return; }
            if (done.Status == 401) { State = CallState.Unauthorized; return; }
            if (done.Status == 408 || done.Status == 429 || done.Status >= 500) { Retry(now, done.RetryAfter); return; }
            Refuse(CodeOf(done.Body));
        }

        /// <summary>Gives up now: the request in flight is stopped on the wire (the transport aborts it).</summary>
        public void Abort()
        {
            if (State != CallState.Running) return;
            if (exchange != null) transport.Abort(exchange);
            exchange = null;
            State = CallState.GaveUp;
        }

        void SendNow(double now)
        {
            Attempts++;
            try { exchange = transport.Send(HttpMethodKind.Post, path, body, Math.Min(policy.AttemptTimeoutSeconds, deadline - now), now); }
            catch (Exception) { exchange = null; Refuse("transport"); } // a transport must not throw; one that does is a definitive refusal
        }

        void Refuse(string code)
        {
            ProblemCode = code;
            State = CallState.Refused;
        }

        void Retry(double now, string retryAfter)
        {
            retries++;
            var backoff = Math.Min(policy.MaxBackoffSeconds, policy.InitialBackoffSeconds * Math.Pow(2, retries - 1));
            var delay = backoff * (1 + policy.JitterFraction * (2 * random01() - 1));
            // Retry-After is a floor (capped), never a way to resend faster than the backoff.
            if (retryAfter != null && int.TryParse(retryAfter.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                delay = Math.Max(delay, Math.Min(policy.MaxBackoffSeconds, seconds));
            nextSend = now + Math.Max(0, delay);
            if (nextSend >= deadline) State = CallState.GaveUp;
        }

        static string CodeOf(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "none";
            try
            {
                var problem = JsonUtility.FromJson<Problem>(System.Text.Encoding.UTF8.GetString(bytes));
                return problem != null && IsCode(problem.code) ? problem.code : "none";
            }
            catch (ArgumentException) { return "none"; }
        }

        static bool IsCode(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 64) return false;
            foreach (var c in text)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) return false;
            return true;
        }
    }
}
