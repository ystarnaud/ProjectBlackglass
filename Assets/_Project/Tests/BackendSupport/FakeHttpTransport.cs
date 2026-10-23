using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Blackglass.Backend.Adapter;

namespace Blackglass.Tests
{
    /// <summary>
    /// A scripted IHttpTransport for the Stage 13 tests. Answers are queued per route suffix ("sessions", "ready", "tickets/redeem",
    /// "start", "heartbeat", "recorded", "failed"); a sticky answer applies once the queue is empty; with neither, a request never
    /// answers and times out at its deadline. Drop() is a request the backend applied whose answer was lost (a timeout). Every request
    /// is recorded with its exact body bytes, and OnSend lets a test inspect the world at send time.
    /// </summary>
    public sealed class FakeHttpTransport : IHttpTransport
    {
        public sealed class Sent
        {
            public string Path;
            public byte[] Body;
            public double At;
            public HttpExchange Exchange;
            public string BodyText => Encoding.UTF8.GetString(Body);
        }

        sealed class Script
        {
            public int Status;
            public string Body;
            public string RetryAfter;
            public bool Drop, Refuse, Oversize, Redirect;
            public double Delay;
        }

        sealed class Pending
        {
            public Sent Sent;
            public Script Script;
            public double Deadline;
        }

        readonly Dictionary<string, Queue<Script>> queues = new Dictionary<string, Queue<Script>>();
        readonly Dictionary<string, Script> sticky = new Dictionary<string, Script>();
        readonly List<Pending> pending = new List<Pending>();

        public FakeHttpTransport(string requiredPrefix = null) => RequiredPrefix = requiredPrefix;

        /// <summary>When set, every path must start with it ("/server/v1/attempts/&lt;aid&gt;/").</summary>
        public string RequiredPrefix { get; }
        public List<Sent> Requests { get; } = new List<Sent>();
        public Action<Sent> OnSend;
        public int Aborted { get; private set; }

        public IEnumerable<Sent> To(string suffix) => Requests.Where(r => Matches(r.Path, suffix));
        public IReadOnlyList<string> Routes => Requests.Select(r => Route(r.Path)).ToList();

        public FakeHttpTransport Answer(string suffix, int status, string body = "{}", string retryAfter = null, double delay = 0) =>
            Queue(suffix, new Script { Status = status, Body = body, RetryAfter = retryAfter, Delay = delay });
        public FakeHttpTransport Drop(string suffix) => Queue(suffix, new Script { Drop = true });
        public FakeHttpTransport Refuse(string suffix) => Queue(suffix, new Script { Refuse = true });
        public FakeHttpTransport Oversize(string suffix, int status = 200) => Queue(suffix, new Script { Status = status, Oversize = true });
        public FakeHttpTransport Redirect(string suffix, int status = 302) => Queue(suffix, new Script { Status = status, Redirect = true });
        public FakeHttpTransport AnswerAlways(string suffix, int status, string body = "{}") { sticky[suffix] = new Script { Status = status, Body = body }; return this; }
        public FakeHttpTransport RefuseAlways(string suffix) { sticky[suffix] = new Script { Refuse = true }; return this; }
        public FakeHttpTransport SilentAlways(string suffix) { sticky[suffix] = new Script { Drop = true }; return this; }
        public FakeHttpTransport Clear(string suffix) { queues.Remove(suffix); sticky.Remove(suffix); return this; }

        public HttpExchange Send(HttpMethodKind method, string path, byte[] jsonBody, double timeoutSeconds, double now)
        {
            if (RequiredPrefix != null && !path.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("A request outside the attempt's routes: " + path);
            var sent = new Sent { Path = path, Body = (byte[])jsonBody.Clone(), At = now, Exchange = new HttpExchange() };
            Requests.Add(sent);
            OnSend?.Invoke(sent);
            pending.Add(new Pending { Sent = sent, Script = Next(path), Deadline = now + timeoutSeconds });
            return sent.Exchange;
        }

        public void Tick(double now)
        {
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (p.Sent.Exchange.IsDone) { pending.RemoveAt(i); continue; }
                var s = p.Script;
                if (s != null && !s.Drop && now >= p.Sent.At + s.Delay)
                {
                    if (s.Refuse) p.Sent.Exchange.Fail(HttpOutcome.ConnectionFailed);
                    else p.Sent.Exchange.Complete(s.Status, s.Body == null ? null : Encoding.UTF8.GetBytes(s.Body), s.RetryAfter, s.Oversize, s.Redirect);
                    pending.RemoveAt(i);
                }
                else if (now >= p.Deadline)
                {
                    p.Sent.Exchange.Fail(HttpOutcome.Timeout);
                    pending.RemoveAt(i);
                }
            }
        }

        public void AbortAll()
        {
            foreach (var p in pending)
            {
                if (p.Sent.Exchange.IsDone) continue;
                p.Sent.Exchange.Fail(HttpOutcome.Aborted);
                Aborted++;
            }
            pending.Clear();
        }

        FakeHttpTransport Queue(string suffix, Script script)
        {
            if (!queues.TryGetValue(suffix, out var queue)) queues[suffix] = queue = new Queue<Script>();
            queue.Enqueue(script);
            return this;
        }

        Script Next(string path)
        {
            foreach (var pair in queues)
                if (Matches(path, pair.Key) && pair.Value.Count > 0) return pair.Value.Dequeue();
            foreach (var pair in sticky)
                if (Matches(path, pair.Key)) return pair.Value;
            return null;
        }

        static bool Matches(string path, string suffix) => path.EndsWith("/" + suffix, StringComparison.Ordinal);

        /// <summary>The route without the attempt prefix and account: "sessions", "ready", "tickets/redeem", "start", ….</summary>
        public static string Route(string path)
        {
            var parts = path.Split('/');
            if (parts.Length >= 2 && parts[parts.Length - 3] == "participants") return parts[parts.Length - 1];
            return parts.Length >= 2 && parts[parts.Length - 2] == "tickets" ? "tickets/" + parts[parts.Length - 1] : parts[parts.Length - 1];
        }
    }
}
