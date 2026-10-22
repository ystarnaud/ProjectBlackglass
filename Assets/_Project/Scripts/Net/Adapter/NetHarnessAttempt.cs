using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The loopback fixture checkpoint's `attempt` role: a scripted, operator-assisted player for the fixture attempt
    /// (-attemptFixture with -net and no -netQuiet). It sends only the four existing requests and reads only what this client was
    /// sent: its own avatars, a hostile's avatar while the squad observes it, and last-known records. It also uses two ground points
    /// the operator read from the fixture server's log: where the hostile starts and where extraction is. Nothing on the wire says
    /// either, so this is an assisted run; it never shows that a client can find its way unaided. The client is not told the
    /// mission ended: the role passes when an own unit stands in the extraction zone, and the server's log shows the freeze.
    /// </summary>
    public sealed partial class NetHarness
    {
        /// <summary>Inside the extraction zone (MissionContent.ZoneRadius 2 m), with a margin for the client's interpolated position.</summary>
        const float ExtractionReach = 1.5f;

        Vector3? assistHostile;
        Vector3? assistExtraction;

        IEnumerator Attempt()
        {
            yield return Connected();
            if (Failed)
                yield break;
            if (!Check("assist", assistHostile.HasValue && assistExtraction.HasValue,
                    "the operator's hints: hostile " + Hint(assistHostile) + ", extraction " + Hint(assistExtraction)))
                yield break;
            yield return Until(() => client.OwnedCount > 0 && Own().Count == client.OwnedCount, 10d);
            if (!Check("squad", waitOk, Own().Count + " of " + client.OwnedCount + " own units replicated:" + Describe(Own())))
                yield break;

            // 1. Toward the hostile's start: ordinary moves through the gate, for every unit.
            yield return Accepted(Own().Select(u => client.Send(new NetMoveTo(u.UnitId, assistHostile.Value))).ToList(), "move toward the hostile", false);
            if (Failed)
                yield break;

            // 2. A hostile's avatar arrives only once the squad observes it.
            yield return Until(() => LivingHostile() != null || !Own().Any(u => u.Alive), 90d);
            if (!Check("hostile observed", LivingHostile() != null, "a hostile avatar arrived:" + Describe(Own())))
                yield break;
            var hostileId = LivingHostile().UnitId;

            // 3. Attack it until it dies. While it is observed, a living unit that is not attacking it is ordered to; while it is
            // hidden, such a unit walks to where it was last seen. The hostile's own AI and the squad's autonomy fight as usual.
            yield return Accepted(Own().Where(u => u.Alive).Select(u => client.Send(new NetAttack(u.UnitId, hostileId))).ToList(), "attack the observed hostile", true);
            if (Failed)
                yield break;
            var deadline = Now + 120d;
            var nextOrders = Now + 1d;
            while (Now < deadline && !HostileDead(hostileId) && Own().Any(u => u.Alive))
            {
                if (Now >= nextOrders)
                {
                    nextOrders = Now + 1d;
                    var hostile = State(hostileId);
                    foreach (var unit in Own().Where(u => u.Alive && u.Order != NetOrderKind.Attack))
                    {
                        if (hostile != null)
                            client.Send(new NetAttack(unit.UnitId, hostileId));
                        else if (client.Record.LastKnown.TryGetValue(hostileId, out var seen))
                            client.Send(new NetMoveTo(unit.UnitId, seen));
                    }
                }
                yield return null;
            }
            if (!Check("hostile eliminated", HostileDead(hostileId), "the hostile's avatar reports it dead:" + Describe(Own())))
                yield break;

            // 4. Every living unit walks to the operator's extraction point; the server decides the extraction on its own.
            yield return Accepted(Own().Where(u => u.Alive).Select(u => client.Send(new NetMoveTo(u.UnitId, assistExtraction.Value))).ToList(), "move to extraction", false);
            if (Failed)
                yield break;
            yield return Until(() => Own().Any(u => u.Alive && Flat(u.transform.position - assistExtraction.Value) <= ExtractionReach), 90d);
            if (!Check("extraction reached", waitOk, "an own unit stands at the extraction point:" + Describe(Own())))
                yield break;
            // Stay a moment, so the server ticks the zone with this client still connected.
            yield return Wait(2d);
        }

        // Waits for every command's answer: all accepted, or with `any` at least one (an attack may race the hostile leaving sight).
        IEnumerator Accepted(List<uint> commands, string step, bool any)
        {
            yield return Until(() => commands.All(c => client.TryGetResult(c, out _)), 5d);
            var reasons = commands.Select(c => client.TryGetResult(c, out var reason) ? reason.ToString() : "no answer").ToList();
            var accepted = reasons.Count(r => r == nameof(NetRejectReason.None));
            Check(step, commands.Count > 0 && (any ? accepted > 0 : accepted == commands.Count), "answers " + string.Join(", ", reasons));
        }

        List<NetUnitState> Own() => States()
            .Where(s => !s.Hostile && s.UnitId >= client.FirstOwnedId && s.UnitId < client.FirstOwnedId + client.OwnedCount)
            .OrderBy(s => s.UnitId).ToList();

        static NetUnitState LivingHostile() => States().FirstOrDefault(s => s.Hostile && s.UnitId > 0 && s.Alive);

        // A hostile that dies while observed keeps its avatar, which then reports it dead.
        static bool HostileDead(int hostileId)
        {
            var state = State(hostileId);
            return state != null && !state.Alive;
        }

        static string Hint(Vector3? point) => point.HasValue ? NetGround.Format(point.Value) : "missing";

        static string Describe(List<NetUnitState> units) =>
            string.Concat(units.Select(u => " #" + u.UnitId + " hp " + u.Health + " at " + NetGround.Format(u.transform.position)));
    }
}
