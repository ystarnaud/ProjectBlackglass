using System;
using System.Collections.Generic;
using UnityEngine.Networking;

namespace Blackglass.Backend.Adapter
{
    /// <summary>
    /// The production transport (Stage 13), the only UnityWebRequest use in the project: one request per exchange, polled on the main
    /// thread (no waits, no threads of its own). It attaches the Authorization header from the credential, sends the exact body bytes
    /// as application/json, never follows a redirect, enforces its own deadline (Abort), and gives up a body once it passes
    /// MaxResponseBytes (Abort; DownloadHandlerBuffer can't truncate). It logs nothing: callers report a status and a problem code.
    /// </summary>
    public sealed class UnityWebRequestTransport : IHttpTransport
    {
        public const int MaxResponseBytes = 262144;

        sealed class Live
        {
            public HttpExchange Exchange;
            public UnityWebRequest Request;
            public UnityWebRequestAsyncOperation Operation;
            public double Deadline;
        }

        readonly string baseUrl;
        readonly ProcessCredential credential;
        readonly List<Live> live = new List<Live>();

        public UnityWebRequestTransport(BackendEndpoint endpoint, ProcessCredential credential)
        {
            baseUrl = (endpoint ?? throw new ArgumentNullException(nameof(endpoint))).BaseUrl;
            this.credential = credential ?? throw new ArgumentNullException(nameof(credential));
        }

        public HttpExchange Send(HttpMethodKind method, string path, byte[] jsonBody, double timeoutSeconds, double now)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/') throw new ArgumentException("A path starts with '/'.", nameof(path));
            if (jsonBody == null) throw new ArgumentNullException(nameof(jsonBody));
            var exchange = new HttpExchange();
            UnityWebRequest request = null;
            try
            {
                request = new UnityWebRequest(baseUrl + path, UnityWebRequest.kHttpVerbPOST)
                {
                    uploadHandler = new UploadHandlerRaw(jsonBody) { contentType = "application/json" },
                    downloadHandler = new DownloadHandlerBuffer(),
                    redirectLimit = 0,
                    disposeUploadHandlerOnDispose = true,
                    disposeDownloadHandlerOnDispose = true,
                };
                request.SetRequestHeader("Authorization", credential.AuthorizationHeaderValue());
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                var entry = new Live { Exchange = exchange, Request = request, Deadline = now + Math.Max(0.1, timeoutSeconds) };
                entry.Operation = request.SendWebRequest();
                live.Add(entry);
            }
            catch (Exception)
            {
                // UnityWebRequest refuses synchronously (insecure HTTP, an invalid URL): no request went out. The exception text is
                // not kept; it never holds the credential, but nothing needs it either.
                request?.Dispose();
                exchange.Complete(0, null, transportError: true);
            }
            return exchange;
        }

        public void Tick(double now)
        {
            for (var i = live.Count - 1; i >= 0; i--)
            {
                var entry = live[i];
                if (Step(entry, now))
                {
                    entry.Request.Dispose();
                    live.RemoveAt(i);
                }
            }
        }

        public void Abort(HttpExchange exchange)
        {
            var index = live.FindIndex(e => ReferenceEquals(e.Exchange, exchange));
            if (index < 0) return;
            var entry = live[index];
            live.RemoveAt(index);
            entry.Request.Abort();
            entry.Exchange.Fail(HttpOutcome.Aborted);
            entry.Request.Dispose();
        }

        public void AbortAll()
        {
            foreach (var entry in live)
            {
                entry.Request.Abort();
                entry.Exchange.Fail(HttpOutcome.Aborted);
                entry.Request.Dispose();
            }
            live.Clear();
        }

        // True when the exchange is finished.
        static bool Step(Live entry, double now)
        {
            var request = entry.Request;
            if (request.downloadedBytes > (ulong)MaxResponseBytes)
            {
                var status = (int)request.responseCode;
                request.Abort();
                entry.Exchange.Complete(status, null, null, oversize: true);
                return true;
            }
            if (!entry.Operation.isDone)
            {
                if (now < entry.Deadline) return false;
                request.Abort();
                entry.Exchange.Fail(HttpOutcome.Timeout);
                return true;
            }
            var code = (int)request.responseCode;
            switch (request.result)
            {
                case UnityWebRequest.Result.Success:
                case UnityWebRequest.Result.ProtocolError:
                    entry.Exchange.Complete(code, request.downloadHandler.data ?? new byte[0], request.GetResponseHeader("Retry-After"),
                        redirect: code >= 300 && code < 400);
                    return true;
                default:
                    // A redirect refused by redirectLimit = 0 is reported as a connection error with the 3xx status: definitive.
                    if (code >= 300 && code < 400) entry.Exchange.Complete(code, null, null, redirect: true);
                    else entry.Exchange.Fail(HttpOutcome.ConnectionFailed);
                    return true;
            }
        }
    }
}
