using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Blackglass.Backend.Core;
using UnityEngine;

namespace Blackglass.Backend.Adapter
{
    /// <summary>A ticket the backend redeemed for this process session: the only value a start can be built from (Stage 13, §6).</summary>
    public sealed class Redeemed
    {
        internal Redeemed(string accountId, IReadOnlyList<string> operativeIds)
        {
            AccountId = accountId;
            OperativeIds = new ReadOnlyCollection<string>(new List<string>(operativeIds));
        }

        public string AccountId { get; }
        public IReadOnlyList<string> OperativeIds { get; }
    }

    /// <summary>
    /// The mission process's backend routes (ProcessEndpoints.cs), as typed calls over one transport: /server/v1/attempts/{aid}/
    /// sessions, ready, tickets/redeem, participants/{acc}/start, heartbeat, participants/{acc}/recorded and failed. Nothing else:
    /// the process never polls a status or a receipt and never relays a result (the agent does, S9-B6). Bodies are JsonUtility over
    /// Stage 11's DTOs (the process-v1 vectors pin their bytes). Answers are validated after parsing, because JsonUtility fills
    /// missing or mistyped members with defaults: an answer that doesn't hold is a definitive refusal, never a success.
    /// </summary>
    public sealed class ProcessApi
    {
        static readonly Regex Timestamp = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z\z", RegexOptions.CultureInvariant);
        const string TicketPrefix = "jt1_";
        const string CanonicalLast = "AEIMQUYcgkosw048";

        readonly IHttpTransport transport;
        readonly RetryPolicy policy;
        readonly Func<double> random01;

        public ProcessApi(IHttpTransport transport, string attemptId, RetryPolicy policy, Func<double> random01)
        {
            if (!CanonicalIds.IsAttemptId(attemptId)) throw new ArgumentException("Not a canonical attempt id.", nameof(attemptId));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            AttemptId = attemptId;
            this.policy = policy ?? new RetryPolicy();
            this.random01 = random01 ?? throw new ArgumentNullException(nameof(random01));
        }

        public string AttemptId { get; }

        public static string Route(string attemptId, string tail) => "/server/v1/attempts/" + attemptId + "/" + tail;

        public BackendCall Register(SessionRegisterRequest body, double deadline, double now) =>
            Call("sessions", Json(body ?? throw new ArgumentNullException(nameof(body))), deadline, now);

        /// <summary>The ready report's exact bytes (the same JsonUtility bytes the bootstrap logs), sent unchanged on every retry.</summary>
        public BackendCall Ready(byte[] readyRequestJson, double deadline, double now) =>
            Call("ready", readyRequestJson ?? throw new ArgumentNullException(nameof(readyRequestJson)), deadline, now);

        public BackendCall Redeem(string processSessionId, string ticket, double deadline, double now)
        {
            Session(processSessionId);
            if (!IsTicket(ticket)) throw new ArgumentException("Not a well-formed join ticket.", nameof(ticket)); // never the ticket in the message
            return Call("tickets/redeem", Json(new RedeemRequest { processSessionId = processSessionId, ticket = ticket }), deadline, now);
        }

        public BackendCall Start(string processSessionId, string accountId, double deadline, double now)
        {
            Session(processSessionId);
            return Call("participants/" + Account(accountId) + "/start", Json(new StartRequest { processSessionId = processSessionId }), deadline, now);
        }

        public BackendCall Heartbeat(ProcessHeartbeatRequest body, double deadline, double now) =>
            Call("heartbeat", Json(body ?? throw new ArgumentNullException(nameof(body))), deadline, now);

        public BackendCall Recorded(string accountId, string manifestHash, double deadline, double now)
        {
            if (!CanonicalIds.IsSha256(manifestHash)) throw new ArgumentException("Not a sha256: manifest hash.", nameof(manifestHash));
            return Call("participants/" + Account(accountId) + "/recorded", Json(new RecordedRequest { manifestHash = manifestHash }), deadline, now);
        }

        public BackendCall Failed(string processSessionId, string reason, double deadline, double now)
        {
            Session(processSessionId);
            if (!CanonicalIds.IsWord(reason, 64)) throw new ArgumentException("A failure reason is a lowercase word of up to 64 characters.", nameof(reason));
            return Call("failed", Json(new FailedRequest { processSessionId = processSessionId, reason = reason }), deadline, now);
        }

