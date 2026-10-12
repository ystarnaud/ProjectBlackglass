using System;
using System.Collections.Generic;
using System.Linq;

namespace Blackglass
{
    /// <summary>Why a hello was refused (the wire carries this byte in NetRefusedMessage).</summary>
    public enum NetRefusal : byte
    {
        None = 0,
        UnknownToken = 1,
        /// <summary>The token's entry names another mission attempt (a ticket for another attempt).</summary>
        WrongAttempt = 2,
        /// <summary>The token (or the client) is already admitted.</summary>
        AlreadyConnected = 3,
    }

    /// <summary>
    /// Decides who may connect (networking experiment, spec section 3.4): a hello token must match a token-file entry, the entry
    /// must not be bound to another attempt, and a token has at most one connection at a time. A `squad` entry owns friendlies
    /// 1..friendlyCount (NetUnits registers friendlies first); a `none` entry owns nothing. Holds no connection objects.
    /// </summary>
    public sealed class NetAdmission
    {
        readonly Dictionary<string, NetTokenEntry> byToken;
        readonly int friendlyCount;
        readonly Dictionary<int, string> tokenByClient = new Dictionary<int, string>();
        readonly HashSet<string> inUse = new HashSet<string>(StringComparer.Ordinal);

        public NetAdmission(IEnumerable<NetTokenEntry> entries, string attemptId, int friendlyCount)
        {
            AttemptId = attemptId ?? throw new ArgumentNullException(nameof(attemptId));
            this.friendlyCount = friendlyCount;
            byToken = entries.ToDictionary(e => e.Token, StringComparer.Ordinal);
        }

        public string AttemptId { get; }

        /// <summary>How many clients are admitted right now.</summary>
        public int Admitted => tokenByClient.Count;

        public NetRefusal TryAdmit(int clientId, string token, out NetConnection connection, out int firstOwnedId, out int ownedCount)
        {
            connection = null;
            firstOwnedId = 0;
            ownedCount = 0;
            if (string.IsNullOrEmpty(token) || !byToken.TryGetValue(token, out var entry))
                return NetRefusal.UnknownToken;
            if (entry.Attempt != null && !string.Equals(entry.Attempt, AttemptId, StringComparison.Ordinal))
                return NetRefusal.WrongAttempt;
            if (tokenByClient.ContainsKey(clientId) || inUse.Contains(token))
                return NetRefusal.AlreadyConnected;

            tokenByClient[clientId] = token;
            inUse.Add(token);
            ownedCount = entry.OwnsSquad ? friendlyCount : 0;
            firstOwnedId = ownedCount > 0 ? 1 : 0;
            connection = new NetConnection(entry.Account, Enumerable.Range(firstOwnedId, ownedCount));
            return NetRefusal.None;
        }

        /// <summary>The client left: its token can be used again. Unknown clients are ignored.</summary>
        public void Release(int clientId)
        {
            if (!tokenByClient.TryGetValue(clientId, out var token))
                return;
            tokenByClient.Remove(clientId);
            inUse.Remove(token);
        }
    }
}
