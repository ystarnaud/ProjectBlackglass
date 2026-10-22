using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The windowless acceptance script of the networking experiment (spec section 3.6): one role per run, a fixed list of
    /// checks with real-time deadlines, and a final PASS or FAIL line with an exit code like the Checkpoint A diagnostic.
    /// It reads only what the client decoded: the NetClient's answers and record, and the avatars this client was actually sent (NetClient.VisibleStates).
    /// </summary>
    public sealed partial class NetHarness : MonoBehaviour
    {
        public const int ExitPass = 0;
        public const int ExitFailed = 1;
        public const int ExitNoConnection = 2;
        public const string Prefix = "[Harness] ";

        struct Snap
        {
            public int Id;
            public Vector3 Position;
            public int Health;
            public NetOrderKind Order;
            public int Queue;
        }

        [SerializeField] NetClient client;

        readonly List<string> passed = new List<string>();
        bool waitOk;
        bool noConnection;
        string failedStep;
        string failure;

        internal Action<int> Exit = code => Application.Quit(code);
        internal int ExitCode { get; private set; } = -1;
        internal string Summary { get; private set; }
        internal double LatencyMedianMs { get; private set; }
        internal long RoundTripMs { get; private set; }

        internal void Initialize(NetClient netClient) => client = netClient;

        public void Begin(string role) => Begin(role, null, null);

        /// <summary>Starts a role; the `attempt` role also takes the operator's two ground points (NetClientOptions).</summary>
        public void Begin(string role, Vector3? hostile, Vector3? extraction)
        {
            assistHostile = hostile;
            assistExtraction = extraction;
            StartCoroutine(Run(role));
        }

        static double Now => Time.realtimeSinceStartupAsDouble;

        bool Failed => failure != null;

        IEnumerator Run(string role)
        {
            switch (role)
            {
                case "a":
                    yield return Owner();
                    break;
                case "b":
                    yield return Observer();
                    break;
                case "ticket":
                    yield return Refused(NetRefusal.WrongAttempt);
                    break;
                case "badtoken":
                    yield return Refused(NetRefusal.UnknownToken);
                    break;
                case "visibility-a":
                    yield return VisibilityA();
                    break;
                case "visibility-b":
                    yield return VisibilityB();
                    break;
                case "visibility-fault":
                    yield return VisibilityFault();
                    break;
                case "fixture-denied":
                    yield return FixtureDenied();
                    break;
                case "attempt":
                    yield return Attempt();
                    break;
                default:
                    Fail("role", "unknown role '" + role + "'");
                    break;
            }
            Finish(role);
        }

        // ---- roles -------------------------------------------------------------------------------------------------

        IEnumerator Refused(NetRefusal expected)
        {
            yield return Until(() => client.State == NetClientState.Refused || client.State == NetClientState.Ready, 10d);
            if (!waitOk)
                noConnection = true;
            Check("refusal", client.State == NetClientState.Refused && client.Refusal == expected,
                "state " + client.State + " refusal " + client.Refusal + ", expected " + expected);
        }

        IEnumerator Observer()
        {
            yield return Connected();
            if (Failed)
                yield break;
            if (!Check("no ownership", client.OwnedCount == 0, "owns " + client.OwnedCount))
                yield break;
            yield return Wait(2d);
            if (!Check("no units", States().Length == 0, States().Length + " avatars arrived; a client that owns nothing receives none"))
                yield break;

            // The fixture mission's friendlies are ids 1 and 2 (NetUnits numbers friendlies first) and its hostile is 3.
            var cases = new List<KeyValuePair<string, KeyValuePair<NetRequest, NetRejectReason>>>();
            foreach (var id in new[] { 1, 2 })
            {
                var other = id == 1 ? 2 : 1;
                cases.Add(Order("not owner: move intent for unit " + id, new NetMoveIntent(id, Vector3.right), NetRejectReason.NotOwner));
                cases.Add(Order("not owner: move for unit " + id, new NetMoveTo(id, Vector3.zero), NetRejectReason.NotOwner));
                cases.Add(Order("not owner: attack by unit " + id, new NetAttack(id, other), NetRejectReason.NotOwner));
                cases.Add(Order("not owner: stop for unit " + id, new NetStop(id), NetRejectReason.NotOwner));
            }
            cases.Add(Order("unknown: an attack on the hidden hostile", new NetAttack(1, 3), NetRejectReason.UnknownUnit));
            cases.Add(Order("unknown: an attack on a made-up id", new NetAttack(1, 999), NetRejectReason.UnknownUnit));
            foreach (var pair in cases)
            {
                var command = client.Send(pair.Value.Key);
                yield return AwaitResult(command, 3d);
                client.TryGetResult(command, out var reason);
                if (!Check(pair.Key, waitOk && reason == pair.Value.Value, "answered " + reason + ", expected " + pair.Value.Value))
                    yield break;
            }
        }

        static KeyValuePair<string, KeyValuePair<NetRequest, NetRejectReason>> Order(string name, NetRequest request, NetRejectReason expected) =>
            new KeyValuePair<string, KeyValuePair<NetRequest, NetRejectReason>>(name, new KeyValuePair<NetRequest, NetRejectReason>(request, expected));

        IEnumerator Owner()
        {
            yield return Connected();
            if (Failed)
                yield break;
            var own = client.FirstOwnedId;
            var second = own + 1;
            // Hidden information: the hostile is sent only while observed, so the owner role needs the fixture's grant
            // (the VM runs the part 2 roles against the fixture build; see Docs/DedicatedServer.md).
            client.SendFixture(NetFixtureVerb.Observe);
            yield return Until(() => State(own) != null && FindHostile() != null, 10d);
            if (!Check("avatars", waitOk, "the client received its units and the hostile"))
                yield break;
            var hostileId = FindHostile().UnitId;

            // 1. Steering, with a latency sample per cycle.
            yield return Settle();
            var samples = new List<double>();
            for (var cycle = 0; cycle < 10; cycle++)
            {
                var direction = cycle % 2 == 0 ? Vector3.right : Vector3.left;
                var before = Position(own);
                var sentAt = Now;
                client.Send(new NetMoveIntent(own, direction));
                var nextRefresh = sentAt + NetIntentSender.RefreshSeconds;
                var deadline = sentAt + 2d;
                while (Now < deadline)
                {
                    if (Now >= nextRefresh)
                    {
                        client.Send(new NetMoveIntent(own, direction));
                        nextRefresh = Now + NetIntentSender.RefreshSeconds;
                    }
                    if ((Position(own) - before).magnitude >= 0.05f)
                    {
                        samples.Add((Now - sentAt) * 1000d);
                        break;
                    }
                    yield return null;
                }
                var hold = Now + 0.3d;
                while (Now < hold)
                {
                    if (Now >= nextRefresh)
                    {
                        client.Send(new NetMoveIntent(own, direction));
                        nextRefresh = Now + NetIntentSender.RefreshSeconds;
                    }
                    yield return null;
                }
                client.Send(new NetMoveIntent(own, Vector3.zero));
                yield return Wait(0.3d);
            }
            if (!Check("steering", samples.Count >= 5, samples.Count + " of 10 cycles moved the unit"))
                yield break;
            LatencyMedianMs = Median(samples);
            RoundTripMs = client.RoundTripMs;
            Debug.Log(Prefix + FormattableString.Invariant($"latency median {LatencyMedianMs:0.0} ms over {samples.Count} samples; client round trip {RoundTripMs} ms"));

            // 2. A Shift-queued move: the replicated queue shows it, and both moves run.
            yield return Settle();
            var start = Position(own);
            // Toward the hostile: a direction the generator left open between the teams, so the 2 m snap finds a walkable point.
            var toward = Position(hostileId) - start;
            toward.y = 0f;
            toward = toward.sqrMagnitude > 0.01f ? toward.normalized * 1.5f : new Vector3(1.5f, 0f, 0f);
            client.Send(new NetMoveTo(own, start + toward));
            client.Send(new NetMoveTo(own, start, append: true));
            var sawQueue = false;
            var farthest = 0f;
            var queueEnd = Now + 25d;
            while (Now < queueEnd)
            {
                var state = State(own);
                if (state.Queue == 1 && state.Order == NetOrderKind.Move)
                    sawQueue = true;
                farthest = Mathf.Max(farthest, (Position(own) - start).magnitude);
                if (sawQueue && state.Order == NetOrderKind.None && state.Queue == 0)
                    break;
                yield return null;
            }
            Check("queue", sawQueue, "the replicated queue showed one pending move behind the current one");
            var back = (Position(own) - start).magnitude;
            Check("queued moves", farthest >= 0.8f && back <= 1f, FormattableString.Invariant($"went {farthest:0.0} m out and ended {back:0.0} m from the start"));
            if (Failed)
                yield break;

            // 3. An attack resolves on the server and the hostile's replicated health falls.
            yield return Settle();
            var healthBefore = State(hostileId).Health;
            client.Send(new NetAttack(own, hostileId));
            yield return Until(() => State(hostileId) != null && State(hostileId).Health < healthBefore, 60d);
            Check("attack", waitOk, "hostile health " + healthBefore + " -> " + (State(hostileId) != null ? State(hostileId).Health.ToString() : "gone"));
            if (Failed)
                yield break;

            // 4. The six bad requests, each answered with its typed reason, and nothing changes.
            yield return Settle();
            var snapshot = Snapshot();
            var attempt = client.AttemptId;
            var position = Position(own);
            var cases = new List<KeyValuePair<string, KeyValuePair<uint, NetRejectReason>>>();
            cases.Add(Case("a move for a hostile", client.Send(new NetMoveTo(hostileId, position)), NetRejectReason.NotOwner));
            cases.Add(Case("an attack on a friendly", client.Send(new NetAttack(own, second)), NetRejectReason.WrongSide));

            var good = client.Send(new NetStop(own));
            yield return AwaitResult(good, 3d);
            client.TryGetResult(good, out var goodReason);
            Check("a good order", waitOk && goodReason == NetRejectReason.None, "a stop was answered " + goodReason);
            client.ForgetResult(good);
            var reused = client.SendMessage(NetWire.ToMessage(attempt, client.NextSequence(), good, new NetStop(own)));
            cases.Add(Case("a reused command id", reused, NetRejectReason.DuplicateCommand));

            var old = client.SendMessage(NetWire.ToMessage(attempt, 1u, client.NextCommandId(), new NetStop(own)));
            cases.Add(Case("an older sequence number", old, NetRejectReason.StaleSequence));

            var wrong = client.SendMessage(NetWire.ToMessage("wrong-attempt", client.NextSequence(), client.NextCommandId(), new NetStop(own)));
            cases.Add(Case("a wrong attempt id", wrong, NetRejectReason.WrongAttempt));

            var malformed = client.SendMessage(new NetRequestMessage
            {
                AttemptId = attempt,
                Sequence = client.NextSequence(),
                CommandId = client.NextCommandId(),
                Kind = 99,
            });
            cases.Add(Case("a malformed request", malformed, NetRejectReason.Malformed));

            foreach (var pair in cases)
            {
                yield return AwaitResult(pair.Value.Key, 3d);
                client.TryGetResult(pair.Value.Key, out var reason);
                if (!Check("rejects " + pair.Key, waitOk && reason == pair.Value.Value, "answered " + reason + ", expected " + pair.Value.Value))
                    yield break;
            }
            yield return Wait(0.5d);
            var difference = Differences(snapshot, Snapshot());
            Check("no state change", difference == null, difference ?? "every replicated unit is as it was");
        }

        static KeyValuePair<string, KeyValuePair<uint, NetRejectReason>> Case(string name, uint command, NetRejectReason expected) =>
            new KeyValuePair<string, KeyValuePair<uint, NetRejectReason>>(name, new KeyValuePair<uint, NetRejectReason>(command, expected));

        // ---- helpers -----------------------------------------------------------------------------------------------

        IEnumerator Connected()
        {
            yield return Until(() => client.State == NetClientState.Ready || client.State == NetClientState.Refused
                || client.State == NetClientState.Disconnected, 10d);
            if (client.State != NetClientState.Ready)
            {
                noConnection = true;
                Fail("connect", "state " + client.State + (client.Refusal != NetRefusal.None ? " refusal " + client.Refusal : string.Empty));
                yield break;
            }
            Check("connect", true, "welcomed; owns " + client.OwnedCount + " from unit " + client.FirstOwnedId);
        }

        // Stops every owned unit and waits, so the next check starts from an idle squad.
        IEnumerator Settle()
        {
            for (var i = 0; i < client.OwnedCount; i++)
                client.Send(new NetStop(client.FirstOwnedId + i));
            yield return Wait(1d);
        }

        IEnumerator AwaitResult(uint command, double seconds) => Until(() => client.TryGetResult(command, out _), seconds);

        IEnumerator Until(Func<bool> condition, double seconds)
        {
            var end = Now + seconds;
            while (!condition() && Now < end)
                yield return null;
            waitOk = condition();
        }

        static IEnumerator Wait(double seconds)
        {
            var end = Now + seconds;
            while (Now < end)
                yield return null;
        }

        bool Check(string step, bool condition, string detail)
        {
            if (Failed)
                return false;
            if (condition)
            {
                passed.Add(step);
                Debug.Log(Prefix + step + " OK " + detail);
                return true;
            }
            failedStep = step;
            failure = detail;
            Debug.LogWarning(Prefix + step + " FAIL " + detail);
            return false;
        }

        void Fail(string step, string detail) => Check(step, false, detail);

        static NetUnitState[] States() => NetClient.VisibleStates().ToArray();

        static NetUnitState State(int id) => States().FirstOrDefault(s => s.UnitId == id);

        static NetUnitState FindHostile() => States().FirstOrDefault(s => s.Hostile && s.UnitId > 0);

        static Vector3 Position(int id)
        {
            var state = State(id);
            return state != null ? state.transform.position : Vector3.zero;
        }

        static List<Snap> Snapshot() => States().Where(s => s.UnitId > 0).OrderBy(s => s.UnitId)
            .Select(s => new Snap { Id = s.UnitId, Position = s.transform.position, Health = s.Health, Order = s.Order, Queue = s.Queue }).ToList();

        // Null when nothing changed; otherwise the first difference. Positions may differ by 2 cm (interpolation settling).
        static string Differences(List<Snap> before, List<Snap> after)
        {
            if (before.Count != after.Count)
                return "the unit count changed from " + before.Count + " to " + after.Count;
            for (var i = 0; i < before.Count; i++)
            {
                var a = before[i];
                var b = after[i];
                if ((a.Position - b.Position).magnitude > 0.02f)
                    return "unit " + a.Id + " moved";
                if (a.Health != b.Health)
                    return "unit " + a.Id + " health " + a.Health + " -> " + b.Health;
                if (a.Order != b.Order || a.Queue != b.Queue)
                    return "unit " + a.Id + " orders changed";
            }
            return null;
        }

        static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2d;
        }

        void Finish(string role)
        {
            ExitCode = noConnection ? ExitNoConnection : Failed ? ExitFailed : ExitPass;
            if (ExitCode == ExitPass)
            {
                Summary = FormattableString.Invariant(
                    $"BLACKGLASS NET HARNESS PASS role={role} steps={passed.Count} latencyMedianMs={LatencyMedianMs:0.0} roundTripMs={RoundTripMs}");
                Debug.Log(Summary);
            }
            else
            {
                Summary = "BLACKGLASS NET HARNESS FAIL role=" + role + " step=" + failedStep + " reason=" + failure;
                Debug.LogWarning(Summary);
            }
            client.Disconnect();
            Exit(ExitCode);
        }
    }
}
