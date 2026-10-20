using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Blackglass.Backend.Core
{
    public enum AdmissionStatus { Pending, Admitted, Refused }

    /// <summary>
    /// Why a client is refused. The networking layer maps it onto the existing wire byte NetRefusal (AdmissionRefusals.WireByte):
    /// UnknownToken, WrongAttempt and AlreadyConnected one to one; Timeout and Unavailable (ticket redemption only) also send
    /// UnknownToken, so the network messages stay unchanged.
    /// </summary>
    public enum AdmissionRefusal { None, UnknownToken, WrongAttempt, AlreadyConnected, Timeout, Unavailable }

    public static class AdmissionRefusals
    {
        /// <summary>The NetRefusal byte to send (None 0, UnknownToken 1, WrongAttempt 2, AlreadyConnected 3).</summary>
        public static byte WireByte(AdmissionRefusal refusal)
        {
            switch (refusal)
            {
                case AdmissionRefusal.None: return 0;
                case AdmissionRefusal.WrongAttempt: return 2;
                case AdmissionRefusal.AlreadyConnected: return 3;
                default: return 1; // UnknownToken, Timeout, Unavailable
            }
        }
    }

    /// <summary>
    /// An immutable admission decision, shape only (owner ruling D5). The account is an opaque non-empty string (the ticket
    /// adapter enforces Base58; the harness keeps its free-form token-file accounts); the owned-unit keys are a distinct list of
    /// 0 to 8 opaque keys (backend operative ids for tickets, the harness's unit ids "1".."n": spec §14.6's explicit set); the
    /// capabilities are distinct opaque words the adapter interprets (the harness's "fixture").
    /// </summary>
    public sealed class AdmissionDecision
    {
        public const int MaxOwnedUnits = 8;

        static readonly IReadOnlyList<string> None = new ReadOnlyCollection<string>(new string[0]);

        AdmissionDecision(AdmissionStatus status, AdmissionRefusal refusal, string account, IReadOnlyList<string> keys, IReadOnlyList<string> capabilities)
        {
            Status = status;
            Refusal = refusal;
            Account = account;
            OwnedUnitKeys = keys;
            Capabilities = capabilities;
        }

        public static AdmissionDecision Pending() => new AdmissionDecision(AdmissionStatus.Pending, AdmissionRefusal.None, null, None, None);

        public static AdmissionDecision Admit(string account, IReadOnlyList<string> ownedUnitKeys, IReadOnlyList<string> capabilities)
        {
            if (string.IsNullOrEmpty(account)) throw new ArgumentException("An admitted client has an account.", nameof(account));
            return new AdmissionDecision(AdmissionStatus.Admitted, AdmissionRefusal.None, account,
                Distinct(ownedUnitKeys, MaxOwnedUnits, nameof(ownedUnitKeys)), Distinct(capabilities, int.MaxValue, nameof(capabilities)));
        }

        public static AdmissionDecision Refuse(AdmissionRefusal reason)
        {
            if (reason == AdmissionRefusal.None) throw new ArgumentException("A refusal has a reason.", nameof(reason));
            return new AdmissionDecision(AdmissionStatus.Refused, reason, null, None, None);
        }

        public AdmissionStatus Status { get; }
        public AdmissionRefusal Refusal { get; }
        /// <summary>Set only when Admitted.</summary>
        public string Account { get; }
        public IReadOnlyList<string> OwnedUnitKeys { get; }
        public IReadOnlyList<string> Capabilities { get; }

        static IReadOnlyList<string> Distinct(IReadOnlyList<string> values, int max, string name)
        {
            if (values == null) throw new ArgumentNullException(name);
            if (values.Count > max || values.Any(string.IsNullOrEmpty) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
                throw new ArgumentException("Values must be distinct, non-empty and at most " + max + ".", name);
            return new ReadOnlyCollection<string>(values.ToArray());
        }
    }

    /// <summary>
    /// The mission server's admission boundary (spec §14.6). Implementations, both in Stage 14: the token-file NetAdmission
    /// (the harness, adapted) and NetTicketAdmission (redeems a ticket with the backend without blocking the main thread). The
    /// networking layer calls Poll each tick until the decision is no longer Pending, then maps the owned-unit keys to unit ids
    /// itself. Library-free: no connection objects, no transport types, no tasks or threads.
    /// </summary>
    public interface INetAdmission
    {
        string AttemptId { get; }

        /// <summary>Clients currently admitted.</summary>
        int Admitted { get; }

        /// <summary>A client's hello token arrived; may return Pending.</summary>
        AdmissionDecision Begin(int clientId, string token);

        /// <summary>The same decision once final; Refuse(UnknownToken) for a client that never began.</summary>
        AdmissionDecision Poll(int clientId);

        /// <summary>A disconnect; unknown ids are ignored.</summary>
        void Release(int clientId);
    }
}
