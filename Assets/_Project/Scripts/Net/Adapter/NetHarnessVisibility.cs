using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Blackglass
{
    // The hidden-information roles of the harness (spec section 3.11). Numbers in comments are the brief's fifteen transitions.
    // Every check reads decoded client state: the avatars this client was sent and NetClient.Record.
    public sealed partial class NetHarness
    {
        /// <summary>How many fixture verbs visibility-a sends (Observe, MoveHostile, Unobserve, MoveHostile, Observe, Search).</summary>
        public const int VisibilityVerbs = 6;
        public const string ReadyLine = "BLACKGLASS NET HARNESS READY role=";

        static readonly HashSet<string> AllowedMessages = new HashSet<string>
        {
            nameof(NetWelcomeMessage), nameof(NetResultMessage), nameof(NetKnowledgeMessage), nameof(NetFixtureStepMessage),
            nameof(NetContainerMessage),
        };

        IEnumerator VisibilityA()
        {
            yield return Connected();
            if (Failed)
                yield break;
            var record = client.Record;
            var own = client.FirstOwnedId;
            var melee = own + 1;
            var guessed = client.FirstOwnedId + client.OwnedCount;   // the first hostile's id, guessed from the numbering

            // 1, 2: nothing hostile before observation.
            yield return Wait(2d);
            if (!Check("hidden at start", FindHostile() == null && record.KnowledgeMessages == 0 && record.ContainerMessages == 0,
                    "no hostile avatar, no knowledge and no container message"))
                yield break;

            // A guessed attack on the hidden hostile is answered exactly like a made-up id, and changes nothing.
            var before = OwnSnapshot();
            var guess = client.Send(new NetAttack(own, guessed));
            var madeUp = client.Send(new NetAttack(own, 999));
            yield return AwaitResult(guess, 3d);
            yield return AwaitResult(madeUp, 3d);
            client.TryGetResult(guess, out var guessReason);
            client.TryGetResult(madeUp, out var madeUpReason);
            if (!Check("guessed attack refused", guessReason == NetRejectReason.UnknownUnit && madeUpReason == NetRejectReason.UnknownUnit,
                    "guessed id answered " + guessReason + ", made-up id answered " + madeUpReason))
                yield break;
            yield return Wait(0.5d);
            var changed = Differences(before, OwnSnapshot());
            if (!Check("guessed attack changed nothing", changed == null, changed ?? "the squad is as it was"))
                yield break;

            // 4, 5: observation reveals the live hostile.
            var observeSentAt = Now;
            yield return Verb(NetFixtureVerb.Observe);
            if (Failed)
                yield break;
            yield return Until(() => FindHostile() != null, 5d);
            if (!Check("revealed", waitOk && FindHostile().Alive && FindHostile().Health > 0, "the hostile arrived with its health"))
                yield break;
            var hostileId = FindHostile().UnitId;
            if (!Check("guess was the hostile", hostileId == guessed, "revealed id " + hostileId + ", guessed id " + guessed))
                yield break;
            yield return Wait(0.5d);
            var revealed = Position(hostileId);
            if (!CheckNothingBefore(hostileId, revealed, observeSentAt))
                yield break;

            // 7, 8: live movement reaches A.
            yield return Verb(NetFixtureVerb.MoveHostile);
            if (Failed)
                yield break;
            yield return Until(() => Flat(Position(hostileId) - revealed) >= 2f, 5d);
            if (!Check("live movement", waitOk, FormattableString.Invariant($"the hostile moved {Flat(Position(hostileId) - revealed):0.0} m")))
                yield break;
            yield return Wait(1d);
            var finalSeen = Position(hostileId);

            // The attack-chase mitigation: an accepted attack ends when observation ends (the melee operative is still walking).
            var attack = client.Send(new NetAttack(melee, hostileId));
            yield return AwaitResult(attack, 3d);
            client.TryGetResult(attack, out var attackReason);
            if (!Check("attack accepted", waitOk && attackReason == NetRejectReason.None, "answered " + attackReason))
                yield break;
            yield return Until(() => State(melee) != null && State(melee).Order == NetOrderKind.Attack, 3d);
            if (!Check("attacking", waitOk, "the melee operative's order is Attack"))
                yield break;

            // 9, 10: observation ends; one frozen record at the final observed position.
            yield return Verb(NetFixtureVerb.Unobserve);
            if (Failed)
                yield break;
            yield return Until(() => FindHostile() == null && record.LastKnown.ContainsKey(hostileId), 5d);
            if (!Check("hidden again", waitOk, "the avatar left and a last-known record arrived"))
                yield break;
            var stale = record.LastKnown[hostileId];
            if (!Check("one record at the last position", record.LastKnown.Count == 1 && Flat(stale - finalSeen) <= 0.25f,
                    FormattableString.Invariant($"{record.LastKnown.Count} record(s), {Flat(stale - finalSeen):0.00} m from the last seen position")))
                yield break;
            yield return Until(() => State(melee) != null && State(melee).Order != NetOrderKind.Attack, 5d);
            if (!Check("attack ended", waitOk, "the server ended the attack on a target this client no longer observes"))
                yield break;

            // 11, 12: hidden movement does not move the record.
            var knowledgeBefore = record.KnowledgeMessages;
            yield return Verb(NetFixtureVerb.MoveHostile);
            if (Failed)
                yield break;
            yield return Wait(1d);
            if (!Check("record frozen", FindHostile() == null && record.LastKnown[hostileId] == stale && record.KnowledgeMessages == knowledgeBefore,
                    "no avatar, no message, the record unchanged"))
                yield break;

            // A reused id is refused like a made-up one.
            var reuse = client.Send(new NetAttack(own, hostileId));
            yield return AwaitResult(reuse, 3d);
            client.TryGetResult(reuse, out var reuseReason);
            if (!Check("reused id refused", waitOk && reuseReason == NetRejectReason.UnknownUnit, "answered " + reuseReason))
                yield break;

            // 14, 15: re-observation shows the current position and clears the stale record.
            yield return Verb(NetFixtureVerb.Observe);
            if (Failed)
                yield break;
            yield return Until(() => FindHostile() != null && !record.LastKnown.ContainsKey(hostileId), 5d);
            if (!Check("re-observed", waitOk, "the avatar is back and the record cleared"))
                yield break;
            yield return Wait(0.5d);
            if (!Check("current position", Flat(Position(hostileId) - stale) >= 2f,
                    FormattableString.Invariant($"{Flat(Position(hostileId) - stale):0.0} m from the stale record")))
                yield break;

            // Loot: nothing before the search, the contents after it.
            if (!Check("no contents before search", record.ContainerMessages == 0 && record.Contents == null, "no container message"))
                yield break;
            yield return Verb(NetFixtureVerb.Search);
            if (Failed)
                yield break;
            yield return Until(() => record.Contents != null, 5d);
            if (!Check("contents after search", waitOk && record.Contents.Count > 0 && record.Contents.All(i =>
                    !string.IsNullOrEmpty(i.DefinitionId) && i.Quantity > 0 && !string.IsNullOrEmpty(i.InstanceId)),
                    (record.Contents != null ? record.Contents.Count : 0) + " stacks"))
                yield break;

            // Only the expected message types were decoded, and nothing was lost from the record.
            var unexpected = record.DecodedMessages.Select(d => d.Type).Where(t => !AllowedMessages.Contains(t)).Distinct().ToList();
            Check("message types", unexpected.Count == 0 && !record.Truncated, unexpected.Count > 0
                ? "unexpected: " + string.Join(", ", unexpected)
                : record.Truncated ? "the record was truncated" : record.DecodedMessages.Count + " messages, all of expected types");
        }

        IEnumerator VisibilityB()
        {
            yield return Connected();
            if (Failed)
                yield break;
            var record = client.Record;
            // B has no fixture identity: its own attempt to drive the fixture changes nothing.
            client.SendFixture(NetFixtureVerb.Observe);
            client.SendFixture(NetFixtureVerb.Search);
            yield return Wait(1d);
            if (!CheckBlind("refused fixture"))
                yield break;
            Debug.Log(ReadyLine + "visibility-b");

            var first = -1;
            var last = record.FixtureStep;
            var end = Now + 180d;
            while (Now < end)
            {
                if (!IsBlind(out _))
                {
                    CheckBlind("blind at step " + record.FixtureStep);
                    yield break;
                }
                if (record.FixtureStep != last)
                {
                    last = record.FixtureStep;
                    if (first < 0)
                        first = last;
                    Debug.Log(Prefix + "step " + last + " blind");
                    if (last >= first + VisibilityVerbs - 1)
                        break;
                }
                yield return null;
            }
            if (!Check("saw A's steps", first >= 0 && last >= first + VisibilityVerbs - 1, "steps " + first + " to " + last))
                yield break;
            yield return Wait(1d);   // time for A's last verb (the search) to arrive here, if it were going to leak
            CheckBlind("blind after the last step");
        }

        IEnumerator FixtureDenied()
        {
            yield return Connected();
            if (Failed)
                yield break;
            client.SendFixture(NetFixtureVerb.Observe);
            client.SendFixture(NetFixtureVerb.Search);
            yield return Wait(3d);
            var record = client.Record;
            Check("fixture refused", FindHostile() == null && !record.Sightings.Any(s => s.Hostile) && record.LastKnown.Count == 0
                && record.FixtureStep == 0 && record.KnowledgeMessages == 0 && record.ContainerMessages == 0 && record.Contents == null
                && !record.Truncated, "no hostile ever seen, no record, no step, no knowledge, no container, no contents");
        }

        IEnumerator Verb(NetFixtureVerb verb)
        {
            var before = client.Record.FixtureStep;
            client.SendFixture(verb);
            yield return Until(() => client.Record.FixtureStep > before, 5d);
            Check("fixture " + verb, waitOk, "step " + client.Record.FixtureStep);
        }

        // Before observation nothing may have carried the hostile: no avatar sighting of it, and no sighting or decoded message
        // holding a position within 5 cm of where it turned out to be (its spawn, since it had not moved).
        bool CheckNothingBefore(int hostileId, Vector3 revealed, double observeSentAt)
        {
            var record = client.Record;
            var hostileSightings = record.Sightings.Count(s => s.Time < observeSentAt && (s.Hostile || s.UnitId == hostileId));
            var positions = record.Sightings.Where(s => s.Time < observeSentAt).Select(s => s.Position)
                .Concat(record.DecodedMessages.Where(d => d.Time < observeSentAt && d.HasPosition).Select(d => d.Position))
                .Count(p => (p - revealed).magnitude <= 0.05f);
            return Check("nothing before the reveal", hostileSightings == 0 && positions == 0 && !record.Truncated,
                hostileSightings + " hostile sightings and " + positions + " positions at the hostile before it was observed");
        }

        bool IsBlind(out string detail)
        {
            var record = client.Record;
            var avatars = States().Length;
            detail = avatars + " avatars, " + record.Sightings.Count + " sightings, " + record.LastKnown.Count + " last-known records, " +
                record.KnowledgeMessages + " knowledge and " + record.ContainerMessages + " container messages";
            var decoded = record.DecodedMessages.Select(d => d.Type).Where(t => !AllowedMessages.Contains(t)).Distinct().ToList();
            var positioned = record.DecodedMessages.Count(d => d.HasPosition);
            detail += ", " + decoded.Count + " unexpected message types, " + positioned + " positioned messages";
            return decoded.Count == 0 && positioned == 0 && avatars == 0 && record.Sightings.Count == 0 && record.LastKnown.Count == 0 && record.KnowledgeMessages == 0
                && record.ContainerMessages == 0 && record.Contents == null && !record.Truncated;
        }

        bool CheckBlind(string step) => Check(step, IsBlind(out var detail), detail);

        List<Snap> OwnSnapshot() =>
            Snapshot().Where(s => s.Id >= client.FirstOwnedId && s.Id < client.FirstOwnedId + client.OwnedCount).ToList();

        static float Flat(Vector3 offset)
        {
            offset.y = 0f;
            return offset.magnitude;
        }
    }
}
