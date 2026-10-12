using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>What the host did with one message.</summary>
    public enum NetDisposition
    {
        /// <summary>The client has not said hello (or was refused): the message is ignored and not answered.</summary>
        NotAuthenticated,
        /// <summary>The client is over its rate: the message is dropped and not answered.</summary>
        OverBudget,
        /// <summary>The gate decided; Result says whether it was accepted or why not.</summary>
        Handled,
        /// <summary>The client has become abusive: the adapter must disconnect it.</summary>
        Disconnect,
    }

    public readonly struct NetOutcome
    {
        public NetOutcome(NetDisposition disposition, NetResult result)
        {
            Disposition = disposition;
            Result = result;
        }

        public NetDisposition Disposition { get; }
        public NetResult Result { get; }
    }

    /// <summary>
    /// Everything the server decides about the messages of its connected clients, with no networking library in sight
    /// (networking experiment, decision 046): who may join (NetAdmission), that nothing is processed before hello, that a client
    /// that never says hello is dropped after a few seconds, each client's request budget, and when a client is abusive. The
    /// request itself goes to ServerCommandGate. The networking adapter only forwards messages here and carries out the answer.
    /// </summary>
    public sealed class NetSessionHost
    {
        public const double HelloTimeoutSeconds = 5d;
        public const double RequestsPerSecond = 60d;
        public const int Burst = 60;
        public const int AbuseLimit = 200;
        public const double AbuseWindowSeconds = 10d;

        sealed class Session
        {
            public double ConnectedAt;
            public NetConnection Connection;
            public NetBudget Budget;
        }

        readonly ServerCommandGate gate;
        readonly NetAdmission admission;
        readonly Dictionary<int, Session> sessions = new Dictionary<int, Session>();

        public NetSessionHost(ServerCommandGate gate, NetAdmission admission)
        {
            this.gate = gate;
            this.admission = admission;
        }

        public int Accepted { get; private set; }
        public int Rejected { get; private set; }
        public int Dropped { get; private set; }

        public void Connected(int clientId, double now)
        {
            sessions[clientId] = new Session
            {
                ConnectedAt = now,
                Budget = new NetBudget(RequestsPerSecond, Burst, AbuseLimit, AbuseWindowSeconds),
            };
        }

        /// <summary>None means welcomed; anything else means the adapter must tell the client why and disconnect it.</summary>
        public NetRefusal Hello(int clientId, string token, double now, out string attemptId, out int firstOwnedId, out int ownedCount)
        {
            attemptId = admission.AttemptId;
            firstOwnedId = 0;
            ownedCount = 0;
            if (!sessions.TryGetValue(clientId, out var session))
            {
                Connected(clientId, now);
                session = sessions[clientId];
            }
            if (session.Connection != null)
                return NetRefusal.AlreadyConnected;
            var refusal = admission.TryAdmit(clientId, token, out var connection, out firstOwnedId, out ownedCount);
            if (refusal == NetRefusal.None)
                session.Connection = connection;
            return refusal;
        }

        public NetOutcome Request(int clientId, NetEnvelope envelope, double now)
        {
            if (!sessions.TryGetValue(clientId, out var session) || session.Connection == null)
                return new NetOutcome(NetDisposition.NotAuthenticated, default);
            if (!session.Budget.TryTake(now))
            {
                Dropped++;
                return new NetOutcome(session.Budget.NoteRefusal(now) ? NetDisposition.Disconnect : NetDisposition.OverBudget, default);
            }
            var result = gate.Handle(session.Connection, envelope, now);
            if (result.IsAccepted)
            {
                Accepted++;
                return new NetOutcome(NetDisposition.Handled, result);
            }
            Rejected++;
            return new NetOutcome(session.Budget.NoteRefusal(now) ? NetDisposition.Disconnect : NetDisposition.Handled, result);
        }

        public string AccountOf(int clientId) =>
            sessions.TryGetValue(clientId, out var session) && session.Connection != null ? session.Connection.Account : null;

        public void Disconnected(int clientId)
        {
            admission.Release(clientId);
            sessions.Remove(clientId);
        }

        /// <summary>Adds (and forgets) every client that has not said hello within the timeout.</summary>
        public void CollectExpired(double now, List<int> into)
        {
            List<int> found = null;
            foreach (var pair in sessions)
            {
                if (pair.Value.Connection == null && now - pair.Value.ConnectedAt > HelloTimeoutSeconds)
                    (found ?? (found = new List<int>())).Add(pair.Key);
            }
            if (found == null)
                return;
            foreach (var clientId in found)
            {
                sessions.Remove(clientId);
                into.Add(clientId);
            }
        }
    }
}