        /// <summary>A join ticket's shape: "jt1_" and 43 canonical base64url characters (the backend's OpaqueSecret).</summary>
        public static bool IsTicket(string text)
        {
            if (text == null || text.Length != 47 || string.CompareOrdinal(text, 0, TicketPrefix, 0, 4) != 0) return false;
            for (var i = 4; i < 47; i++)
            {
                var c = text[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
            }
            return CanonicalLast.IndexOf(text[46]) >= 0;
        }

        /// <summary>A backend timestamp: UTC, 'Z', 0 to 7 fractional digits (System.Text.Json over PostgreSQL microseconds).</summary>
        public static bool TryParseTimestamp(string text, out DateTime utc)
        {
            utc = default;
            if (text == null || !Timestamp.IsMatch(text)) return false;
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) || value.Kind != DateTimeKind.Utc) return false;
            utc = value;
            return true;
        }

        public static bool TryRegistered(BackendCall call, out SessionRegisterResponse response, out string problem)
        {
            response = null;
            if (!TryParse(call, out response, out problem)) return false;
            if (!CanonicalIds.IsUuid(response.processSessionId)) problem = "bad_session_id";
            else if (response.missionTemplate == null || string.IsNullOrEmpty(response.missionTemplate.id)) problem = "no_template";
            else if (response.participants == null || response.participants.Length == 0) problem = "no_participants";
            else if (response.deadlines == null || !TryParseTimestamp(response.deadlines.launch, out _)) problem = "bad_deadline";
            if (problem == null) return true;
            response = null;
            return false;
        }

        public static bool TryReady(BackendCall call, out DateTime joinDeadlineUtc, out string problem)
        {
            joinDeadlineUtc = default;
            if (!TryParse(call, out ReadyResponse response, out problem)) return false;
            if (TryParseTimestamp(response.joinDeadline, out joinDeadlineUtc)) return true;
            problem = "bad_join_deadline";
            return false;
        }

        public static bool TryRedeemed(BackendCall call, out Redeemed redeemed, out string problem)
        {
            redeemed = null;
            if (!TryParse(call, out RedeemResponse response, out problem)) return false;
            if (!Base58Id.TryParse(response.accountId, out _)) { problem = "bad_account"; return false; }
            if (response.operativeIds == null || response.operativeIds.Length == 0) { problem = "no_operatives"; return false; }
            foreach (var id in response.operativeIds)
                if (!Base58Id.TryParse(id, out _)) { problem = "bad_operative"; return false; }
            redeemed = new Redeemed(response.accountId, response.operativeIds);
            return true;
        }

        public static bool TryStarted(BackendCall call, out StartResponse response, out string problem)
        {
            if (!TryParse(call, out response, out problem)) return false;
            if (response.started && TryParseTimestamp(response.startedAt, out _)) return true;
            problem = response.started ? "bad_started_at" : "not_started";
            response = null;
            return false;
        }

        BackendCall Call(string tail, byte[] body, double deadline, double now) =>
            new BackendCall(transport, Route(AttemptId, tail), body, policy, deadline, random01, now);

        static byte[] Json(object dto) => Encoding.UTF8.GetBytes(JsonUtility.ToJson(dto));

        static void Session(string processSessionId)
        {
            if (!CanonicalIds.IsUuid(processSessionId)) throw new ArgumentException("Not a canonical process session id.", nameof(processSessionId));
        }

        static string Account(string accountId) =>
            Base58Id.TryParse(accountId, out _) ? accountId : throw new ArgumentException("Not a canonical Base58 account id.", nameof(accountId));

        static bool TryParse<T>(BackendCall call, out T value, out string problem) where T : class
        {
            value = null;
            if (call == null || call.State != CallState.Succeeded) { problem = "not_succeeded"; return false; }
            try
            {
                var text = Encoding.UTF8.GetString(call.Body);
                var trimmed = text.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] != '{') { problem = "bad_json"; return false; }
                value = JsonUtility.FromJson<T>(text);
            }
            catch (ArgumentException) { value = null; }
            problem = value == null ? "bad_json" : null;
            return value != null;
        }
    }
}
