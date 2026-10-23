using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Blackglass.Backend.Adapter;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The production transport against a real loopback HTTP listener inside the test (Stage 13, Task 2). The first test is the
    /// owner's S13-D11 check: plain HTTP to 127.0.0.1 under the project's existing Player Settings (insecureHttpOption 0), on this
    /// exact Unity version. Nothing leaves the machine.
    /// </summary>
    public sealed class UnityWebRequestTransportTests
    {
        sealed class Received
        {
            public string Method, Path, Authorization, ContentType, Accept;
            public byte[] Body;
        }

        /// <summary>A loopback listener answering each request with the current responder; never anything but 127.0.0.1.</summary>
        sealed class LoopbackServer : IDisposable
        {
            readonly HttpListener listener = new HttpListener();
            readonly Thread thread;
            public readonly ConcurrentQueue<Received> Requests = new ConcurrentQueue<Received>();
            public Action<HttpListenerResponse> Respond = r => { r.StatusCode = 200; Write(r, "{}"); };
            public int Port { get; }

            public LoopbackServer()
            {
                Port = FreePort();
                listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                listener.Start();
                thread = new Thread(Serve) { IsBackground = true };
                thread.Start();
            }

            void Serve()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = listener.GetContext(); }
                    catch (Exception) { return; }
                    using (var body = new MemoryStream())
                    {
                        context.Request.InputStream.CopyTo(body);
                        Requests.Enqueue(new Received
                        {
                            Method = context.Request.HttpMethod,
                            Path = context.Request.Url.AbsolutePath,
                            Authorization = context.Request.Headers["Authorization"],
                            ContentType = context.Request.ContentType,
                            Accept = context.Request.Headers["Accept"],
                            Body = body.ToArray(),
                        });
                    }
                    try { Respond(context.Response); context.Response.Close(); }
                    catch (Exception) { }
                }
            }

            public static void Write(HttpListenerResponse response, string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                response.ContentType = "application/json";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
            }

            public void Dispose()
            {
                try { listener.Stop(); listener.Close(); } catch (Exception) { }
            }
        }

        static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static double Now => Clock.Elapsed.TotalSeconds;

        LoopbackServer server;
        UnityWebRequestTransport transport;

        [SetUp]
        public void SetUp()
        {
            server = new LoopbackServer();
            BackendEndpoint.TryParse($"http://127.0.0.1:{server.Port}", out var endpoint, out _);
            transport = new UnityWebRequestTransport(endpoint, ProcessCredential.FromTextForTests(ProcessCredentialTests.Valid));
        }

        [TearDown]
        public void TearDown()
        {
            transport.AbortAll();
            server.Dispose();
        }

        IEnumerator Until(HttpExchange exchange, double seconds = 10)
        {
            var end = Now + seconds;
            while (!exchange.IsDone && Now < end)
            {
                transport.Tick(Now);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Loopback_plain_http_is_allowed_under_the_existing_player_settings()
        {
            var exchange = transport.Send(HttpMethodKind.Post, "/server/v1/probe", Encoding.UTF8.GetBytes("{}"), 5, Now);
            yield return Until(exchange);
            Assert.That(exchange.Outcome, Is.EqualTo(HttpOutcome.Completed), "S13-D11: plain HTTP to 127.0.0.1 was not completed; stop for the owner");
            Assert.That(exchange.Status, Is.EqualTo(200));
        }

        // The control for S13-D11: the project's "not allowed" setting is enforced here, so the loopback pass above is the exemption,
        // not an Editor that ignores the setting. 192.0.2.1 is TEST-NET-1 (never routed); the request is refused before any packet.
        [Test]
        public void Plain_http_to_a_non_loopback_address_is_refused_by_the_same_settings()
        {
            using (var request = new UnityEngine.Networking.UnityWebRequest("http://192.0.2.1:9/", "POST"))
            {
                var error = Assert.Catch(() => { request.SendWebRequest(); request.Abort(); });
                Assert.That(error, Is.Not.Null, "plain HTTP to a non-loopback address was not refused: the setting is not enforced in this context");
                Assert.That(error.Message, Does.Contain("nsecure").IgnoreCase);
            }
        }

        [UnityTest]
        public IEnumerator The_request_carries_the_credential_the_json_type_and_the_exact_bytes()
        {
            var body = Encoding.UTF8.GetBytes("{\"processSessionId\":\"x\",\"n\":1}");
            var exchange = transport.Send(HttpMethodKind.Post, "/server/v1/attempts/a/ready", body, 5, Now);
            yield return Until(exchange);
            Assert.That(server.Requests.TryDequeue(out var received), Is.True);
            Assert.That(received.Method, Is.EqualTo("POST"));
            Assert.That(received.Path, Is.EqualTo("/server/v1/attempts/a/ready"));
            Assert.That(received.Authorization, Is.EqualTo("Bearer " + ProcessCredentialTests.Valid));
            Assert.That(received.ContentType, Does.StartWith("application/json"));
            Assert.That(received.Accept, Is.EqualTo("application/json"));
            Assert.That(received.Body, Is.EqualTo(body));
        }

        [UnityTest]
        public IEnumerator A_refusal_completes_with_its_status_body_and_retry_after()
        {
            server.Respond = r => { r.StatusCode = 409; r.Headers["Retry-After"] = "7"; LoopbackServer.Write(r, "{\"code\":\"Cancelled\"}"); };
            var exchange = transport.Send(HttpMethodKind.Post, "/x", new byte[] { 0x7b, 0x7d }, 5, Now);
            yield return Until(exchange);
            Assert.That(exchange.Outcome, Is.EqualTo(HttpOutcome.Completed));
            Assert.That(exchange.Status, Is.EqualTo(409));
            Assert.That(Encoding.UTF8.GetString(exchange.Body), Is.EqualTo("{\"code\":\"Cancelled\"}"));
            Assert.That(exchange.RetryAfter, Is.EqualTo("7"));
        }

        [UnityTest]
        public IEnumerator A_server_that_never_answers_times_out_at_the_given_timeout()
        {
            server.Respond = r => Thread.Sleep(5000);
            var started = Now;
            var exchange = transport.Send(HttpMethodKind.Post, "/x", new byte[] { 0x7b, 0x7d }, 1, Now);
            yield return Until(exchange);
            Assert.That(exchange.Outcome, Is.EqualTo(HttpOutcome.Timeout));
            Assert.That(Now - started, Is.LessThan(3));
        }

        [UnityTest]
        public IEnumerator A_closed_port_fails_to_connect()
        {
            BackendEndpoint.TryParse($"http://127.0.0.1:{FreePort()}", out var closed, out _);
            var other = new UnityWebRequestTransport(closed, ProcessCredential.FromTextForTests(ProcessCredentialTests.Valid));
            var exchange = other.Send(HttpMethodKind.Post, "/x", new byte[] { 0x7b, 0x7d }, 5, Now);
            var end = Now + 10;
            while (!exchange.IsDone && Now < end)
            {
                other.Tick(Now);
                yield return null;
            }
            Assert.That(exchange.Outcome, Is.EqualTo(HttpOutcome.ConnectionFailed));
        }

        [UnityTest]
        public IEnumerator A_redirect_is_never_followed()
        {
            server.Respond = r => { r.StatusCode = 302; r.Headers["Location"] = "http://127.0.0.1:1/elsewhere"; LoopbackServer.Write(r, ""); };
            var exchange = transport.Send(HttpMethodKind.Post, "/x", new byte[] { 0x7b, 0x7d }, 5, Now);
            yield return Until(exchange);
            Assert.That(exchange.Outcome, Is.EqualTo(HttpOutcome.Completed));
            Assert.That(exchange.Redirect, Is.True);
            Assert.That(server.Requests.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator A_body_over_the_cap_is_given_up()
        {
            server.Respond = r => { r.StatusCode = 200; LoopbackServer.Write(r, "{\"x\":\"" + new string('a', 300 * 1024) + "\"}"); };
            var exchange = transport.Send(HttpMethodKind.Post, "/x", new byte[] { 0x7b, 0x7d }, 5, Now);
            yield return Until(exchange);
            Assert.That(exchange.Outcome, Is.EqualTo(HttpOutcome.Completed));
            Assert.That(exchange.Oversize, Is.True);
            Assert.That(exchange.Body, Is.Null);
        }

        [UnityTest]
        public IEnumerator AbortAll_ends_a_pending_exchange()
        {
            server.Respond = r => Thread.Sleep(5000);
            var exchange = transport.Send(HttpMethodKind.Post, "/x", new byte[] { 0x7b, 0x7d }, 30, Now);
            yield return null;
            transport.AbortAll();
            Assert.That(exchange.Outcome, Is.EqualTo(HttpOutcome.Aborted));
        }
    }
}
